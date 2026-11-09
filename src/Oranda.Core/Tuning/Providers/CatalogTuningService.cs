using System.Diagnostics;
using Oranda.Core.Catalog.Providers;
using Oranda.Core.Execution;
using Oranda.Core.Execution.SqlPlus;
using Oranda.Core.Language;
using Oranda.Core.Monitoring;
using Oranda.Core.Plans;
using Oranda.Core.Sessions;
using static Oranda.Core.Catalog.Providers.CatalogValues;
using static Oranda.Core.Tuning.Providers.TuningRows;

namespace Oranda.Core.Tuning.Providers;

/// <summary>
/// 튜닝 조회(PRD 11장, TSK-06-02)의 DBMS 공통 본문. 소유: TSK-06-02(본문), P0-b(기반 클래스로 옮김).
/// 조회는 카탈로그 <c>tuning.*</c> 를 세션 연결로 직접 읽는다(모니터와 같은 경로: <c>@PFX@</c> = 딕셔너리 권한이면 dba, 아니면 all ·
/// 세션마다 한 번에 한 문장 · 팩이 필요한 쿼리는 보유 선언과 대조해 모자라면 보내지 않는다 — 질의 포트 몫).
/// <para>
/// 모든 카탈로그 쿼리는 SELECT 다. 벤치마크만 사용자 문장을 실행하는데, SELECT/WITH 한 문장만 받아 <see cref="IExecutionService"/>(정책 관문)로 보낸다.
/// 자식 계획은 <see cref="IExecutionPlanService"/>(plan.cursor_steps) 를 그대로 부른다.
/// </para>
/// <para>
/// DBMS 에 매이지 않는다(D2). 등록은 DBMS 쪽 하위 형식이 한다(<c>[OrandaService(typeof(ITuningService), Dbms = …)]</c>) — 이 형식 자체는 등록하지 않는다.
/// DBMS 차이는 질의 포트 <see cref="QueryAsync"/> 와 고리(<see cref="TraceUnavailableReason"/>·<see cref="PreviousPlanHashAsync"/>·<see cref="BeginReadOnlyAsync"/>·
/// 세션 통계 출처)로만 남긴다.
/// </para>
/// </summary>
public abstract partial class CatalogTuningService : ITuningService
{
    /// <summary>tuning.*: 열 이름 포함, <c>@PFX@</c> 치환.</summary>
    protected static readonly CatalogQueryOptions TuningQueryOptions = new() { ColumnNames = true, DictionaryPrefix = true };

    private readonly IExecutionService _execution;
    private readonly IExecutionPlanService _plans;
    private readonly ILanguageService _language;
    private readonly SessionStatisticsSource? _statistics;

    /// <param name="statistics">벤치마크 전후 세션 통계 출처. null 이면 논리·물리 읽기를 재지 않는다(0).</param>
    protected CatalogTuningService(IExecutionService execution, IExecutionPlanService plans, ILanguageService language, SessionStatisticsSource? statistics)
    {
        _execution = execution;
        _plans = plans;
        _language = language;
        _statistics = statistics;
    }

    public async Task<TableStatsRow?> GetTableStatsAsync(ISession s, string owner, string table, CancellationToken ct)
    {
        var rows = (await QueryAsync(s, "tuning.table_stats", TableBinds(owner, table), TuningQueryOptions, ct)).Rows;
        if (rows.Count == 0)
        {
            return null;
        }

        var r = rows[0];
        var lastAnalyzed = Date(r[5]);
        if (lastAnalyzed is null && Long(r[2]) is null)
        {
            return null; // 표는 있으나 통계를 모은 적이 없다.
        }

        return new TableStatsRow(Str(r[0]) ?? owner, Str(r[1]) ?? table, Long(r[2]), Long(r[3]), Long(r[4]), lastAnalyzed, Yes(r[6], "YES"));
    }

    public async Task<IReadOnlyList<ColumnStatsRow>> GetColumnStatsAsync(ISession s, string owner, string table, CancellationToken ct)
    {
        var rows = (await QueryAsync(s, "tuning.column_stats", TableBinds(owner, table), TuningQueryOptions, ct)).Rows;
        return [.. rows.Select(r => new ColumnStatsRow(
            Str(r[0]) ?? string.Empty, Long(r[1]), Long(r[2]), Double(r[3]), Str(r[4]) ?? "NONE", Long(r[5]), Date(r[6])))];
    }

    public async Task<IReadOnlyList<HistogramBucket>> GetHistogramAsync(ISession s, string owner, string table, string column, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(column);
        var rows = (await QueryAsync(s, "tuning.histogram",
            CatalogBinds.Defer(() => [.. BindTable(owner, table), CatalogBinds.Trimmed("column_name", column)]), TuningQueryOptions, ct)).Rows;
        return ToBuckets(rows.Select(r => (Long(r[0]) ?? 0, Str(r[1]) ?? Str(r[2]))));
    }

    public async Task<IReadOnlyList<IndexStatsRow>> GetIndexStatsAsync(ISession s, string owner, string table, CancellationToken ct)
    {
        var rows = (await QueryAsync(s, "tuning.index_stats", TableBinds(owner, table), TuningQueryOptions, ct)).Rows;
        return [.. rows.Select(r => new IndexStatsRow(
            Str(r[0]) ?? string.Empty, Str(r[1]) ?? string.Empty, Long(r[2]), Long(r[3]), Long(r[4]), Int(r[5]), Date(r[6]), Yes(r[7], "YES")))];
    }

    public async Task<IReadOnlyList<ChildCursorRow>> GetChildCursorsAsync(ISession s, string sqlId, CancellationToken ct)
    {
        var rows = (await QueryAsync(s, "tuning.child_cursors", SqlIdBinds(sqlId), TuningQueryOptions, ct)).Rows;
        return [.. rows.Select(r => new ChildCursorRow(
            Int(r[1]) ?? 0, Long(r[2]) ?? 0, Yes(r[3], "Y"), Yes(r[4], "Y"), Yes(r[5], "Y"), Long(r[6]) ?? 0, Long(r[7]) ?? 0, Long(r[8]) ?? 0)
        {
            InstanceId = Int(r[0]),
        })];
    }

    /// <summary>자식 커서 사유. 원본이 없는 DBMS(<see cref="Connection.DbmsFeature.SharedCursorReasons"/>)는 재정의해 <see cref="NotSupportedException"/>.</summary>
    public virtual async Task<IReadOnlyList<SharedCursorReason>> GetSharedCursorReasonsAsync(ISession s, string sqlId, CancellationToken ct)
    {
        var result = await QueryAsync(s, "tuning.shared_cursor", SqlIdBinds(sqlId), TuningQueryOptions, ct);
        return ToReasons(result.Names, result.Rows);
    }

    public Task<ExecutionPlan> GetChildPlanAsync(ISession s, CursorRef cursor, string format, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(cursor);
        if (cursor.ChildNumber is null)
        {
            throw new ArgumentException("자식 커서 번호(ChildNumber)가 필요합니다.", nameof(cursor));
        }

        return _plans.GetCursorPlanAsync(s, cursor, format, ct);
    }

    public async Task<IReadOnlyList<WaitSample>> SampleWaitsAsync(ISession s, string? sqlId, CancellationToken ct)
    {
        var id = sqlId is null ? null : CheckSqlId(sqlId);
        var at = DateTime.Now;
        var rows = (await QueryAsync(s, "tuning.wait_sample", [CatalogBinds.Trimmed("sql_id", id)], TuningQueryOptions, ct)).Rows;
        return [.. rows.Select(r => new WaitSample(at, Long(r[1]) ?? 0, Str(r[2]), Str(r[3]), Str(r[4])))];
    }

    public virtual async Task<TraceFileInfo> GetTraceFileAsync(ISession s, SessionKey target, CancellationToken ct)
    {
        // default(SessionKey) = 이 세션(TSK-06-04): v$diag_info 의 Default Trace File(gv$process 권한 없이도 읽힌다).
        var rows = target == default
            ? (await QueryAsync(s, "tuning.trace_file_self", CatalogBinds.None, TuningQueryOptions, ct)).Rows
            : (await QueryAsync(s, "tuning.trace_file",
                [CatalogBinds.Int("inst_id", target.InstanceId), CatalogBinds.Long("sid", target.Sid), CatalogBinds.Long("serial", target.Serial)],
                TuningQueryOptions, ct)).Rows;
        var path = rows.Count == 0 ? null : Str(rows[0][0]);
        if (string.IsNullOrEmpty(path))
        {
            throw new InvalidOperationException(
                $"세션의 트레이스 파일을 찾을 수 없습니다(이미 끝났을 수 있습니다): SID {target.Sid}, SERIAL# {target.Serial}, 인스턴스 {target.InstanceId}.");
        }

        var reason = TraceUnavailableReason(s);
        return new TraceFileInfo(path, reason is null, reason);
    }

    public async Task<IReadOnlyList<string>> ReadTraceLinesAsync(ISession s, TraceFileInfo file, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(file);
        if (TraceUnavailableReason(s) is { } reason)
        {
            throw new NotSupportedException(reason);
        }

        if (!file.Readable)
        {
            throw new NotSupportedException(file.UnavailableReason ?? "이 트레이스 파일은 읽을 수 없습니다.");
        }

        var rows = (await QueryAsync(s, "tuning.trace_lines",
            CatalogBinds.Defer(() => [CatalogBinds.Trimmed("file_name", TraceFileName(file.Path))]), TuningQueryOptions, ct)).Rows;
        return [.. rows.Select(r => (Str(r[0]) ?? string.Empty).TrimEnd('\r', '\n'))];
    }

    public async Task<IReadOnlyList<IndexColumnsRow>> GetIndexColumnsAsync(ISession s, string owner, string table, CancellationToken ct)
    {
        var rows = (await QueryAsync(s, "tuning.index_columns", TableBinds(owner, table), TuningQueryOptions, ct)).Rows;
        return [.. rows
            .GroupBy(r => Str(r[0]) ?? string.Empty, StringComparer.Ordinal)
            .Select(g => new IndexColumnsRow(g.Key, [.. g.OrderBy(r => Long(r[2]) ?? 0).Select(r => Str(r[1]) ?? string.Empty)]))];
    }

    public async Task<BenchmarkResult> RunBenchmarkAsync(ISession s, BenchmarkCase benchmark, int iterations, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(benchmark);
        ArgumentOutOfRangeException.ThrowIfLessThan(iterations, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(iterations, Benchmark.MaxIterations);
        var stmt = Benchmark.Statement(_language, benchmark.Sql, benchmark.Binds);

        if (benchmark.ReadOnly && !s.Profile.ReadOnly)
        {
            // 사용자 함수가 든 문장: 읽기 전용 트랜잭션 안에서만 돌린다(함수 안 DML 은 DB 가 ORA-01456 으로 막는다). 시작하지 못하면 문장은 보내지 않는다.
            // 읽기 전용 프로필은 실행 서비스가 이미 문장마다 읽기 전용 트랜잭션을 연다(SET TRANSACTION 을 다시 하면 ORA-01453).
            await BeginReadOnlyAsync(s, ct);
            try
            {
                return await RunBenchmarkCoreAsync(s, benchmark, stmt, iterations, ct);
            }
            finally
            {
                await s.RollbackAsync(CancellationToken.None);
            }
        }

        return await RunBenchmarkCoreAsync(s, benchmark, stmt, iterations, ct);
    }

    public async Task<IReadOnlyList<AwrPlanRow>> GetAwrPlanHistoryAsync(ISession s, string sqlId, CancellationToken ct)
    {
        var rows = (await QueryAsync(s, "tuning.awr_plan_history", SqlIdBinds(sqlId), TuningQueryOptions, ct)).Rows;
        return [.. rows.Select(r => new AwrPlanRow(
            Long(r[0]) ?? 0, Date(r[1]) ?? DateTime.MinValue, Long(r[2]) ?? 0, Long(r[3]) ?? 0, Long(r[4]) ?? 0, Long(r[5]) ?? 0))];
    }

    public async Task<IReadOnlyList<AshWaitRow>> GetAshWaitsAsync(ISession s, string sqlId, DateTime since, CancellationToken ct)
    {
        var rows = (await QueryAsync(s, "tuning.ash_waits",
            CatalogBinds.Defer(() => [CatalogBinds.Trimmed("sql_id", CheckSqlId(sqlId)), CatalogBinds.Date("since", since)]), TuningQueryOptions, ct)).Rows;
        return ToAshRows(rows.Select(r => (Str(r[0]) ?? "ON CPU", Str(r[1]), Long(r[2]) ?? 0)));
    }

    /// <summary>SQL Profile·Baseline·Patch. 원본이 없는 DBMS(<see cref="Connection.DbmsFeature.PlanControls"/>)는 재정의해 <see cref="NotSupportedException"/>.</summary>
    public virtual async Task<IReadOnlyList<SqlPlanControlRow>> GetSqlPlanControlsAsync(ISession s, string sqlId, CancellationToken ct)
    {
        var id = CheckSqlId(sqlId);
        IReadOnlyList<object?[]> rows;
        try
        {
            rows = (await QueryAsync(s, "tuning.plan_controls", [CatalogBinds.Trimmed("sql_id", id)], TuningQueryOptions, ct)).Rows;
        }
        catch (Exception ex) when (IsNoAccess(ex))
        {
            return []; // dba_sql_* 를 볼 권한이 없다(계약: 빈 목록).
        }

        return [.. rows.Select(r => new SqlPlanControlRow(
            Str(r[0]) switch
            {
                "PROFILE" => SqlPlanControlKind.Profile,
                "BASELINE" => SqlPlanControlKind.Baseline,
                _ => SqlPlanControlKind.Patch,
            },
            Str(r[1]) ?? string.Empty, Str(r[2]) ?? string.Empty, Date(r[3]) ?? DateTime.MinValue, Str(r[4]))
        {
            Accepted = Str(r[5]) is { } a ? string.Equals(a, "YES", StringComparison.OrdinalIgnoreCase) : null,
        })];
    }

    /// <summary>
    /// 질의 포트: 카탈로그 <c>tuning.*</c> 를 세션 연결로 읽는다(열 이름 포함). 공급자가 맡는 것 = 세션 확인, 카탈로그 Get,
    /// 라이선스 관문(<see cref="CatalogLicenseGate"/>, 팩이 모자라면 드라이버로 보내지 않는다), <paramref name="options"/> 적용, 세션 명령 잠금(D-041), 드라이버 실패 보고.
    /// <paramref name="binds"/> 는 명령을 만든 뒤 읽는다(<see cref="CatalogBinds.Defer"/>).
    /// </summary>
    protected abstract Task<CatalogRows> QueryAsync(ISession s, string id, IReadOnlyList<CatalogBind> binds, CatalogQueryOptions options, CancellationToken ct);

    /// <summary>권한·뷰 없음(「볼 수 없음」)인가. tuning.plan_controls 는 이때 빈 목록.</summary>
    protected abstract bool IsNoAccess(Exception ex);

    /// <summary>트레이스 본문을 SQL 로 못 읽는 이유. 읽을 수 있으면 null.</summary>
    protected abstract string? TraceUnavailableReason(ISession s);

    /// <summary>방금 끝낸 문장(벤치마크 첫 회)의 계획 해시. 모르면(권한 없음 포함) null.</summary>
    protected abstract Task<long?> PreviousPlanHashAsync(ISession s, CancellationToken ct);

    /// <summary>
    /// 읽기 전용 트랜잭션을 연다(사용자 함수가 든 벤치마크). 시작하지 못하면 예외 — 문장은 보내지 않는다.
    /// 앞의 열린 트랜잭션을 되돌려야 하면 모니터 세션에서만 한다(사용자 작업 세션의 미커밋 작업을 지우지 않게).
    /// </summary>
    protected abstract Task BeginReadOnlyAsync(ISession s, CancellationToken ct);

    /// <summary>벤치마크가 논리 읽기로 세는 세션 통계 이름(<see cref="SessionStatisticsSource"/> 가 주는 이름). DBMS 마다 다르면 재정의한다.</summary>
    protected virtual string LogicalReadsStatistic => "session logical reads";

    /// <summary>벤치마크가 물리 읽기로 세는 세션 통계 이름. DBMS 마다 다르면 재정의한다.</summary>
    protected virtual string PhysicalReadsStatistic => "physical reads";

    /// <summary>소유자·표 이름은 딕셔너리 표기 그대로(보통 대문자) 묶는다.</summary>
    private static IReadOnlyList<CatalogBind> BindTable(string owner, string table)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        return [CatalogBinds.Trimmed("owner", owner), CatalogBinds.Trimmed("table_name", table)];
    }

    private static IReadOnlyList<CatalogBind> TableBinds(string owner, string table) =>
        CatalogBinds.Defer(() => BindTable(owner, table));

    private static IReadOnlyList<CatalogBind> SqlIdBinds(string sqlId) =>
        CatalogBinds.Defer(() => [CatalogBinds.Trimmed("sql_id", CheckSqlId(sqlId))]);

    private async Task<BenchmarkResult> RunBenchmarkCoreAsync(ISession s, BenchmarkCase benchmark, SqlStatement stmt, int iterations, CancellationToken ct)
    {
        var binds = BenchmarkBinds.For(stmt.BindNames, benchmark.Binds); // 문장이 쓰는 이름만(검사는 Benchmark.Statement 가 했다)
        var elapsed = TimeSpan.Zero;
        long logical = 0, physical = 0, rows = 0;
        long? planHash = null;
        for (var i = 0; i < iterations; i++)
        {
            ct.ThrowIfCancellationRequested();
            var before = _statistics is null ? null : await SessionStatistics.CaptureAsync(_statistics, _execution, s, ct);

            var sw = Stopwatch.StartNew();
            var result = await _execution.ExecuteAsync(s, stmt, binds,
                new ExecutionOptions { Source = benchmark.Source, SkipHistory = i > 0 }, ct);
            rows = await Benchmark.DrainAsync(result, ct);
            sw.Stop();
            elapsed += sw.Elapsed;

            // 첫 회만: 직전 문장(방금 끝낸 벤치마크 문장)의 계획 해시. v$ 고정 뷰라 논리 읽기는 거의 없다.
            if (i == 0)
            {
                planHash = await PreviousPlanHashAsync(s, ct);
            }

            var after = _statistics is null ? null : await SessionStatistics.CaptureAsync(_statistics, _execution, s, ct);
            if (before is not null && after is not null)
            {
                var diff = SessionStatistics.Diff(before, after);
                logical += Stat(diff, LogicalReadsStatistic);
                physical += Stat(diff, PhysicalReadsStatistic);
            }
        }

        return new BenchmarkResult(benchmark, iterations, elapsed / iterations, logical / iterations, physical / iterations, rows, planHash);

        static long Stat(IReadOnlyDictionary<string, decimal> diff, string name) =>
            diff.TryGetValue(name, out var v) ? (long)v : 0;
    }
}
