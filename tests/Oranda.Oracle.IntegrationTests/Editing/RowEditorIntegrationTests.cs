using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Grid;
using Oranda.Core.Metadata;
using Oranda.Core.Security;
using Oranda.Core.Sessions;
using Oranda.Language.Editing;
using Oranda.Oracle.Connection;
using Oranda.Oracle.Execution;
using Oranda.Oracle.IntegrationTests.Execution;
using Oranda.Oracle.IntegrationTests.Integration;
using Oranda.Oracle.IntegrationTests.Metadata;
using Oranda.Oracle.Metadata;
using Oranda.Oracle.Sessions;

namespace Oranda.Oracle.IntegrationTests.Editing;

// 소유: TSK-06-04. 그리드 편집 시나리오(컨테이너 26ai): 키 없는 결과 거부, ROWID 추가 후 편집, PK 바인드 DML,
// 동시 삭제·중복 키·영향 행 2·낙관적 잠금에서 반영 안 됨, 읽기 전용 프로필.
[Trait("Category", "Integration")]
public sealed class RowEditorIntegrationTests : IClassFixture<OracleFreeFixture>, IAsyncLifetime
{
    private readonly OracleFreeFixture _db;
    private readonly ExecBus _bus = new();
    private readonly ExecInteraction _ui = new();
    private OracleSessionManager _manager = null!;
    private OracleExecutionService _exec = null!;
    private RowEditor _editor = null!;
    private OracleMetadataProvider _meta = null!;
    private readonly string _pkTable = "ORANDA_ED_PK_" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
    private readonly string _bareTable = "ORANDA_ED_NK_" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    public RowEditorIntegrationTests(OracleFreeFixture db) => _db = db;

    private static readonly Dictionary<string, BindValue> NoBinds = [];

    public async Task InitializeAsync()
    {
        _manager = new OracleSessionManager(_bus);
        _exec = new OracleExecutionService(new ExecutionPolicy(), _ui, _bus, null, pageSize: 100);
        _editor = new RowEditor(_exec);
        _meta = new OracleMetadataProvider(new MemoryMetadataCache(), _bus, Timeout.InfiniteTimeSpan);
        await AdminAsync($"CREATE TABLE {_pkTable} (id NUMBER PRIMARY KEY, name VARCHAR2(30), sal NUMBER(10,2), hired DATE)");
        await AdminAsync($"INSERT INTO {_pkTable} SELECT level, 'N' || level, level * 100, DATE '2020-01-01' + level FROM dual CONNECT BY level <= 5");
        await AdminAsync($"CREATE TABLE {_bareTable} (grp NUMBER, name VARCHAR2(30))");
        await AdminAsync($"INSERT INTO {_bareTable} VALUES (1, 'a')");
        await AdminAsync($"INSERT INTO {_bareTable} VALUES (1, 'b')");
        await AdminAsync($"INSERT INTO {_bareTable} VALUES (2, 'c')");
        await AdminAsync("COMMIT");
    }

    public async Task DisposeAsync()
    {
        foreach (var s in _manager.Sessions)
        {
            await s.DisposeAsync();
        }

        await AdminAsync($"DROP TABLE {_pkTable} PURGE");
        await AdminAsync($"DROP TABLE {_bareTable} PURGE");
    }

    private async Task<OracleSession> OpenAsync(bool readOnly = false)
    {
        var profile = new ConnectionProfile("free-system", "system", _db.DataSource, ReadOnly: readOnly);
        using var cred = new SecureCredential(_db.SystemPassword);
        return (OracleSession)await _manager.OpenAsync(profile, cred, SessionPurpose.Work, default);
    }

    private static SqlStatement Q(string text) => new(text, default, StatementKind.Query) { Verb = "SELECT" };

    private async Task<(RowEditPlan Plan, ResultPage Page)> QueryForEditAsync(ISession s, SqlStatement q, string table)
    {
        var r = await _exec.ExecuteAsync(s, q, NoBinds, null, default);
        Assert.Equal(ExecutionStatus.Succeeded, r.Status);
        await using var rs = r.ResultSet!;
        var page = await rs.GetPageAsync(0, default);
        var info = await _meta.GetTableAsync(s, new DbObjectRef("SYSTEM", table, "TABLE"), default);
        return (RowEditPlanner.Plan(q, rs.Columns, TableKeys.From(info)), page);
    }

    private static Dictionary<string, object?> Row(RowEditPlan plan, ResultPage page, int row, IEnumerable<int> ordinals) =>
        ordinals.ToDictionary(o => plan.ColumnNames[o]!, o => page.Columns[o][row], StringComparer.Ordinal);

    [Fact]
    public async Task Keyless_result_is_refused_until_rowid_is_added_then_updates_by_rowid()
    {
        await using var s = await OpenAsync();

        var (plan, _) = await QueryForEditAsync(s, Q($"SELECT name, sal FROM {_pkTable} ORDER BY id"), _pkTable);
        Assert.False(plan.Capability.CanEdit);
        Assert.Equal(EditBlockReason.NoKeyColumns, plan.Capability.Reason);
        Assert.True(plan.Capability.CanAddRowId);

        var (plan2, page) = await QueryForEditAsync(s, plan.Capability.QueryWithRowId!, _pkTable);
        Assert.True(plan2.Capability.CanEdit);
        Assert.True(plan2.UsesRowId);

        var change = new RowChange(RowChangeKind.Update, 0, plan2.Capability.Target!)
        {
            Key = Row(plan2, page, 0, plan2.Capability.KeyColumnOrdinals),
            NewValues = new Dictionary<string, object?> { ["NAME"] = "rowid-edit" },
        };
        Assert.Contains("WHERE ROWID = :rid", _editor.BuildDml([change])[0].Text, StringComparison.Ordinal);

        var result = await _editor.ApplyAsync(s, [change], commit: true, default);

        Assert.Equal(1, result.AppliedCount);
        Assert.True(result.Committed);
        Assert.Equal("rowid-edit", await ScalarFromOtherSessionAsync($"SELECT name FROM {_pkTable} WHERE id = 1"));
    }

    [Fact]
    public async Task Table_without_any_key_or_rowid_is_not_editable()
    {
        await using var s = await OpenAsync();

        var (plan, _) = await QueryForEditAsync(s, Q($"SELECT * FROM {_bareTable}"), _bareTable);

        Assert.False(plan.Capability.CanEdit);
        Assert.Equal(EditBlockReason.NoKeyColumns, plan.Capability.Reason);
        Assert.NotNull(plan.Capability.Message);

        // ROWID 를 더한 문장은 서버에서 실제로 돈다("*" → "t.*").
        var (plan2, _) = await QueryForEditAsync(s, plan.Capability.QueryWithRowId!, _bareTable);
        Assert.True(plan2.Capability.CanEdit);
    }

    [Fact]
    public async Task Pk_result_updates_with_bound_where_and_rolls_back_concurrently_deleted_row()
    {
        await using var s = await OpenAsync();
        var (plan, page) = await QueryForEditAsync(s, Q($"SELECT t.id, t.name, t.sal FROM {_pkTable} t WHERE id IN (2, 3) ORDER BY id"), _pkTable);
        Assert.True(plan.Capability.CanEdit);
        var key = plan.Capability.KeyColumnOrdinals;

        var changes = new[]
        {
            new RowChange(RowChangeKind.Update, 0, plan.Capability.Target!)
            {
                Key = Row(plan, page, 0, key),
                NewValues = new Dictionary<string, object?> { ["SAL"] = 222.5m },
            },
            new RowChange(RowChangeKind.Update, 1, plan.Capability.Target!)
            {
                Key = Row(plan, page, 1, key),
                NewValues = new Dictionary<string, object?> { ["SAL"] = 333m },
            },
        };
        Assert.Equal($"UPDATE SYSTEM.{_pkTable} SET SAL = :v1 WHERE ID = :k1", _editor.BuildDml(changes)[0].Text);

        // 다른 세션이 3번 행을 지우고 커밋.
        await AdminAsync($"DELETE FROM {_pkTable} WHERE id = 3");
        await AdminAsync("COMMIT");

        var result = await _editor.ApplyAsync(s, changes, commit: true, default);

        Assert.Equal(1, result.AppliedCount);
        Assert.False(result.Committed); // 실패가 있으면 커밋하지 않는다.
        var f = Assert.Single(result.Failures);
        Assert.Equal(1, f.RowIndex);
        Assert.Equal(0, f.AffectedRows);
        Assert.True(s.HasPendingTransaction);
        await s.RollbackAsync(default);
        Assert.Equal("200", await ScalarFromOtherSessionAsync($"SELECT sal FROM {_pkTable} WHERE id = 2"));
    }

    [Fact]
    public async Task Duplicate_key_insert_is_rolled_back_and_reported()
    {
        await using var s = await OpenAsync();
        var target = new DbObjectRef("SYSTEM", _pkTable, "TABLE");
        var changes = new[]
        {
            new RowChange(RowChangeKind.Insert, 10, target) { NewValues = new Dictionary<string, object?> { ["ID"] = 100m, ["NAME"] = "new" } },
            new RowChange(RowChangeKind.Insert, 11, target) { NewValues = new Dictionary<string, object?> { ["ID"] = 1m, ["NAME"] = "dup" } },
        };

        var result = await _editor.ApplyAsync(s, changes, commit: false, default);

        Assert.Equal(1, result.AppliedCount);
        var f = Assert.Single(result.Failures);
        Assert.Equal(11, f.RowIndex);
        Assert.Equal(1, f.Error?.Number); // ORA-00001
        var count = await _exec.ExecuteAsync(s, Q($"SELECT COUNT(*) FROM {_pkTable} WHERE id IN (1, 100)"), NoBinds, null, default);
        await using (var rs = count.ResultSet!)
        {
            Assert.Equal(2m, (await rs.GetPageAsync(0, default)).Columns[0][0]); // 기존 1 + 새 100, dup 은 없음
        }

        await s.RollbackAsync(default);
    }

    [Fact]
    public async Task Two_affected_rows_are_rolled_back()
    {
        await using var s = await OpenAsync();
        var target = new DbObjectRef("SYSTEM", _bareTable, "TABLE");

        // 메타데이터가 틀렸다고 가정(GRP 를 키로 믿음): 서버가 2 행을 바꾸면 그 문장을 되돌린다.
        var change = new RowChange(RowChangeKind.Update, 0, target)
        {
            Key = new Dictionary<string, object?> { ["GRP"] = 1m },
            NewValues = new Dictionary<string, object?> { ["NAME"] = "zz" },
        };

        var result = await _editor.ApplyAsync(s, [change], commit: true, default);

        Assert.Equal(0, result.AppliedCount);
        Assert.Equal(2, Assert.Single(result.Failures).AffectedRows);
        Assert.False(result.Committed);
        var r = await _exec.ExecuteAsync(s, Q($"SELECT COUNT(*) FROM {_bareTable} WHERE name = 'zz'"), NoBinds, null, default);
        await using var rs = r.ResultSet!;
        Assert.Equal(0m, (await rs.GetPageAsync(0, default)).Columns[0][0]);
        await s.RollbackAsync(default);
    }

    [Fact]
    public async Task Optimistic_lock_detects_change_by_other_session()
    {
        await using var s = await OpenAsync();
        var (plan, page) = await QueryForEditAsync(s, Q($"SELECT id, name, hired FROM {_pkTable} WHERE id = 4"), _pkTable);
        var change = new RowChange(RowChangeKind.Update, 0, plan.Capability.Target!)
        {
            Key = Row(plan, page, 0, plan.Capability.KeyColumnOrdinals),
            NewValues = new Dictionary<string, object?> { ["NAME"] = "mine" },
            OriginalValues = Row(plan, page, 0, plan.Capability.EditableColumnOrdinals.Where(o => plan.ColumnNames[o] != "ID")),
        };

        await AdminAsync($"UPDATE {_pkTable} SET name = 'theirs' WHERE id = 4");
        await AdminAsync("COMMIT");

        var result = await _editor.ApplyAsync(s, [change], commit: true, default);

        Assert.Equal(0, result.AppliedCount);
        Assert.Equal(0, Assert.Single(result.Failures).AffectedRows);
        Assert.Equal("theirs", await ScalarFromOtherSessionAsync($"SELECT name FROM {_pkTable} WHERE id = 4"));

        // 원래 값이 그대로면(DATE 비교 포함) 반영된다.
        var (plan2, page2) = await QueryForEditAsync(s, Q($"SELECT id, name, hired FROM {_pkTable} WHERE id = 5"), _pkTable);
        var ok = change with
        {
            Key = Row(plan2, page2, 0, plan2.Capability.KeyColumnOrdinals),
            OriginalValues = Row(plan2, page2, 0, plan2.Capability.EditableColumnOrdinals.Where(o => plan2.ColumnNames[o] != "ID")),
        };
        Assert.Equal(1, (await _editor.ApplyAsync(s, [ok], commit: true, default)).AppliedCount);
    }

    [Fact]
    public async Task Read_only_profile_cannot_apply()
    {
        await using var s = await OpenAsync(readOnly: true);
        var change = new RowChange(RowChangeKind.Delete, 0, new DbObjectRef("SYSTEM", _pkTable, "TABLE"))
        {
            Key = new Dictionary<string, object?> { ["ID"] = 1m },
        };

        var result = await _editor.ApplyAsync(s, [change], commit: true, default);

        Assert.Equal(0, result.AppliedCount);
        Assert.Equal("ORANDA-READONLY", Assert.Single(result.Failures).Error?.Code);
        Assert.Equal("1", await ScalarFromOtherSessionAsync($"SELECT COUNT(*) FROM {_pkTable} WHERE id = 1"));
    }

    private async Task AdminAsync(string sql)
    {
        await using var other = new OracleConnection(
            new ConnectionProfile("o", "system", _db.DataSource).BuildConnectionString(_db.SystemPassword));
        await other.OpenAsync();
        await using var cmd = other.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<string?> ScalarFromOtherSessionAsync(string sql)
    {
        await using var other = new OracleConnection(
            new ConnectionProfile("o", "system", _db.DataSource).BuildConnectionString(_db.SystemPassword));
        await other.OpenAsync();
        await using var cmd = other.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToString(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
