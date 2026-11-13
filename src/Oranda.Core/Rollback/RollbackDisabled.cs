using Oranda.Core.Execution;

namespace Oranda.Core.Rollback;

/// <summary>끔 구현 모음(설계 13.2 끝). 앱 조립은 실구현이 들어오기 전 이것을 등록한다.</summary>
public static class RollbackCapture
{
    /// <summary>아무것도 하지 않는 엔진: 제안 없음, 받은 문장을 그대로 보내는 캡처.</summary>
    public static IRollbackCapture Disabled { get; } = new DisabledRollbackCapture();
}

/// <summary>아무것도 하지 않는 엔진. <see cref="RollbackCapture.Disabled"/>.</summary>
public sealed class DisabledRollbackCapture : IRollbackCapture
{
    public Task<ConfirmExtras> PrepareAsync(RollbackRequest request, IRollbackChannel gatedChannel, IRollbackDialect dialect, CancellationToken ct) =>
        Task.FromResult(ConfirmExtras.None);

    public Task<IStatementCapture> BeginAsync(RollbackRequest request, ConfirmExtras extras, ConfirmAnswer answer,
                                              IRollbackChannel channel, IRollbackDialect dialect, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.FromResult<IStatementCapture>(new PassThroughStatementCapture(request.Statement, request.Binds));
    }

    public Task<TransactionEndKind> ProbeEndAsync(Guid sessionId, IRollbackChannel channel, CancellationToken ct) =>
        Task.FromResult(TransactionEndKind.Unknown);

    public Task<ChangePoint?> ReadPointAsync(Guid sessionId, IRollbackChannel channel, IRollbackDialect dialect, CancellationToken ct) =>
        Task.FromResult<ChangePoint?>(null);
}

/// <summary>받은 문장·바인드를 그대로 보내고 아무것도 기록하지 않는 캡처.</summary>
public sealed class PassThroughStatementCapture(SqlStatement statement, IReadOnlyDictionary<string, BindValue> binds) : IStatementCapture
{
    public SqlStatement StatementToSend { get; } = statement;

    public IReadOnlyDictionary<string, BindValue> BindsToSend { get; } = binds;

    public Task<RollbackStatementSummary> CompleteAsync(long? rowsAffected, IReadOnlyDictionary<string, object?> outValues,
                                                        string? transactionId, CancellationToken ct) =>
        Task.FromResult(RollbackStatementSummary.None);

    public Task<RollbackStatementSummary> AbandonAsync(DbError? failure, CancellationToken ct) =>
        Task.FromResult(RollbackStatementSummary.None);
}

/// <summary>아무것도 저장하지 않는 원장.</summary>
public sealed class DisabledRollbackLedger : IRollbackLedger
{
    public static DisabledRollbackLedger Instance { get; } = new();

    public void Record(RollbackStatementRecord record)
    {
    }

    public void MarkSavepoint(Guid sessionId, string name)
    {
    }

    public RollbackOpenState? Open(Guid sessionId) => null;

    public IReadOnlyList<RollbackProbe> Probes(Guid sessionId, int max = 5) => [];

    public Task BeforeEndAsync(Guid sessionId, CancellationToken ct) => Task.CompletedTask;

    public Task EndAsync(TransactionEnded ended, CancellationToken ct) => Task.CompletedTask;

    public void RecordDerived(Guid sessionId, RollbackOrigin origin, long originGroupId, RollbackStatementRecord record)
    {
    }
}

/// <summary>어떤 글도 쓰기 문장으로 보지 않는 분석기(모든 문장이 원복 대상 밖).</summary>
public sealed class NullDmlAnalyzer : IDmlAnalyzer
{
    public static NullDmlAnalyzer Instance { get; } = new();

    public DmlShape? Analyze(string text) => null;
}
