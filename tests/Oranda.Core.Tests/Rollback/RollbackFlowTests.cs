using Oranda.Core.Execution;
using Oranda.Core.History;
using Oranda.Core.Rollback;
using Oranda.Core.Rollback.Capture;
using Oranda.Core.Text;
using Oranda.Testing;

namespace Oranda.Core.Tests.Rollback;

/// <summary>공급자 공통 판단(되돌리기 지점 설계 7.3 끝·13.5).</summary>
public class RollbackFlowTests
{
    private static SqlStatement Stmt(string text, StatementKind kind, bool uncertain = false) =>
        new(text, default(TextRange), kind) { IsClassificationUncertain = uncertain };

    [Theory]
    [InlineData("COMMIT", TransactionControlKind.Commit, null)]
    [InlineData("commit work comment 'x' write batch nowait;", TransactionControlKind.Commit, null)]
    [InlineData("ROLLBACK WORK", TransactionControlKind.Rollback, null)]
    [InlineData("ROLLBACK TO SAVEPOINT sp1", TransactionControlKind.RollbackToSavepoint, "SP1")]
    [InlineData("rollback work to \"Sp 2\"", TransactionControlKind.RollbackToSavepoint, "Sp 2")]
    [InlineData("SAVEPOINT /* 주석 */ sp1", TransactionControlKind.Savepoint, "SP1")]
    public void Parses_transaction_control(string text, TransactionControlKind kind, string? savepoint)
    {
        Assert.Equal(new TransactionControl(kind, savepoint), RollbackFlow.ParseTransactionControl(text));
    }

    [Theory]
    [InlineData("COMMIT FORCE '1.2.3'")]
    [InlineData("ROLLBACK FORCE '1.2.3'")]
    [InlineData("SET TRANSACTION READ ONLY")]
    public void Unknown_control_shapes_are_null(string text)
    {
        Assert.Null(RollbackFlow.ParseTransactionControl(text));
    }

    [Fact]
    public void Can_end_transaction_only_for_commit_ddl_plsql_and_uncertain()
    {
        Assert.True(RollbackFlow.CanEndTransaction(Stmt("COMMIT", StatementKind.TransactionControl)));
        Assert.False(RollbackFlow.CanEndTransaction(Stmt("ROLLBACK", StatementKind.TransactionControl)));
        Assert.True(RollbackFlow.CanEndTransaction(Stmt("DROP TABLE T", StatementKind.Ddl)));
        Assert.True(RollbackFlow.CanEndTransaction(Stmt("BEGIN NULL; END;", StatementKind.PlSql)));
        Assert.True(RollbackFlow.CanEndTransaction(Stmt("CALL P()", StatementKind.Other, uncertain: true)));
        Assert.False(RollbackFlow.CanEndTransaction(Stmt("UPDATE T SET A = 1", StatementKind.Dml)));
    }

    [Fact]
    public void End_after_statement()
    {
        var id = Guid.NewGuid();
        Assert.Equal(TransactionEndKind.Commit, RollbackFlow.EndAfter(id, Stmt("COMMIT", StatementKind.TransactionControl), true, true, false, hasOpenGroup: false)!.Kind);
        Assert.Null(RollbackFlow.EndAfter(id, Stmt("COMMIT", StatementKind.TransactionControl), false, true, true, hasOpenGroup: false));
        Assert.Equal("SP1", RollbackFlow.EndAfter(id, Stmt("ROLLBACK TO sp1", StatementKind.TransactionControl), true, true, true, hasOpenGroup: false)!.Savepoint);
        Assert.Equal(TransactionEndSource.Ddl, RollbackFlow.EndAfter(id, Stmt("ALTER TABLE T ADD X NUMBER", StatementKind.Ddl), false, true, false, hasOpenGroup: false)!.Source);
        Assert.Null(RollbackFlow.EndAfter(id, Stmt("ALTER SESSION SET NLS_DATE_FORMAT = 'YYYY'", StatementKind.Ddl), true, true, true, hasOpenGroup: false));
        Assert.Equal((TransactionEndKind.Unknown, TransactionEndSource.Statement),
                     RollbackFlow.EndAfter(id, Stmt("COMMIT", StatementKind.TransactionControl), false, true, false, hasOpenGroup: false) is { } failedCommit
                         ? (failedCommit.Kind, failedCommit.Source) : default);
        Assert.Equal(TransactionEndSource.PlSql, RollbackFlow.EndAfter(id, Stmt("BEGIN COMMIT; END;", StatementKind.PlSql), true, true, false, hasOpenGroup: false)!.Source);
        Assert.Null(RollbackFlow.EndAfter(id, Stmt("BEGIN NULL; END;", StatementKind.PlSql), true, true, true, hasOpenGroup: false));
        Assert.Null(RollbackFlow.EndAfter(id, Stmt("SAVEPOINT sp1", StatementKind.TransactionControl), true, true, true, hasOpenGroup: false));
        Assert.Equal("SP1", RollbackFlow.SavepointMarked(Stmt("SAVEPOINT sp1", StatementKind.TransactionControl), true));
        Assert.Null(RollbackFlow.SavepointMarked(Stmt("SAVEPOINT ORANDA$RB", StatementKind.TransactionControl), true));
    }

    [Fact]
    public void Plsql_and_ddl_end_when_pending_before_or_open_group()
    {
        var id = Guid.NewGuid();
        var block = Stmt("BEGIN INSERT INTO T VALUES (1); COMMIT; END;", StatementKind.PlSql);
        // 미커밋이 없던 세션: 블록 기록이 열린 묶음에 있으므로 끝(모름 → 판정)
        Assert.Equal((TransactionEndKind.Unknown, TransactionEndSource.PlSql),
                     RollbackFlow.EndAfter(id, block, true, false, false, hasOpenGroup: true) is { } e ? (e.Kind, e.Source) : default);
        Assert.Null(RollbackFlow.EndAfter(id, block, true, false, false, hasOpenGroup: false));
        // 되돌리기를 끈 세션의 DDL: 미커밋도 열린 묶음도 없으면 끝을 내지 않는다
        Assert.Null(RollbackFlow.EndAfter(id, Stmt("CREATE TABLE T (A NUMBER)", StatementKind.Ddl), true, false, false, hasOpenGroup: false));
        Assert.NotNull(RollbackFlow.EndAfter(id, Stmt("CREATE TABLE T (A NUMBER)", StatementKind.Ddl), true, false, false, hasOpenGroup: true));
        Assert.Null(RollbackFlow.EndAfter(id, Stmt("ALTER SESSION SET X = 1", StatementKind.Ddl), true, true, true, hasOpenGroup: true));
    }

    [Fact]
    public void Recording_is_none_for_read_only_agent_and_copy()
    {
        Assert.Equal(ChangeRecording.None, RollbackFlow.RecordingFor(new FakeSession(TestData.Profile(readOnly: true)), ExecutionOptions.Default));
        Assert.Equal(ChangeRecording.None, RollbackFlow.RecordingFor(new FakeSession(), new ExecutionOptions { Source = HistorySource.Agent }));
        Assert.Equal(ChangeRecording.PointOnly, RollbackFlow.RecordingFor(new FakeSession(), new ExecutionOptions { Recording = ChangeRecording.PointOnly }));
    }

    [Fact]
    public void Bind_names_skip_strings_comments_and_assignment()
    {
        Assert.Equal(["x"], RollbackFlow.BindNamesIn("a = q'[it's :no]' AND b = :x"));
        Assert.Equal(TransactionControlKind.Commit, RollbackFlow.ParseTransactionControl("COMMIT/*x*/")!.Kind);
        Assert.Equal(["a", "B_2"], RollbackFlow.BindNamesIn("x = :a AND y = ':no' AND \":q\" = 1 -- :c\n AND z = :B_2 /* :d */ AND v := 1 AND w = :a"));
        var binds = new Dictionary<string, BindValue> { ["A"] = new(BindDataType.Number, 1m), ["other"] = new(BindDataType.Number, 2m) };
        Assert.Equal(["a"], RollbackFlow.BindsFor("c = :a", binds).Keys);
    }
}
