using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Sessions;
using Oranda.Tibero.Execution;
using Oranda.Tibero.Tests.Integration;

namespace Oranda.Tibero.Tests.Execution;

/// <summary>
/// 실행 서비스 E1 을 실제 서버로 확인한다(쓰기 없음: PSM 블록·바인드·트랜잭션 추적·읽기 전용 준비). 서버·드라이버가 없으면 아무것도 하지 않는다.
/// 트랜잭션은 쓰기 없이 <c>DBMS_TRANSACTION.LOCAL_TRANSACTION_ID(TRUE)</c> 로 연다(docs/design/tibero-driver-exec.md 2장).
/// </summary>
[Trait("Category", "Integration")]
public class TiberoExecutionServiceIt
{
    private const string OpenTransactionBlock =
        "DECLARE x VARCHAR2(100); BEGIN x := DBMS_TRANSACTION.LOCAL_TRANSACTION_ID(TRUE); END;";

    private static readonly IReadOnlyDictionary<string, BindValue> NoBinds = new Dictionary<string, BindValue>();

    [Fact]
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

    [Fact]
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

    [Fact]
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

    [Fact]
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
