using Oranda.Core.Transfer.Copy;

namespace Oranda.Core.Composition.Dispatch;

/// <summary>
/// 세션의 DBMS 로 구현을 골라 넘긴다(D-046). 구현 등록 = <c>[OrandaService(typeof(ICopyRunner), Dbms = …)]</c>.
/// 원본·대상 세션의 DBMS 가 같아야 한다 — 다르면 <see cref="NotSupportedException"/>(서로 다른 DBMS 사이 복사는 T5).
/// </summary>
[OrandaService(typeof(ICopyRunner))]
public sealed class CopyRunnerDispatcher : ICopyRunner
{
    private readonly IDbmsServiceSelector _selector;

    public CopyRunnerDispatcher(IDbmsServiceSelector selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        _selector = selector;
    }

    public Task<CopyRunResult> RunAsync(CopySessions sessions, CopyJob job, PrecheckReport report, CopyDecisions decisions,
                                        ResolvedPeriod period, IProgress<CopyProgress>? progress, CopyRunControl control,
                                        CancellationToken ct) =>
        _selector.Get<ICopyRunner>(DispatchRules.DbmsOf(sessions)).RunAsync(sessions, job, report, decisions, period, progress, control, ct);

    public Task<CopyRunResult> RetryFailedRowsAsync(CopySessions sessions, CopyJob job, PrecheckReport report,
                                                    IReadOnlyList<FailedRow> rows, IProgress<CopyProgress>? progress,
                                                    CancellationToken ct) =>
        _selector.Get<ICopyRunner>(DispatchRules.DbmsOf(sessions)).RetryFailedRowsAsync(sessions, job, report, rows, progress, ct);
}
