using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Transfer.Copy;

namespace Oranda.Core.Composition.Dispatch;

/// <summary>
/// 세션의 DBMS 로 구현을 골라 넘긴다(D-046). 구현 등록 = <c>[OrandaService(typeof(ICopyPrecheckService), Dbms = …)]</c>.
/// 원본·대상 세션의 DBMS 가 같으면 그 DBMS 의 키 등록 점검 그대로다.
/// 다르면(교차 복사, xdb-copy XC6) 지원하는 쌍(Oracle↔Tibero)일 때 두 DBMS 의 키 등록 <see cref="ICopyDbmsParts"/> 로 <see cref="CopyPrecheckService"/> 를 만든다
/// (원본 벌, 대상 벌). 지원하지 않는 쌍이거나 어느 쪽에 부품이 없으면 <see cref="NotSupportedException"/>.
/// </summary>
/// <remarks>교차 점검이 쓰는 대량 실행·메타데이터는 키 없는 디스패처(세션 DBMS 로 다시 고른다)를 받는다.</remarks>
[OrandaService(typeof(ICopyPrecheckService))]
public sealed class CopyPrecheckServiceDispatcher : ICopyPrecheckService
{
    private readonly IDbmsServiceSelector _selector;
    private readonly IBulkExecutionService _bulk;
    private readonly IMetadataProvider _metadata;
    private readonly IExecutionPolicy _policy;

    public CopyPrecheckServiceDispatcher(IDbmsServiceSelector selector, IBulkExecutionService bulk, IMetadataProvider metadata, IExecutionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(bulk);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(policy);
        _selector = selector;
        _bulk = bulk;
        _metadata = metadata;
        _policy = policy;
    }

    public Task<PrecheckReport> RunAsync(CopySessions sessions, CopyJob job, ResolvedPeriod period,
                                         IProgress<string>? progress, CancellationToken ct) =>
        Pick(sessions).RunAsync(sessions, job, period, progress, ct);

    private ICopyPrecheckService Pick(CopySessions sessions)
    {
        var (source, target) = DispatchRules.CopyPair(sessions);
        if (source == target)
        {
            return _selector.Get<ICopyPrecheckService>(source);
        }

        var (sourceParts, targetParts) = DispatchRules.CrossCopyParts(_selector, source, target);
        return new CopyPrecheckService(_bulk, _metadata, _policy, sourceParts, targetParts);
    }
}
