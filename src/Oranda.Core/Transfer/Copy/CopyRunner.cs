using System.Globalization;
using System.Text.RegularExpressions;
using Oranda.Core.Catalog;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Transfer.Copy.CrossDbms;

namespace Oranda.Core.Transfer.Copy;

/// <summary>
/// 복사 실행기(설계 8.2). 순서: 끌 트리거·FK 기록 → 끄기 → 테이블 다시 만들기(보존 DDL 받기 → 자식 → 부모 DROP → 부모 → 자식 CREATE, 개발 대상만)
/// → 대상 정리(자식 → 부모 DELETE) → 적재(부모 → 자식, 배치 바인드 배열) → 시퀀스 보정 → 대상 트랜잭션 끝내기 → 다시 켜기
/// → 다시 만든 표의 트리거·권한·참조 FK 되살리기 → 건수 비교.
/// <para>
/// 원본은 읽기 전용 프로필의 전용 세션이다. 시작할 때 바인드 없는 ROLLBACK 으로 앞 트랜잭션을 닫고, 「같은 시점 기준 읽기」면
/// <c>SET TRANSACTION READ ONLY</c> 를 연다. 원본 읽기는 모두 <see cref="IBulkExecutionService.StreamRowsAsync"/> 다(원본에 <see cref="IExecutionService"/> 를 쓰지 않는다).
/// </para>
/// <para>
/// ALTER 는 DDL 이라 묵시 커밋한다. 그래서 다시 켜기 전에 대상 트랜잭션을 반드시 명시적으로 끝낸다:
/// 중지·예외 = ROLLBACK, 「모두 끝난 뒤 한 번」 = 실패 표가 있으면 ROLLBACK 아니면 COMMIT, 나머지 = COMMIT.
/// 중지 뒤 정리(롤백·다시 켜기·기록)는 <c>ct</c> 대신 새 토큰을 쓴다: 실행 엔진은 취소된 토큰을 받으면 문장을 보내지 않는다.
/// </para>
/// <para>
/// DBMS 마다 다른 것은 생성자로 받는다(T5 결정 Q2): 문장(<see cref="ICopyDialect"/>, DBMS 무관 문장은 <see cref="CopyCommonStatements"/>)·
/// 값 해석(<see cref="IBulkValueCodec"/>)·오류 분류(<see cref="IDbErrorClassifier"/>, 잡는 범위는 <see cref="CopyErrors"/>)·카탈로그 조회 글(<see cref="ISqlCatalog"/>)·
/// 다시 만들기 전 받아 두기(<see cref="ICopyRecreateStep"/>). 키 서비스 등록은 그 DBMS 의 본문 없는 하위 형식(예: <c>OracleCopyRunner</c>)이 한다.
/// 이것들은 원본 벌과 대상 벌(<see cref="CopyDbmsParts"/>)로 따로 받는다(xdb-copy XC2): 원본 세션의 문장·원본 FK·원본 값 해석은 원본 벌, 나머지는 대상 벌,
/// 오류 분류는 둘을 합친 것(<see cref="CopyErrors.Combine"/>). 같은 DBMS 하위 형식은 같은 벌을 두 번 넘긴다.
/// </para>
/// </summary>
public partial class CopyRunner : ICopyRunner
{
    /// <summary>중지 뒤 정리에 주는 시간.</summary>
    internal static readonly TimeSpan CleanupTimeout = TimeSpan.FromMinutes(2);

    /// <summary>오래 걸릴 수 있는 정리(큰 적재의 ROLLBACK, FK ENABLE VALIDATE, 건수 비교)에 주는 시간.</summary>
    internal static readonly TimeSpan LongCleanupTimeout = TimeSpan.FromMinutes(30);

    internal const string PhaseDisable = "끄기";
    internal const string PhaseDelete = "정리";
    public const string PhaseLoad = "적재";

    /// <summary>
    /// 한 테이블의 적재가 끝났다는 알림(진행 값만, 작업 로그 줄이 아니다): 읽기를 마쳤고(<see cref="CopyProgress.Rows"/> = 실제로 읽은 수, <see cref="CopyProgress.Estimated"/> = 같은 수)
    /// 테이블마다 커밋이면 커밋까지 끝났다. 받는 쪽은 예상 건수 대신 이 수로 막대를 채우고, 뒤따르는 시퀀스·다시 켜기·건수 비교 보고는 단계 글만 바꾼다.
    /// </summary>
    public const string PhaseLoadEnd = "적재 끝";

    public const string PhaseSequence = "시퀀스";
    public const string PhaseEnable = "다시 켜기";
    public const string PhaseCount = "건수 비교";
    public const string PhaseRetry = "다시 넣기";
    internal const string PhaseRecreate = "다시 만들기";
    internal const string PhaseCreate = "테이블 만들기";
    public const string PhaseRestore = "되살리기";
    internal const string PhaseStart = "시작";
    internal const string PhaseSource = "원본 준비";
    internal const string PhaseFinish = "마무리";

    // 스냅샷 너무 오래됨(SnapshotTooOld)·컴파일 오류와 함께 만들어짐(CompiledWithErrors: 되살린 트리거가 바뀐 열 구조와 맞지 않음)·
    // 연결 끊김(Disconnected: COMMIT 중이면 확정됐는지 알 수 없다)은 분류기(IDbErrorClassifier)의 DbErrorKind 로 본다.
    // Core 에 대응하는 분류가 없는 둘(부모 키 없음·안 보이는 FK 가 DROP 을 막음)은 방언(ICopyDialect)이 판정한다.

    private static readonly IReadOnlyDictionary<string, BindValue> NoBinds = new Dictionary<string, BindValue>();

    private static readonly Regex QuotedValue = new("'[^']*'", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static readonly Regex ColumnValues = new(@"column values \([^)]*\)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));

    private readonly IBulkExecutionService _bulk;
    private readonly IExecutionService _execution;
    private readonly ICopyRunLog _runLog;
    // 원본 벌: 원본 세션에 보내는 문장(SET TRANSACTION READ ONLY)·원본 FK 읽기·원본 이름 판정·원본 값 해석(마스킹·실패 행 키).
    private readonly ICopyDialect _sourceDialect;
    private readonly IBulkValueCodec _sourceCodec;
    private readonly ICopyRecreateStep _sourceRecreate;

    // 대상 벌: 그 밖의 모든 것(대상 조회·DML·DDL·되살리기·시퀀스·건수).
    private readonly ISqlCatalog _catalog;
    private readonly ICopyDialect _dialect;
    private readonly IBulkValueCodec _codec;
    private readonly ICopyRecreateStep _recreate;

    /// <summary>대상 DBMS 가 표 만들기·다시 만들기를 막는 이유(사전 점검 오류가 없어도 실행기가 한 번 더 막는다).</summary>
    private readonly ICopyPrecheckDialect _targetPrecheck;

    /// <summary>원본·대상 두 DBMS 의 예외를 함께 알아보는 분류기(<see cref="CopyErrors.Combine"/>, 같은 DBMS 면 대상 것 그대로).</summary>
    private readonly IDbErrorClassifier _errors;

    /// <summary>원본·대상 DBMS. 다르면 교차 복사(xdb-copy XC6): 열을 <see cref="CrossTypeMap"/> 으로 옮기고 값을 이식 값으로 바꾼다.</summary>
    private readonly Dbms _sourceDbms;
    private readonly Dbms _targetDbms;

    public CopyRunner(IBulkExecutionService bulk, IExecutionService execution, ICopyRunLog runLog, CopyDbmsParts source, CopyDbmsParts target)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        _bulk = bulk;
        _execution = execution;
        _runLog = runLog;
        _sourceDbms = source.Dbms;
        _targetDbms = target.Dbms;
        _sourceDialect = source.Dialect;
        _sourceCodec = source.Codec;
        _sourceRecreate = source.Recreate;
        _catalog = target.Catalog;
        _dialect = target.Dialect;
        _codec = target.Codec;
        _recreate = target.Recreate;
        _targetPrecheck = target.PrecheckDialect;
        _errors = CopyErrors.Combine(target.Errors, source.Errors);
    }

    /// <summary>같은 DBMS 복사: 같은 벌(<paramref name="parts"/>)을 원본·대상에 같은 인스턴스로 넘긴다(DBMS 별 하위 형식이 쓴다).</summary>
    protected CopyRunner(IBulkExecutionService bulk, IExecutionService execution, ICopyRunLog runLog, CopyDbmsParts parts)
        : this(bulk, execution, runLog, parts, parts)
    {
    }

    public async Task<CopyRunResult> RunAsync(CopySessions sessions, CopyJob job, PrecheckReport report, CopyDecisions decisions,
                                              ResolvedPeriod period, IProgress<CopyProgress>? progress, CopyRunControl control,
                                              CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(decisions);
        ArgumentNullException.ThrowIfNull(period);
        ArgumentNullException.ThrowIfNull(control);
        EnsureSessions(sessions);
        if (report.HasErrors)
        {
            throw new InvalidOperationException("사전 점검에 오류가 있어 실행할 수 없습니다.");
        }

        EnsureReportMatchesJob(job, report, period);
        EnsureRecreateAllowed(sessions, job);
        EnsureCreateAllowed(sessions, job, report);
        EnsureAddColumnsAllowed(sessions, report);
        EnsureDistinct(sessions, job);
        EnsureWorkers(sessions);
        var run = new RunState(sessions, job, report, period, decisions.PreConfirmedSql, progress);

        // 대기 원인 감시(WH1): 대상 쪽만, 실행당 감시 세션 하나. 앱이 감시 세션 여는 길을 주지 않으면 하지 않는다.
        await using var waitProbe = sessions.OpenMonitor is { } openMonitor
            ? new CopyWaitProbe(token => openMonitor(sessions.Target, token), _bulk, _catalog, _codec, _errors, threshold: WatchTiming.ProbeAfter)
            : null;
        run.WaitProbe = waitProbe;
        var lanes = PrepareLanes(run, sessions); // 동시 테이블 수(PF2). 1 이면 아래 흐름이 예전 그대로다.
        await using var record = await _runLog.BeginAsync(
            new CopyRunHeader(job.Id, job.Name, job.Source.Name, job.Target.Name, period, DateTimeOffset.Now), ct);
        run.Record = record;
        LogStart(run, "복사 시작", period);
        LogLanes(run, lanes);

        var finished = false;
        try
        {
            await OpenSourceAsync(run, ct);
            if (!run.Aborted && UsesReadPoint(run))
            {
                // 같은 시점 읽기 + 동시 복사(PF2b): 끄기·정리보다 먼저 원본 시점을 잡는다(못 잡으면 아무것도 바꾸지 않고 멈춘다).
                await CaptureReadPointAsync(run, ct);
            }

            if (!run.Aborted)
            {
                await DisableAsync(run, decisions, ct);
            }

            if (!run.Aborted)
            {
                // DROP·CREATE 는 DDL(묵시 커밋)이라 DELETE·적재보다 먼저 한다: 뒤에 하면 앞의 DELETE 를 확정해 버린다.
                await RecreateTablesAsync(run, ct);
            }

            if (!run.Aborted)
            {
                // 대상에 없는 표 만들기(「없으면 만들기」)도 DDL 이라 DELETE·적재보다 먼저 한다.
                // Tibero 의 CREATE TABLE 도 앞선 트랜잭션을 묵시 커밋한다(실측): 대상 쓰기 트랜잭션을 여는 DeleteTargetsAsync·LoadAllAsync 앞이라 대기 중인 DML 이 없다.
                // 만든 표 정리(DROP … PURGE)는 대상 트랜잭션을 끝낸 뒤에만 한다(PurgeUnloadedNewTablesAsync).
                await CreateMissingTablesAsync(run, ct);
            }

            if (!run.Aborted)
            {
                // 「대상에 열 추가」(PC1)도 DDL 이라 DELETE·적재보다 먼저 한다.
                await AddColumnsAsync(run, ct);
            }

            if (!run.Aborted)
            {
                // 시퀀스 보정 연결이 쓰는데 대상에 없는 시퀀스(SQ3)도 DDL 이라 DELETE·적재보다 먼저 만든다.
                await CreateRuleSequencesAsync(run);
            }

            if (!run.Aborted)
            {
                await DeleteTargetsAsync(run, ct);
                if (run.LaneCount > 1)
                {
                    await LoadInLanesAsync(run, control, ct); // 동시 복사(PF2, CopyRunner.Lanes.cs): 정리 확정 → FK 단계마다 레인
                }
                else
                {
                    await LoadAllAsync(run, control, ct);
                }
            }

            if (!run.Aborted)
            {
                await AdvanceSequencesAsync(run, decisions.ProceedSequenceGap, ct);
            }

            finished = true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            run.Stopped = true;
            finished = true;
        }
        finally
        {
            NoteWaitProbe(run);

            // 레인(둘째부터)의 트랜잭션을 먼저 끝낸다: 남은 레인의 잠금이 다시 켜기(DDL)를 막고, 켜기는 모든 레인이 끝난 뒤여야 한다(PF2).
            await FinishLanesAsync(run, faulted: !finished);

            // finished 가 거짓이면 예상 못 한 예외가 올라가는 중이다: 되돌리고 다시 켠 뒤 예외를 그대로 올린다.
            await FinishAsync(run, faulted: !finished);
        }

        return run.Result();
    }

    public async Task<CopyRunResult> RetryFailedRowsAsync(CopySessions sessions, CopyJob job, PrecheckReport report,
                                                          IReadOnlyList<FailedRow> rows, IProgress<CopyProgress>? progress,
                                                          CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(rows);
        EnsureSessions(sessions);

        // 기간 경계는 키로 다시 읽을 때 쓰지 않는다(키 조건만). 실행 기록 머리글에는 작업의 기간 규칙 이름만 남는다.
        EnsureReportMatchesJob(job, report, period: null);
        EnsureDistinct(sessions, job);
        var period = new ResolvedPeriod(null, null);
        var run = new RunState(sessions, job, report, period, new HashSet<string>(StringComparer.Ordinal), progress);
        run.IsRetry = true;
        run.RetryInputs = rows;
        await using var record = await _runLog.BeginAsync(
            new CopyRunHeader(job.Id, job.Name, job.Source.Name, job.Target.Name, period, DateTimeOffset.Now), ct);
        run.Record = record;
        LogStart(run, $"실패 행 다시 넣기 시작({rows.Count.ToString("N0", CultureInfo.InvariantCulture)}행)", period: null);

        var finished = false;
        try
        {
            await OpenSourceAsync(run, ct);
            if (!run.Aborted)
            {
                foreach (var group in rows.GroupBy(r => r.Table, StringComparer.Ordinal))
                {
                    await RetryTableAsync(run, group.Key, [.. group], ct);
                }
            }

            finished = true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            run.Stopped = true;
            finished = true;
        }
        finally
        {
            await FinishAsync(run, faulted: !finished);
        }

        return run.Result();
    }
}
