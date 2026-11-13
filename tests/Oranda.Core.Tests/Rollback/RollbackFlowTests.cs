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
        Assert.Equal(TransactionEndKind.Commit, RollbackFlow.EndAfter(id, Stmt("COMMIT", StatementKind.TransactionControl), true, true, false)!.Kind);
        Assert.Null(RollbackFlow.EndAfter(id, Stmt("COMMIT", StatementKind.TransactionControl), false, true, true));
        Assert.Equal("SP1", RollbackFlow.EndAfter(id, Stmt("ROLLBACK TO sp1", StatementKind.TransactionControl), true, true, true)!.Savepoint);
        Assert.Equal(TransactionEndSource.Ddl, RollbackFlow.EndAfter(id, Stmt("ALTER TABLE T ADD X NUMBER", StatementKind.Ddl), false, true, false)!.Source);
        Assert.Null(RollbackFlow.EndAfter(id, Stmt("ALTER SESSION SET NLS_DATE_FORMAT = 'YYYY'", StatementKind.Ddl), true, true, true));
        Assert.Equal((TransactionEndKind.Unknown, TransactionEndSource.Statement),
                     RollbackFlow.EndAfter(id, Stmt("COMMIT", StatementKind.TransactionControl), false, true, false) is { } failedCommit
                         ? (failedCommit.Kind, failedCommit.Source) : default);
        Assert.Equal(TransactionEndSource.PlSql, RollbackFlow.EndAfter(id, Stmt("BEGIN COMMIT; END;", StatementKind.PlSql), true, true, false)!.Source);
        Assert.Null(RollbackFlow.EndAfter(id, Stmt("BEGIN NULL; END;", StatementKind.PlSql), true, true, true));
        Assert.Null(RollbackFlow.EndAfter(id, Stmt("SAVEPOINT sp1", StatementKind.TransactionControl), true, true, true));
        Assert.Equal("SP1", RollbackFlow.SavepointMarked(Stmt("SAVEPOINT sp1", StatementKind.TransactionControl), true));
        Assert.Null(RollbackFlow.SavepointMarked(Stmt("SAVEPOINT ORANDA$RB", StatementKind.TransactionControl), true));
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
