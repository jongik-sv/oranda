using Oranda.Core.Execution;
using Oranda.Core.Rollback;

namespace Oranda.Testing.Rollback;

/// <summary>
/// 부른 차례를 기록하는 원장. 상태 계산은 하지 않는다(그것은 RB-04 <c>RollbackLedger</c> 몫). <see cref="Calls"/> 에 「Record」·「EndAsync:Commit」 처럼 남는다.
/// <see cref="Open"/> 은 <see cref="OpenState"/> 를, <see cref="Probes"/> 는 <see cref="ProbeList"/> 를 돌려준다.
/// </summary>
public sealed class RecordingRollbackLedger : IRollbackLedger
{
    private readonly object _gate = new();
    private readonly List<string> _calls = [];
    private readonly List<RollbackStatementRecord> _records = [];
    private readonly List<TransactionEnded> _ends = [];
    private readonly List<(Guid SessionId, string Name)> _savepoints = [];
    private readonly List<(Guid SessionId, RollbackOrigin Origin, long OriginGroupId, RollbackStatementRecord Record)> _derived = [];

    public IReadOnlyList<string> Calls { get { lock (_gate) { return [.. _calls]; } } }

    public IReadOnlyList<RollbackStatementRecord> Records { get { lock (_gate) { return [.. _records]; } } }

    public IReadOnlyList<TransactionEnded> Ends { get { lock (_gate) { return [.. _ends]; } } }

    public IReadOnlyList<(Guid SessionId, string Name)> Savepoints { get { lock (_gate) { return [.. _savepoints]; } } }

    public IReadOnlyList<(Guid SessionId, RollbackOrigin Origin, long OriginGroupId, RollbackStatementRecord Record)> Derived
    {
        get { lock (_gate) { return [.. _derived]; } }
    }

    public RollbackOpenState? OpenState { get; set; }

    public IReadOnlyList<RollbackProbe> ProbeList { get; set; } = [];

    public void Record(RollbackStatementRecord record) => Note("Record", () => _records.Add(record));

    public void MarkSavepoint(Guid sessionId, string name) => Note("MarkSavepoint:" + name, () => _savepoints.Add((sessionId, name)));

    public RollbackOpenState? Open(Guid sessionId) => OpenState;

    public IReadOnlyList<RollbackProbe> Probes(Guid sessionId, int max = 5) => [.. ProbeList.Take(max)];

    public Task BeforeEndAsync(Guid sessionId, CancellationToken ct)
    {
        Note("BeforeEndAsync", static () => { });
        return Task.CompletedTask;
    }

    public Task EndAsync(TransactionEnded ended, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ended);
        Note("EndAsync:" + ended.Kind, () => _ends.Add(ended));
        return Task.CompletedTask;
    }

    public void RecordDerived(Guid sessionId, RollbackOrigin origin, long originGroupId, RollbackStatementRecord record) =>
        Note("RecordDerived:" + origin, () => _derived.Add((sessionId, origin, originGroupId, record)));

    private void Note(string call, Action action)
    {
        lock (_gate)
        {
            _calls.Add(call);
            action();
        }
    }
}

/// <summary>
/// 정해진 값을 돌려주고 부른 차례를 기록하는 캡처 엔진(공급자 레인 RB-10·RB-11 시험용).
/// <see cref="BeginAsync"/> 는 <see cref="StatementToSend"/> 가 null 이면 받은 문장을 그대로 보내는 캡처를 만든다.
/// </summary>
public sealed class RecordingRollbackCapture : IRollbackCapture
{
    private readonly object _gate = new();
    private readonly List<string> _calls = [];

    public IReadOnlyList<string> Calls { get { lock (_gate) { return [.. _calls]; } } }

    public ConfirmExtras Extras { get; set; } = ConfirmExtras.None;

    /// <summary>null 이면 받은 문장 그대로.</summary>
    public SqlStatement? StatementToSend { get; set; }

    public IReadOnlyDictionary<string, BindValue>? BindsToSend { get; set; }

    public RollbackStatementSummary CompleteSummary { get; set; } = RollbackStatementSummary.None;

    public RollbackStatementSummary AbandonSummary { get; set; } = RollbackStatementSummary.None;

    public TransactionEndKind ProbeResult { get; set; } = TransactionEndKind.Unknown;

    public ChangePoint? Point { get; set; }

    public RollbackRequest? LastRequest { get; private set; }

    public ConfirmAnswer? LastAnswer { get; private set; }

    /// <summary>마지막 <see cref="IStatementCapture.CompleteAsync"/> 인자(rowsAffected, outValues, transactionId).</summary>
    public (long? Rows, IReadOnlyDictionary<string, object?> OutValues, string? TransactionId)? LastComplete { get; private set; }

    public DbError? LastAbandonError { get; private set; }

    public Task<ConfirmExtras> PrepareAsync(RollbackRequest request, IRollbackChannel gatedChannel, IRollbackDialect dialect, CancellationToken ct)
    {
        Note("Prepare");
        LastRequest = request;
        return Task.FromResult(Extras);
    }

    public Task<IStatementCapture> BeginAsync(RollbackRequest request, ConfirmExtras extras, ConfirmAnswer answer,
                                              IRollbackChannel channel, IRollbackDialect dialect, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        Note("Begin");
        LastRequest = request;
        LastAnswer = answer;
        return Task.FromResult<IStatementCapture>(new Capture(this, StatementToSend ?? request.Statement, BindsToSend ?? request.Binds));
    }

    public Task<TransactionEndKind> ProbeEndAsync(Guid sessionId, IRollbackChannel channel, CancellationToken ct)
    {
        Note("ProbeEnd");
        return Task.FromResult(ProbeResult);
    }

    public Task<ChangePoint?> ReadPointAsync(Guid sessionId, IRollbackChannel channel, IRollbackDialect dialect, CancellationToken ct)
    {
        Note("ReadPoint");
        return Task.FromResult(Point);
    }

    private void Note(string call)
    {
        lock (_gate)
        {
            _calls.Add(call);
        }
    }

    private sealed class Capture(RecordingRollbackCapture owner, SqlStatement statement, IReadOnlyDictionary<string, BindValue> binds) : IStatementCapture
    {
        public SqlStatement StatementToSend { get; } = statement;

        public IReadOnlyDictionary<string, BindValue> BindsToSend { get; } = binds;

        public Task<RollbackStatementSummary> CompleteAsync(long? rowsAffected, IReadOnlyDictionary<string, object?> outValues,
                                                            string? transactionId, CancellationToken ct)
        {
            owner.Note("Complete");
            owner.LastComplete = (rowsAffected, outValues, transactionId);
            return Task.FromResult(owner.CompleteSummary);
        }

        public Task<RollbackStatementSummary> AbandonAsync(DbError? failure, CancellationToken ct)
        {
            owner.Note("Abandon");
            owner.LastAbandonError = failure;
            return Task.FromResult(owner.AbandonSummary);
        }
    }
}

/// <summary>글 → 모양 표를 미리 정하는 분석기(Language 레인 RB-30 을 기다리지 않는 시험용). 표에 없는 글은 null.</summary>
public sealed class FakeDmlAnalyzer : IDmlAnalyzer
{
    private readonly Dictionary<string, DmlShape> _shapes = new(StringComparer.Ordinal);

    public FakeDmlAnalyzer Add(string text, DmlShape shape)
    {
        _shapes[text] = shape;
        return this;
    }

    /// <summary>표에 없는 글에 돌려줄 모양(null = 쓰기 문장 아님).</summary>
    public Func<string, DmlShape?>? Fallback { get; set; }

    public DmlShape? Analyze(string text) => _shapes.TryGetValue(text, out var s) ? s : Fallback?.Invoke(text);
}

/// <summary>
/// 확인 창의 덧붙임(<see cref="ConfirmExtras"/>)을 기록하고 정해진 답을 돌려주는 실행 대화 창구.
/// <see cref="Answer"/> 가 null 이면 <see cref="ConfirmAnswer.Default"/>(제안의 기본값으로 진행).
/// </summary>
public sealed class RecordingRollbackInteraction : IExecutionInteraction
{
    private readonly List<(SqlStatement Statement, PolicyDecision Decision, ConfirmExtras Extras)> _asked = [];

    public IReadOnlyList<(SqlStatement Statement, PolicyDecision Decision, ConfirmExtras Extras)> Asked => _asked;

    public ConfirmAnswer? Answer { get; set; }

    public Task<bool> ConfirmAsync(SqlStatement stmt, PolicyDecision decision, CancellationToken ct) =>
        Task.FromResult(Answer?.Proceed ?? true);

    public Task<ConfirmAnswer> ConfirmChangeAsync(SqlStatement stmt, PolicyDecision decision, ConfirmExtras extras, CancellationToken ct)
    {
        _asked.Add((stmt, decision, extras));
        return Task.FromResult(Answer ?? ConfirmAnswer.Default(extras));
    }

    public Task<IReadOnlyDictionary<string, string>?> AskSubstitutionsAsync(IReadOnlyList<string> names, CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<string, string>?>(new Dictionary<string, string>());

    public Task<ScriptErrorAction> AskScriptErrorAsync(SqlStatement stmt, DbError failure, CancellationToken ct) =>
        Task.FromResult(ScriptErrorAction.Stop);
}
