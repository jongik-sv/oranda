using System.Globalization;

namespace Oranda.Core.Tuning;

// 튜닝 추천(TSK-14-17, D-051). 공유 풀 통계(gv$sqlstats) 한 줄 + 선택적 계획 요약을 규칙으로 점수 매긴다. LLM·외부 서비스 없음, 순수 함수.
// 조회는 카탈로그 tuning.recommend_candidates·tuning.recommend_literals·tuning.recommend_plan(SELECT 전용, 팩 없음).

/// <summary>후보 SQL 한 줄(gv$sqlstats: sql_id·plan_hash 단위 누적). 시간은 마이크로초.</summary>
public sealed record SqlStatsSample(string SqlId)
{
    public int? InstanceId { get; init; }

    public long PlanHashValue { get; init; }

    /// <summary>파싱 스키마(gv$sql 에서 붙임). 커서가 밀려났으면 null.</summary>
    public string? ParsingSchema { get; init; }

    /// <summary>SQL 앞부분(gv$sqlstats.sql_text, 최대 1000자).</summary>
    public string SqlText { get; init; } = string.Empty;

    public long Executions { get; init; }

    public long ElapsedMicros { get; init; }

    public long CpuMicros { get; init; }

    public long BufferGets { get; init; }

    public long DiskReads { get; init; }

    public long RowsProcessed { get; init; }

    public long ParseCalls { get; init; }

    public long VersionCount { get; init; }

    /// <summary>force_matching_signature(부호 없는 64비트라 글자로 둔다). 0·null 이면 없음.</summary>
    public string? ForceMatchingSignature { get; init; }

    public long UserIoWaitMicros { get; init; }

    public long ApplicationWaitMicros { get; init; }

    public long ConcurrencyWaitMicros { get; init; }

    public long ClusterWaitMicros { get; init; }

    public DateTime? LastActiveTime { get; init; }

    /// <summary>리터럴만 다르고 같은 force_matching_signature 를 가진 sql_id 수(자기 포함). 모르면 0.</summary>
    public int SimilarSqlCount { get; init; }

    /// <summary>true 면 이 줄은 리터럴 묶음(여러 sql_id 의 합)이고 <see cref="SqlId"/> 는 표본이다.</summary>
    public bool IsLiteralGroup { get; init; }
}

/// <summary>리터럴 묶음 한 줄(같은 force_matching_signature, sql_id 여럿). 숫자는 묶음 합.</summary>
public sealed record LiteralSqlGroup(string ForceMatchingSignature, int SqlCount, SqlStatsSample Sample);

/// <summary>후보 조회 조건.</summary>
public sealed record TuningCandidateQuery
{
    /// <summary>누적 경과 시간 상위 몇 개를 읽을지(부하 제한).</summary>
    public int Top { get; init; } = TuningRecommendationRules.DefaultCandidateCount;

    public long MinExecutions { get; init; } = 1;

    /// <summary>이 sql_id 들만(세션 모니터 — 활성 세션 SQL). null 이면 전체.</summary>
    public IReadOnlyList<string>? SqlIds { get; init; }

    /// <summary>리터럴 묶음도 읽을지(전체 조회 때만).</summary>
    public bool IncludeLiteralGroups { get; init; } = true;
}

/// <summary>후보 조회 결과. <see cref="TotalElapsedMicros"/> = 조건에 맞는 모든 SQL 의 경과 시간 합(비중 계산용).</summary>
public sealed record TuningCandidateSet(IReadOnlyList<SqlStatsSample> Rows, long TotalElapsedMicros)
{
    public IReadOnlyList<LiteralSqlGroup> LiteralGroups { get; init; } = [];
}

/// <summary>계획에서 눈여겨볼 줄 하나(gv$sql_plan). 전체 스캔 표는 딕셔너리 통계(num_rows·blocks)를 붙인다.</summary>
public sealed record PlanFindingLine(int Id, string Operation, string? Options, string? ObjectOwner, string? ObjectName)
{
    public long? Cardinality { get; init; }

    public long? TableRows { get; init; }

    public long? TableBlocks { get; init; }
}

/// <summary>추천 규칙.</summary>
public enum TuningRule
{
    SlowPerExecution,
    TimeShare,
    ReadsPerExecution,
    ReadsPerRow,
    DiskReadsPerExecution,
    FullScanLargeTable,
    IndexFullScan,
    CartesianJoin,
    ManyChildCursors,
    LiteralSql,
    ParsePerExecution,
    WaitHeavy,
    CpuHeavy,
}

/// <summary>걸린 규칙 하나: 점수, 짧은 이유, 근거 수치 글.</summary>
public sealed record TuningReason(TuningRule Rule, int Points, string Title, string Evidence);

/// <summary>추천 한 줄: 점수(0~100) 큰 순.</summary>
public sealed record TuningRecommendation(SqlStatsSample Sql, int Score, IReadOnlyList<TuningReason> Reasons)
{
    /// <summary>실행당 경과(초). 실행 0 이면 null.</summary>
    public double? ElapsedPerExecSeconds => Sql.Executions > 0 ? Sql.ElapsedMicros / 1_000_000d / Sql.Executions : null;

    /// <summary>실행당 논리 읽기. 실행 0 이면 null.</summary>
    public double? GetsPerExec => Sql.Executions > 0 ? (double)Sql.BufferGets / Sql.Executions : null;

    /// <summary>이유 요약(점수 큰 순 제목을 「·」로).</summary>
    public string Summary => string.Join(" · ", Reasons.Select(r => r.Title));
}

/// <summary>추천 거르기·점수 선택지(이번에는 설정화하지 않는다 — 화면 토글만).</summary>
public sealed record TuningRecommendationOptions
{
    /// <summary>SYS·SYSTEM 같은 Oracle 내부 스키마와 옵티마이저 재귀 SQL 을 뺀다.</summary>
    public bool ExcludeInternalSchemas { get; init; } = true;

    /// <summary>보일 최대 줄 수.</summary>
    public int Top { get; init; } = 100;

    /// <summary>
    /// 실행 횟수 하한(기본 1 — 실행 1회 미만 제외). 세션 모니터는 0 으로 낮춰 지금 처음 도는 SQL(실행 수가 끝나야 오른다)도 본다.
    /// </summary>
    public long MinExecutions { get; init; } = 1;
}

/// <summary>
/// 규칙 기반 튜닝 추천(TSK-14-17). 문턱값은 상수(D-051). 점수 = 걸린 규칙 점수 합(최대 100). 하나도 안 걸리면 추천하지 않는다.
/// 대상 제외: 실행 1회 미만, 누적 경과 <see cref="MinTotalElapsedSeconds"/> 미만(리터럴 묶음은 예외), 내부 스키마·재귀 SQL(끌 수 있음).
/// </summary>
public static class TuningRecommendationRules
{
    /// <summary>후보로 읽는 기본 줄 수(누적 경과 상위).</summary>
    public const int DefaultCandidateCount = 200;

    /// <summary>계획을 더 읽어 보는 점수 상위 개수(부하 제한).</summary>
    public const int PlanCheckCount = 5;

    /// <summary>리터럴 묶음으로 보는 sql_id 수 하한.</summary>
    public const int LiteralGroupMinCount = 10;

    public const int LiteralGroupMaxCount = 20;

    public const double MinTotalElapsedSeconds = 0.1;

    public const double SlowPerExecSeconds = 1;

    public const double VerySlowPerExecSeconds = 10;

    public const double TimeShareRatio = 0.10;

    public const double BigTimeShareRatio = 0.30;

    public const double GetsPerExec = 10_000;

    public const double BigGetsPerExec = 100_000;

    public const double GetsPerRow = 1_000;

    public const double DiskReadsPerExec = 1_000;

    public const double BigDiskReadsPerExec = 10_000;

    public const long LargeTableBlocks = 10_000;

    public const long LargeTableRows = 1_000_000;

    /// <summary>통계가 없는 표의 전체 스캔은 계획 예상 행으로 본다.</summary>
    public const long LargeScanCardinality = 100_000;

    public const long IndexFullScanCardinality = 10_000;

    public const long ManyChildCursors = 20;

    public const long VeryManyChildCursors = 100;

    public const int ManyLiterals = 100;

    /// <summary>파싱 호출/실행 비율 문턱(실행 <see cref="ParseMinExecutions"/> 회 이상일 때만).</summary>
    public const double ParsePerExecRatio = 0.9;

    public const long ParseMinExecutions = 100;

    /// <summary>대기 비중 문턱(실행당 0.1초 이상일 때만).</summary>
    public const double WaitShareRatio = 0.7;

    public const double CpuShareRatio = 0.9;

    public const double WaitMinPerExecSeconds = 0.1;

    /// <summary>Oracle 이 만든 내부 스키마(기본 제외).</summary>
    public static readonly IReadOnlySet<string> InternalSchemas = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "SYS", "SYSTEM", "SYSMAN", "DBSNMP", "XDB", "MDSYS", "CTXSYS", "ORDSYS", "ORDDATA", "OLAPSYS", "WMSYS", "EXFSYS",
        "OUTLN", "APPQOSSYS", "AUDSYS", "GSMADMIN_INTERNAL", "OJVMSYS", "LBACSYS", "DVSYS", "DBSFWUSER", "GGSYS",
        "REMOTE_SCHEDULER_AGENT", "SYSBACKUP", "SYSDG", "SYSKM", "SYSRAC", "ANONYMOUS", "XS$NULL", "SI_INFORMTN_SCHEMA",
        "FLOWS_FILES", "MGMT_VIEW", "ORACLE_OCM", "DIP", "SPATIAL_CSW_ADMIN_USR", "SPATIAL_WFS_ADMIN_USR",
    };

    /// <summary>옵티마이저·통계 수집이 스스로 만드는 재귀 SQL 표지(사용자 스키마로 파싱돼도 뺀다).</summary>
    public static readonly IReadOnlyList<string> RecursiveSqlMarkers =
        ["/* DS_SVC */", "/* OPT_DYN_SAMP */", "/* SQL Analyze(", "/*+ DYNAMIC_SAMPLING", "/* KSXM:", "DBMS_STATS"];

    /// <summary>내부 스키마인가(APEX_* 포함).</summary>
    public static bool IsInternalSchema(string? schema) =>
        !string.IsNullOrWhiteSpace(schema)
        && (InternalSchemas.Contains(schema.Trim()) || schema.Trim().StartsWith("APEX_", StringComparison.OrdinalIgnoreCase));

    /// <summary>내부 스키마 SQL 이거나 옵티마이저 재귀 SQL 인가.</summary>
    public static bool IsInternal(SqlStatsSample s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return IsInternalSchema(s.ParsingSchema)
            || RecursiveSqlMarkers.Any(m => s.SqlText.Contains(m, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 후보 묶음을 거르고 점수 매겨 큰 순으로(같으면 누적 경과 큰 순). 리터럴 묶음은 같은 signature 의 후보 줄에 붙이고,
    /// 후보에 없는 묶음은 표본 줄로 더한다. 계획 요약은 sql_id 로 찾는다(없으면 계획 규칙을 건너뛴다).
    /// </summary>
    public static IReadOnlyList<TuningRecommendation> Rank(
        TuningCandidateSet set,
        TuningRecommendationOptions? options = null,
        IReadOnlyDictionary<string, IReadOnlyList<PlanFindingLine>>? plans = null)
    {
        ArgumentNullException.ThrowIfNull(set);
        options ??= new TuningRecommendationOptions();
        var groups = new Dictionary<string, LiteralSqlGroup>(StringComparer.Ordinal);
        foreach (var g in set.LiteralGroups)
        {
            if (HasSignature(g.ForceMatchingSignature))
            {
                groups[g.ForceMatchingSignature] = g;
            }
        }

        var samples = new List<SqlStatsSample>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in set.Rows)
        {
            var s = row;
            if (HasSignature(row.ForceMatchingSignature) && groups.TryGetValue(row.ForceMatchingSignature!, out var g))
            {
                s = row with { SimilarSqlCount = Math.Max(row.SimilarSqlCount, g.SqlCount) };
                used.Add(g.ForceMatchingSignature);
            }

            samples.Add(s);
        }

        foreach (var g in groups.Values.Where(g => !used.Contains(g.ForceMatchingSignature)))
        {
            samples.Add(g.Sample with { SimilarSqlCount = g.SqlCount, IsLiteralGroup = true, ForceMatchingSignature = g.ForceMatchingSignature });
        }

        var total = set.TotalElapsedMicros; // 0 이면(sql_id 로 좁힌 조회) 비중 규칙을 건너뛴다
        var result = new List<TuningRecommendation>();
        foreach (var s in samples)
        {
            if (!IsTarget(s, options))
            {
                continue;
            }

            IReadOnlyList<PlanFindingLine>? plan = null;
            plans?.TryGetValue(s.SqlId, out plan);
            var rec = Evaluate(s, total, plan);
            if (rec.Reasons.Count > 0)
            {
                result.Add(rec);
            }
        }

        return [.. result
            .OrderByDescending(r => r.Score)
            .ThenByDescending(r => r.Sql.ElapsedMicros)
            .ThenBy(r => r.Sql.SqlId, StringComparer.Ordinal)
            .Take(Math.Max(1, options.Top))];
    }

    /// <summary>추천 대상인가(실행·경과 하한, 내부 제외).</summary>
    public static bool IsTarget(SqlStatsSample s, TuningRecommendationOptions options)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(options);
        if (s.Executions < options.MinExecutions || (s.Executions < 1 && s.ElapsedMicros <= 0))
        {
            return false;
        }

        if (!s.IsLiteralGroup && s.ElapsedMicros < MinTotalElapsedSeconds * 1_000_000)
        {
            return false;
        }

        return !options.ExcludeInternalSchemas || !IsInternal(s);
    }

    /// <summary>
    /// 한 줄 평가. <paramref name="totalElapsedMicros"/> = 비중 계산 분모(0 이하면 비중 규칙을 건너뛴다).
    /// <paramref name="plan"/> 이 null 이면 계획 규칙을 건너뛴다.
    /// </summary>
    public static TuningRecommendation Evaluate(SqlStatsSample s, long totalElapsedMicros, IReadOnlyList<PlanFindingLine>? plan = null)
    {
        ArgumentNullException.ThrowIfNull(s);
        var reasons = new List<TuningReason>();
        // 실행 0 인데 경과가 있으면 지금 처음 도는 실행 하나로 본다(실행 수는 실행이 끝나야 오른다).
        var exec = s.Executions > 0 ? s.Executions : s.ElapsedMicros > 0 ? 1 : 0;
        var elapsedSec = s.ElapsedMicros / 1_000_000d;
        var perExec = exec > 0 ? elapsedSec / exec : (double?)null;
        var getsPerExec = exec > 0 ? (double)s.BufferGets / exec : (double?)null;
        var diskPerExec = exec > 0 ? (double)s.DiskReads / exec : (double?)null;

        if (perExec is { } pe && pe >= SlowPerExecSeconds)
        {
            reasons.Add(new(TuningRule.SlowPerExecution, pe >= VerySlowPerExecSeconds ? 45 : 30, "실행당 오래 걸림",
                $"실행당 경과 {Sec(pe)} (문턱 {Sec(SlowPerExecSeconds)}) · 실행 {N(exec)}회"));
        }

        if (!s.IsLiteralGroup && totalElapsedMicros > 0 && s.ElapsedMicros > 0)
        {
            var share = (double)s.ElapsedMicros / totalElapsedMicros;
            if (share >= TimeShareRatio)
            {
                reasons.Add(new(TuningRule.TimeShare, share >= BigTimeShareRatio ? 35 : 25, "전체 경과 시간 비중 큼",
                    $"공유 풀 SQL 경과 시간 합의 {Pct(share)} ({Sec(elapsedSec)} / {Sec(totalElapsedMicros / 1_000_000d)})"));
            }
        }

        if (getsPerExec is { } g && g >= GetsPerExec)
        {
            reasons.Add(new(TuningRule.ReadsPerExecution, g >= BigGetsPerExec ? 25 : 15, "실행당 논리 읽기 많음",
                $"실행당 buffer_gets {N(g)} (문턱 {N(GetsPerExec)})"));
        }

        // 처리 행 대비 읽기: 행이 0 이면(나눌 수 없음) 건너뛴다. 실행당 읽기가 작은 SQL 은 비율이 커도 뺀다.
        if (s.RowsProcessed > 0 && getsPerExec is { } g2 && g2 >= GetsPerRow)
        {
            var perRow = (double)s.BufferGets / s.RowsProcessed;
            if (perRow >= GetsPerRow)
            {
                reasons.Add(new(TuningRule.ReadsPerRow, 15, "처리 행 대비 읽기 많음",
                    $"행당 buffer_gets {N(perRow)} (문턱 {N(GetsPerRow)}) · 처리 행 {N(s.RowsProcessed)}"));
            }
        }

        if (diskPerExec is { } d && d >= DiskReadsPerExec)
        {
            reasons.Add(new(TuningRule.DiskReadsPerExecution, d >= BigDiskReadsPerExec ? 25 : 15, "실행당 물리 읽기 많음",
                $"실행당 disk_reads {N(d)} (문턱 {N(DiskReadsPerExec)})"));
        }

        if (plan is not null)
        {
            AddPlanReasons(plan, reasons);
        }

        if (s.VersionCount >= ManyChildCursors)
        {
            reasons.Add(new(TuningRule.ManyChildCursors, s.VersionCount >= VeryManyChildCursors ? 25 : 15, "자식 커서 많음",
                $"version_count {N(s.VersionCount)} (문턱 {N(ManyChildCursors)}) — 자식 커서 화면에서 공유 안 된 이유 확인"));
        }

        if (s.SimilarSqlCount >= LiteralGroupMinCount)
        {
            reasons.Add(new(TuningRule.LiteralSql, s.SimilarSqlCount >= ManyLiterals ? 30 : 20, "리터럴 SQL 반복(바인드 변수 권장)",
                $"리터럴만 다른 sql_id {N(s.SimilarSqlCount)}개 (문턱 {N(LiteralGroupMinCount)})"));
        }

        if (exec >= ParseMinExecutions && s.ParseCalls >= exec * ParsePerExecRatio)
        {
            reasons.Add(new(TuningRule.ParsePerExecution, 10, "실행마다 파싱(커서 재사용 안 됨)",
                $"parse_calls {N(s.ParseCalls)} / 실행 {N(exec)} = {Pct((double)s.ParseCalls / exec)}"));
        }

        if (s.ElapsedMicros > 0 && perExec is { } pw && pw >= WaitMinPerExecSeconds)
        {
            var cpuShare = Math.Clamp((double)s.CpuMicros / s.ElapsedMicros, 0, 1);
            if (1 - cpuShare >= WaitShareRatio)
            {
                reasons.Add(new(TuningRule.WaitHeavy, 10, "대기 비중 큼",
                    $"경과의 {Pct(1 - cpuShare)} 가 대기 — 가장 큰 대기: {TopWait(s)}"));
            }
            else if (cpuShare >= CpuShareRatio && pw >= SlowPerExecSeconds)
            {
                reasons.Add(new(TuningRule.CpuHeavy, 5, "CPU 위주",
                    $"경과의 {Pct(cpuShare)} 가 CPU — 논리 읽기·정렬·함수 호출을 줄일 곳"));
            }
        }

        var score = Math.Min(100, reasons.Sum(r => r.Points));
        return new TuningRecommendation(s, score, [.. reasons.OrderByDescending(r => r.Points)]);
    }

    private static void AddPlanReasons(IReadOnlyList<PlanFindingLine> plan, List<TuningReason> reasons)
    {
        var full = plan.Where(IsLargeFullScan).ToList();
        if (full.Count > 0)
        {
            reasons.Add(new(TuningRule.FullScanLargeTable, 20, "큰 표 전체 스캔",
                string.Join(", ", full.Take(3).Select(l => $"Id {l.Id} {Name(l)} {Size(l)}"))));
        }

        var index = plan.Where(l => Is(l, "INDEX", "FULL SCAN") && (l.Cardinality ?? 0) >= IndexFullScanCardinality).ToList();
        if (index.Count > 0)
        {
            reasons.Add(new(TuningRule.IndexFullScan, 10, "INDEX FULL SCAN",
                string.Join(", ", index.Take(3).Select(l => $"Id {l.Id} {Name(l)} 예상 {N(l.Cardinality ?? 0)}행"))));
        }

        var cartesian = plan.Where(l => string.Equals(l.Options, "CARTESIAN", StringComparison.OrdinalIgnoreCase)).ToList();
        if (cartesian.Count > 0)
        {
            reasons.Add(new(TuningRule.CartesianJoin, 25, "MERGE JOIN CARTESIAN",
                string.Join(", ", cartesian.Take(3).Select(l => $"Id {l.Id}")) + " — 조인 조건 빠짐 의심"));
        }
    }

    /// <summary>큰 표 전체 스캔인가: 통계가 있으면 blocks·num_rows, 없으면 계획 예상 행.</summary>
    public static bool IsLargeFullScan(PlanFindingLine l)
    {
        ArgumentNullException.ThrowIfNull(l);
        if (!string.Equals(l.Operation, "TABLE ACCESS", StringComparison.OrdinalIgnoreCase)
            || l.Options?.Contains("FULL", StringComparison.OrdinalIgnoreCase) != true)
        {
            return false;
        }

        if (l.TableBlocks is not null || l.TableRows is not null)
        {
            return (l.TableBlocks ?? 0) >= LargeTableBlocks || (l.TableRows ?? 0) >= LargeTableRows;
        }

        return (l.Cardinality ?? 0) >= LargeScanCardinality;
    }

    private static bool Is(PlanFindingLine l, string op, string options) =>
        string.Equals(l.Operation, op, StringComparison.OrdinalIgnoreCase) && string.Equals(l.Options, options, StringComparison.OrdinalIgnoreCase);

    private static bool HasSignature(string? fms) => !string.IsNullOrWhiteSpace(fms) && fms.Trim() != "0";

    private static string Name(PlanFindingLine l) =>
        string.IsNullOrEmpty(l.ObjectOwner) ? l.ObjectName ?? "?" : l.ObjectOwner + "." + l.ObjectName;

    private static string Size(PlanFindingLine l) => l.TableBlocks is not null || l.TableRows is not null
        ? $"({N(l.TableRows ?? 0)}행·{N(l.TableBlocks ?? 0)}블록)"
        : $"(예상 {N(l.Cardinality ?? 0)}행)";

    private static string TopWait(SqlStatsSample s)
    {
        var waits = new (string Name, long Micros)[]
        {
            ("User I/O", s.UserIoWaitMicros), ("Application(잠금)", s.ApplicationWaitMicros),
            ("Concurrency", s.ConcurrencyWaitMicros), ("Cluster", s.ClusterWaitMicros),
        };
        var top = waits.MaxBy(w => w.Micros);
        return top.Micros <= 0 ? "기타" : $"{top.Name} {Sec(top.Micros / 1_000_000d)}";
    }

    private static string N(double v) => v.ToString("N0", CultureInfo.InvariantCulture);

    private static string Sec(double seconds) => seconds >= 100
        ? seconds.ToString("N0", CultureInfo.InvariantCulture) + " s"
        : seconds.ToString("0.00", CultureInfo.InvariantCulture) + " s";

    private static string Pct(double ratio) => (ratio * 100).ToString("0", CultureInfo.InvariantCulture) + " %";
}
