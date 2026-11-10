using Oranda.Core.Execution;
using Oranda.Core.Transfer.Copy;

namespace Oranda.Core.Composition.Dispatch;

/// <summary>
/// 세션의 DBMS 로 구현을 골라 넘긴다(D-046). 구현 등록 = <c>[OrandaService(typeof(ICopyRunner), Dbms = …)]</c>.
/// 원본·대상 세션의 DBMS 가 같으면 그 DBMS 의 키 등록 실행기 그대로다.
/// 다르면(교차 복사, xdb-copy XC6) 지원하는 쌍(Oracle↔Tibero)일 때 두 DBMS 의 키 등록 <see cref="ICopyDbmsParts"/> 로 <see cref="CopyRunner"/> 를 만든다
/// (원본 벌, 대상 벌). 지원하지 않는 쌍이거나 어느 쪽에 부품이 없으면 <see cref="NotSupportedException"/>.
/// </summary>
/// <remarks>교차 실행기가 쓰는 대량 실행·실행 엔진은 키 없는 디스패처(세션 DBMS 로 다시 고른다)를 받는다 — 원본 읽기는 원본 DBMS, 대상 DML 은 대상 DBMS 로 간다.</remarks>
[OrandaService(typeof(ICopyRunner))]
public sealed class CopyRunnerDispatcher : ICopyRunner
{
    private readonly IDbmsServiceSelector _selector;
    private readonly IBulkExecutionService _bulk;
    private readonly IExecutionService _execution;
    private readonly ICopyRunLog _runLog;

    public CopyRunnerDispatcher(IDbmsServiceSelector selector, IBulkExecutionService bulk, IExecutionService execution, ICopyRunLog runLog)
    {
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(bulk);
        ArgumentNullException.ThrowIfNull(execution);
        ArgumentNullException.ThrowIfNull(runLog);
        _selector = selector;
        _bulk = bulk;
        _execution = execution;
        _runLog = runLog;
    }

    public Task<CopyRunResult> RunAsync(CopySessions sessions, CopyJob job, PrecheckReport report, CopyDecisions decisions,
                                        ResolvedPeriod period, IProgress<CopyProgress>? progress, CopyRunControl control,
                                        CancellationToken ct) =>
        Pick(sessions).RunAsync(sessions, job, report, decisions, period, progress, control, ct);

    public Task<CopyRunResult> RetryFailedRowsAsync(CopySessions sessions, CopyJob job, PrecheckReport report,
                                                    IReadOnlyList<FailedRow> rows, IProgress<CopyProgress>? progress,
                                                    CancellationToken ct) =>
        Pick(sessions).RetryFailedRowsAsync(sessions, job, report, rows, progress, ct);

    private ICopyRunner Pick(CopySessions sessions)
    {
        var (source, target) = DispatchRules.CopyPair(sessions);
        if (source == target)
        {
            return _selector.Get<ICopyRunner>(source);
        }

        var (sourceParts, targetParts) = DispatchRules.CrossCopyParts(_selector, source, target);
        return new CopyRunner(_bulk, _execution, _runLog, sourceParts, targetParts);
    }
}
