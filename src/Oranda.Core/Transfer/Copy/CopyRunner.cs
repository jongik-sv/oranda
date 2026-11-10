using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Oranda.Core.Catalog;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.History;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Core.Text;
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

    // ---- 시작 ----

    /// <summary>작업 로그 첫 줄: 작업·접속 이름(프로필 이름만, 비밀 없음)·커밋 단위·읽기 방식, 그리고 기간 경계.</summary>
    private static void LogStart(RunState run, string what, ResolvedPeriod? period)
    {
        var job = run.Job;
        var options = job.Options;
        var commit = options.Commit switch
        {
            CommitMode.PerBatch => "배치마다",
            CommitMode.AtEnd => "모두 끝난 뒤 한 번",
            _ => "테이블마다",
        };
        run.Enter(PhaseStart);
        run.Log($"{what}: 작업 「{job.Name}」 · 원본 {job.Source.Name} → 대상 {job.Target.Name} · 테이블 {run.Report.Order.Count.ToString(CultureInfo.InvariantCulture)}개"
                + $" · 커밋 {commit} · 배치 {options.BatchSize.ToString("N0", CultureInfo.InvariantCulture)}행 · 실패 행 상한 {options.MaxFailedRows.ToString("N0", CultureInfo.InvariantCulture)}"
                + (job.ConsistentRead ? " · 같은 시점 기준 읽기" : " · 테이블마다 따로 읽기")
                // 기본값(읽기·쓰기 겹치기, 대기 한도 없음)이면 덧붙이지 않는다(예전 첫 줄 그대로).
                + (options.SerialReadWrite ? " · 읽기·쓰기 번갈아(겹치지 않음)" : string.Empty)
                + (options.WaitLimitMinutes > 0 ? $" · 테이블당 대기 한도 {options.WaitLimitMinutes.ToString(CultureInfo.InvariantCulture)}분" : string.Empty)
                // 동시 복사(PF2)일 때만(한 테이블씩이면 예전 첫 줄 그대로).
                + (run.LaneCount > 1 ? $" · 동시 테이블 {run.LaneCount.ToString(CultureInfo.InvariantCulture)}개" : string.Empty));
        if (period is { } p && (p.TimeFrom is not null || p.TimeTo is not null))
        {
            static string At(DateTime? d) => d?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "(열림)";
            run.Log($"기간: {At(p.TimeFrom)} 이상 ~ {At(p.TimeTo)} 미만");
        }
    }

    /// <summary>
    /// 사전 점검 뒤 규칙이 바뀌었으면 실행하지 않는다. 문장(DELETE·SELECT)은 작업에서, MERGE 판정·마스킹·끄기는 보고서에서 만들므로
    /// 둘이 다르면 미리 받은 확인(PreConfirmedSql)이 다른 뜻의 문장에 쓰이거나 제외한 표를 읽을 수 있다.
    /// </summary>
    /// <remarks>
    /// 표마다의 계정(묶음 B)도 본다: 규칙의 원본·대상 계정이 점검한 표의 계정과 같아야 하고, 지금 작업으로 만든 대상 DELETE 가
    /// 점검 미리 보기의 DELETE 와 글자까지 같아야 한다(작업 밖·제외된 부모의 대상 계정이 바뀌어 지우는 범위가 달라지는 것도 막는다).
    /// DELETE 비교는 실행(<paramref name="period"/> 있음)에서만 한다. 「실패 행만 다시 넣기」는 지우지 않는다.
    /// </remarks>
    internal static void EnsureReportMatchesJob(CopyJob job, PrecheckReport report, ResolvedPeriod? period)
    {
        if (CopyOwners.Problems(job) is { Count: > 0 } problems)
        {
            throw new InvalidOperationException($"작업 규칙에 모순이 있습니다: {problems[0]} 사전 점검을 다시 하세요.");
        }

        var active = job.Tables.Where(r => r.Kind != RuleKind.Exclude).ToDictionary(r => CopyOwners.Key(job, r), StringComparer.Ordinal);
        var planned = report.Order.Select(p => CopyOwners.Key(job, p.Rule)).ToHashSet(StringComparer.Ordinal);
        var same = active.Count == planned.Count && planned.Count == report.Order.Count
                   && report.Order.All(p => active.TryGetValue(CopyOwners.Key(job, p.Rule), out var rule) && SameRule(rule, p.Rule));
        if (!same)
        {
            throw new InvalidOperationException("사전 점검 뒤 작업 규칙이 바뀌었습니다. 사전 점검을 다시 하세요.");
        }

        if (report.Order.Any(p => !string.Equals(p.Source.Table.Owner, CopyOwners.Source(job, p.Rule), StringComparison.Ordinal)
                                  || !string.Equals(p.Target.Table.Owner, CopyOwners.Target(job, p.Rule), StringComparison.Ordinal)
                                  || !string.Equals(p.Source.Table.Name, p.Rule.Table, StringComparison.Ordinal)
                                  || !string.Equals(p.Target.Table.Name, p.Rule.Table, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("사전 점검 뒤 원본·대상 소유자가 바뀌었습니다. 사전 점검을 다시 하세요.");
        }

        if (period is null)
        {
            return;
        }

        if (report.Order.Any(p => p.CreateMissing && (!p.Rule.CreateIfMissing || p.Rule.Action == TargetAction.Recreate)))
        {
            throw new InvalidOperationException("사전 점검 뒤 「없으면 만들기」 선택이 바뀌었습니다. 사전 점검을 다시 하세요.");
        }

        var builder = new CopySqlBuilder(job, period,
            report.Order.ToDictionary(p => CopyOwners.Key(job, p.Rule), p => p.Source, StringComparer.Ordinal),
            report.Order.ToDictionary(p => CopyOwners.Key(job, p.Rule), p => p.Target, StringComparer.Ordinal),
            FreshKeys(job, report));
        var previewed = report.Preview.Where(x => x.Side == PreviewSide.Target).Select(x => x.Sql).ToHashSet(StringComparer.Ordinal);
        foreach (var plan in report.Order)
        {
            if (DeleteText(builder, CopyOwners.Key(job, plan.Rule)) is { } text && !previewed.Contains(text))
            {
                throw new InvalidOperationException("사전 점검 뒤 대상 지우기 문장이 바뀌었습니다(계정·부모 테이블). 사전 점검을 다시 하세요.");
            }
        }
    }

    /// <summary>대상 DELETE 글(지우지 않는 처리면 null). 문장을 만들 수 없는 규칙은 사전 점검이 이미 오류로 냈으므로 비교하지 않는다(null).</summary>
    private static string? DeleteText(CopySqlBuilder builder, string key)
    {
        try
        {
            return builder.TargetDelete(key)?.Sql;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    /// <summary>규칙 값 비교(목록은 내용으로 비교한다. record 의 기본 비교는 목록을 참조로 비교한다). 표마다의 계정도 비교한다.</summary>
    internal static bool SameRule(TableRule a, TableRule b) =>
        a.Table == b.Table && a.Kind == b.Kind && a.Action == b.Action && a.CreateIfMissing == b.CreateIfMissing
        && string.Equals(a.SourceOwner, b.SourceOwner, StringComparison.Ordinal)
        && string.Equals(a.TargetOwner, b.TargetOwner, StringComparison.Ordinal)
        && string.Equals(a.CustomWhere, b.CustomWhere, StringComparison.Ordinal)
        && string.Equals(a.ExtraWhere, b.ExtraWhere, StringComparison.Ordinal)
        && a.PeriodColumns.SequenceEqual(b.PeriodColumns)
        && a.Masks.SequenceEqual(b.Masks)
        && a.SkipColumns.SequenceEqual(b.SkipColumns, StringComparer.Ordinal)
        && (a.Parent is null) == (b.Parent is null)
        && (a.Parent is null || (a.Parent.ParentTable == b.Parent!.ParentTable
                                 && a.Parent.ChildColumns.SequenceEqual(b.Parent.ChildColumns, StringComparer.Ordinal)
                                 && a.Parent.ParentColumns.SequenceEqual(b.Parent.ParentColumns, StringComparer.Ordinal)
                                 && a.Parent.ForeignKeyName == b.Parent.ForeignKeyName
                                 && string.Equals(a.Parent.ParentOwner, b.Parent.ParentOwner, StringComparison.Ordinal)));

    /// <summary>
    /// 원본과 대상이 같은 DB·같은 소유자면 거부한다. 다른 프로필 둘이 같은 곳을 가리키면 대상 DELETE·커밋이 원본 행을 지운다.
    /// <see cref="ISession.DbKey"/> 는 <c>{DB_UNIQUE_NAME}/{CON_NAME}/{SESSION_USER}</c> 다(앞 두 마디가 같으면 같은 DB).
    /// </summary>
    /// <remarks>
    /// 이름만으로는 복제 DB·기본 이름 컨테이너(FREE/FREEPDB1 등)를 같은 DB 로 오판한다. 그래서 호스트·인스턴스 이름도 본다.
    /// RAC 는 노드마다 호스트·인스턴스가 달라 DB 이름만 본다. 호스트·인스턴스를 모르면 같다고 본다(막는 쪽).
    /// </remarks>
    internal static bool IsSameDatabaseAndOwner(string sourceDbKey, ServerInfo source, string targetDbKey, ServerInfo target,
                                                string sourceOwner, string targetOwner)
        => CopyOwners.SameOwner(sourceOwner, targetOwner)
           && IsSameDatabase(sourceDbKey, source, targetDbKey, target);

    /// <summary>원본과 대상이 같은 DB 인가(<see cref="IsSameDatabaseAndOwner"/> 의 DB 판정). 모르면 같다고 본다(막는 쪽).</summary>
    internal static bool IsSameDatabase(string sourceDbKey, ServerInfo source, string targetDbKey, ServerInfo target)
        => CopySameDatabase.IsSameDatabase(sourceDbKey, source, targetDbKey, target);

    /// <summary>
    /// 같은 DB 일 때 대상 정리가 원본 행을 지우는 규칙(규칙 사이 비교, <see cref="CopyOwners.TargetsReadAsSource"/>). 다른 DB 면 빈 목록.
    /// 사전 점검과 실행기가 같은 판정(<see cref="CopySameDatabase.SameSourceTargets"/>)을 쓴다.
    /// </summary>
    internal static IReadOnlyList<(string Key, string Table)> SameSourceTargets(CopySessions sessions, CopyJob job)
        => CopySameDatabase.SameSourceTargets(sessions, job);

    /// <summary>DROP·CREATE 는 개발 라벨 대상에서만 실행한다(사전 점검과 따로 한 번 더 막는다, Global Constraints).</summary>
    private void EnsureRecreateAllowed(CopySessions sessions, CopyJob job)
    {
        var recreate = job.Tables.Where(r => r.Kind != RuleKind.Exclude && r.Action == TargetAction.Recreate).ToList();
        if (recreate.Count > 0 && !CopyTargetGuard.CanRecreate(sessions.Target.Profile))
        {
            throw new InvalidOperationException($"대상 접속 '{sessions.Target.Profile.Name}' 은 개발 접속이 아니라 「테이블 다시 만들기」를 실행할 수 없습니다.");
        }

        // 대상 DBMS 가 다시 만들기를 막으면(Tibero 는 아직 막는다) 오류 없는 점검 결과를 받았어도 DROP 전에 멈춘다.
        if (recreate.Count > 0 && _targetPrecheck.RecreateBlockedReason(sessions.Target) is { } blocked)
        {
            throw new InvalidOperationException($"대상 DBMS({_targetDbms}) 에서는 「테이블 다시 만들기」를 실행할 수 없습니다: {blocked}");
        }

        // 다시 만들기는 대상 소유자 계정으로만 한다(표마다, 사전 점검 RecreateNoPrivilege 와 같은 판정). DbKey 의 끝 마디가 SESSION_USER 다.
        var user = SessionUser(sessions.Target.DbKey);
        if (user is not null && recreate.FirstOrDefault(r => !CopyOwners.SameOwner(CopyOwners.Target(job, r), user)) is { } other)
        {
            throw new InvalidOperationException($"{CopyOwners.Key(job, other)}: 「테이블 다시 만들기」는 대상 소유자({CopyOwners.Target(job, other)}) 계정으로 접속해야 합니다(지금 {user}).");
        }
    }

    /// <summary>
    /// 「없으면 만들기」 표(<see cref="CopyTablePlan.CreateMissing"/>)도 개발 접속·대상 소유자 계정으로만 만든다(사전 점검과 따로 한 번 더 막는다).
    /// </summary>
    private void EnsureCreateAllowed(CopySessions sessions, CopyJob job, PrecheckReport report)
    {
        var creating = report.Order.Where(p => p.CreateMissing).ToList();
        if (creating.Count == 0)
        {
            return;
        }

        if (!CopyTargetGuard.CanRecreate(sessions.Target.Profile))
        {
            throw new InvalidOperationException($"대상 접속 '{sessions.Target.Profile.Name}' 은 개발 접속이 아니라 「없으면 만들기」로 테이블을 만들 수 없습니다.");
        }

        // 대상 DBMS 가 표 만들기를 막으면 오류 없는 점검 결과를 받았어도 CREATE 전에 멈춘다.
        if (_targetPrecheck.CreateBlockedReason(sessions.Target) is { } blocked)
        {
            throw new InvalidOperationException($"대상 DBMS({_targetDbms}) 에서는 「없으면 만들기」로 테이블을 만들 수 없습니다: {blocked}");
        }

        var user = SessionUser(sessions.Target.DbKey);
        if (user is not null && creating.FirstOrDefault(p => !CopyOwners.SameOwner(CopyOwners.Target(job, p.Rule), user)) is { } other)
        {
            throw new InvalidOperationException($"{CopyOwners.Key(job, other.Rule)}: 「없으면 만들기」는 대상 소유자({CopyOwners.Target(job, other.Rule)}) 계정으로 접속해야 합니다(지금 {user}).");
        }
    }

    /// <summary>이번 실행이 만드는 표(「없으면 만들기」)의 규칙 키. 새 표라 대상 DELETE 를 만들지 않는다(<see cref="CopySqlBuilder"/>).</summary>
    private static HashSet<string> FreshKeys(CopyJob job, PrecheckReport report)
        => report.Order.Where(p => p.CreateMissing).Select(p => CopyOwners.Key(job, p.Rule)).ToHashSet(StringComparer.Ordinal);

    /// <summary><c>{DB_UNIQUE_NAME}/{CON_NAME}/{SESSION_USER}</c> 의 끝 마디. 모양이 다르면 null.</summary>
    private static string? SessionUser(string dbKey)
    {
        var last = dbKey.LastIndexOf('/');
        return last < 0 || last == dbKey.Length - 1 ? null : dbKey[(last + 1)..];
    }

    /// <summary>같은 DB 에서 어떤 규칙의 대상 표가 어떤 규칙이 읽는 원본 표면 거부한다(사전 점검과 따로 한 번 더, 표 단위·규칙 사이).</summary>
    private static void EnsureDistinct(CopySessions sessions, CopyJob job)
    {
        if (CopyOwners.Problems(job) is { Count: > 0 } problems)
        {
            throw new InvalidOperationException(problems[0]);
        }

        if (SameSourceTargets(sessions, job) is { Count: > 0 } same)
        {
            throw new InvalidOperationException($"원본과 대상이 같은 DB 이고 대상 테이블 {same[0].Table} 을(를) 원본으로도 읽습니다. 원본 행을 지울 수 있어 실행하지 않습니다.");
        }
    }

    private void EnsureSessions(CopySessions sessions)
    {
        // 부품 벌(방언·코덱)은 자기 DBMS 의 것이다: 세션 DBMS 와 다르면 다른 DBMS 의 문장·값 코덱으로 실행하게 된다.
        if (sessions.Source.Profile.Dbms != _sourceDbms)
        {
            throw new InvalidOperationException($"원본 세션의 DBMS({sessions.Source.Profile.Dbms})가 원본 복사 부품의 DBMS({_sourceDbms})와 다릅니다. 세션에 맞는 복사 실행기를 쓰세요.");
        }

        if (sessions.Target.Profile.Dbms != _targetDbms)
        {
            throw new InvalidOperationException($"대상 세션의 DBMS({sessions.Target.Profile.Dbms})가 대상 복사 부품의 DBMS({_targetDbms})와 다릅니다. 세션에 맞는 복사 실행기를 쓰세요.");
        }

        if (sessions.Target.HasPendingTransaction)
        {
            // 첫 DDL(끄기)이나 COMMIT 이 남의 미커밋 작업을 확정하지 않게 한다. 복사는 전용 세션을 새로 연다(설계 8.1).
            throw new InvalidOperationException("대상 세션에 커밋하지 않은 작업이 있습니다. 복사용 전용 세션을 새로 여세요.");
        }

        CopyTargetGuard.EnsureAllowedTarget(sessions.Target.Profile);
        if (!sessions.Source.Profile.ReadOnly)
        {
            throw new InvalidOperationException("원본 세션은 읽기 전용 프로필(profile with { ReadOnly = true })로 열어야 합니다.");
        }
    }

    /// <summary>원본 앞 트랜잭션을 닫고 읽기 전용 트랜잭션을 연다. 그 시각이 <c>ReadStartedAt</c> 이다.</summary>
    private async Task OpenSourceAsync(RunState run, CancellationToken ct)
    {
        run.Enter(PhaseSource);
        var rollback = await ControlLoggedAsync(run, run.Source, CopyCommonStatements.Rollback(), ct, "앞 트랜잭션 닫기");
        if (rollback.Status != ExecutionStatus.Succeeded)
        {
            run.Abort("원본 세션의 트랜잭션을 정리하지 못했습니다: " + Describe(rollback));
            return;
        }

        // 「같은 시점 기준 읽기」를 꺼도 원본에는 DB 수준 READ ONLY 를 건다(그때는 표마다 새로 연다, BeginTableReadAsync).
        run.ReadStartedAt = DateTimeOffset.Now;
        var set = await ControlLoggedAsync(run, run.Source, _sourceDialect.SetTransactionReadOnly(), ct,
            run.Job.ConsistentRead ? "모든 테이블을 이 시점 기준으로 읽기" : "읽기 전용(테이블마다 새로 엽니다)");
        if (set.Status != ExecutionStatus.Succeeded)
        {
            run.Abort("원본에서 읽기 전용 트랜잭션을 시작하지 못했습니다: " + Describe(set));
        }
    }

    // ---- 1. 끄기 ----

    /// <summary>끌 목록을 먼저 기록(디스크)한 뒤 끈다(Review Focus 3). 하나라도 끄지 못하면 더 진행하지 않는다.</summary>
    private async Task DisableAsync(RunState run, CopyDecisions decisions, CancellationToken ct)
    {
        var chosen = DisableTargets(run.Report, decisions, run.Key);
        if (chosen.Count == 0)
        {
            return;
        }

        await run.Record!.RecordDisabledAsync([.. chosen.Select(c => c.Object)], ct);
        run.Log($"끌 목록을 실행 기록에 먼저 남겼습니다: 트리거 {chosen.Count(c => c.Object.Kind == DisabledKind.Trigger).ToString(CultureInfo.InvariantCulture)}개"
                + $", FK {chosen.Count(c => c.Object.Kind == DisabledKind.ForeignKey).ToString(CultureInfo.InvariantCulture)}개", phase: PhaseDisable);
        foreach (var (obj, key) in chosen)
        {
            run.Progress(key, 0, null, PhaseDisable); // 진행 줄은 규칙 키다(다른 계정의 같은 이름 표와 섞이지 않게)
            var stmt = obj.Kind == DisabledKind.Trigger
                ? _dialect.DisableTrigger(obj.Owner, obj.Name)
                : _dialect.DisableConstraint(obj);
            // 보내기 전에 목록에 넣는다: ALTER 가 도는 중에 중지되면 서버에서 꺼졌는지 알 수 없다. 꺼지지 않은 것을 켜는 것은 해가 없다.
            run.Disabled.Add(obj);
            run.DisabledKeys[obj] = key;
            var r = await ExecuteLoggedAsync(run, run.Target, stmt, NoBinds, ct);
            if (r.Status != ExecutionStatus.Succeeded)
            {
                // 확정적으로 끄지 못했다(거부·실패): 다시 켤 것도, 「다시 켜기」로 보일 것도 아니다.
                run.Disabled.Remove(obj);
                run.Abort($"{obj.Name} 을(를) 끄지 못해 복사를 시작하지 않았습니다: {Describe(r)}");
                return;
            }
        }
    }

    /// <summary>
    /// 결정에 따라 끌 트리거·FK 와 그 진행을 보일 규칙 키. 트리거는 규칙 키로 고른 표의 대상 계정 것(사전 점검이 그 계정 소유만 골랐다),
    /// FK 는 (계정, 이름)이 결정에 있는 것만이다: 이름만 보면 다른 계정의 같은 이름 FK 까지 끈다. 같은 FK 는 한 번만 넣는다.
    /// </summary>
    internal static IReadOnlyList<(DisabledObject Object, string Key)> DisableTargets(PrecheckReport report, CopyDecisions decisions,
                                                                                       Func<CopyTablePlan, string> keyOf)
    {
        var targets = new List<(DisabledObject, string)>();
        foreach (var plan in report.Order)
        {
            var key = keyOf(plan);
            if (plan.CanDisableTriggers && decisions.DisableTriggerTables.Contains(key))
            {
                targets.AddRange(plan.Triggers.Select(t => (new DisabledObject(DisabledKind.Trigger, plan.Target.Table.Owner, plan.Rule.Table, t), key)));
            }
        }

        var seen = new HashSet<(string, string, string)>();
        foreach (var plan in report.Order)
        {
            foreach (var fk in plan.DisablableForeignKeys)
            {
                if (decisions.DisableForeignKeys.Contains(ForeignKeyRef.Of(fk)) && seen.Add((fk.Owner, fk.Table, fk.Name)))
                {
                    targets.Add((fk, keyOf(plan)));
                }
            }
        }

        return targets;
    }

    // ---- 2. 대상 정리 ----

    /// <summary>
    /// 자식 → 부모 순서로 DELETE. 지우지 못한 표는 적재하지 않는다(겹친 행이 생기므로).
    /// <para>
    /// DELETE 하나하나도 멈춤 표시·대기 한도 아래 보낸다(리뷰 PF1-4: 행 잠금을 기다리는 DELETE 가 실제로 흔한 멈춤이다). 정리는 모든 테이블을 먼저
    /// 지우는 단계라, 한도를 넘으면 <b>그 DELETE 의 테이블만</b> 실패(지우지 않음 → 적재하지 않음)로 두고 다음 테이블을 계속 지운다:
    /// 취소한 문장은 그 문장만 되돌아가고(문장 단위 원자성) 앞 테이블의 DELETE 는 트랜잭션에 남는다. 부모 테이블의 DELETE 가 남은 자식 행 때문에
    /// 실패하면 그것도 보통 DELETE 실패로 그 테이블만 실패다. 취소한 DELETE 가 돌아오지 않으면 대상 세션을 버리고 실행을 멈춘다(적재로 가지 않는다).
    /// </para>
    /// </summary>
    private async Task DeleteTargetsAsync(RunState run, CancellationToken ct)
    {
        for (var i = run.Report.Order.Count - 1; i >= 0; i--)
        {
            var plan = run.Report.Order[i];
            var delete = run.Builder.TargetDelete(run.Key(plan));
            if (delete is null)
            {
                continue;
            }

            var table = run.Table(plan);
            run.Progress(table.Name, 0, null, PhaseDelete);
            var deleteStarted = Stopwatch.GetTimestamp();
            (PumpEnd End, ExecutionResult? Result, SideWatch Side) watched;
            try
            {
                watched = await WatchTargetCommandAsync(run, table, null, CopyWaitStep.Delete,
                    token => ExecuteLoggedAsync(run, run.Target, CopyCommonStatements.Delete(delete), delete.Binds, token), ct);
            }
            finally
            {
                table.Times.AddOutside(deleteStarted); // 테이블 전체 경과에 든다(원격 #155)
            }

            if (watched.End == PumpEnd.Stuck)
            {
                await AbandonStuckAsync(run, table, watched.Side, ct);
                return;
            }

            if (watched.End == PumpEnd.WaitLimit
                || (watched.Result is { Status: not ExecutionStatus.Succeeded } && watched.Side.LimitHit && !ct.IsCancellationRequested))
            {
                table.Fail(WaitLimitText(run, watched.Side));
                continue;
            }

            var r = watched.Result!;
            if (r.Status != ExecutionStatus.Succeeded)
            {
                table.Fail("대상 정리(DELETE)를 하지 못했습니다: " + Describe(r));
            }
            else
            {
                table.Deleted = true;
            }
        }
    }

    // ---- 3. 적재 ----

    private async Task LoadAllAsync(RunState run, CopyRunControl control, CancellationToken ct)
    {
        foreach (var plan in run.Report.Order)
        {
            if (run.Aborted)
            {
                return;
            }

            var table = run.Table(plan);
            if (table.Outcome == CopyOutcome.Failed)
            {
                continue;
            }

            await LoadTableAsync(run, plan, table, control, ct);
        }
    }

    private async Task LoadTableAsync(RunState run, CopyTablePlan plan, TableState table, CopyRunControl control, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var shape = ShapeFor(run, plan);
            var dml = CopyCommonStatements.ArrayDml(DmlFor(run.Builder, plan, table.Name, shape, freshTable: plan.CreateMissing), shape.CopyColumns.Count);
            var estimated = run.Report.EstimatedRows.TryGetValue(table.Name, out var e) ? e : (long?)null;
            run.Progress(table.Name, 0, estimated, PhaseLoad);
            run.Log("적재 시작" + (estimated is { } est ? $" · 예상 {est.ToString("N0", CultureInfo.InvariantCulture)}건" : string.Empty));
            LogDroppedColumns(run, table, shape);
            if (UsesExternalTool(run, plan))
            {
                // 외부 적재 도구(조정 결정 88, CopyRunner.External.cs). 못 넣어 테이블을 정리했으면 아래 기본 방식으로 다시 넣는다.
                if (await LoadExternalAsync(run, plan, table, shape, estimated, ct) != ExternalEnd.Fallback)
                {
                    return;
                }

                run.Log("기본 방식(배열 INSERT)으로 다시 넣습니다", CopyLogKind.Warning);
            }

            // 실패 행 상한을 넘거나 원본 읽기가 실패하면 이 표의 적재분만 되돌린다(앞 표의 정리·적재는 남긴다).
            var savepoint = await ControlLoggedAsync(run, run.Target, _dialect.Savepoint(CopyCommonStatements.TableSavepoint), ct, "이 테이블을 되돌릴 자리");
            if (!RunState.Accept(savepoint, table, "적재를 시작하지 못했습니다", ct))
            {
                return;
            }

            table.SavepointSet = true;
            if (!run.Job.ConsistentRead && !await BeginTableReadAsync(run, table, ct))
            {
                return;
            }

            var select = run.Builder.SourceSelect(table.Name, shape.CopyColumns);
            LogLoadStatements(run, plan, shape, select, dml);
            // 읽기·쓰기(겹치기 또는 옛 동작 번갈아)·멈춤 감시·대기 한도는 PumpTableAsync(CopyRunner.Pipeline.cs).
            table.Pumped = true;
            var pumpStarted = Stopwatch.GetTimestamp();
            PumpResult pumped;
            try
            {
                pumped = await PumpTableAsync(run, new TableLoad(plan, table, shape, dml, select, estimated, control), ct);
            }
            finally
            {
                table.Times.AddLoad(pumpStarted); // 적재 구간(행/초의 기준, 원격 #155)
            }

            switch (pumped.End)
            {
                case PumpEnd.ReadFailed:
                    return; // 원본 읽기 실패: 이미 되돌리고 실패로 뒀다
                case PumpEnd.WaitLimit:
                    await StopAtWaitLimitAsync(run, table, pumped.Side!, ct);
                    return;
                case PumpEnd.Stuck:
                    await AbandonStuckAsync(run, table, pumped.Side!, ct, pumped.SourceHeld);
                    return;
            }

            if (table.Outcome == CopyOutcome.Failed)
            {
                await RollbackTableAsync(run, table, ct);
                return;
            }

            if (run.Job.Options.Commit == CommitMode.PerTable)
            {
                // 커밋도 멈춤 표시·대기 한도 아래 보낸다(리뷰 PF1-4). 한도를 넘으면 취소한 뒤 테이블을 되돌려 본다(되돌리면 커밋이 되지 않은 것이다).
                var watched = await WatchTargetCommandAsync(run, table, estimated, CopyWaitStep.Commit,
                    token => ControlLoggedAsync(run, run.Target, CopyCommonStatements.Commit(), token, "테이블마다 커밋"), ct);
                if (watched.End == PumpEnd.Stuck)
                {
                    await AbandonStuckAsync(run, table, watched.Side, ct);
                    return;
                }

                if (watched.End == PumpEnd.WaitLimit
                    || (watched.Result is { Status: not ExecutionStatus.Succeeded } && watched.Side.LimitHit && !ct.IsCancellationRequested))
                {
                    await StopCommitAtWaitLimitAsync(run, table, watched.Side, ct);
                    return;
                }

                var commit = watched.Result!;
                if (RunState.Accept(commit, table, "커밋하지 못했습니다", ct))
                {
                    run.DeletesPersisted = true;
                    await ConfirmFailedRowsAsync(run, table);
                }
            }

            table.LoadCompleted = table.Outcome != CopyOutcome.Failed;
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            // 규칙·열 정보가 맞지 않음(마스킹 고정값 변환 실패, 키로 쓸 수 없는 형식 등). 이 표만 실패로 두고 다음 표로 간다.
            await RollbackTableAsync(run, table, ct);
            table.Fail(SafeError(ex.Message));
        }
        catch (BulkDeniedException ex)
        {
            await RollbackTableAsync(run, table, ct);
            table.Fail("정책이 원본 읽기를 거부했습니다: " + SafeError(ex.Message));
        }
        finally
        {
            table.Elapsed += sw.Elapsed;
            var end = LogTableEnd(run, table, sw.Elapsed);

            // 실행 기록(copy-runs)에도 바로 한 줄: 표 결과 줄은 마무리 때 한꺼번에 쓰므로 실행 중에는 이 줄로 어디까지 왔는지 안다(결정 70).
            await RecordEventAsync(run, "tableEnd", table.Name, end);

            // 적재를 마친 테이블만(실패·되돌린 테이블은 결과가 정한다): 막대는 예상 건수가 아니라 실제로 읽은 수로 가득 찬다.
            if (table.LoadCompleted)
            {
                run.ReportLoadEnd(table.Name, table.SourceRows, table.Loaded);
            }
        }
    }

    /// <summary>
    /// 표의 열 배치. 원본·대상 DBMS 가 같으면 예전 그대로(열 = 점검 열, 바인드 = 원본 형식, 값 변환 없음).
    /// 다르면 교차 복사 배치(<see cref="CrossTypeMap"/>: 옮길 형식이 없는 열 빼기, 바인드 = 원본 형식을 대상 DBMS 로 옮긴 형식, 값 = 이식 값을 거쳐 대상 코덱).
    /// </summary>
    private RowShape ShapeFor(RunState run, CopyTablePlan plan) =>
        _sourceDbms == _targetDbms
            ? RowShape.Create(_sourceCodec, run.Job, plan)
            : RowShape.Create(_sourceCodec, run.Job, plan, new CrossSides(_sourceDbms, _targetDbms, _codec));

    /// <summary>교차 복사에서 옮길 형식이 없어 뺀 열을 알림(작업 로그 경고 + 결과 알림) 한 줄로 남긴다. 같은 DBMS 면 아무것도 하지 않는다.</summary>
    private void LogDroppedColumns(RunState run, TableState table, RowShape shape)
    {
        if (shape.Dropped.Count == 0)
        {
            return;
        }

        var columns = string.Join(", ", shape.Dropped.Select(d => $"{d.Name}({d.DataType} — {DropReasonText(d.Reason)})"));
        run.Notices.Add($"{table.Name}: {_targetDbms} 로 옮길 형식이 없는 열을 빼고 복사합니다: {columns}.");
    }

    private static string DropReasonText(CrossDropReason reason) => reason switch
    {
        CrossDropReason.LongSource => "LONG·LONG RAW 는 대량 읽기를 지원하지 않음",
        CrossDropReason.RowId => "ROWID 는 다른 DB 에서 뜻이 없음",
        CrossDropReason.LocalTimeZone => "TIMESTAMP WITH LOCAL TIME ZONE 은 옮기지 않음",
        _ => "대량 읽기를 지원하지 않는 형식",
    };

    /// <summary>원본 SELECT(바인드 요약)·대상 INSERT/MERGE(표마다 한 번)·마스킹 열(이름과 방식만, 값은 남기지 않는다).</summary>
    private static void LogLoadStatements(RunState run, CopyTablePlan plan, RowShape shape, SqlWithBinds select, SqlStatement dml)
    {
        run.LogStatement(run.Source, select.Sql, select.Binds);
        run.Log(CopyLogText.Statement(run.SideOf(run.Target), dml.Text)
                + $"  [바인드 배열 :C1…:C{shape.CopyColumns.Count.ToString(CultureInfo.InvariantCulture)}, 배치 {run.Job.Options.BatchSize.ToString("N0", CultureInfo.InvariantCulture)}행씩]",
                CopyLogKind.Statement);
        var masks = plan.Rule.Masks.Where(m => shape.CopyColumns.Contains(m.Column, StringComparer.Ordinal)).ToList();
        if (masks.Count > 0)
        {
            run.Log("마스킹(앱에서 바꿔 보냄, 값은 남기지 않음): " + string.Join(", ", masks.Select(m => $"{m.Column}({MaskText(m)})")));
        }
    }

    private static string MaskText(MaskRule m) => m.Kind switch
    {
        MaskKind.Fixed => "고정값",
        MaskKind.KeepFirst => $"앞 {m.Count.ToString(CultureInfo.InvariantCulture)}자 남김",
        MaskKind.KeepLast => $"뒤 {m.Count.ToString(CultureInfo.InvariantCulture)}자 남김",
        MaskKind.Hash => "해시",
        _ => "NULL",
    };

    private static string LogTableEnd(RunState run, TableState table, TimeSpan elapsed)
    {
        var text = $"적재 끝 · 원본 {table.SourceRows.ToString("N0", CultureInfo.InvariantCulture)}건 읽음 · {table.Loaded.ToString("N0", CultureInfo.InvariantCulture)}행 넣음"
                   + (table.FailedRows > 0 ? $" · 실패 {table.FailedRows.ToString("N0", CultureInfo.InvariantCulture)}행" : string.Empty)
                   + (table.WasRolledBack ? " · 되돌림" : string.Empty)
                   + " · " + CopyLogText.Elapsed(elapsed)
                   // 시간 나눔(결정 74): 겹치기 켬·끔 전후 비교용. 적재 흐름을 지난 표만.
                   + (table.Pumped ? " · " + CopyLogText.Times(table.Times.ToTimes(table.Loaded, elapsed)) : string.Empty);
        run.Log(text, table.Outcome == CopyOutcome.Failed ? CopyLogKind.Error : CopyLogKind.Info, table.Name);
        return text;
    }

    /// <summary>
    /// 배치 하나를 보낸다. 실패하면 배치 세이브포인트로 되돌린 뒤 한 행씩 다시 넣어 실패 행을 찾는다(설계 8.2).
    /// 표를 더 진행하면 true, 그만두면(실패 행 상한 초과·거부) false.
    /// </summary>
    /// <param name="writer">대상 쓰기 감시(멈춤 표시·대기 한도): 일시 정지를 기다리는 동안은 쉬는 중, 배치를 보내는 동안은 응답을 기다리는 중으로 둔다.</param>
    private async Task<bool> FlushAsync(RunState run, TableState table, RowShape shape, SqlStatement dml,
                                        List<object?[]> batch, CopyRunControl control, SideWatch writer, CancellationToken ct)
    {
        if (StopForOtherLaneAbort(run, table))
        {
            return false; // 동시 복사(PF2): 다른 레인이 실행 전체를 멈췄다 — 이 배치를 보내지 않고 표를 멈춘다(늦어도 배치 하나)
        }

        await control.WaitIfPausedAsync(ct);
        writer.Begin(table.Batches + 1);
        var started = Stopwatch.GetTimestamp();
        try
        {
            return await SendBatchAsync(run, table, shape, dml, batch, writer, ct);
        }
        finally
        {
            writer.Idle();

            // 쓰기 시간 = 배치를 보낸 시간(세이브포인트·배열 DML·한 행씩 다시 넣기·배치 커밋)에서 값 변환 시간을 뺀 것(결정 74, 변환은 SendBatchAsync 가 따로 더한다).
            table.Times.AddWrite(started);
        }
    }

    /// <summary><see cref="FlushAsync"/> 의 본문(일시 정지를 지난 뒤). 한 행씩 다시 넣을 때는 행마다 감시 시각을 새로 잡는다(움직이는 중이면 멈춤이 아니다).</summary>
    private async Task<bool> SendBatchAsync(RunState run, TableState table, RowShape shape, SqlStatement dml,
                                            List<object?[]> batch, SideWatch writer, CancellationToken ct)
    {
        var perBatch = run.Job.Options.Commit == CommitMode.PerBatch;
        var pendingAtBatchStart = table.PendingFailed.Count;
        table.PendingAtBatchStart = pendingAtBatchStart; // 대기 한도로 이 배치를 되돌릴 때 쓴다(StopAtWaitLimitAsync)
        var savepoint = await ControlAsync(run.Target, _dialect.Savepoint(CopyCommonStatements.BatchSavepoint), ct);
        if (!RunState.Accept(savepoint, table, "배치를 시작하지 못했습니다", ct))
        {
            return false;
        }

        table.Batches++;

        // 같은 DBMS: 보낼 행 = 읽은 행 그대로. 교차: 값을 대상 DBMS 값으로 바꾼 새 행, 못 바꾼 행은 여기서 실패 행이 된다(키는 원본 값, D5).
        var rows = batch;
        var sources = batch;
        var conversionFailed = 0;
        if (shape.Converts)
        {
            var convertStarted = Stopwatch.GetTimestamp();
            long failedRowTime = 0; // 실패 행 처리(상한 초과면 ROLLBACK TO 왕복) = 대상 DB 시간: 변환에서 빼고 쓰기에 남긴다
            var converted = new List<object?[]>(batch.Count);
            var kept = new List<object?[]>(batch.Count);
            try
            {
                foreach (var source in batch)
                {
                    if (shape.TryToTarget(source, out var values, out var error))
                    {
                        converted.Add(values);
                        kept.Add(source);
                        continue;
                    }

                    var failedAt = Stopwatch.GetTimestamp();
                    var over = await AddFailedRowAsync(run, table, new FailedRow(table.Name, shape.KeyOf(source), error!), pendingAtBatchStart, ct);
                    failedRowTime += Stopwatch.GetTimestamp() - failedAt;
                    if (over)
                    {
                        return false;
                    }
                }
            }
            finally
            {
                // 상한 초과로 일찍 끝나도 변환 시간을 남긴다(쓰기 시간에서 빠진다, 결정 74·리뷰 PF1-5).
                table.Times.AddBatchConvert(convertStarted, failedRowTime);
            }

            rows = converted;
            sources = kept;
            conversionFailed = batch.Count - rows.Count;
            if (rows.Count == 0)
            {
                return await EndBatchAsync(run, table, 0, conversionFailed, perBatch, ct);
            }
        }

        var r = await _bulk.ExecuteArrayAsync(run.Target, dml, shape.Columns, rows, run.BulkOptions, ct);
        if (r.Status == ExecutionStatus.Succeeded)
        {
            table.Loaded += rows.Count;
        }
        else if (r.Status == ExecutionStatus.Failed)
        {
            run.Log($"배치 {table.Batches.ToString(CultureInfo.InvariantCulture)} 실패({Describe(r)}) → 배치 시작점으로 되돌리고 한 행씩 다시 넣습니다", CopyLogKind.Warning);
            // 배열 DML 이 실패하면 실패한 행 앞의 행이 트랜잭션에 남아 있을 수 있다.
            var back = await ControlLoggedAsync(run, run.Target, _dialect.RollbackToSavepoint(CopyCommonStatements.BatchSavepoint), ct);
            if (!RunState.Accept(back, table, "실패한 배치를 되돌리지 못했습니다", ct))
            {
                return false;
            }

            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                var one = await _bulk.ExecuteArrayAsync(run.Target, dml, shape.Columns, [row], run.BulkOptions, ct);
                writer.Touch();
                if (one.Status == ExecutionStatus.Succeeded)
                {
                    table.Loaded++;
                    continue;
                }

                if (one.Status != ExecutionStatus.Failed)
                {
                    RunState.Accept(one, table, "행을 넣지 못했습니다", ct);
                    return false;
                }

                // 실패 행 키는 원본 값으로 남긴다(교차 복사면 sources[i] 가 바꾸기 전 원본 행, 같은 DBMS 면 row 그대로).
                if (await AddFailedRowAsync(run, table, new FailedRow(table.Name, shape.KeyOf(sources[i]), ErrorText(one.Error)), pendingAtBatchStart, ct))
                {
                    return false;
                }
            }
        }
        else
        {
            RunState.Accept(r, table, "배치를 넣지 못했습니다", ct);
            return false;
        }

        return await EndBatchAsync(run, table, rows.Count, conversionFailed, perBatch, ct);
    }

    /// <summary>
    /// 실패 행 하나를 대기 목록에 넣고 작업 로그에 남긴다. 상한을 넘으면 이 배치를 되돌리고(이 배치의 대기 행도 뺀다) 표를 실패로 둔 뒤 true(그만둔다).
    /// </summary>
    private async Task<bool> AddFailedRowAsync(RunState run, TableState table, FailedRow failed, int pendingAtBatchStart, CancellationToken ct)
    {
        // 실패 행은 그 표의 적재가 확정(커밋)될 때 결과·실행 기록에 넣는다. 되돌린 적재의 실패 행으로
        // 「실패 행만 다시 넣기」를 하면 나머지 행이 빠진 표가 성공으로 보이기 때문이다.
        table.PendingFailed.Add(failed);
        table.FailedRows++;
        run.Log($"실패 행 {table.FailedRows.ToString("N0", CultureInfo.InvariantCulture)}: {failed.Error}", CopyLogKind.Error);
        if (table.FailedRows <= run.Job.Options.MaxFailedRows)
        {
            return false;
        }

        // 이 배치에서 한 행씩 넣은 행도 되돌린다(「배치마다 커밋」이면 표 세이브포인트가 없으므로 여기서만 되돌릴 수 있다).
        var undo = await ControlLoggedAsync(run, run.Target, _dialect.RollbackToSavepoint(CopyCommonStatements.BatchSavepoint), ct, "상한을 넘은 배치 되돌리기");
        if (undo.Status == ExecutionStatus.Succeeded)
        {
            // 이 배치의 실패 행만 뺀다. 앞 배치의 대기 행은 표 되돌리기가 성공할 때 함께 버린다(RolledBack).
            table.PendingFailed.RemoveRange(pendingAtBatchStart, table.PendingFailed.Count - pendingAtBatchStart);
        }
        else
        {
            run.Notices.Add($"{table.Name}: 상한을 넘은 배치를 되돌리지 못했습니다: " + Describe(undo));
        }

        table.Fail($"실패 행이 상한({run.Job.Options.MaxFailedRows.ToString(CultureInfo.InvariantCulture)})을 넘어 이 테이블을 멈췄습니다.");
        return true;
    }

    /// <summary>배치 끝: 작업 로그 한 줄, 「배치마다 커밋」이면 커밋하고 실패 행을 확정한다. 표를 더 진행하면 true.</summary>
    private async Task<bool> EndBatchAsync(RunState run, TableState table, int sent, int conversionFailed, bool perBatch, CancellationToken ct)
    {
        // sent = 실제로 보낸 행 수. 교차 복사에서 값 변환에 실패해 보내지 않은 행이 있으면 그 수를 덧붙인다(같은 DBMS 는 conversionFailed 가 0 이라 글이 그대로).
        var batchText = $"배치 {table.Batches.ToString(CultureInfo.InvariantCulture)} · {sent.ToString("N0", CultureInfo.InvariantCulture)}행 보냄"
                        + (conversionFailed > 0 ? $"(값 변환 실패 {conversionFailed.ToString("N0", CultureInfo.InvariantCulture)}행 제외)" : string.Empty)
                        + $" · 누적 {table.Loaded.ToString("N0", CultureInfo.InvariantCulture)}행";
        if (perBatch)
        {
            var commit = await ControlAsync(run.Target, CopyCommonStatements.Commit(), ct);
            if (!RunState.Accept(commit, table, "커밋하지 못했습니다", ct))
            {
                return false;
            }

            run.Log(batchText + " · COMMIT");

            run.DeletesPersisted = true;
            await ConfirmFailedRowsAsync(run, table);
        }
        else
        {
            run.Log(batchText);
        }

        return true;
    }

    /// <summary>
    /// 표 시작 세이브포인트로 되돌린다. 세이브포인트를 세우기 전이면(열 배치·문장을 만들다 실패) 되돌릴 것이 없다.
    /// 「배치마다 커밋」은 커밋이 세이브포인트를 지우므로 되돌릴 것이 없다.
    /// </summary>
    private async Task RollbackTableAsync(RunState run, TableState table, CancellationToken ct)
    {
        // 「배치마다 커밋」 적재는 커밋이 세이브포인트를 지운다. 다시 넣기는 커밋 단위와 상관없이 표마다 커밋하므로 되돌릴 수 있다.
        if (!table.SavepointSet || (run.Job.Options.Commit == CommitMode.PerBatch && !run.IsRetry))
        {
            return;
        }

        var token = ct.IsCancellationRequested ? CancellationToken.None : ct;
        var r = await ControlLoggedAsync(run, run.Target, _dialect.RollbackToSavepoint(CopyCommonStatements.TableSavepoint), token, "이 테이블의 적재분 되돌리기");
        if (r.Status != ExecutionStatus.Succeeded)
        {
            run.Notices.Add("실패한 테이블의 적재분을 되돌리지 못했습니다: " + Describe(r));
            return;
        }

        table.RolledBack();
    }

    /// <summary>
    /// 확정(커밋)된 표의 실패 행을 결과와 실행 기록에 넣는다. 「실패 행만 다시 넣기」는 이 목록만 쓴다.
    /// 기록 실패는 예외로 올리지 않는다(행은 이미 커밋됐고, 결과에는 남는다).
    /// </summary>
    private static async Task ConfirmFailedRowsAsync(RunState run, TableState table)
    {
        // 커밋은 이미 끝났다: 결과에 먼저 옮기고 확정 표시를 한 뒤 기록한다. 기록은 중지 토큰과 상관없는 새 토큰으로,
        // 실패(시간 초과 포함)는 여기서 가둔다(호출한 쪽의 마무리·다시 켜기를 끊지 않게).
        var rows = table.PendingFailed.ToList();
        table.PendingFailed.Clear();
        table.Confirmed = true;
        lock (run.FailedRows)
        {
            run.FailedRows.AddRange(rows); // 동시 복사(PF2)면 레인 여럿이 더한다
        }
        if (run.Record is not { } record || rows.Count == 0)
        {
            return;
        }

        try
        {
            using var step = new CancellationTokenSource(CleanupTimeout);
            foreach (var row in rows)
            {
                await record.RecordFailedRowAsync(row, step.Token);
            }
        }
        catch (Exception ex)
        {
            // 기록 호출만 감싼다: 어떤 예외든(권한·디스크 등) 마무리·다시 켜기를 끊지 않게 모두 가둔다(3차 리뷰 2).
            run.Notices.Add("실행 기록에 실패 행을 남기지 못했습니다: " + SafeError(ex.Message));
        }
    }

    /// <summary>
    /// 「같은 시점 기준 읽기」를 끈 경우: 표를 읽기 전마다 원본 트랜잭션을 닫고 읽기 전용 트랜잭션을 새로 연다
    /// (표마다 따로 읽되, 원본에는 언제나 DB 수준 READ ONLY 가 걸려 있게 한다).
    /// </summary>
    private async Task<bool> BeginTableReadAsync(RunState run, TableState table, CancellationToken ct)
    {
        var rollback = await ControlLoggedAsync(run, run.Source, CopyCommonStatements.Rollback(), ct);
        if (!RunState.Accept(rollback, table, "원본 트랜잭션을 정리하지 못했습니다", ct))
        {
            return false;
        }

        var set = await ControlLoggedAsync(run, run.Source, _sourceDialect.SetTransactionReadOnly(), ct, "이 테이블을 읽을 시점");
        return RunState.Accept(set, table, "원본에서 읽기 전용 트랜잭션을 시작하지 못했습니다", ct);
    }

    private string DmlFor(CopySqlBuilder builder, CopyTablePlan plan, string key, RowShape shape, bool freshTable = false)
    {
        // 사전 점검의 필요 권한(UPDATE)과 같은 판정을 쓴다. 이번에 새로 만든 빈 표(「없으면 만들기」)는 MERGE 가 필요 없어 INSERT 로 넣는다(AppendOnly 와 같다).
        var merge = !freshTable && PrecheckRules.UsesMerge(plan.Rule, plan.KeyColumns);
        if (!merge && !freshTable && plan.Rule.Action == TargetAction.MergeByPk)
        {
            throw new InvalidOperationException($"{key}: PK 기준 MERGE 에 쓸 키 열이 없습니다.");
        }

        // 값 자리 글은 방언이 정한다(바인드 이름·바인드 형식 = 행 배치의 열). Oracle 은 :Cn 그대로라 글이 바뀌지 않는다.
        // 대상에 보내는 DML 이라 대상 방언이다. 형식은 같은 DBMS 면 원본 열 형식, 교차면 원본 형식을 대상 DBMS 로 옮긴 형식(D3)이다.
        // 열은 행 배치의 복사 열이다(교차 복사에서 옮길 형식이 없는 열은 빠졌다).
        // 교차는 교차 쓰기 값 자리(ICopyDialect.CrossBindPlaceholder): Tibero 는 TZ 를 같은 DBMS 값 자리로는 받지 않는다(win144 #137).
        string ValueAt(int i) => _sourceDbms != _targetDbms
            ? _dialect.CrossBindPlaceholder(shape.Columns[i].BindName, shape.Columns[i].ServerType)
            : _dialect.BindPlaceholder(shape.Columns[i].BindName, shape.Columns[i].ServerType);
        return merge ? builder.Merge(key, shape.CopyColumns, plan.KeyColumns, ValueAt) : builder.Insert(key, shape.CopyColumns, ValueAt);
    }

    // ---- 「실패 행만 다시 넣기」 ----

    private async Task RetryTableAsync(RunState run, string tableName, IReadOnlyList<FailedRow> rows, CancellationToken ct)
    {
        var plan = run.Report.Order.FirstOrDefault(p => string.Equals(run.Key(p), tableName, StringComparison.Ordinal));
        if (plan is null)
        {
            run.Notices.Add($"{tableName}: 사전 점검 결과에 없는 표라 다시 넣지 않았습니다.");
            return;
        }

        var table = run.Table(plan);
        var sw = Stopwatch.StartNew();
        try
        {
            if (plan.KeyColumns.Count == 0)
            {
                table.Fail("키 열이 없어 실패 행을 다시 읽을 수 없습니다.");
                return;
            }

            var shape = ShapeFor(run, plan);
            var dml = CopyCommonStatements.ArrayDml(DmlFor(run.Builder, plan, tableName, shape), shape.CopyColumns.Count);
            var query = CopyCommonStatements.Query(run.Builder.SourceSelectByKeys(tableName, shape.CopyColumns, plan.KeyColumns));
            run.Progress(tableName, 0, rows.Count, PhaseRetry);
            LogDroppedColumns(run, table, shape);
            run.Log(CopyLogText.Statement(run.SideOf(run.Source), query.Text)
                    + $"  [바인드 키 :K1…:K{plan.KeyColumns.Count.ToString(CultureInfo.InvariantCulture)}, {rows.Count.ToString("N0", CultureInfo.InvariantCulture)}행을 한 행씩]", CopyLogKind.Statement);
            run.Log(CopyLogText.Statement(run.SideOf(run.Target), dml.Text), CopyLogKind.Statement);
            var savepoint = await ControlLoggedAsync(run, run.Target, _dialect.Savepoint(CopyCommonStatements.TableSavepoint), ct, "이 테이블을 되돌릴 자리");
            if (!RunState.Accept(savepoint, table, "다시 넣기를 시작하지 못했습니다", ct))
            {
                return;
            }

            table.SavepointSet = true;
            foreach (var failedRow in rows)
            {
                run.Progress(tableName, table.SourceRows, rows.Count, PhaseRetry);
                var binds = shape.KeyBinds(failedRow);
                object?[]? source = null;
                await foreach (var row in _bulk.StreamRowsAsync(run.Source, query, binds, SourceReadOptions(run), ct))
                {
                    source = shape.Apply(row);
                }

                table.SourceRows++;
                if (source is null)
                {
                    // 목록에서 빼지 않는다: 사용자가 그 행이 왜 안 들어갔는지 결과에서 봐야 한다.
                    table.PendingFailed.Add(failedRow with { Error = "원본에 이 키의 행이 없습니다(그 사이 지워졌거나 키 값이 바뀌었습니다)." });
                    table.FailedRows++;
                    continue;
                }

                // 교차 복사: 적재와 같은 값 변환. 못 바꾸면 그 행만 다시 실패 행으로 남긴다(D5).
                var values = source;
                if (shape.Converts && !shape.TryToTarget(source, out values, out var conversionError))
                {
                    table.PendingFailed.Add(failedRow with { Error = conversionError! });
                    table.FailedRows++;
                    continue;
                }

                var one = await _bulk.ExecuteArrayAsync(run.Target, dml, shape.Columns, [values], run.BulkOptions, ct);
                if (one.Status == ExecutionStatus.Succeeded)
                {
                    table.Loaded++;
                    continue;
                }

                if (one.Status != ExecutionStatus.Failed)
                {
                    RunState.Accept(one, table, "행을 넣지 못했습니다", ct);
                    await RollbackTableAsync(run, table, ct);
                    return;
                }

                table.PendingFailed.Add(failedRow with { Error = ErrorText(one.Error) });
                table.FailedRows++;
            }

            // 다시 넣기는 표마다 커밋한다. 커밋하지 못한 표의 입력 행은 끝낼 때 그대로 돌려준다(FinishAsync).
            var commit = await ControlLoggedAsync(run, run.Target, CopyCommonStatements.Commit(), ct, "테이블마다 커밋");
            // 연결 끊김, 또는 보내는 도중 중지로 취소되면 서버에서 확정됐는지 알 수 없다.
            table.CommitUnknown = (commit.Status == ExecutionStatus.Failed && commit.Error is { Kind: DbErrorKind.Disconnected })
                                  || (commit.Status == ExecutionStatus.Cancelled && ct.IsCancellationRequested);
            if (RunState.Accept(commit, table, "커밋하지 못했습니다", ct))
            {
                await ConfirmFailedRowsAsync(run, table);
                table.LoadCompleted = true;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or FormatException)
        {
            await RollbackTableAsync(run, table, ct);
            table.Fail(SafeError(ex.Message));
        }
        catch (BulkDeniedException ex)
        {
            await RollbackTableAsync(run, table, ct);
            table.Fail("정책이 원본 읽기를 거부했습니다: " + SafeError(ex.Message));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && CopyErrors.IsServer(_errors, ex))
        {
            await RollbackTableAsync(run, table, ct);
            table.Fail("원본을 다시 읽지 못했습니다: " + SafeError(ex.Message));
        }
        finally
        {
            table.Elapsed += sw.Elapsed;
            var end = LogTableEnd(run, table, sw.Elapsed);

            // 실행 기록(copy-runs)에도 바로 한 줄: 표 결과 줄은 마무리 때 한꺼번에 쓰므로 실행 중에는 이 줄로 어디까지 왔는지 안다(결정 70).
            await RecordEventAsync(run, "tableEnd", table.Name, end);
        }
    }

    // ---- 4. 시퀀스 보정 ----

    private async Task AdvanceSequencesAsync(RunState run, bool proceedLargeGap, CancellationToken ct)
    {
        // 읽기 전용 대상은 NEXTVAL 조회도 시퀀스 상태를 바꾸므로 보정하지 않는다(조회로 분류돼 정책을 지난다).
        if (run.Target.Profile.ReadOnly)
        {
            return;
        }

        foreach (var rule in run.Job.Sequences)
        {
            // 적재를 끝내고 되돌리지 않은 표만 보정한다(실패·되돌린 표의 시퀀스는 그대로 둔다).
            if (!run.Tables.TryGetValue(rule.Table, out var loaded)
                || !loaded.LoadCompleted || loaded.WasRolledBack || loaded.Outcome == CopyOutcome.Failed || loaded.Loaded == 0)
            {
                continue;
            }

            run.Progress(rule.Table, 0, null, PhaseSequence);
            // 시퀀스는 그 계정(없으면 표의 대상 계정), 최대값은 표의 대상 계정에서 읽는다.
            var owner = CopyOwners.SequenceOwner(run.Job, rule);
            try
            {
                var next = _dialect.NextValues(owner, rule.Sequence);
                var maxQuery = CopyCommonStatements.Query($"SELECT MAX({SqlIdentifier.QuoteAlways(rule.Column)}) FROM {CopyCommonStatements.Qualified(loaded.Owner, loaded.TableName)}");
                run.LogStatement(run.Target, maxQuery.Text);
                var maxCell = await ScalarAsync(run, run.Target, maxQuery, NoBinds, ct);
                if (_codec.NumberText(maxCell) is not { } maxText)
                {
                    run.Log("→ 대상 테이블이 비어 보정하지 않습니다");
                    continue; // 대상 표가 비었다.
                }

                run.Log("→ 최대값 " + maxText);

                var info = await RowsAsync(run, run.Target, CopyCommonStatements.Query(CopyCatalogQueries.Sql(_catalog, run.Target.Server, "copy.sequence_increment")),
                    new Dictionary<string, BindValue>
                    {
                        ["OWNER"] = new(BindDataType.Varchar2, owner),
                        ["NAME"] = new(BindDataType.Varchar2, rule.Sequence),
                    }, ct);
                if (info.Count == 0 || _codec.NumberText(info[0][0]) is null)
                {
                    run.Notices.Add($"시퀀스 {rule.Sequence}: SELECT 권한이 없어 보정하지 않았습니다. 테스트 중 PK 충돌이 날 수 있습니다.");
                    continue;
                }

                long need;
                try
                {
                    var step = _codec.ToInt64Exact(info[0][0]);
                    var max = _codec.ToInt64Exact(maxCell);

                    // 다음 값은 LAST_NUMBER − 캐시 × 증가값 보다 작을 수 없다. 그것이 이미 최대값보다 크면 NEXTVAL 을 하나도 쓰지 않는다.
                    // RAC NOORDER 는 다른 인스턴스 캐시에 더 작은 값이 남을 수 있어 이 하한을 쓰지 않는다.
                    // 숫자인지 먼저 보고 long 으로는 step > 0 뒤에 바꾼다(바꾸다 넘치면 「값이 너무 큼」, 예전 순서 그대로).
                    if (!run.Target.Server.IsRac && _codec.NumberText(info[0][1]) is not null && _codec.NumberText(info[0][2]) is not null
                        && step > 0 && checked(_codec.ToInt64Exact(info[0][1]) - (_codec.ToInt64Exact(info[0][2]) * step)) > max)
                    {
                        continue;
                    }

                    var first = await ScalarAsync(run, run.Target, next, CountBind(1), ct);
                    if (_codec.NumberText(first) is null)
                    {
                        continue;
                    }

                    need = SequenceAdvance.Plan(checked(_codec.ToInt64Exact(first) + step), max, step);
                }
                catch (OverflowException)
                {
                    run.Notices.Add($"시퀀스 {rule.Sequence}: 값이 너무 커서 보정하지 않았습니다.");
                    continue;
                }

                if (need >= run.Job.Options.SequenceGapConfirm && !proceedLargeGap)
                {
                    run.Notices.Add($"시퀀스 {rule.Sequence}: 모자란 수({need.ToString(CultureInfo.InvariantCulture)})가 커서 보정을 건너뛰었습니다.");
                    continue;
                }

                if (need > 0)
                {
                    var rounds = (need + SequenceAdvance.ChunkSize - 1) / SequenceAdvance.ChunkSize;
                    run.Log(CopyLogText.Statement(run.SideOf(run.Target), next.Text)
                            + $"  [:{CopyCommonStatements.NextValuesBind} = 한 번에 최대 {SequenceAdvance.ChunkSize.ToString("N0", CultureInfo.InvariantCulture)}, {rounds.ToString("N0", CultureInfo.InvariantCulture)}회 · 모두 {need.ToString("N0", CultureInfo.InvariantCulture)}개]",
                            CopyLogKind.Statement);
                }
                else
                {
                    run.Log("→ 시퀀스가 이미 최대값 뒤에 있어 보정하지 않습니다");
                }

                for (var left = need; left > 0;)
                {
                    var chunk = Math.Min(left, SequenceAdvance.ChunkSize);
                    await foreach (var _ in _bulk.StreamRowsAsync(run.Target, next, CountBind(chunk), run.BulkOptions, ct))
                    {
                        // 행을 끝까지 읽어야 그만큼 소비된다.
                    }

                    left -= chunk;
                }

                if (need > 0)
                {
                    run.Notices.Add($"시퀀스 {rule.Sequence}: NEXTVAL {need.ToString(CultureInfo.InvariantCulture)}개를 소비해 {rule.Table}.{rule.Column} 최대값 뒤로 옮겼습니다.");
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && CopyErrors.IsServer(_errors, ex))
            {
                run.Notices.Add($"시퀀스 {rule.Sequence}: 보정하지 못했습니다({SafeError(ex.Message)}). 테스트 중 PK 충돌이 날 수 있습니다.");
            }
            catch (BulkDeniedException ex)
            {
                run.Notices.Add($"시퀀스 {rule.Sequence}: 정책이 거부해 보정하지 않았습니다({SafeError(ex.Message)}).");
            }
        }
    }

    private static Dictionary<string, BindValue> CountBind(long n) =>
        new() { [CopyCommonStatements.NextValuesBind] = new BindValue(BindDataType.Number, (decimal)n) };

    // ---- 5. 끝내기(트랜잭션 → 다시 켜기 → 건수 → 기록) ----

    /// <summary>
    /// 중지·예외 뒤에도 반드시 지나간다. <c>ct</c> 를 쓰지 않고 단계마다 새 토큰을 쓴다(한 단계가 시간을 다 써도 다음 단계는 돈다).
    /// 대상 트랜잭션을 끝내지 못했으면 다시 켜기(DDL, 묵시 커밋)를 하지 않는다: 켜면 되돌리려던 행이 확정된다.
    /// </summary>
    private async Task FinishAsync(RunState run, bool faulted)
    {
        run.Enter(PhaseFinish);
        if (run.Stopped)
        {
            run.Log("중지 요청을 받았습니다. 대상 변경을 되돌리고 끈 것을 다시 켭니다.", CopyLogKind.Warning);
        }
        else if (faulted)
        {
            run.Log("예상하지 못한 오류로 멈춥니다. 대상 변경을 되돌리고 끈 것을 다시 켭니다.", CopyLogKind.Error);
        }

        var ended = false;
        try
        {
            // 끝내지 못한 레인(PF2)이 있으면 그 트랜잭션이 남아 다시 켜기(DDL)를 막는다: 끝내지 못한 것으로 둔다.
            ended = await EndTargetTransactionAsync(run, faulted) && !run.LaneLeftOpen;
        }
        catch (Exception ex) when (IsCleanupFailure(ex))
        {
            run.Notices.Add("대상 트랜잭션을 끝내지 못했습니다: " + SafeError(ex.Message));
        }

        if (ended)
        {
            await PurgeUnloadedNewTablesAsync(run);
            run.EnableFailed = !await EnableAsync(run);
            await RestoreAsync(run);
        }
        else
        {
            if (run.Tables.Values.Any(t => t.CreatedNew && (!t.LoadCompleted || run.Job.Options.Commit == CommitMode.AtEnd)))
            {
                // 지우는 DROP 도 DDL(묵시 커밋)이라 대상 트랜잭션을 끝내지 못했으면 보내지 않는다.
                var left = run.Tables.Values.Where(t => t.CreatedNew && (!t.LoadCompleted || run.Job.Options.Commit == CommitMode.AtEnd)).Select(t => t.TargetName);
                run.Notices.Add("대상 트랜잭션을 끝내지 못해 적재를 끝내지 못한 새 테이블을 지우지 않았습니다. 세션을 정리한 뒤 직접 지우세요(DROP … PURGE): " + string.Join(", ", left));
            }

            if (run.Disabled.Count > 0)
            {
                run.EnableFailed = true;
                run.Notices.Add("대상 트랜잭션을 끝내지 못해 끈 트리거·FK 를 다시 켜지 않았습니다(켜면 미커밋 행이 확정됩니다). 세션을 정리한 뒤 직접 켜세요: "
                                + string.Join(", ", run.Disabled.Select(d => d.Owner + "." + d.Name)));
            }

            if (run.Tables.Values.Any(t => (t.Created && t.Preserved is { } p && p.Triggers.Count + p.Grants.Count + p.ReferencingForeignKeys.Count > 0)
                                           || (!t.Dropped && t.DetachedReferencing.Count > 0)))
            {
                // 되살리기도 DDL 이라 미커밋 행을 확정한다. 문장 원문은 NoteRecreated 가 표마다 남긴다.
                run.RestoreFailed = true;
                run.Notices.Add("대상 트랜잭션을 끝내지 못해 다시 만든 테이블의 트리거·권한·참조 FK 를 되살리지 않았습니다. 세션을 정리한 뒤 아래 DDL 을 직접 실행하세요.");
            }
        }

        NoteRecreated(run, restored: ended);

        if (run.SourceAbandoned)
        {
            // 돌아오지 않는 원본 명령이 세션을 쥐고 있다: 보내도 차례를 못 받는다. 세션을 닫으면 읽기 전용 트랜잭션도 끝난다.
            run.Notices.Add("원본 세션의 명령이 돌아오지 않아 원본 읽기 전용 트랜잭션을 닫지 않았습니다. 복사 창을 닫아 세션을 정리하세요.");
        }
        else
        {
            try
            {
                using var step = new CancellationTokenSource(CleanupTimeout);
                run.Enter(PhaseFinish);
                var rollback = await ControlLoggedAsync(run, run.Source, CopyCommonStatements.Rollback(), step.Token, "원본 읽기 전용 트랜잭션 닫기");
                if (rollback.Status != ExecutionStatus.Succeeded)
                {
                    run.Notices.Add("원본 읽기 전용 트랜잭션을 닫지 못했습니다: " + Describe(rollback));
                }
            }
            catch (Exception ex) when (IsCleanupFailure(ex))
            {
                run.Notices.Add("원본 읽기 전용 트랜잭션을 닫지 못했습니다: " + SafeError(ex.Message));
            }
        }

        if (!run.IsRetry)
        {
            // 적재를 끝내지 못한 표(상한 초과·중지·ORA-01555)는 「배치마다 커밋」이면 앞 배치의 실패 행이 확정돼 있다.
            // 그 행만 다시 넣으면 나머지 배치가 빠진 표가 성공으로 보이므로 결과에서 빼고 다시 실행하라고 적는다.
            // 실행 기록에는 앞 배치 커밋 때 쓴 failedRow 줄이 남는다(기록에 무효 표시 메서드가 없다). 그 표의 표 결과 메시지
            // (「적재를 끝내지 못해 … 다시 실행하세요」)가 그 줄들을 덮는다는 것이 기록의 계약이다(3차 리뷰 1, T13 문서).
            foreach (var t in run.Tables.Values.Where(t => !t.LoadCompleted))
            {
                if (run.FailedRows.RemoveAll(r => r.Table == t.Name) > 0)
                {
                    t.Note("적재를 끝내지 못해 「실패 행만 다시 넣기」를 쓸 수 없습니다. 다시 실행하세요.");
                }
            }
        }

        if (run.IsRetry)
        {
            // 확정(커밋)하지 못한 표의 입력 행은 사라지지 않게 그대로 돌려준다(중지·실패·시작하지 못한 표).
            var confirmed = run.Tables.Values.Where(t => t.Confirmed).Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
            // 커밋 도중 연결이 끊긴 표는 확정됐는지 알 수 없다: 행은 돌려주되 이미 들어갔을 수 있다고 적는다(3차 리뷰 3).
            var unknown = run.Tables.Values.Where(t => t.CommitUnknown).Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
            var unconfirmed = run.RetryInputs.Where(r => !confirmed.Contains(r.Table))
                .Select(r => unknown.Contains(r.Table) ? r with { Error = "확정 여부 모름(커밋 중 연결이 끊김, 이미 들어갔을 수 있음): " + r.Error } : r)
                .ToList();
            run.FailedRows.AddRange(unconfirmed);
            if (run.Record is { } retryRecord)
            {
                try
                {
                    using var step = new CancellationTokenSource(CleanupTimeout);
                    foreach (var row in unconfirmed)
                    {
                        await retryRecord.RecordFailedRowAsync(row, step.Token);
                    }
                }
                catch (Exception ex)
                {
                    run.Notices.Add("실행 기록에 실패 행을 남기지 못했습니다: " + SafeError(ex.Message));
                }
            }

            // 다시 넣기는 기간 조건 없이 키로 읽으므로 「같은 조건의 대상 건수」가 없다. 넣은 행 수를 보인다.
            foreach (var t in run.Tables.Values)
            {
                t.TargetRows = t.Loaded;
            }
        }
        else if (!run.Stopped && !faulted && !run.TargetAbandoned)
        {
            using var step = new CancellationTokenSource(LongCleanupTimeout);
            await CountTargetsAsync(run, step.Token);
        }

        NoteDeletedButNotLoaded(run);
        LogEnd(run, faulted);

        if (run.Record is not { } record)
        {
            return;
        }

        try
        {
            using var step = new CancellationTokenSource(CleanupTimeout);
            foreach (var t in run.Tables.Values)
            {
                await record.RecordTableAsync(t.ToResult(), step.Token);
            }

            // 다시 켜지 못한 객체가 있으면 기록을 끝내지 않는다: 다음에 작업을 열 때 「다시 켜기」 목록으로 보인다(설계 9장).
            if (!run.EnableFailed)
            {
                await record.CompleteAsync(run.Outcome(faulted), step.Token);
            }
        }
        catch (Exception ex)
        {
            run.Notices.Add("실행 기록을 마무리하지 못했습니다: " + SafeError(ex.Message));
        }
    }

    private bool IsCleanupFailure(Exception ex) =>
        CopyErrors.IsServer(_errors, ex) || ex is OperationCanceledException or InvalidOperationException or IOException or BulkDeniedException;

    /// <summary>작업 로그 끝줄: 결과·표 수·넣은 행·실패 행·걸린 시간.</summary>
    private static void LogEnd(RunState run, bool faulted)
    {
        var outcome = run.Outcome(faulted);
        var results = run.Tables.Values.Select(t => t.ToResult()).ToList();
        var text = (outcome switch
                   {
                       CopyOutcome.Succeeded => "끝: 성공",
                       CopyOutcome.PartiallyFailed => "끝: 일부 실패",
                       CopyOutcome.Stopped => "끝: 중지",
                       _ => "끝: 실패",
                   })
                   + $" · 테이블 {results.Count.ToString(CultureInfo.InvariantCulture)}개(성공 {results.Count(r => r.Outcome == CopyOutcome.Succeeded).ToString(CultureInfo.InvariantCulture)})"
                   + $" · {run.Tables.Values.Sum(t => t.Loaded).ToString("N0", CultureInfo.InvariantCulture)}행 넣음"
                   + $" · 실패 행 {run.FailedRows.Count.ToString("N0", CultureInfo.InvariantCulture)}"
                   + " · 걸린 시간 " + CopyLogText.Elapsed(run.Clock.Elapsed);
        run.Enter(PhaseFinish);
        run.Log(text, outcome is CopyOutcome.Succeeded ? CopyLogKind.Info : outcome is CopyOutcome.Failed ? CopyLogKind.Error : CopyLogKind.Warning);
    }

    /// <summary>
    /// 정리(DELETE)는 모든 표에 먼저 하므로(설계 8.2, 자식 → 부모), 한 표의 커밋이 다른 표의 DELETE 도 확정한다.
    /// 적재를 끝내지 못한 표의 DELETE 가 확정됐으면 그 사실을 표 결과와 알림에 적는다(다시 실행하면 같은 결과가 된다).
    /// </summary>
    private static void NoteDeletedButNotLoaded(RunState run)
    {
        if (run.Stopped)
        {
            foreach (var t in run.Tables.Values.Where(t => !t.LoadCompleted))
            {
                t.MarkStopped();
            }
        }

        if (!run.DeletesPersisted)
        {
            return;
        }

        // 다시 만들지 못한 표(표가 없음)는 다시 실행해도 사전 점검이 막는다: 휴지통 안내(NoteRecreated)만 한다.
        var tables = run.Tables.Values.Where(t => t.Deleted && !t.LoadCompleted && !(t.Recreate && !t.Created)).ToList();
        foreach (var t in tables)
        {
            if (t.Recreate)
            {
                if (t.Created)
                {
                    t.Degrade("테이블을 지우고 다시 만들었지만 적재를 끝내지 못했습니다(비어 있거나 일부만 들어 있습니다). 다시 실행하거나 휴지통에서 되돌리세요.");
                }
            }
            else
            {
                t.Note("대상 정리(DELETE)는 확정됐지만 적재를 끝내지 못했습니다. 다시 실행하세요.");
            }
        }

        if (tables.Count > 0)
        {
            run.Notices.Add("대상 행을 지운 뒤 적재를 끝내지 못한 테이블이 있습니다(" + string.Join(", ", tables.Select(t => t.Name))
                            + "). 다시 실행하면 같은 결과가 됩니다.");
        }
    }

    /// <summary>다시 켜기(DDL, 묵시 커밋) 전에 대상 트랜잭션을 끝낸다. 끝났으면(커밋 또는 롤백 성공) true.</summary>
    private async Task<bool> EndTargetTransactionAsync(RunState run, bool faulted)
    {
        if (run.TargetAbandoned)
        {
            // 취소에도 돌아오지 않는 대상 명령이 세션을 쥐고 있다(AbandonStuckAsync): 커밋·롤백을 보내도 차례를 못 받는다.
            // 끝내지 못한 것으로 두어 다시 켜기(DDL, 묵시 커밋)도 하지 않는다. 세션을 닫으면 커밋하지 않은 적재는 되돌아간다.
            run.Notices.Add("대상 세션의 명령이 돌아오지 않아 대상 트랜잭션을 끝내지 못했습니다. 복사 창을 닫아 세션을 정리하면 커밋하지 않은 변경은 되돌아갑니다.");
            return false;
        }

        // 전체 중단(원본 준비·끄기 실패, ORA-01555)도 되돌린다: 커밋하면 아직 적재하지 않은 뒤쪽 표의 DELETE 까지 확정된다.
        var rollback = run.Stopped || faulted || run.Aborted
                       || (run.Job.Options.Commit == CommitMode.AtEnd && run.Tables.Values.Any(t => t.Outcome == CopyOutcome.Failed));
        if (!rollback)
        {
            using var commitStep = new CancellationTokenSource(CleanupTimeout);
            var commit = await ControlLoggedAsync(run, run.Target, CopyCommonStatements.Commit(), commitStep.Token, "대상 트랜잭션 끝내기");
            if (commit.Status == ExecutionStatus.Succeeded)
            {
                run.DeletesPersisted = true;
                foreach (var t in run.Tables.Values.Where(t => !t.WasRolledBack && (t.PendingFailed.Count > 0 || t.LoadCompleted)))
                {
                    await ConfirmFailedRowsAsync(run, t);
                }

                return true;
            }

            run.CommitFailed = true;
            run.Notices.Add("대상 커밋을 하지 못해 되돌립니다: " + Describe(commit));
        }

        // 큰 적재를 되돌리는 ROLLBACK 은 오래 걸릴 수 있다.
        using var step = new CancellationTokenSource(LongCleanupTimeout);
        var r = await ControlLoggedAsync(run, run.Target, CopyCommonStatements.Rollback(), step.Token, "확정하지 않은 대상 변경 되돌리기");
        if (r.Status != ExecutionStatus.Succeeded)
        {
            run.Notices.Add("대상 롤백을 하지 못했습니다: " + Describe(r));
            return false;
        }

        run.TargetRolledBack = true;

        // 확정되지 않은 적재는 모두 되돌아갔다: 그 실패 행은 버린다.
        foreach (var t in run.Tables.Values.Where(t => t.PendingFailed.Count > 0 || (!t.Confirmed && t.FailedRows > 0)))
        {
            t.RolledBack();
        }

        // 다시 만든 표는 DROP·CREATE 가 되돌아가지 않는다: 적재까지 끝냈지만 확정하지 못한 표는 이제 비어 있다(중지여도 알린다).
        // 새로 만든 표(「없으면 만들기」)는 옛 표가 없다: 비어 있다고만 알린다(끝낼 때 적재를 끝내지 못한 표는 지운다, PurgeUnloadedNewTablesAsync).
        foreach (var t in run.Tables.Values.Where(t => t.CreatedNew && t.LoadCompleted && !t.Confirmed && run.Job.Options.Commit != CommitMode.AtEnd))
        {
            t.Degrade("적재를 되돌려 이번에 만든 테이블이 비어 있습니다. 다시 실행하세요(테이블이 있으므로 만들지 않고 규칙대로 넣습니다).");
        }

        var emptied = run.Tables.Values.Where(t => t.Created && !t.CreatedNew && t.LoadCompleted && !t.Confirmed).ToList();
        foreach (var t in emptied)
        {
            t.Degrade("적재를 되돌려 다시 만든 테이블이 비어 있습니다(DROP·CREATE 는 되돌릴 수 없습니다). 옛 행은 휴지통의 옛 테이블에 있습니다. 다시 실행하세요.");
        }

        if (emptied.Count > 0)
        {
            run.Notices.Add("다시 만든 테이블(" + string.Join(", ", emptied.Select(t => t.Name))
                            + ")는 적재를 되돌려 비어 있습니다. DROP·CREATE 는 되돌릴 수 없어 옛 행은 휴지통의 옛 테이블에 있습니다.");
        }

        if (run.Job.Options.Commit == CommitMode.AtEnd && !run.Stopped)
        {
            run.Notices.Add(run.Tables.Values.Any(t => t.Created)
                ? "「모두 끝난 뒤 한 번 커밋」이라 실패 때문에 적재를 되돌렸습니다(다시 만든 테이블의 DROP·CREATE 는 되돌리지 못합니다)."
                : "「모두 끝난 뒤 한 번 커밋」이라 실패 때문에 모든 변경을 되돌렸습니다.");
            run.RolledBackAll = true;
        }

        return true;
    }

    /// <summary>
    /// 끈 순서의 반대로 켠다. FK 는 VALIDATE 가 실패하면 NOVALIDATE 로 켜고 부모 없는 행 수를 알린다. 모두 켰으면 true.
    /// 객체마다 따로 토큰을 주고 예외를 가둔다: 하나가 실패해도(시간 초과·기록 실패 포함) 나머지는 켠다.
    /// </summary>
    private async Task<bool> EnableAsync(RunState run)
    {
        var allEnabled = true;
        for (var i = run.Disabled.Count - 1; i >= 0; i--)
        {
            var obj = run.Disabled[i];
            run.Progress(run.DisabledKeys.GetValueOrDefault(obj, obj.Table), 0, null, PhaseEnable); // 끌 때와 같은 규칙 키
            try
            {
                if (obj.Kind == DisabledKind.Trigger)
                {
                    var r = await CleanupStatementAsync(run, _dialect.EnableTrigger(obj.Owner, obj.Name), CleanupTimeout);
                    if (r.Status != ExecutionStatus.Succeeded)
                    {
                        allEnabled = false;
                        run.Notices.Add($"트리거 {obj.Owner}.{obj.Name} 을(를) 다시 켜지 못했습니다. 직접 켜세요: {Describe(r)}");
                    }

                    continue;
                }

                // VALIDATE 는 자식 표 전체를 읽으므로 오래 걸릴 수 있다.
                var validated = await CleanupStatementAsync(run, _dialect.EnableConstraint(obj, validate: true), LongCleanupTimeout);
                if (validated.Status == ExecutionStatus.Succeeded)
                {
                    continue;
                }

                var loose = await CleanupStatementAsync(run, _dialect.EnableConstraint(obj, validate: false), CleanupTimeout);
                if (loose.Status != ExecutionStatus.Succeeded)
                {
                    allEnabled = false;
                    run.Notices.Add($"FK {obj.Owner}.{obj.Name} 을(를) 다시 켜지 못했습니다. 직접 켜세요: {Describe(loose)}");
                    continue;
                }

                using var count = new CancellationTokenSource(LongCleanupTimeout);
                var orphans = await OrphanCountAsync(run, obj, count.Token);
                run.Notices.Add($"FK {obj.Owner}.{obj.Name}: 부모 없는 행 {orphans} 때문에 ENABLE NOVALIDATE 로 켰습니다.");
            }
            catch (Exception ex) when (IsCleanupFailure(ex))
            {
                allEnabled = false;
                run.Notices.Add($"{obj.Owner}.{obj.Name} 을(를) 다시 켜지 못했습니다. 직접 켜세요: {SafeError(ex.Message)}");
            }
        }

        return allEnabled;
    }

    /// <summary>정리 문장 하나를 자기 토큰으로 실행하고, 성공하면 실행 기록에 남긴다. 기록 실패는 문장 결과를 바꾸지 않는다.</summary>
    private async Task<ExecutionResult> CleanupStatementAsync(RunState run, SqlStatement stmt, TimeSpan timeout)
    {
        using var step = new CancellationTokenSource(timeout);
        run.LogStatement(run.Target, stmt.Text);
        var r = await _execution.ExecuteAsync(run.Target, stmt, NoBinds, run.HistoryOptions, step.Token);
        LogResult(run, r);
        if (r.Status == ExecutionStatus.Succeeded && run.Record is { } record)
        {
            try
            {
                await record.RecordStatementAsync(stmt.Text, step.Token);
            }
            catch (Exception ex)
            {
                run.Notices.Add("실행 기록에 문장을 남기지 못했습니다: " + SafeError(ex.Message));
            }
        }

        return r;
    }

    private async Task<string> OrphanCountAsync(RunState run, DisabledObject fk, CancellationToken token)
    {
        try
        {
            var cols = await RowsAsync(run, run.Target, CopyCommonStatements.Query(CopyCatalogQueries.Sql(_catalog, run.Target.Server, "copy.fk_columns")),
                new Dictionary<string, BindValue>
                {
                    ["OWNER"] = new(BindDataType.Varchar2, fk.Owner),
                    ["NAME"] = new(BindDataType.Varchar2, fk.Name),
                }, token);
            if (cols.Count == 0)
            {
                return "수 모름";
            }

            var child = cols.Select(c => SqlIdentifier.QuoteAlways((string)c[0]!)).ToList();
            var parent = cols.Select(c => SqlIdentifier.QuoteAlways((string)c[1]!)).ToList();
            var parentTable = CopyCommonStatements.Qualified((string)cols[0][2]!, (string)cols[0][3]!);
            var notNull = string.Join(" AND ", child.Select(c => $"c.{c} IS NOT NULL"));
            var join = string.Join(" AND ", child.Select((c, i) => $"p.{parent[i]} = c.{c}"));
            var sql = $"SELECT COUNT(*) FROM {CopyCommonStatements.Qualified(fk.Owner, fk.Table)} c WHERE {notNull}"
                      + $" AND NOT EXISTS (SELECT 1 FROM {parentTable} p WHERE {join})";
            var n = await ScalarAsync(run, run.Target, CopyCommonStatements.Query(sql), NoBinds, token);
            return _codec.NumberText(n) is { } text ? text + "건" : "수 모름";
        }
        catch (Exception ex) when (CopyErrors.IsServer(_errors, ex) || ex is BulkDeniedException)
        {
            return "수 모름";
        }
    }

    /// <summary>원본 건수 = 적재할 때 읽은 행 수, 대상 건수 = 같은 조건으로 대상에서 COUNT(*).</summary>
    private async Task CountTargetsAsync(RunState run, CancellationToken token)
    {
        foreach (var table in run.Tables.Values.Where(t => !t.Purged && !(t.CreateRequested && !t.Created)))
        {
            run.Progress(table.Name, table.SourceRows, null, PhaseCount);
            var countStarted = Stopwatch.GetTimestamp();
            try
            {
                var count = run.Builder.TargetCount(table.Name);
                run.LogStatement(run.Target, count.Sql, count.Binds);
                var n = await ScalarAsync(run, run.Target, CopyCommonStatements.Query(count), count.Binds, token);
                table.TargetRows = _codec.NumberText(n) is not null ? _codec.ToInt64Exact(n) : -1;
                run.Log($"→ 원본 {table.SourceRows.ToString("N0", CultureInfo.InvariantCulture)}건 · 대상 {(table.TargetRows < 0 ? "모름" : table.TargetRows.ToString("N0", CultureInfo.InvariantCulture))}건"
                        + (table.TargetRows == table.SourceRows ? " · 같음" : " · 다름"),
                        table.TargetRows == table.SourceRows ? CopyLogKind.Info : CopyLogKind.Warning);
            }
            catch (Exception ex) when (CopyErrors.IsServer(_errors, ex) || ex is BulkDeniedException or InvalidOperationException)
            {
                table.TargetRows = -1;
                table.Note("대상 건수를 세지 못했습니다: " + SafeError(ex.Message));
            }
            finally
            {
                table.Times.AddOutside(countStarted); // 결과의 테이블 전체 경과에 든다(원격 #155)
            }
        }
    }

    // ---- 실행 도우미 ----

    /// <summary>바인드 없는 트랜잭션 제어(대량 경로: 읽기 전용 원본에서도 SET TRANSACTION·COMMIT·ROLLBACK 은 지난다).</summary>
    private Task<BulkResult> ControlAsync(ISession s, SqlStatement stmt, CancellationToken ct) =>
        _bulk.ExecuteArrayAsync(s, stmt, [], [], new ExecutionOptions { Source = HistorySource.DataCopy, SkipHistory = true }, ct);

    /// <summary>트랜잭션 제어(<see cref="ControlAsync"/>)를 작업 로그에 남기며 보낸다. 배치마다 도는 세이브포인트에는 쓰지 않는다(줄이 넘친다).</summary>
    private async Task<BulkResult> ControlLoggedAsync(RunState run, ISession s, SqlStatement stmt, CancellationToken ct, string? note = null)
    {
        run.Log(CopyLogText.Statement(run.SideOf(s), stmt.Text) + (note is null ? string.Empty : "  — " + note), CopyLogKind.Statement);
        var r = await ControlAsync(s, stmt, ct);
        if (r.Status != ExecutionStatus.Succeeded)
        {
            run.Log("→ " + Describe(r), CopyLogKind.Error);
        }

        return r;
    }

    /// <summary>실행 엔진 결과 한 줄: 완료(영향 행 수·걸린 시간) 또는 실패 사유.</summary>
    private static void LogResult(RunState run, ExecutionResult r)
    {
        if (r.Status == ExecutionStatus.Succeeded)
        {
            var rows = r.RowsAffected is { } n ? n.ToString("N0", CultureInfo.InvariantCulture) + "행 · " : string.Empty;
            run.Log($"→ 완료 · {rows}{CopyLogText.Elapsed(r.Elapsed)}");
        }
        else
        {
            run.Log("→ " + Describe(r), CopyLogKind.Error);
        }
    }

    /// <summary>DELETE·ALTER: 실행 엔진(정책·이력)으로 보내고, 성공하면 실행 기록에도 같은 글을 남긴다.</summary>
    private async Task<ExecutionResult> ExecuteLoggedAsync(RunState run, ISession s, SqlStatement stmt,
                                                           IReadOnlyDictionary<string, BindValue> binds, CancellationToken ct, ExecutionOptions? options = null)
    {
        run.LogStatement(s, stmt.Text, binds);
        var r = await _execution.ExecuteAsync(s, stmt, binds, options ?? run.HistoryOptions, ct);
        LogResult(run, r);
        if (r.Status == ExecutionStatus.Cancelled && ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }

        if (r.Status == ExecutionStatus.Succeeded && run.Record is { } record)
        {
            await record.RecordStatementAsync(stmt.Text, ct);
        }

        return r;
    }

    private async Task<List<object?[]>> RowsAsync(RunState run, ISession s, SqlStatement query,
                                                 IReadOnlyDictionary<string, BindValue> binds, CancellationToken ct)
    {
        var rows = new List<object?[]>();
        await foreach (var row in _bulk.StreamRowsAsync(s, query, binds, run.BulkOptions, ct))
        {
            rows.Add(row);
        }

        return rows;
    }

    private async Task<object?> ScalarAsync(RunState run, ISession s, SqlStatement query,
                                            IReadOnlyDictionary<string, BindValue> binds, CancellationToken ct)
    {
        var rows = await RowsAsync(run, s, query, binds, ct);
        return rows.Count > 0 && rows[0].Length > 0 ? rows[0][0] : null;
    }

    private static string Describe(ExecutionResult r) =>
        r.Status switch
        {
            ExecutionStatus.Denied => "정책 거부: " + (r.Policy?.Message ?? "거부"),
            ExecutionStatus.Cancelled => "확인을 받지 못해 실행하지 않았습니다" + (r.Policy?.Message is { } m ? ": " + m : string.Empty),
            _ => ErrorText(r.Error),
        };

    internal static string Describe(BulkResult r) =>
        r.Status switch
        {
            ExecutionStatus.Denied => "정책 거부: " + (r.Policy?.Message ?? "거부"),
            ExecutionStatus.Cancelled => "확인을 받지 못해 실행하지 않았습니다" + (r.Policy?.Message is { } m ? ": " + m : string.Empty),
            _ => ErrorText(r.Error),
        };

    /// <summary>실패 행 기록에는 오류 문구만 남긴다(서버 오류 문구에는 행 값이 들어가지 않는다).</summary>
    private static string ErrorText(DbError? e) =>
        e is null ? "알 수 없는 오류"
        : SafeError(e.Message.StartsWith(e.Code, StringComparison.Ordinal) ? e.Message : e.Code + ": " + e.Message);

    /// <summary>
    /// 서버 오류 문구에서 행 값을 지운다. 23ai 부터 일부 오류가 값을 문구에 넣는다
    /// (ORA-01722 「unable to convert string value containing '…'」, ORA-03301 「row with column values (…)」).
    /// 첫 줄만 남기고 작은따옴표 안의 글자와 「column values (…)」 를 지운다. 열·제약 이름(큰따옴표)은 남긴다.
    /// 실행 기록·표 결과·알림에 남는 오류 문구는 모두 이것을 거친다(행 값은 디스크에 남기지 않는다, 설계 4장).
    /// </summary>
    internal static string SafeError(string message)
    {
        var line = message.Split('\n', 2)[0].TrimEnd('\r');
        line = QuotedValue.Replace(line, "'…'");
        return ColumnValues.Replace(line, "column values (…)");
    }


    // ---- 상태 ----

    /// <summary>교차 복사(원본·대상 DBMS 가 다름)의 두 DBMS 와 대상 값 코덱. 같은 DBMS 복사는 이것이 없다(값 변환을 거치지 않는다).</summary>
    private sealed record CrossSides(Dbms Source, Dbms Target, IBulkValueCodec TargetCodec);

    /// <summary>교차 복사에서 옮길 형식이 없어 뺀 열(<see cref="CrossColumnMapping.DropReason"/>).</summary>
    private sealed record DroppedColumn(string Name, string DataType, CrossDropReason Reason);

    /// <summary>
    /// 표 하나의 열 배치: 복사 열·바인드 열, 마스킹, 키 열 위치. 값 해석(마스킹·실패 행 키)은 원본 값을 보므로 원본 벌의 코덱이다.
    /// 교차 복사면 열을 <see cref="CrossTypeMap"/> 으로 옮기고(옮길 형식이 없는 열은 뺀다) 마스킹한 원본 값을 대상 DBMS 값으로 바꾼다(<see cref="TryToTarget"/>).
    /// </summary>
    private sealed class RowShape
    {
        private readonly IBulkValueCodec _codec;
        private readonly (int Index, MaskRule Rule, int MaxLength, object? Fixed, bool Text)[] _masks;
        private readonly int[] _keyIndexes;
        private readonly string[] _keyTypes;
        private readonly string _salt;

        // 교차 복사만: 대상 값 코덱, 복사 열마다의 원본 형식(이식 값으로 읽을 형식). 같은 DBMS 면 null.
        private readonly IBulkValueCodec? _targetCodec;
        private readonly string[] _sourceTypes;

        private RowShape(IBulkValueCodec codec, IReadOnlyList<string> copyColumns, IReadOnlyList<BulkColumn> columns,
                         (int, MaskRule, int, object?, bool)[] masks, int[] keyIndexes, string[] keyTypes, IReadOnlyList<string> keyNames, string salt,
                         IBulkValueCodec? targetCodec, string[] sourceTypes, IReadOnlyList<DroppedColumn> dropped)
        {
            _codec = codec;
            CopyColumns = copyColumns;
            Columns = columns;
            _masks = masks;
            _keyIndexes = keyIndexes;
            _keyTypes = keyTypes;
            KeyNames = keyNames;
            _salt = salt;
            _targetCodec = targetCodec;
            _sourceTypes = sourceTypes;
            Dropped = dropped;
        }

        /// <summary>읽고 넣는 열(원본 SELECT·대상 DML 의 열 순서). 같은 DBMS 면 <see cref="CopyTablePlan.Columns"/> 그대로, 교차면 옮길 형식이 없는 열을 뺀 것.</summary>
        public IReadOnlyList<string> CopyColumns { get; }

        /// <summary>대상 DML 바인드 열. 같은 DBMS = 원본 열 형식, 교차 = 원본 형식을 대상 DBMS 로 옮긴 형식(<see cref="CrossColumnMapping.BindType"/>, D3).</summary>
        public IReadOnlyList<BulkColumn> Columns { get; }

        public IReadOnlyList<string> KeyNames { get; }

        /// <summary>교차 복사에서 옮길 형식이 없어 뺀 열(원본 열 순서). 같은 DBMS 면 비었다.</summary>
        public IReadOnlyList<DroppedColumn> Dropped { get; }

        /// <summary>값을 대상 DBMS 값으로 바꿔야 한다(교차 복사).</summary>
        public bool Converts => _targetCodec is not null;

        public static RowShape Create(IBulkValueCodec codec, CopyJob job, CopyTablePlan plan, CrossSides? cross = null)
        {
            var source = plan.Source.Columns.ToDictionary(c => c.Name, StringComparer.Ordinal);
            var target = plan.Target.Columns.ToDictionary(c => c.Name, StringComparer.Ordinal);
            ColumnInfo SourceColumn(string name) => source.TryGetValue(name, out var c)
                ? c
                : throw new InvalidOperationException($"{plan.Rule.Table}: 원본에 열 {name} 이(가) 없습니다.");

            // 같은 DBMS: 바인드 형식은 원본 열 형식이다. 값이 원본 형식의 DBMS 고유 값이고, 형식이 달라도 변환은 서버가 맡는다.
            // 교차: 열마다 CrossTypeMap 으로 옮긴다. 옮길 형식이 없는 열은 빼고, 바인드 형식은 원본 형식을 대상 DBMS 로 옮긴 형식이다(대상 열 형식이 아님, D3).
            IReadOnlyList<string> copyColumns = plan.Columns;
            string[] sourceTypes = [];
            IReadOnlyList<DroppedColumn> dropped = [];
            List<BulkColumn> columns;
            if (cross is null)
            {
                var bindNames = CopyCommonStatements.BindNames(plan.Columns.Count);
                columns = plan.Columns.Select((c, i) => new BulkColumn(bindNames[i], SourceColumn(c).DataType)).ToList();
            }
            else
            {
                var kept = plan.Columns.Select(SourceColumn)
                    .Select(c => (Column: c, Map: CrossTypeMap.Map(cross.Source, cross.Target, c)))
                    .Where(x => !x.Map.IsDropped)
                    .ToList();
                dropped = DroppedColumns(cross, plan, target);
                if (plan.KeyColumns.FirstOrDefault(k => dropped.Any(d => string.Equals(d.Name, k, StringComparison.Ordinal))) is { } lostKey)
                {
                    throw new InvalidOperationException($"{plan.Rule.Table}.{lostKey}: 키 열을 대상 DBMS 로 옮길 형식이 없어 이 테이블을 복사할 수 없습니다.");
                }

                copyColumns = [.. kept.Select(x => x.Column.Name)];
                sourceTypes = [.. kept.Select(x => x.Column.DataType)];
                var bindNames = CopyCommonStatements.BindNames(kept.Count);
                columns = kept.Select((x, i) => new BulkColumn(bindNames[i], x.Map.BindType!)).ToList();
            }

            var masks = new List<(int, MaskRule, int, object?, bool)>();
            foreach (var rule in plan.Rule.Masks)
            {
                var index = IndexOf(copyColumns, rule.Column);
                if (index < 0)
                {
                    continue; // 복사하지 않는 열이다.
                }

                if (plan.KeyColumns.Contains(rule.Column, StringComparer.Ordinal))
                {
                    throw new InvalidOperationException($"{plan.Rule.Table}.{rule.Column}: 키 열에는 마스킹을 걸 수 없습니다.");
                }

                var col = SourceColumn(rule.Column);
                var kind = codec.KindOf(col.DataType);
                var text = kind == BulkKind.Text;
                if (!text && rule.Kind is not (MaskKind.Fixed or MaskKind.Null))
                {
                    throw new InvalidOperationException($"{plan.Rule.Table}.{rule.Column}: 문자 열이 아니면 고정값·NULL 마스킹만 쓸 수 있습니다.");
                }

                var maxLength = (target.TryGetValue(rule.Column, out var t) ? t.Length : null) ?? col.Length ?? 64;
                object? fixedValue = null;
                if (!text && rule.Kind == MaskKind.Fixed && rule.Value is { } v)
                {
                    try
                    {
                        fixedValue = kind switch
                        {
                            BulkKind.Clob => v,
                            BulkKind.Blob => Convert.FromHexString(v),
                            _ => codec.FromKeyText(v, col.DataType),
                        };
                    }
                    catch (Exception ex) when (ex is FormatException or NotSupportedException or OverflowException)
                    {
                        throw new InvalidOperationException($"{plan.Rule.Table}.{rule.Column}: 마스킹 고정값을 열 형식으로 바꾸지 못했습니다({ex.Message}).", ex);
                    }
                }

                masks.Add((index, rule, maxLength, fixedValue, text));
            }

            var keyIndexes = plan.KeyColumns.Select(k => IndexOf(copyColumns, k)).ToArray();
            if (keyIndexes.Any(i => i < 0))
            {
                throw new InvalidOperationException($"{plan.Rule.Table}: 키 열이 복사 열에 없습니다.");
            }

            var keyTypes = plan.KeyColumns.Select(k => SourceColumn(k).DataType).ToArray();
            return new RowShape(codec, copyColumns, columns, [.. masks], keyIndexes, keyTypes, plan.KeyColumns, job.MaskSalt,
                                cross?.TargetCodec, sourceTypes, dropped);
        }

        /// <summary>
        /// 교차 복사에서 형식 때문에 빠지는 열: 형식만 아니면 복사했을 열(가상 열 아님·건너뛸 열 아님·대상에도 있음) 가운데 <see cref="CrossTypeMap"/> 이 Dropped 로 본 것.
        /// 점검이 미리 뺐든(<see cref="CopyTablePlan.Columns"/> 에 없음) 아니든 같은 목록이다 — 실행 로그에 남길 목록.
        /// </summary>
        private static List<DroppedColumn> DroppedColumns(CrossSides cross, CopyTablePlan plan, Dictionary<string, ColumnInfo> target)
        {
            var skip = plan.Rule.SkipColumns.ToHashSet(StringComparer.Ordinal);
            return [.. plan.Source.Columns
                .Where(c => !c.IsVirtual && !skip.Contains(c.Name) && target.TryGetValue(c.Name, out var t) && !t.IsVirtual)
                .Select(c => (Column: c, Map: CrossTypeMap.Map(cross.Source, cross.Target, c)))
                .Where(x => x.Map.IsDropped)
                .Select(x => new DroppedColumn(x.Column.Name, x.Column.DataType, x.Map.DropReason))];
        }

        /// <summary>
        /// 외부 도구 적재(조정 결정 88): 마스킹한 원본 행 → 이식 값 행(원본 코덱 <c>ToPortable</c>, 같은 DBMS·교차 모두). 데이터 파일은 이 값을 대상 형식의 글자로 쓴다.
        /// 못 바꾸면 false 와 고정 글 사유(<see cref="TryToTarget"/> 와 같은 규칙: 값은 넣지 않는다).
        /// </summary>
        public bool TryToPortable(object?[] row, out object?[] values, out string? error)
        {
            var portable = new object?[row.Length];
            for (var i = 0; i < row.Length; i++)
            {
                var sourceType = _targetCodec is null ? Columns[i].ServerType : _sourceTypes[i];
                try
                {
                    portable[i] = row[i] is null ? null : _codec.ToPortable(row[i], sourceType);
                }
                catch (Exception ex) when (ex is PortableConversionException or InvalidCastException or FormatException or OverflowException)
                {
                    values = [];
                    error = $"열 {CopyColumns[i]} 의 값(원본 형식 {sourceType})을 외부 도구용 값으로 바꾸지 못했습니다.";
                    return false;
                }
            }

            values = portable;
            error = null;
            return true;
        }

        /// <summary>
        /// 교차 복사: 마스킹한 원본 행 → 새 배열의 대상 DBMS 값(원본 코덱 <c>ToPortable</c> → 대상 코덱 <c>FromPortable</c>, 바인드 형식 = <see cref="Columns"/>).
        /// 값 하나라도 못 바꾸면(<see cref="PortableConversionException"/>·<see cref="InvalidCastException"/>·<see cref="FormatException"/>·<see cref="OverflowException"/>)
        /// false 와 그 행의 실패 사유(열 이름·형식 이름만의 고정 글, 예외 글·값은 넣지 않는다, D5). <see cref="NotSupportedException"/> 은 잡지 않는다(표가 실패). 원본 행은 바꾸지 않는다(실패 행 키를 원본 값으로 남긴다).
        /// </summary>
        public bool TryToTarget(object?[] row, out object?[] values, out string? error)
        {
            var target = _targetCodec ?? throw new InvalidOperationException("같은 DBMS 복사는 값을 바꾸지 않습니다.");
            var converted = new object?[row.Length];
            for (var i = 0; i < row.Length; i++)
            {
                try
                {
                    converted[i] = row[i] is null ? null : target.FromPortable(_codec.ToPortable(row[i], _sourceTypes[i]), Columns[i].ServerType);
                }
                catch (Exception ex) when (ex is PortableConversionException or InvalidCastException or FormatException or OverflowException)
                {
                    // 코덱 예외 글에는 행 값(숫자·날짜 등)이 들어 있을 수 있어 사유에 넣지 않는다: 열 이름·형식 이름만으로 만든 고정 글.
                    values = [];
                    error = $"열 {CopyColumns[i]} 의 값(원본 형식 {_sourceTypes[i]})을 대상 형식({Columns[i].ServerType})으로 바꾸지 못했습니다.";
                    return false;
                }
            }

            values = converted;
            error = null;
            return true;
        }

        /// <summary>원본 행에 마스킹을 건 새 행. 원본 값은 어디에도 남기지 않는다.</summary>
        public object?[] Apply(object?[] row)
        {
            foreach (var (index, rule, maxLength, fixedValue, text) in _masks)
            {
                row[index] = text
                    ? MaskFunctions.Apply(rule, row[index] as string, _salt, maxLength)
                    : rule.Kind == MaskKind.Fixed ? fixedValue : null;
            }

            return row;
        }

        /// <summary>실패 행 기록용 키 글자. 키 열이 없으면 빈 사전(다시 넣기 불가).</summary>
        public Dictionary<string, string?> KeyOf(object?[] row)
        {
            var key = new Dictionary<string, string?>(StringComparer.Ordinal);
            for (var i = 0; i < _keyIndexes.Length; i++)
            {
                key[KeyNames[i]] = _codec.ToKeyText(row[_keyIndexes[i]], _keyTypes[i]);
            }

            return key;
        }

        /// <summary>실패 행 키 글자 → 다시 읽기 바인드(<c>K1</c>…<c>Kn</c>).</summary>
        public Dictionary<string, BindValue> KeyBinds(FailedRow row)
        {
            var binds = new Dictionary<string, BindValue>(StringComparer.Ordinal);
            for (var i = 0; i < KeyNames.Count; i++)
            {
                if (!row.Key.TryGetValue(KeyNames[i], out var text))
                {
                    throw new InvalidOperationException($"{row.Table}: 실패 행 기록에 키 열 {KeyNames[i]} 이(가) 없습니다.");
                }

                binds["K" + (i + 1).ToString(CultureInfo.InvariantCulture)] =
                    new BindValue(_codec.BindTypeOf(_keyTypes[i]), _codec.FromKeyText(text, _keyTypes[i]));
            }

            return binds;
        }

        private static int IndexOf(IReadOnlyList<string> columns, string name)
        {
            for (var i = 0; i < columns.Count; i++)
            {
                if (string.Equals(columns[i], name, StringComparison.Ordinal))
                {
                    return i;
                }
            }

            return -1;
        }
    }

    /// <param name="name">규칙 키(결과·진행·실패 행·실행 기록의 표 이름).</param>
    /// <param name="owner">대상 계정.</param>
    /// <param name="table">대상 표 이름.</param>
    private sealed class TableState(string name, string owner, string table)
    {
        private readonly List<string> _messages = [];

        /// <summary>규칙 키.</summary>
        public string Name { get; } = name;

        /// <summary>대상 계정.</summary>
        public string Owner { get; } = owner;

        /// <summary>대상 표 이름.</summary>
        public string TableName { get; } = table;

        /// <summary>대상 표 <c>계정.표</c>(안내 글).</summary>
        public string TargetName => Owner + "." + TableName;

        public long SourceRows { get; set; }

        public long Loaded { get; set; }

        /// <summary>보낸 배치 수(작업 로그의 배치 번호).</summary>
        public int Batches { get; set; }

        public long TargetRows { get; set; } = -1;

        public int FailedRows { get; set; }

        /// <summary>확정(커밋)을 기다리는 실패 행.</summary>
        public List<FailedRow> PendingFailed { get; } = [];

        /// <summary>이 표의 적재가 커밋으로 확정됐다(실패 행을 결과에 넣었다).</summary>
        public bool Confirmed { get; set; }

        /// <summary>이 표의 적재를 되돌렸다(실패 행 목록을 비웠다).</summary>
        public bool WasRolledBack { get; private set; }

        /// <summary>적재를 되돌렸다: 실패 행은 「실패 행만 다시 넣기」에 쓸 수 없으므로 버리고, 다시 실행하라고 적는다.</summary>
        public void RolledBack()
        {
            if (!WasRolledBack && (PendingFailed.Count > 0 || FailedRows > 0))
            {
                Note("적재를 되돌려 확정되지 않은 실패 행을 버렸습니다. 「실패 행만 다시 넣기」 대신 다시 실행하세요.");
            }

            WasRolledBack = true;
            PendingFailed.Clear();
        }

        /// <summary>대상 정리(DELETE)를 했다. 다시 만든 표는 DROP 을 했다.</summary>
        public bool Deleted { get; set; }

        /// <summary>「테이블 다시 만들기」 표.</summary>
        public bool Recreate { get; set; }

        /// <summary>지우기 전에 받아 둔 트리거·권한·참조 FK.</summary>
        public PreservedDdl? Preserved { get; set; }

        /// <summary>DROP 이 성공했다(옛 표는 휴지통에 있다).</summary>
        public bool Dropped { get; set; }

        /// <summary>CREATE TABLE 이 성공했다(되살리기 대상).</summary>
        public bool Created { get; set; }

        /// <summary>지운 뒤 CREATE 가 실패해 옛 표를 휴지통에서 되살렸다(<see cref="Created"/> 는 거짓, 옛 표·데이터가 그대로 돌아왔다).</summary>
        public bool Flashbacked { get; set; }

        /// <summary>대상에 없던 표를 이번 실행이 만들었다(「없으면 만들기」, <see cref="Created"/> 도 참). 옛 표가 없으니 휴지통·보존 DDL 이 없다.</summary>
        public bool CreatedNew { get; set; }

        /// <summary>만들 표로 정해 CREATE 를 시도했다(<see cref="Created"/> 가 거짓이면 만들지 못했다 — 그 표는 대상 건수를 세지 않는다).</summary>
        public bool CreateRequested { get; set; }

        /// <summary>적재를 끝내지 못해 이번에 만든 표를 <c>DROP … PURGE</c> 로 지웠다(되살리기·건수 비교 대상이 아니다).</summary>
        public bool Purged { get; set; }

        /// <summary>CREATE 문장(FK 없음, 준비 단계에서 만든다).</summary>
        public IReadOnlyList<string> CreateDdl { get; set; } = [];

        /// <summary>새 표에 적재 뒤 보낼 PK·UK·인덱스 문장(PF4a, <see cref="CrossCreate.AfterLoad"/>).</summary>
        public IReadOnlyList<string> AfterLoadDdl { get; set; } = [];

        /// <summary>함께 다시 만드는 부모를 참조하는 원본 FK(부모 표, 문장). 모든 CREATE 뒤에 붙인다.</summary>
        public IReadOnlyList<(string Parent, string Ddl, string? Name)> JobForeignKeys { get; set; } = [];

        /// <summary>DROP 전 휴지통의 이 이름 표(BIN$…).</summary>
        public IReadOnlySet<string> BinBefore { get; set; } = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>이번 DROP 이 만든 휴지통 이름. <see cref="BinKnown"/> 인데 null 이면 영구 삭제됐다.</summary>
        public string? BinName { get; set; }

        /// <summary>DROP 뒤 휴지통을 확인했다.</summary>
        public bool BinKnown { get; set; }

        /// <summary>DROP 전에 떼어 낸 참조 FK(<see cref="PreservedDdl.ReferencingForeignKeys"/> 의 위치). 지우지 못한 표는 이것을 되붙인다.</summary>
        public List<int> DetachedReferencing { get; } = [];

        /// <summary>다시 넣기 커밋이 연결 끊김으로 실패했다(확정됐는지 모른다).</summary>
        public bool CommitUnknown { get; set; }

        /// <summary>적재 밖의 일부(다시 만든 표의 문장·되살리기)를 하지 못했다. 성공 표는 부분 실패로 보인다.</summary>
        public bool Incomplete { get; private set; }

        public void Degrade(string message)
        {
            Incomplete = true;
            _messages.Add(message);
        }

        /// <summary>적재를 끝까지 했다(실패 행이 있어도 표를 멈추지 않았다).</summary>
        public bool LoadCompleted { get; set; }

        /// <summary>표 시작 세이브포인트를 세웠다. 세우기 전에는 되돌리지 않는다(ORA-01086).</summary>
        public bool SavepointSet { get; set; }

        public TimeSpan Elapsed { get; set; }

        /// <summary>적재 시간 나눔(결정 74: 읽기·변환·쓰기). 적재 흐름(PumpTableAsync)을 지났을 때만 결과에 싣는다(<see cref="Pumped"/>).</summary>
        public TableTimes Times { get; } = new();

        /// <summary>적재 흐름을 지났다(시간 나눔이 뜻이 있다).</summary>
        public bool Pumped { get; set; }

        /// <summary>
        /// 지금 배치를 시작할 때의 <see cref="PendingFailed"/> 수. 대기 한도로 그 배치를 배치 세이브포인트로 되돌리면 그 뒤에 넣은 실패 행을 뺀다
        /// (「배치마다 커밋」, 리뷰 PF1-2: 되돌린 배치의 실패 행이 확정되지 않게).
        /// </summary>
        public int PendingAtBatchStart { get; set; }

        public CopyOutcome Outcome { get; private set; } = CopyOutcome.Succeeded;

        /// <summary>실패 메시지를 작업 로그에 남기는 통로.</summary>
        public Action<string>? Failed { get; init; }

        public void Fail(string message)
        {
            Outcome = CopyOutcome.Failed;
            _messages.Add(message);
            Failed?.Invoke(message);
        }

        public void Note(string message) => _messages.Add(message);

        /// <summary>중지로 적재를 끝내지 못한 표(실패로 판정된 표는 그대로 둔다).</summary>
        public void MarkStopped()
        {
            if (Outcome != CopyOutcome.Failed)
            {
                Outcome = CopyOutcome.Stopped;
            }
        }

        public TableRunResult ToResult()
        {
            var outcome = Outcome == CopyOutcome.Succeeded && (FailedRows > 0 || Incomplete) ? CopyOutcome.PartiallyFailed : Outcome;
            // 표 하나가 「중지」여도 실행 전체 판정(RunState.Outcome)은 따로 한다.
            var message = _messages.Count > 0 ? string.Join(" ", _messages) : null;
            return new TableRunResult(Name, SourceRows, TargetRows, Elapsed, outcome, message) { Times = Pumped ? Times.ToTimes(Loaded, Elapsed) : null };
        }
    }

    /// <summary>
    /// 테이블 하나의 적재 시간 나눔(결정 74). 읽기 = 원본 행을 받기까지 기다린 시간(겹치기에서는 읽기 작업이 실제로 DB 를 기다린 시간),
    /// 변환 = 마스킹·교차 복사 값 변환(바인드 값 준비), 쓰기 = 배치를 보낸 시간에서 그 배치의 값 변환을 뺀 것. 겹치기 켬·끔 모두 같은 자리에서 잰다.
    /// 읽기·마스킹은 읽기 작업, 쓰기·값 변환은 쓰기 흐름이 더한다(Interlocked).
    /// </summary>
    private sealed class TableTimes
    {
        private long _read;
        private long _convert;
        private long _write;
        private long _batchConvert; // 쓰기 흐름만: 지금 배치의 값 변환(쓰기 시간에서 뺀다)
        private long _firstRow = -1; // 첫 행까지(질의 시작·커서 열기). -1 = 아직
        private long _load; // 적재 구간(PumpTableAsync) 경과
        private long _outside; // 적재 흐름(LoadTableAsync) 밖에서 이 테이블에 쓴 시간: 대상 정리(DELETE)·건수 비교

        /// <summary>원본 행 하나를 받기까지 기다린 시간. 처음 부른 것(질의 시작 → 첫 행)은 따로도 남긴다(원격 #155).</summary>
        public void AddRead(long started)
        {
            var spent = Stopwatch.GetTimestamp() - started;
            Interlocked.Add(ref _read, spent);
            Interlocked.CompareExchange(ref _firstRow, spent, -1);
        }

        /// <summary>적재 구간(원본 질의 시작 → 마지막 배치 쓰기 끝) 경과. 행/초의 기준이다.</summary>
        public void AddLoad(long started) => Interlocked.Add(ref _load, Stopwatch.GetTimestamp() - started);

        /// <summary>적재 흐름 밖에서 이 테이블에 쓴 시간(대상 정리 DELETE·건수 비교): 테이블 전체 경과에 든다.</summary>
        public void AddOutside(long started) => Interlocked.Add(ref _outside, Stopwatch.GetTimestamp() - started);

        public void AddConvert(long started) => Interlocked.Add(ref _convert, Stopwatch.GetTimestamp() - started);

        /// <summary>
        /// 지금 배치의 값 변환. <paramref name="excluded"/> = 변환 고리 안에서 실패 행을 처리한 시간(상한을 넘으면 ROLLBACK TO 왕복):
        /// 대상 DB 를 기다린 시간이라 변환에서 빼고 쓰기에 남긴다(리뷰 PF1-5).
        /// </summary>
        public void AddBatchConvert(long started, long excluded = 0)
        {
            var spent = Math.Max(0, Stopwatch.GetTimestamp() - started - excluded);
            Interlocked.Add(ref _convert, spent);
            _batchConvert += spent;
        }

        public void AddWrite(long started)
        {
            var spent = Stopwatch.GetTimestamp() - started - _batchConvert;
            _batchConvert = 0;
            Interlocked.Add(ref _write, Math.Max(0, spent));
        }

        /// <summary>
        /// 행/초 = 넣은 행 / 적재 구간 경과(적재 구간을 재지 못했으면 <paramref name="elapsed"/>). 테이블 전체 = <paramref name="elapsed"/>(적재 흐름: 세이브포인트·
        /// 읽기 시점·적재 구간·커밋) + 대상 정리·건수 비교(원격 #155: 「걸린 시간과 행/초가 안 맞음」 — 기준을 나눠 적는다).
        /// </summary>
        public CopyTableTimes ToTimes(long written, TimeSpan elapsed)
        {
            var load = Interlocked.Read(ref _load);
            var basis = load > 0 ? Span(load) : elapsed;
            var first = Interlocked.Read(ref _firstRow);
            return new CopyTableTimes(Span(Interlocked.Read(ref _read)), Span(Interlocked.Read(ref _convert)), Span(Interlocked.Read(ref _write)),
                basis.TotalSeconds > 0 ? written / basis.TotalSeconds : 0)
            {
                Load = load > 0 ? Span(load) : null,
                Total = elapsed + Span(Interlocked.Read(ref _outside)),
                FirstRow = first >= 0 ? Span(first) : null,
            };
        }

        private static TimeSpan Span(long stopwatchTicks) => Stopwatch.GetElapsedTime(0, stopwatchTicks);
    }

    /// <summary>알림 목록. 더할 때마다 <paramref name="added"/> 로 작업 로그에 남긴다.</summary>
    private sealed class NoticeList(Action<string> added) : IEnumerable<string>
    {
        private readonly List<string> _items = [];

        public void Add(string notice)
        {
            // 동시 복사(PF2)면 레인 여럿이 더한다. 읽기(결과 만들기)는 레인이 모두 끝난 뒤다.
            lock (_items)
            {
                _items.Add(notice);
            }

            added(notice);
        }

        public IEnumerator<string> GetEnumerator() => _items.GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class RunState
    {
        private readonly IProgress<CopyProgress>? _progress;
        private readonly object _reportGate = new();
        private readonly Dictionary<string, TableInfo> _sourceTables;
        private readonly Dictionary<string, TableInfo> _targetTables;

        // 동시 복사(PF2): 레인(세션 쌍)마다 따로인 것(세션·지금 표·단계·버린 세션)은 지금 흐름의 레인(_lane)에 두고, 없으면(첫 레인·한 테이블씩) 주 값을 쓴다.
        // 레인 흐름은 시작할 때 UseLane 으로 자기 레인을 건다(AsyncLocal: 그 흐름과 그 흐름이 띄운 읽기·감시 작업에만 보인다). 나머지는 모두 공유다.
        private readonly AsyncLocal<RunLane?> _lane = new();
        private readonly ISession _source;
        private readonly ISession _target;
        private string _phase = PhaseStart;
        private string? _table;
        private volatile bool _aborted;
        private bool _sourceAbandoned;
        private bool _targetAbandoned;

        public RunState(CopySessions sessions, CopyJob job, PrecheckReport report, ResolvedPeriod period,
                        IReadOnlySet<string> preConfirmed, IProgress<CopyProgress>? progress)
        {
            _source = sessions.Source;
            _target = sessions.Target;
            External = sessions.ExternalLoad;
            Job = job;
            Report = report;
            _progress = progress;
            _sourceTables = report.Order.ToDictionary(p => CopyOwners.Key(job, p.Rule), p => p.Source, StringComparer.Ordinal);
            _targetTables = report.Order.ToDictionary(p => CopyOwners.Key(job, p.Rule), p => p.Target, StringComparer.Ordinal);
            Builder = new CopySqlBuilder(job, period, _sourceTables, _targetTables, FreshKeys(job, report));
            HistoryOptions = new ExecutionOptions { Source = HistorySource.DataCopy, PreConfirmed = preConfirmed };
            BulkOptions = new ExecutionOptions { Source = HistorySource.DataCopy, SkipHistory = true, PreConfirmed = preConfirmed };
            Notices = new NoticeList(text => Log(text, CopyLogKind.Warning));
        }

        /// <summary>오래된 외부 도구 임시 폴더를 이 실행에서 지웠는가(1 = 지움, 레인 여럿이 함께 본다).</summary>
        public int ExternalStaleCleaned;

        /// <summary>이 실행의 짧은 식별자(외부 도구 임시 폴더 이름).</summary>
        public string RunId { get; } = Guid.NewGuid().ToString("N")[..12];

        /// <summary>외부 적재 도구 준비물(<see cref="CopySessions.ExternalLoad"/>). null = 쓸 수 없음.</summary>
        public ExternalLoad.CopyExternalLoad? External { get; }

        /// <summary>원본 세션(지금 흐름이 레인이면 그 레인의 원본).</summary>
        public ISession Source => _lane.Value?.Source ?? _source;

        /// <summary>대상 세션(지금 흐름이 레인이면 그 레인의 대상).</summary>
        public ISession Target => _lane.Value?.Target ?? _target;

        /// <summary>지금 흐름의 레인(첫 레인·레인 밖이면 null).</summary>
        public RunLane? CurrentLane => _lane.Value;

        /// <summary>둘째 레인부터(<see cref="CopySessions.Workers"/>로 연 쌍). 첫 레인은 주 쌍이라 여기 없다. 한 테이블씩이면 비었다.</summary>
        public List<RunLane> Workers { get; } = [];

        /// <summary>동시 테이블 수(레인 수). 1 이면 예전처럼 한 테이블씩.</summary>
        public int LaneCount { get; set; } = 1;

        /// <summary>레인이 도는 중(<see cref="CopyRunner.LoadInLanesAsync"/>). 이때만 다른 레인의 전체 중단을 배치마다 본다.</summary>
        public bool InLanes { get; set; }

        /// <summary>둘째 레인부터 가운데 대상 트랜잭션을 끝내지 못한 것이 있다(<see cref="CopyRunner.FinishLanesAsync"/>). 마무리가 다시 켜기·되살리기를 하지 않는다.</summary>
        public bool LaneLeftOpen { get; set; }

        /// <summary>이 흐름(과 그 흐름이 띄우는 작업)을 <paramref name="lane"/> 의 세션으로 돌린다. 부른 비동기 메서드가 끝나면 원래대로 돌아간다.</summary>
        public void UseLane(RunLane lane) => _lane.Value = lane;

        public CopyJob Job { get; }

        public PrecheckReport Report { get; }

        public CopySqlBuilder Builder { get; }

        /// <summary>DELETE·ALTER: 이력에 남긴다.</summary>
        public ExecutionOptions HistoryOptions { get; }

        /// <summary>조회·배치 INSERT·MERGE·트랜잭션 제어: 이력에 남기지 않는다.</summary>
        public ExecutionOptions BulkOptions { get; }

        public ICopyRunRecord? Record { get; set; }

        public DateTimeOffset ReadStartedAt { get; set; } = DateTimeOffset.Now;

        public Dictionary<string, TableState> Tables { get; } = new(StringComparer.Ordinal);

        public List<DisabledObject> Disabled { get; } = [];

        /// <summary>이번 실행이 표보다 먼저 만들려 한 시퀀스(대상 소유자·이름 → 상태, CopyRunner.Create.cs).</summary>
        public Dictionary<(string Owner, string Name), CreatedSequence> Sequences { get; } = [];

        /// <summary>끈 객체의 진행을 보인 규칙 키(다시 켤 때도 같은 줄에 보인다).</summary>
        public Dictionary<DisabledObject, string> DisabledKeys { get; } = [];

        public List<FailedRow> FailedRows { get; } = [];

        /// <summary>알림. 더할 때마다 작업 로그에도 한 줄 남긴다.</summary>
        public NoticeList Notices { get; }

        /// <summary>지금 단계(마지막 진행 보고의 단계). 작업 로그 줄의 단계 칸. 레인이면 그 레인의 것.</summary>
        public string CurrentPhase
        {
            get => _lane.Value is { } lane ? lane.Phase : _phase;
            private set
            {
                if (_lane.Value is { } lane)
                {
                    lane.Phase = value;
                }
                else
                {
                    _phase = value;
                }
            }
        }

        /// <summary>지금 표(마지막 진행 보고의 규칙 키). 작업 로그 줄의 표 칸. 레인이면 그 레인의 것.</summary>
        public string? CurrentTable
        {
            get => _lane.Value is { } lane ? lane.Table : _table;
            private set
            {
                if (_lane.Value is { } lane)
                {
                    lane.Table = value;
                }
                else
                {
                    _table = value;
                }
            }
        }

        /// <summary>실행 전체에 걸린 시간.</summary>
        public Stopwatch Clock { get; } = Stopwatch.StartNew();

        /// <summary>전체를 멈춤(끄기 실패·ORA-01555·원본 준비 실패). 중지(<see cref="Stopped"/>)와 달리 결과는 실패다.</summary>
        public bool Aborted => _aborted;

        public bool Stopped { get; set; }

        public bool CommitFailed { get; set; }

        public bool RolledBackAll { get; set; }

        /// <summary>「실패 행만 다시 넣기」 실행.</summary>
        public bool IsRetry { get; set; }

        /// <summary>「실패 행만 다시 넣기」의 입력 행. 확정되지 않은 표의 행은 끝낼 때 그대로 결과에 돌려준다.</summary>
        public IReadOnlyList<FailedRow> RetryInputs { get; set; } = [];

        /// <summary>끈 트리거·FK 중 다시 켜지 못한 것이 있음. 결과는 실패다(꺼진 채 남은 것을 사용자가 알아야 한다, 설계 4장).</summary>
        public bool EnableFailed { get; set; }

        /// <summary>다시 만든 표의 트리거·권한·참조 FK 중 되살리지 못한 것이 있음. 결과는 실패다(사라진 객체를 사용자가 알아야 한다).</summary>
        public bool RestoreFailed { get; set; }

        /// <summary>대상 정리(DELETE) 뒤 커밋이 한 번이라도 성공함(다른 표의 DELETE 도 확정됨).</summary>
        public bool DeletesPersisted { get; set; }

        /// <summary>끝낼 때 대상 트랜잭션을 롤백으로 끝냈다(커밋하지 않은 적재가 모두 되돌아갔다).</summary>
        public bool TargetRolledBack { get; set; }

        /// <summary>
        /// 원본 명령이 취소에도 돌아오지 않아 원본 세션을 더 쓰지 않는다(<see cref="CopyRunner.AbandonStuckAsync"/>). Core 는 세션을 새로 열 수 없어 실행을 멈춘다.
        /// </summary>
        public bool SourceAbandoned
        {
            // 레인이면 그 레인의 원본이다.
            get => _lane.Value is { } lane ? lane.SourceAbandoned : _sourceAbandoned;
            set
            {
                if (_lane.Value is { } lane)
                {
                    lane.SourceAbandoned = value;
                }
                else
                {
                    _sourceAbandoned = value;
                }
            }
        }

        /// <summary>
        /// 대상 명령이 취소에도 돌아오지 않아 대상 세션을 더 쓰지 않는다(마무리의 커밋·롤백·다시 켜기·건수 비교를 보내지 않는다). 레인이면 그 레인의 대상이다
        /// (레인 마무리가 그 레인을 끝내지 못하면 주 값도 켠다: 남은 트랜잭션이 다시 켜기를 막는다).
        /// </summary>
        public bool TargetAbandoned
        {
            get => _lane.Value is { } lane ? lane.TargetAbandoned : _targetAbandoned;
            set
            {
                if (_lane.Value is { } lane)
                {
                    lane.TargetAbandoned = value;
                }
                else
                {
                    _targetAbandoned = value;
                }
            }
        }

        /// <summary>대상 쪽 대기 원인 감시(WH1). 앱이 감시 세션 여는 길을 주지 않았으면 null.</summary>
        public CopyWaitProbe? WaitProbe { get; set; }

        /// <summary>규칙 키.</summary>
        public string Key(CopyTablePlan plan) => CopyOwners.Key(Job, plan.Rule);

        public TableState Table(CopyTablePlan plan)
        {
            var key = Key(plan);
            if (!Tables.TryGetValue(key, out var t))
            {
                t = new TableState(key, plan.Target.Table.Owner, plan.Rule.Table)
                {
                    Failed = message => Log(message, CopyLogKind.Error, key),
                };
                Tables[key] = t;
            }

            return t;
        }

        /// <summary>대상 계정·표로 이 실행의 표를 찾는다(대상 FK 의 자식·부모를 맞출 때). 없으면 null.</summary>
        public TableState? ByTarget(string owner, string table)
            => Tables.Values.FirstOrDefault(t => CopyOwners.SameOwner(t.Owner, owner)
                                                 && string.Equals(t.TableName, table, StringComparison.Ordinal));

        public void Abort(string? notice)
        {
            _aborted = true;
            if (notice is not null)
            {
                Notices.Add(notice);
            }
        }

        public void Progress(string table, long rows, long? estimated, string phase)
        {
            CurrentPhase = phase;
            CurrentTable = table;
            Send(new CopyProgress(table, rows, estimated, phase));
        }

        /// <summary>적재 진행: 읽은 수(<paramref name="rows"/>)와 넣은 수(<paramref name="written"/>)를 함께 보낸다.</summary>
        public void Progress(string table, long rows, long? estimated, string phase, long written)
        {
            CurrentPhase = phase;
            CurrentTable = table;
            Send(new CopyProgress(table, rows, estimated, phase) { Written = written });
        }

        /// <summary>
        /// 멈춤 표시(감시가 보낸다). 지금 표·단계(<see cref="CurrentTable"/>·<see cref="CurrentPhase"/>)를 바꾸지 않는다: 감시는 적재 흐름과 따로 돈다.
        /// <paramref name="waits"/> 가 비면 표시를 지운다.
        /// </summary>
        /// <param name="phase">진행 줄 단계(적재, 또는 대상 정리 DELETE 를 기다릴 때 대상 정리).</param>
        public void ReportWaits(string table, long rows, long? estimated, long written, IReadOnlyList<CopyWait> waits, string phase = PhaseLoad) =>
            Send(new CopyProgress(table, rows, estimated, phase) { Written = written, Waits = waits });

        /// <summary>
        /// 테이블 적재 끝 알림(<see cref="PhaseLoadEnd"/>). 지금 표·단계를 바꾸지 않는다(뒤따르는 로그 줄의 단계 이름이 달라지지 않게).
        /// </summary>
        public void ReportLoadEnd(string table, long rows, long written) =>
            Send(new CopyProgress(table, rows, rows, PhaseLoadEnd) { Written = written });

        /// <summary>진행·로그 보고는 한 번에 하나씩(적재 흐름·멈춤 감시가 서로 다른 스레드에서 보낼 수 있다).</summary>
        private void Send(CopyProgress p)
        {
            if (_progress is null)
            {
                return;
            }

            lock (_reportGate)
            {
                _progress.Report(p);
            }
        }

        /// <summary>단계를 바꾼다(표와 상관없는 단계: 시작·원본 준비·마무리). 진행 줄은 보내지 않는다.</summary>
        public void Enter(string phase)
        {
            CurrentPhase = phase;
            CurrentTable = null;
        }

        /// <summary>작업 로그 한 줄. 표·단계를 주지 않으면 지금 표·단계다. 행 값·비밀은 넣지 않는다(부르는 쪽 책임).</summary>
        public void Log(string text, CopyLogKind kind = CopyLogKind.Info, string? table = null, string? phase = null) =>
            Send(CopyProgress.ForLog(new CopyLogEntry(DateTimeOffset.Now, phase ?? CurrentPhase, table ?? CurrentTable, text, kind)));

        /// <summary>보낸 문장 한 줄(원본·대상, 바인드 요약).</summary>
        public void LogStatement(ISession session, string sql, IReadOnlyDictionary<string, BindValue>? binds = null) =>
            Log(CopyLogText.Statement(SideOf(session), sql, binds), CopyLogKind.Statement);

        /// <summary>문장 줄의 쪽 글. 원본 세션 집합(주 원본·레인 원본) 가운데 하나면 「원본」, 아니면 「대상」.</summary>
        public string SideOf(ISession session) =>
            ReferenceEquals(session, _source) || ReferenceEquals(session, Source) || Workers.Exists(w => ReferenceEquals(w.Source, session)) ? "원본" : "대상";

        /// <summary>동시 복사 단계 알림(<see cref="CopyProgress.Stage"/>): 화면 상태 글.</summary>
        public void ReportStage(CopyStage stage) => Send(new CopyProgress(string.Empty, 0, null, PhaseLoad) { Stage = stage });

        /// <summary>
        /// 대상 문장 결과를 받는다. 성공이면 true. 중지로 취소됐으면 중지 예외를 올리고, 정책 거부·확인 거절·실패면 표를 실패로 둔다(예외로 던지지 않는다).
        /// </summary>
        public static bool Accept(BulkResult r, TableState table, string what, CancellationToken ct)
        {
            if (r.Status == ExecutionStatus.Succeeded)
            {
                return true;
            }

            if (r.Status == ExecutionStatus.Cancelled && ct.IsCancellationRequested)
            {
                throw new OperationCanceledException(ct);
            }

            table.Fail(what + ": " + Describe(r));
            return false;
        }

        public CopyOutcome Outcome(bool faulted)
        {
            // 다시 켜지 못한 트리거·FK 가 있으면 중지했더라도 실패다(꺼진 채 남은 것을 결과에서 알아야 한다).
            if (EnableFailed || RestoreFailed)
            {
                return CopyOutcome.Failed;
            }

            if (Stopped)
            {
                return CopyOutcome.Stopped;
            }

            var results = Tables.Values.Select(t => t.ToResult().Outcome).ToList();
            if (faulted || Aborted || CommitFailed || RolledBackAll || EnableFailed || (results.Count > 0 && results.All(o => o == CopyOutcome.Failed)))
            {
                return CopyOutcome.Failed;
            }

            return results.Any(o => o is CopyOutcome.Failed or CopyOutcome.PartiallyFailed) ? CopyOutcome.PartiallyFailed : CopyOutcome.Succeeded;
        }

        public CopyRunResult Result() =>
            new(Outcome(faulted: false), [.. Tables.Values.Select(t => t.ToResult())], [.. FailedRows], [.. Notices], ReadStartedAt)
            {
                // 버린 세션은 앱이 닫고 새로 연다(리뷰 PF1-3).
                SourceAbandoned = SourceAbandoned,
                TargetAbandoned = TargetAbandoned,
            };
    }
}
