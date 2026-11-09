using Oranda.Core.Transfer.Copy;

namespace Oranda.Core.Composition.Dispatch;

/// <summary>
/// 세션의 DBMS 로 구현을 골라 넘긴다(D-046). 구현 등록 = <c>[OrandaService(typeof(ICopyPrecheckService), Dbms = …)]</c>.
/// 원본·대상 세션의 DBMS 가 같아야 한다 — 다르면 <see cref="NotSupportedException"/>(서로 다른 DBMS 사이 복사는 T5).
/// </summary>
[OrandaService(typeof(ICopyPrecheckService))]
public sealed class CopyPrecheckServiceDispatcher : ICopyPrecheckService
{
    private readonly IDbmsServiceSelector _selector;

    public CopyPrecheckServiceDispatcher(IDbmsServiceSelector selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        _selector = selector;
    }

    public Task<PrecheckReport> RunAsync(CopySessions sessions, CopyJob job, ResolvedPeriod period,
                                         IProgress<string>? progress, CancellationToken ct) =>
        _selector.Get<ICopyPrecheckService>(DispatchRules.DbmsOf(sessions)).RunAsync(sessions, job, period, progress, ct);
}
