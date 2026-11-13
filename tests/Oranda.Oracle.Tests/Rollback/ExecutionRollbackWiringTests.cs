using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.History;
using Oranda.Core.Rollback;
using Oranda.Core.Sessions;
using Oranda.Oracle.Execution;
using Oranda.Oracle.Rollback;
using Oranda.Oracle.Sessions;
using Oranda.Testing;
using Oranda.Testing.Rollback;

namespace Oranda.Oracle.Tests.Rollback;

// 소유: rb-m2-oracle RB-10b. 실행 서비스가 캡처 엔진을 부르는 차례(설계 6.1·13.5). DB 없음: 열지 않은 연결이라 사용자 문장은 드라이버에서
// 실패한다(그 실패 경로가 Abandon 을 부르는지까지 본다). 실제 저장점·잠금 읽기·RETURNING 은 RB-12 통합 시험.
public sealed class ExecutionRollbackWiringTests : IAsyncDisposable
{
    private const string Update = "UPDATE SAMPLE.PRODUCTS SET UNIT_PRICE = 1";
    private readonly OracleConnection _connection = new("User Id=u;Password=p;Data Source=localhost:1/x");
    private readonly RecordingRollbackCapture _capture = new();
    private readonly List<string> _gateLog = [];
    private readonly RecordingRollbackLedger _ledger = new();
    private readonly RecordingRollbackInteraction _ui = new();
    private readonly FakeDmlAnalyzer _analyzer = new();
    private readonly FakeEventBus _bus = new();
    private readonly OracleSession _session;
    private readonly OracleExecutionService _exec;

    public ExecutionRollbackWiringTests()
    {
        var info = new ServerInfo("19.0.0.0.0", "ORCL", "localhost", "ORCLPDB1", "ORCL", "ORCLPDB1", true, 1,
            new ServerCapabilities(false, true, true, false, false));
        var profile = new ConnectionProfile("prod-example", "scott", "localhost:1521/ORCLPDB1", Environment: ConnectionEnvironment.Production);
        _session = new OracleSession(Guid.NewGuid(), profile, info, SessionPurpose.Work, "K", _connection, _bus,
            closeConnection: () => ValueTask.CompletedTask, rollback: new OracleRollbackHooks(new GateWatch(_capture, () => _session!, _gateLog), _ledger, _bus));
        _exec = new OracleExecutionService(new ExecutionPolicy(), _ui, _bus, null, dbmsOutput: false, analyzer: _analyzer);
        _analyzer.Add(Update, new DmlShape(DmlVerb.Update) { Owner = "SAMPLE", Name = "PRODUCTS", SetColumns = ["UNIT_PRICE"] });
        _capture.Extras = new ConfirmExtras(new RollbackOffer(true, NotCreatedReason.None) { DefaultOn = true, EstimatedRows = 12 }, null);
    }

    [Fact]
    public async Task Prepare_runs_outside_the_command_gate_and_begin_inside_it()
    {
        await _exec.ExecuteAsync(_session, Unfiltered(Update), new Dictionary<string, BindValue>(), null, CancellationToken.None);

        Assert.Equal(["Prepare:free", "Begin:held"], _gateLog);
    }

    [Fact]
    public async Task Result_keeps_the_users_text_when_the_engine_sends_another()
    {
        _capture.StatementToSend = Unfiltered(Update + "\nRETURNING ROWID INTO :ORANDA_RID") with { BindNames = ["ORANDA_RID"] };
        _capture.BindsToSend = new Dictionary<string, BindValue> { ["ORANDA_RID"] = new(BindDataType.Varchar2, null, BindDirection.Out) };

        var result = await _exec.ExecuteAsync(_session, Unfiltered(Update), new Dictionary<string, BindValue>(), null, CancellationToken.None);

        Assert.Equal(Update, result.Statement.Text);
    }

    [Fact]
    public async Task Statements_the_engine_does_not_take_have_no_rollback_summary()
    {
        var query = await _exec.ExecuteAsync(_session, new SqlStatement("SELECT 1 FROM DUAL", default, StatementKind.Query),
            new Dictionary<string, BindValue>(), null, CancellationToken.None);

        Assert.Null(query.Rollback);
    }

    /// <summary>캡처를 부를 때 명령 잠금이 쥐여 있었는지 남기는 감싸개.</summary>
    private sealed class GateWatch(IRollbackCapture inner, Func<OracleSession> session, List<string> log) : IRollbackCapture
    {
        public Task<ConfirmExtras> PrepareAsync(RollbackRequest request, IRollbackChannel gatedChannel, IRollbackDialect dialect, CancellationToken ct)
        {
            log.Add("Prepare:" + State());
            return inner.PrepareAsync(request, gatedChannel, dialect, ct);
        }

        public Task<IStatementCapture> BeginAsync(RollbackRequest request, ConfirmExtras extras, ConfirmAnswer answer, IRollbackChannel channel,
                                                  IRollbackDialect dialect, CancellationToken ct)
        {
            log.Add("Begin:" + State());
            return inner.BeginAsync(request, extras, answer, channel, dialect, ct);
        }

        public Task<TransactionEndKind> ProbeEndAsync(Guid sessionId, IRollbackChannel channel, CancellationToken ct) =>
            inner.ProbeEndAsync(sessionId, channel, ct);

        public Task<ChangePoint?> ReadPointAsync(Guid sessionId, IRollbackChannel channel, IRollbackDialect dialect, CancellationToken ct) =>
            inner.ReadPointAsync(sessionId, channel, dialect, ct);

        private string State()
        {
            var gate = session().CommandGate;
            if (!gate.TryEnter())
            {
                return "held";
            }

            gate.Release();
            return "free";
        }
    }

    private static SqlStatement Unfiltered(string text) =>
        new(text, default, StatementKind.Dml) { Verb = "UPDATE", IsUnfilteredDml = true };

    [Fact]
    public async Task Write_statement_prepares_before_the_gate_begins_inside_and_abandons_on_failure()
    {
        var result = await _exec.ExecuteAsync(_session, Unfiltered(Update), new Dictionary<string, BindValue>(), null, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Failed, result.Status); // 열지 않은 연결
        Assert.Equal(["Prepare", "Begin", "Abandon"], _capture.Calls);
        Assert.Equal(DmlVerb.Update, _capture.LastRequest!.Shape!.Verb); // 분석기가 준 모양
        var asked = Assert.Single(_ui.Asked);
        Assert.Same(_capture.Extras, asked.Extras); // 제안이 확인 창에 갔다
        Assert.True(_capture.LastAnswer!.CreateScript); // 확인 창의 답이 Begin 에 갔다
        Assert.Equal(Update, result.Statement.Text);
        Assert.True(_session.CommandGate.TryEnter());
        _session.CommandGate.Release();
    }

    [Fact]
    public async Task Recording_none_and_queries_do_not_reach_the_engine()
    {
        var none = new ExecutionOptions { Recording = ChangeRecording.None };
        await _exec.ExecuteAsync(_session, Unfiltered(Update), new Dictionary<string, BindValue>(), none, CancellationToken.None);
        await _exec.ExecuteAsync(_session, new SqlStatement("SELECT 1 FROM DUAL", default, StatementKind.Query),
            new Dictionary<string, BindValue>(), null, CancellationToken.None);
        await _exec.ExecuteAsync(_session, Unfiltered(Update), new Dictionary<string, BindValue>(),
            new ExecutionOptions { Source = HistorySource.Agent }, CancellationToken.None);

        Assert.Empty(_capture.Calls);
    }

    [Fact]
    public async Task Shape_from_the_builder_is_used_and_dropped_after_substitution()
    {
        var given = new DmlShape(DmlVerb.Delete) { Name = "GIVEN" };
        await _exec.ExecuteAsync(_session, Unfiltered(Update) with { Dml = given }, new Dictionary<string, BindValue>(), null, CancellationToken.None);
        Assert.Same(given, _capture.LastRequest!.Shape);

        const string withVariable = "UPDATE SAMPLE.PRODUCTS SET UNIT_PRICE = &x";
        _analyzer.Add(withVariable, new DmlShape(DmlVerb.Update) { Name = "ANALYZED" }); // 답이 없는 치환 변수는 글에 그대로 남는다
        var substituted = Unfiltered(withVariable) with { Dml = given, SubstitutionNames = ["x"] };
        await _exec.ExecuteAsync(_session, substituted, new Dictionary<string, BindValue>(), null, CancellationToken.None);
        Assert.Equal("ANALYZED", _capture.LastRequest!.Shape!.Name); // 치환을 거친 문장은 만든 쪽 모양을 버리고 다시 분석했다
    }

    public async ValueTask DisposeAsync()
    {
        await _session.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
