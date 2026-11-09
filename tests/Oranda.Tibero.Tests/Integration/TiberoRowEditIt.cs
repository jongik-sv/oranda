using Oranda.Core.Execution;
using Oranda.Core.Grid;
using Oranda.Core.Metadata;
using Oranda.Language.Editing;
using Oranda.Tibero.Execution;
using Oranda.Tibero.Sessions;

namespace Oranda.Tibero.Tests.Integration;

/// <summary>
/// 결과 편집(R4)을 실제 서버로 확인한다. 읽기: 질의 경로가 ROWID 열(<see cref="ColumnDataKind.RowId"/>)을 내고 편집 계획이 그것을 행 키로 쓰며,
/// ROWID 없는 <c>SELECT *</c> 는 「ROWID 추가 후 다시 실행」 문장을 내놓는다. 쓰기(<c>ORANDA_IT_TIBERO_WRITE=1</c>): <c>ORT_</c> 표 하나로
/// <c>RowEditor.ApplyAsync</c>(SAVEPOINT → DML → 영향 행 수 1) 가 ROWID·기본 키로 UPDATE·DELETE·INSERT 를 하고 실패한 행만 되돌리는지 본다.
/// 서버·드라이버가 없으면 아무것도 하지 않는다. 접속 정보는 출력에 싣지 않는다.
/// </summary>
[Trait("Category", "Integration")]
public class TiberoRowEditIt
{
    private static readonly IReadOnlyDictionary<string, BindValue> NoBinds = new Dictionary<string, BindValue>();

    [TiberoItFact]
    public async Task Query_with_rowid_column_plans_as_rowid_key_and_select_star_offers_the_rowid_rerun()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var session = await it.OpenAsync();
        var service = NewService(it);
        var qualified = await FindReadableTableAsync(session);
        if (qualified is null)
        {
            return; // 이 계정이 읽을 수 있는 표가 없다
        }

        var dot = qualified.IndexOf('.', StringComparison.Ordinal);
        var table = new DbObjectRef(qualified[..dot], qualified[(dot + 1)..], "TABLE");
        var noKey = new TableKeyInfo(table, TableKeyKind.None, []);

        var withRowId = Query($"SELECT t.ROWID, t.* FROM {qualified} t WHERE ROWNUM <= 3");
        await using (var rs = await RunQueryAsync(service, session, withRowId))
        {
            Assert.Equal(ColumnDataKind.RowId, rs.Columns[0].Kind);
            Assert.All(rs.Columns, c => Assert.Null(c.BaseTable)); // 드라이버가 준 출처는 믿지 않는다
            var plan = RowEditPlanner.Plan(withRowId, rs.Columns, noKey);
            Assert.True(plan.Capability.CanEdit, plan.Capability.Message);
            Assert.True(plan.UsesRowId);
            Assert.Equal([0], plan.Capability.KeyColumnOrdinals);

            var page = await rs.GetPageAsync(0, CancellationToken.None);
            Assert.True(page.RowCount > 0);
            Assert.Equal(18, Assert.IsType<string>(page.Columns[0][0]).Length);
        }

        var star = Query($"SELECT * FROM {qualified} WHERE ROWNUM <= 3");
        SqlStatement? rerun;
        await using (var rs = await RunQueryAsync(service, session, star))
        {
            var plan = RowEditPlanner.Plan(star, rs.Columns, noKey);
            Assert.False(plan.Capability.CanEdit);
            Assert.True(plan.Capability.CanAddRowId);
            rerun = plan.Capability.QueryWithRowId;
        }

        Assert.NotNull(rerun);
        await using (var rs = await RunQueryAsync(service, session, rerun!))
        {
            // 「ROWID 추가」 재실행문은 SELECT q.*, q.ROWID 꼴이라 ROWID 는 첫 열이 아니라 마지막 열이다.
            Assert.Equal(ColumnDataKind.RowId, rs.Columns[^1].Kind);
            Assert.Equal("ROWID", rs.Columns[^1].Name, ignoreCase: true);
            Assert.True(RowEditPlanner.Plan(rerun!, rs.Columns, noKey).Capability.CanEdit);
        }
    }

    [TiberoWriteFact]
    public async Task Row_editor_updates_and_deletes_by_rowid_and_key_and_rolls_back_only_the_failed_row()
    {
        if (TiberoItSessions.SkipWrite())
        {
            return;
        }

        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var name = TiberoItSessions.ObjectName("R4");
        var owner = it.Env.User.ToUpperInvariant();
        var session = await it.OpenAsync();
        var service = NewService(it);
        var editor = NewEditor(service);
        var table = new DbObjectRef(owner, name, "TABLE");
        if (TiberoSessionQuery.Long(await TiberoSessionQuery.ScalarAsync(session, $"SELECT COUNT(*) FROM user_tables WHERE table_name = '{name}'")) > 0)
        {
            await ExecAsync(session, $"DROP TABLE {name}");
        }

        await ExecAsync(session, $"CREATE TABLE {name} (ID NUMBER NOT NULL, NAME VARCHAR2(20), CONSTRAINT {name}_PK PRIMARY KEY (ID))");
        try
        {
            await ExecAsync(session, $"INSERT INTO {name} VALUES (1, 'one')");
            await ExecAsync(session, $"INSERT INTO {name} VALUES (2, 'two')");
            await session.CommitAsync(CancellationToken.None);

            string rowId1;
            var select = Query($"SELECT t.ROWID, t.ID, t.NAME FROM {owner}.{name} t ORDER BY t.ID");
            await using (var rs = await RunQueryAsync(service, session, select))
            {
                var plan = RowEditPlanner.Plan(select, rs.Columns, new TableKeyInfo(table, TableKeyKind.PrimaryKey, ["ID"]));
                Assert.True(plan.Capability.CanEdit, plan.Capability.Message);
                var page = await rs.GetPageAsync(0, CancellationToken.None);
                rowId1 = Assert.IsType<string>(page.Columns[0][0]);
            }

            // ROWID 로 UPDATE: 영향 행 1 → 반영(커밋 안 함), 롤백하면 되돌아간다.
            var update = new RowChange(RowChangeKind.Update, 0, table)
            {
                Key = new Dictionary<string, object?> { ["ROWID"] = rowId1 },
                NewValues = new Dictionary<string, object?> { ["NAME"] = "uno" },
            };
            var applied = await editor.ApplyAsync(session, [update], commit: false, CancellationToken.None);
            Assert.Equal(1, applied.AppliedCount);
            Assert.Empty(applied.Failures);
            Assert.False(applied.Committed);
            Assert.Equal("uno", await TextAsync(session, $"SELECT name FROM {name} WHERE id = 1"));
            await session.RollbackAsync(CancellationToken.None);
            Assert.Equal("one", await TextAsync(session, $"SELECT name FROM {name} WHERE id = 1"));

            // 기본 키로 UPDATE(낙관적 잠금: 원래 값이 맞으면 반영, 틀리면 그 행만 실패).
            var good = new RowChange(RowChangeKind.Update, 0, table)
            {
                Key = new Dictionary<string, object?> { ["ID"] = 1 },
                NewValues = new Dictionary<string, object?> { ["NAME"] = "uno" },
                OriginalValues = new Dictionary<string, object?> { ["NAME"] = "one" },
            };
            var stale = new RowChange(RowChangeKind.Update, 1, table)
            {
                Key = new Dictionary<string, object?> { ["ID"] = 2 },
                NewValues = new Dictionary<string, object?> { ["NAME"] = "dos" },
                OriginalValues = new Dictionary<string, object?> { ["NAME"] = "someone else" },
            };
            var mixed = await editor.ApplyAsync(session, [good, stale], commit: false, CancellationToken.None);
            Assert.Equal(1, mixed.AppliedCount);
            var failure = Assert.Single(mixed.Failures);
            Assert.Equal(1, failure.RowIndex);
            Assert.Equal(0, failure.AffectedRows);
            Assert.Equal("uno", await TextAsync(session, $"SELECT name FROM {name} WHERE id = 1"));
            Assert.Equal("two", await TextAsync(session, $"SELECT name FROM {name} WHERE id = 2"));

            // INSERT·DELETE 를 한 번에 반영하고 커밋.
            var insert = new RowChange(RowChangeKind.Insert, 2, table)
            {
                NewValues = new Dictionary<string, object?> { ["ID"] = 3, ["NAME"] = "three" },
            };
            var delete = new RowChange(RowChangeKind.Delete, 3, table)
            {
                Key = new Dictionary<string, object?> { ["ID"] = 2 },
            };
            var final = await editor.ApplyAsync(session, [insert, delete], commit: true, CancellationToken.None);
            Assert.Equal(2, final.AppliedCount);
            Assert.True(final.Committed);
            Assert.False(session.HasPendingTransaction);
            Assert.Equal("1|3", await TextAsync(session, $"SELECT LISTAGG(TO_CHAR(id), '|') WITHIN GROUP (ORDER BY id) FROM {name}"));
        }
        finally
        {
            await session.RollbackAsync(CancellationToken.None);
            await ExecAsync(session, $"DROP TABLE {name}");
        }
    }

    /// <summary>접속 사용자가 <c>ROWID</c> 까지 읽을 수 있고 행이 있는 표 하나(<c>OWNER.TABLE</c>). 없으면 null.</summary>
    private static async Task<string?> FindReadableTableAsync(TiberoSession session)
    {
        var candidates = await TiberoSessionQuery.RunAsync(session,
            "SELECT owner || '.' || table_name FROM all_tables WHERE owner NOT IN ('PUBLIC') ORDER BY CASE WHEN owner = USER THEN 0 ELSE 1 END, owner, table_name",
            maxRows: 300);
        foreach (var row in candidates.Rows)
        {
            var qualified = TiberoSessionQuery.Str(row[0]);
            if (qualified is null)
            {
                continue;
            }

            try
            {
                var probe = await TiberoSessionQuery.RunAsync(session, $"SELECT t.ROWID FROM {qualified} t WHERE ROWNUM <= 1");
                if (probe.Rows.Count > 0)
                {
                    return qualified; // 행이 하나라도 있는 표(빈 표는 페이지 확인이 안 된다)
                }
            }
#pragma warning disable CA1031 // 읽을 수 없는 표(권한·뷰 성격)는 건너뛴다.
            catch (Exception)
#pragma warning restore CA1031
            {
            }
        }

        return null;
    }

    private static async Task<IResultSet> RunQueryAsync(TiberoExecutionService service, TiberoSession session, SqlStatement statement)
    {
        var result = await service.ExecuteAsync(session, statement, NoBinds, null, CancellationToken.None);
        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        return Assert.IsAssignableFrom<IResultSet>(result.ResultSet);
    }

    private static TiberoExecutionService NewService(TiberoItSessions it) =>
        new(new AllowAll(), new NoQuestions(), it.Events, null, dbmsOutput: false);

    /// <summary><c>RowEditor</c> 는 Oranda.Language 안의 internal 형식이라 공개 등록(<see cref="IRowEditor"/>)과 같은 모양으로 직접 만든다.</summary>
    private static IRowEditor NewEditor(IExecutionService execution)
    {
        var type = Type.GetType("Oranda.Language.Editing.RowEditor, Oranda.Language", throwOnError: true)!;
        return (IRowEditor)Activator.CreateInstance(type, execution)!;
    }

    private static SqlStatement Query(string text) => new(text, default, StatementKind.Query);

    private static async Task<string?> TextAsync(TiberoSession session, string sql) =>
        TiberoSessionQuery.Str(await TiberoSessionQuery.ScalarAsync(session, sql));

    private static async Task ExecAsync(TiberoSession session, string sql)
    {
        await session.CommandGate.EnterAsync(CancellationToken.None);
        try
        {
            await using var command = session.CreateCommand(sql);
            await command.ExecuteNonQueryAsync();
        }
        finally
        {
            session.CommandGate.Release();
        }
    }

    private sealed class AllowAll : IExecutionPolicy
    {
        public ValueTask<PolicyDecision> EvaluateAsync(PolicyContext ctx, SqlStatement stmt, CancellationToken ct) =>
            ValueTask.FromResult(PolicyDecision.Allowed);
    }

    private sealed class NoQuestions : IExecutionInteraction
    {
        public Task<bool> ConfirmAsync(SqlStatement stmt, PolicyDecision decision, CancellationToken ct) => Task.FromResult(true);

        public Task<IReadOnlyDictionary<string, string>?> AskSubstitutionsAsync(IReadOnlyList<string> names, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<string, string>?>(null);

        public Task<ScriptErrorAction> AskScriptErrorAsync(SqlStatement stmt, DbError failure, CancellationToken ct) =>
            Task.FromResult(ScriptErrorAction.Stop);
    }
}
