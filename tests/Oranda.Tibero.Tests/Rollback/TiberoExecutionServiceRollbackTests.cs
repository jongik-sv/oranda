using System.Data.Common;
using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Execution;
using Oranda.Core.Execution.Providers;
using Oranda.Core.Rollback;
using Oranda.Core.Sessions;
using Oranda.Testing.Rollback;
using Oranda.Tibero.Execution;
using Oranda.Tibero.Sessions;
using Oranda.Tibero.Tests.Driver;

namespace Oranda.Tibero.Tests.Rollback;

/// <summary>
/// 실행 서비스의 되돌리기 연결(설계 13.5, 가짜 드라이버 + <see cref="RecordingRollbackCapture"/>): 쓰기 문장만 엔진에 넘기고,
/// 캡처가 준 글을 보내되 결과에는 사용자 글을 남기며, 성공이면 Complete(행 수·트랜잭션 id), 실패면 Abandon 을 부른다.
/// </summary>
[Collection("FakeDriver")]
public sealed class TiberoExecutionServiceRollbackTests : IDisposable
{
    private const string Update = "UPDATE T SET A = 1 WHERE K = 2";

    private static readonly ServerInfo Server = new("6", "i", "h", null, "D", null, false, 7,
        new ServerCapabilities(false, false, true, false, false, Dbms.Tibero), DbUniqueName: "D", SessionUser: "U", Dbms: Dbms.Tibero);

    private static readonly IReadOnlyDictionary<string, BindValue> NoBinds = new Dictionary<string, BindValue>();

    private readonly EventBus _bus = new();
    private readonly List<string> _sent = [];
    private readonly RecordingRollbackCapture _capture = new();

    public TiberoExecutionServiceRollbackTests() => FakeDriverHost.Reset();

    public void Dispose() => FakeDriverHost.Reset();

    [Fact]
    public async Task Write_statement_sends_the_capture_text_keeps_the_user_text_and_completes_with_rows_and_transaction_id()
    {
        var shape = new DmlShape(DmlVerb.Update) { Name = "T", WhereText = "K = 2" };
        var analyzer = new FakeDmlAnalyzer().Add(Update, shape);
        _capture.Extras = new ConfirmExtras(new RollbackOffer(true, NotCreatedReason.None) { DefaultOn = true }, null);
        _capture.StatementToSend = Stmt(Update + " /*sent*/", StatementKind.Dml);
        _capture.CompleteSummary = new RollbackStatementSummary(RollbackScriptState.Open, NotCreatedReason.None, 3) { LogLine = "원복 스크립트 3문장" };
        Serve(cmd => cmd.CommandText == TiberoExecutionService.TransactionIdSql ? "1.2.3" : 3);
        var service = NewService(analyzer);
        await using var session = NewSession();

        var result = await service.ExecuteAsync(session, Stmt(Update, StatementKind.Dml), NoBinds, null, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal(Update, result.Statement.Text);
        Assert.Equal([Update + " /*sent*/", TiberoExecutionService.TransactionIdSql], Sent());
        Assert.Equal(["Prepare", "Begin", "Complete"], _capture.Calls);
        Assert.Same(shape, _capture.LastRequest!.Shape);
        Assert.Equal(ConfirmAnswer.Default(_capture.Extras), _capture.LastAnswer);
        Assert.Equal((3L, "1.2.3"), (_capture.LastComplete!.Value.Rows, _capture.LastComplete.Value.TransactionId));
        Assert.Same(_capture.CompleteSummary, result.Rollback);
        Assert.False(session.CommandGate.IsHeld);
    }

    [Fact]
    public async Task Failed_user_statement_is_abandoned_with_the_classified_error()
    {
        _capture.AbandonSummary = new RollbackStatementSummary(RollbackScriptState.NotCreated, NotCreatedReason.Failed, 0) { LogLine = "잠금을 풀었습니다" };
        Serve(cmd => cmd.CommandText == Update ? throw FakeDriverHost.ServerError(-10007, "TBR-10007: unique") : null);
        var service = NewService();
        await using var session = NewSession();

        var result = await service.ExecuteAsync(session, Stmt(Update, StatementKind.Dml), NoBinds, null, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal(["Prepare", "Begin", "Abandon"], _capture.Calls);
        Assert.Equal(DbErrorKind.ConstraintViolation, _capture.LastAbandonError!.Kind);
        Assert.Same(_capture.AbandonSummary, result.Rollback);
    }

    [Theory]
    [InlineData("SELECT * FROM T", StatementKind.Query, ChangeRecording.Normal)]
    [InlineData("CREATE TABLE X (A NUMBER)", StatementKind.Ddl, ChangeRecording.Normal)]
    [InlineData(Update, StatementKind.Dml, ChangeRecording.None)]
    public async Task Queries_ddl_and_recording_none_never_reach_the_capture(string text, StatementKind kind, ChangeRecording recording)
    {
        Serve(_ => null);
        var service = NewService();
        await using var session = NewSession();

        var result = await service.ExecuteAsync(session, Stmt(text, kind), NoBinds, new ExecutionOptions { Recording = recording }, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Empty(_capture.Calls);
        Assert.Null(result.Rollback);
    }

    [Fact]
    public async Task Out_bind_added_by_the_capture_goes_to_the_engine_but_not_to_the_user_result()
    {
        const string Insert = "INSERT INTO T (A) VALUES (1)";
        var sent = Stmt(Insert + " RETURNING ROWID INTO :ORANDA_RID", StatementKind.Dml) with { BindNames = ["ORANDA_RID"] };
        _capture.StatementToSend = sent;
        _capture.BindsToSend = new Dictionary<string, BindValue> { ["ORANDA_RID"] = new(BindDataType.Varchar2, null, BindDirection.Out) };
        Serve(cmd =>
        {
            if (cmd.CommandText == sent.Text)
            {
                cmd.Parameters["ORANDA_RID"].Value = FakeDriverHost.ProviderValue("TiberoString", "AAAAAAAAAAAAAAAAAB");
                return 1;
            }

            return cmd.CommandText == TiberoExecutionService.TransactionIdSql ? "1.2.3" : null;
        });
        var service = NewService();
        await using var session = NewSession();

        var result = await service.ExecuteAsync(session, Stmt(Insert, StatementKind.Dml), NoBinds, null, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal("AAAAAAAAAAAAAAAAAB", _capture.LastComplete!.Value.OutValues["ORANDA_RID"]);
        Assert.False(result.OutValues.ContainsKey("ORANDA_RID"));
    }

    [Fact]
    public async Task Cancel_after_the_user_statement_ran_completes_the_capture_instead_of_rolling_back_to_the_savepoint()
    {
        using var cts = new CancellationTokenSource();
        Serve(cmd =>
        {
            if (cmd.CommandText == Update)
            {
                cts.Cancel(); // 드라이버 호출은 돌아왔고(1행) 그 뒤에 취소가 보인다
                return 1;
            }

            return null;
        });
        var service = NewService();
        await using var session = NewSession();

        var result = await service.ExecuteAsync(session, Stmt(Update, StatementKind.Dml), NoBinds, null, cts.Token);

        Assert.Equal(ExecutionStatus.Cancelled, result.Status);
        Assert.Equal(["Prepare", "Begin", "Complete"], _capture.Calls);
        Assert.Equal(1L, _capture.LastComplete!.Value.Rows);
    }

    [Fact]
    public async Task Engine_that_throws_in_prepare_and_begin_still_runs_the_user_statement_and_returns_a_result()
    {
        Serve(_ => 1);
        var service = new TiberoExecutionService(new AllowAll(), new RecordingRollbackInteraction(), _bus, null, dbmsOutput: false, capture: new ThrowingCapture());
        await using var session = NewSession();

        var result = await service.ExecuteAsync(session, Stmt(Update, StatementKind.Dml), NoBinds, null, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal(Update, Sent()[0]);
        Assert.Null(result.Rollback);
    }

    private TiberoExecutionService NewService(IDmlAnalyzer? analyzer = null) =>
        new(new AllowAll(), new RecordingRollbackInteraction(), _bus, null, dbmsOutput: false, capture: _capture, analyzer: analyzer);

    /// <summary>계약을 어기고 던지는 엔진(엔진 안 제한 시간·통로 밖 예외).</summary>
    private sealed class ThrowingCapture : IRollbackCapture
    {
        public Task<ConfirmExtras> PrepareAsync(RollbackRequest request, IRollbackChannel gatedChannel, IRollbackDialect dialect, CancellationToken ct) =>
            throw new OperationCanceledException("엔진 안 제한 시간");

        public Task<IStatementCapture> BeginAsync(RollbackRequest request, ConfirmExtras extras, ConfirmAnswer answer, IRollbackChannel channel,
                                                  IRollbackDialect dialect, CancellationToken ct) =>
            throw new InvalidOperationException("엔진 실패");

        public Task<TransactionEndKind> ProbeEndAsync(Guid sessionId, IRollbackChannel channel, CancellationToken ct) => Task.FromResult(TransactionEndKind.Unknown);

        public Task<ChangePoint?> ReadPointAsync(Guid sessionId, IRollbackChannel channel, IRollbackDialect dialect, CancellationToken ct) =>
            Task.FromResult<ChangePoint?>(null);
    }

    private static SqlStatement Stmt(string text, StatementKind kind) => new(text, default, kind);

    private TiberoSession NewSession()
    {
        var driver = FakeDriverHost.Driver;
        var connection = driver.CreateConnection("Data Source=x");
        connection.Open();
        var profile = new ConnectionProfile("t", "u", "h:1/d", Dbms: Dbms.Tibero);
        return new TiberoSession(Guid.NewGuid(), profile, Server, SessionPurpose.Work, "D/-/U", connection, null, _bus, driver: driver);
    }

    private void Serve(Func<DbCommand, object?> respond) =>
        FakeDriverHost.SetHandler(cmd =>
        {
            lock (_sent)
            {
                _sent.Add(cmd.CommandText);
            }

            return respond(cmd);
        });

    private List<string> Sent()
    {
        lock (_sent)
        {
            return [.. _sent];
        }
    }

    private sealed class AllowAll : IExecutionPolicy
    {
        public ValueTask<PolicyDecision> EvaluateAsync(PolicyContext ctx, SqlStatement stmt, CancellationToken ct) => ValueTask.FromResult(PolicyDecision.Allowed);
    }
}
