using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Grid;
using Oranda.Core.Sessions;
using Oranda.Tibero.Execution;
using Oranda.Tibero.Tests.Integration;

namespace Oranda.Tibero.Tests.Execution;

/// <summary>
/// 실행 서비스(E1·E4)를 실제 서버로 확인한다(쓰기 없음: PSM 블록·바인드·트랜잭션 추적·읽기 전용 준비·질의 결과 집합·스크립트). 서버·드라이버가 없으면 아무것도 하지 않는다.
/// 트랜잭션은 쓰기 없이 <c>DBMS_TRANSACTION.LOCAL_TRANSACTION_ID(TRUE)</c> 로 연다(docs/design/tibero-driver-exec.md 2장).
/// </summary>
[Trait("Category", "Integration")]
public class TiberoExecutionServiceIt
{
    private const string OpenTransactionBlock =
        "DECLARE x VARCHAR2(100); BEGIN x := DBMS_TRANSACTION.LOCAL_TRANSACTION_ID(TRUE); END;";

    private static readonly IReadOnlyDictionary<string, BindValue> NoBinds = new Dictionary<string, BindValue>();

    [TiberoItFact]
    public async Task Block_with_in_and_out_binds_returns_the_out_value_and_no_pending_transaction()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var session = await it.OpenAsync();
        var service = NewService(it);
        var stmt = Stmt("BEGIN :msg := 'hi ' || :name; END;") with { BindNames = ["msg", "name"] };
        var binds = new Dictionary<string, BindValue>
        {
            ["msg"] = new(BindDataType.Varchar2, null, BindDirection.Out),
            ["name"] = new(BindDataType.Varchar2, "kim"),
        };

        var result = await service.ExecuteAsync(session, stmt, binds, null, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal("hi kim", result.OutValues["msg"]);
        Assert.False(result.TransactionPending);
        Assert.False(session.HasPendingTransaction);
        Assert.Equal(SessionState.Idle, session.State);
    }

    [TiberoItFact]
    public async Task An_open_transaction_is_tracked_as_pending_and_sql_rollback_clears_it()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var session = await it.OpenAsync();
        var service = NewService(it);

        var opened = await service.ExecuteAsync(session, Stmt(OpenTransactionBlock), NoBinds, null, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Succeeded, opened.Status);
        Assert.True(opened.TransactionPending);
        Assert.True(session.HasPendingTransaction);

        await session.RollbackAsync(CancellationToken.None);
        var after = await service.ExecuteAsync(session, Stmt("BEGIN NULL; END;"), NoBinds, null, CancellationToken.None);

        Assert.False(after.TransactionPending);
        Assert.False(session.HasPendingTransaction);
    }

    [TiberoItFact]
    public async Task Read_only_profile_prepares_inside_a_block_and_server_errors_come_back_as_results()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var session = await it.OpenAsync(profile: it.Profile() with { ReadOnly = true });
        var service = NewService(it);

        // 최상위 SET TRANSACTION READ ONLY 는 TBR-7001 — 준비 블록이 그 오류 없이 지나가야 한다.
        var ok = await service.ExecuteAsync(session, Stmt("BEGIN NULL; END;"), NoBinds, null, CancellationToken.None);
        var failed = await service.ExecuteAsync(session, Stmt("BEGIN RAISE_APPLICATION_ERROR(-20001, 'oranda it'); END;"), NoBinds, null,
            CancellationToken.None);

        Assert.Equal(ExecutionStatus.Succeeded, ok.Status);
        Assert.Equal(ExecutionStatus.Failed, failed.Status);
        Assert.NotNull(failed.Error);
        Assert.NotEqual(0, failed.Error!.Number);
        Assert.Equal(Dbms.Tibero, failed.Error.Dbms);
        Assert.Equal(SessionState.Idle, session.State);
        Assert.False(session.CommandGate.IsHeld);
    }

    [TiberoItFact]
    public async Task Dbms_output_lines_are_collected_after_a_block_and_a_null_line_is_empty()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var session = await it.OpenAsync();
        var service = new TiberoExecutionService(new AllowAll(), new NoQuestions(), it.Events, null, dbmsOutput: true);

        var result = await service.ExecuteAsync(session,
            Stmt("BEGIN DBMS_OUTPUT.PUT_LINE('첫 줄'); DBMS_OUTPUT.PUT_LINE(NULL); END;"), NoBinds, null, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal(["첫 줄", ""], result.DbmsOutput);
        Assert.Equal(SessionState.Idle, session.State);
        Assert.False(session.CommandGate.IsHeld);
    }

    [TiberoItFact]
    public async Task A_select_returns_a_result_set_with_one_row_and_releases_the_gate_when_disposed()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var session = await it.OpenAsync();
        var service = NewService(it);
        var stmt = new SqlStatement("SELECT 1 AS a, 'x' AS b FROM dual", default, StatementKind.Query);

        var result = await service.ExecuteAsync(session, stmt, NoBinds, null, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        var resultSet = Assert.IsAssignableFrom<IResultSet>(result.ResultSet);
        await using (resultSet)
        {
            Assert.Equal(["A", "B"], resultSet.Columns.Select(c => c.Name.ToUpperInvariant()));
            Assert.False(session.CommandGate.IsHeld);
            var page = await resultSet.GetPageAsync(0, CancellationToken.None);
            Assert.Equal(1, page.RowCount);
            Assert.True(page.IsLast);
            Assert.Equal(1m, Convert.ToDecimal(page.Columns[0][0], System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal("x", page.Columns[1][0]);
        }

        Assert.False(session.CommandGate.IsHeld);
        Assert.Equal(SessionState.Idle, session.State);
        Assert.False(session.HasPendingTransaction);
    }

    [TiberoItFact]
    public async Task A_three_statement_script_runs_query_block_query_and_every_result_succeeds()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var session = await it.OpenAsync();
        var service = NewService(it);
        SqlStatement[] script =
        [
            new("SELECT 1 AS a FROM dual", default, StatementKind.Query),
            new("BEGIN NULL; END;", default, StatementKind.PlSql),
            new("SELECT 2 AS a FROM dual", default, StatementKind.Query),
        ];
        var results = new List<ExecutionResult>();

        try
        {
            await foreach (var r in service.ExecuteScriptAsync(session, script, ScriptErrorMode.Stop, null, CancellationToken.None))
            {
                results.Add(r);
            }

            Assert.Equal(3, results.Count);
            Assert.All(results, r => Assert.Equal(ExecutionStatus.Succeeded, r.Status));
            Assert.NotNull(results[0].ResultSet);
            Assert.Null(results[1].ResultSet);
            Assert.NotNull(results[2].ResultSet);
            Assert.Equal(SessionState.Idle, session.State);
        }
        finally
        {
            foreach (var r in results.Where(r => r.ResultSet is not null))
            {
                await r.ResultSet!.DisposeAsync();
            }
        }

        Assert.False(session.CommandGate.IsHeld);
    }

    [TiberoItFact]
    public async Task A_ref_cursor_out_bind_comes_back_as_a_readable_result_set_after_its_command_is_gone()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var session = await it.OpenAsync();
        var service = NewService(it);
        var stmt = Stmt("BEGIN OPEN :c FOR SELECT 1 AS a FROM dual UNION ALL SELECT 2 FROM dual; END;") with { BindNames = ["c"] };
        var binds = new Dictionary<string, BindValue> { ["c"] = new(BindDataType.RefCursor, null, BindDirection.Out) };

        var result = await service.ExecuteAsync(session, stmt, binds, null, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        var cursor = Assert.IsAssignableFrom<IResultSet>(result.OutValues["c"]);
        await using (cursor)
        {
            // 커서를 연 명령은 실행 뒤 닫혔다 — 커서 리더는 그 뒤에도 읽혀야 한다.
            var page = await cursor.GetPageAsync(0, CancellationToken.None);
            Assert.Equal(2, page.RowCount);
        }

        Assert.False(session.CommandGate.IsHeld);
    }

    [TiberoItFact]
    public async Task An_uncertain_query_keeps_its_reader_and_leaves_dbms_output_for_the_next_statement()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var session = await it.OpenAsync();
        var service = new TiberoExecutionService(new AllowAll(), new NoQuestions(), it.Events, null, dbmsOutput: true);
        Assert.Equal(ExecutionStatus.Succeeded, (await service.ExecuteAsync(session, Stmt("BEGIN NULL; END;"), NoBinds, null, CancellationToken.None)).Status);

        // 서비스를 거치지 않고 버퍼에 한 줄을 넣는다(서비스로 보내면 그 자리에서 거둬 간다).
        await using (var put = session.CreateCommand("BEGIN DBMS_OUTPUT.PUT_LINE('before'); END;"))
        {
            await put.ExecuteNonQueryAsync();
        }

        var stmt = new SqlStatement("WITH x AS (SELECT 1 AS a FROM dual) SELECT a FROM x", default, StatementKind.Other) { IsClassificationUncertain = true };

        // 리더가 열린 채로는 거두지 않는다: 같은 연결의 CLOB OUT 읽기가 드라이버 안에서 NullReferenceException(win144 #20). 트랜잭션 확인은 된다.
        var result = await service.ExecuteAsync(session, stmt, NoBinds, null, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        var resultSet = Assert.IsAssignableFrom<IResultSet>(result.ResultSet);
        await using (resultSet)
        {
            var page = await resultSet.GetPageAsync(0, CancellationToken.None);
            Assert.Equal(1, page.RowCount);
        }

        Assert.False(result.TransactionPending);
        Assert.Empty(result.DbmsOutput);
        Assert.Equal(SessionState.Idle, session.State);

        var next = await service.ExecuteAsync(session, Stmt("BEGIN NULL; END;"), NoBinds, null, CancellationToken.None);

        Assert.Equal(["before"], next.DbmsOutput);
    }

    /// <summary>
    /// 오래 도는 읽기 질의(3중 카티전 COUNT)를 2초 뒤 취소하면 Cancelled 로 돌아온다. 서버가 멈추는 데 10ms~58초가 걸리고(실측 4장)
    /// 그동안 호출은 끝나지 않는다 → 최대 90초 기다린다. 취소 뒤에도 같은 세션이 정상이다. 서버 CPU 를 쓰므로 IT 는 한 번에 하나만 돌린다.
    /// </summary>
    [TiberoItFact]
    public async Task A_long_read_query_cancelled_after_two_seconds_returns_cancelled_and_the_session_stays_usable()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var session = await it.OpenAsync();
        var service = NewService(it);
        var query = new SqlStatement("SELECT COUNT(*) FROM all_objects a, all_objects b, all_objects d", default, StatementKind.Query);
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromSeconds(2));

        var result = await service.ExecuteAsync(session, query, NoBinds, null, cts.Token).WaitAsync(TimeSpan.FromSeconds(90));

        Assert.Equal(ExecutionStatus.Cancelled, result.Status);
        Assert.Null(result.Error);
        Assert.Equal(SessionState.Idle, session.State);
        Assert.False(session.CommandGate.IsHeld);

        var next = await service.ExecuteAsync(session, new SqlStatement("SELECT 1 FROM dual", default, StatementKind.Query), NoBinds, null,
            CancellationToken.None);

        Assert.Equal(ExecutionStatus.Succeeded, next.Status);
        var resultSet = Assert.IsAssignableFrom<IResultSet>(next.ResultSet);
        await using (resultSet)
        {
            var page = await resultSet.GetPageAsync(0, CancellationToken.None);
            Assert.Equal(1, page.RowCount);
        }
    }

    private static TiberoExecutionService NewService(TiberoItSessions it) =>
        new(new AllowAll(), new NoQuestions(), it.Events, null, dbmsOutput: false);

    private static SqlStatement Stmt(string text) => new(text, default, StatementKind.PlSql);

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
