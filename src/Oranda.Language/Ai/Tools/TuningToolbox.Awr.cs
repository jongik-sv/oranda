using System.Globalization;
using Oranda.Core.Connection;
using Oranda.Core.Sessions;
using Oranda.Core.Tuning;

namespace Oranda.Language.Ai.Tools;

/// <summary>AWR 도구 응답의 크기 한도.</summary>
public static class AwrToolLimits
{
    /// <summary><c>get_awr_plan_history</c> 가 싣는 스냅숏 줄 수(가장 최근 것부터). 계획별 요약은 늘 전부로 낸다.</summary>
    public const int MaxSnapshots = 120;
}

/// <summary><c>get_awr_plan_history</c> 의 계획 하나: 쓰인 기간(스냅숏 끝 시각, 서버 시각)과 실행당 값.</summary>
public sealed record AwrPlanSummary(
    long PlanHash, string FirstSnapTime, string LastSnapTime, int Snapshots, long Executions, double? ElapsedMsPerExec, double? BufferGetsPerExec);

/// <summary><c>get_awr_plan_history</c> 의 스냅숏 구간 하나. 실행이 0 이면 실행당 값은 null.</summary>
public sealed record AwrSnapshotLine(long SnapId, string SnapTime, long PlanHash, long Executions, double? ElapsedMsPerExec, double? BufferGetsPerExec);

/// <summary>
/// <c>get_awr_plan_history</c> 응답. 쓸 수 없는 서버(팩 선언 없음·서버 설정·AWR 이 없는 DBMS)는 <see cref="Supported"/> 가 거짓이고 <see cref="Reason"/> 에 까닭을 적는다.
/// </summary>
public sealed record AwrPlanHistoryDescription(
    bool Supported, string? Reason, string SqlId, IReadOnlyList<AwrPlanSummary> Plans, IReadOnlyList<AwrSnapshotLine> Snapshots, int OmittedSnapshots);

/// <summary><c>get_awr_plan</c> 응답. 계획 줄의 술어는 <c>access</c>·<c>filter</c> 이름이라 게이트가 가린다. 줄이 남아 있지 않으면 <see cref="Found"/> 가 거짓.</summary>
public sealed record AwrPlanDescription(bool Supported, string? Reason, string SqlId, long PlanHash, bool Found, IReadOnlyList<PlanStepDescription> Steps);

public sealed partial class TuningToolbox
{
    /// <summary>팩 선언이 모자라 카탈로그 관문이 보내지 않았을 때(LLM 이 읽는 글: 접속 이름·서버 값 원문은 싣지 않는다).</summary>
    internal const string AwrPackMissing =
        "AWR 을 읽지 않았습니다: 이 접속에 Diagnostics Pack 보유가 선언되어 있지 않거나 서버 설정(control_management_pack_access)이 막습니다. AWR 없이 지금 커서(get_cursor_plan·get_sql_stats)로 보십시오.";

    /// <summary>DBMS 에 AWR 계획 이력이 없을 때.</summary>
    internal const string AwrNotAvailable = "이 DBMS 는 AWR 계획 이력을 지원하지 않습니다. 지금 커서(get_cursor_plan·get_sql_stats)로 보십시오.";

    /// <summary>
    /// <c>get_awr_plan_history</c>: AWR 스냅숏 구간별 plan_hash_value·실행·경과·버퍼(<see cref="ITuningService.GetAwrPlanHistoryAsync"/>)를 계획별로 합치고,
    /// 스냅숏 줄은 가장 최근 <see cref="AwrToolLimits.MaxSnapshots"/> 개만 싣는다. 읽기만 한다.
    /// </summary>
    public async Task<AwrPlanHistoryDescription> GetAwrPlanHistoryAsync(ISession s, string sqlId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentException.ThrowIfNullOrWhiteSpace(sqlId);
        if (AwrUnavailable(s) is { } reason)
        {
            return new AwrPlanHistoryDescription(false, reason, sqlId, [], [], 0);
        }

        IReadOnlyList<AwrPlanRow> rows;
        try
        {
            rows = await tuning.GetAwrPlanHistoryAsync(s, sqlId, ct).ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException)
        {
            return new AwrPlanHistoryDescription(false, AwrPackMissing, sqlId, [], [], 0);
        }
        catch (NotSupportedException)
        {
            return new AwrPlanHistoryDescription(false, AwrNotAvailable, sqlId, [], [], 0);
        }

        var ordered = rows.OrderBy(r => r.SnapId).ThenBy(r => r.PlanHashValue).ToList();
        var plans = ordered
            .GroupBy(r => r.PlanHashValue)
            .OrderBy(g => g.Min(r => r.SnapId))
            .Select(g =>
            {
                var execs = g.Sum(r => r.Executions);
                return new AwrPlanSummary(
                    g.Key, SnapTime(g.Min(r => r.SnapTime)), SnapTime(g.Max(r => r.SnapTime)), g.Select(r => r.SnapId).Distinct().Count(), execs,
                    PerExec(g.Sum(r => r.ElapsedMicros) / 1000d, execs), PerExec(g.Sum(r => r.BufferGets), execs));
            })
            .ToList();
        var omitted = Math.Max(0, ordered.Count - AwrToolLimits.MaxSnapshots);
        var lines = ordered.Skip(omitted)
            .Select(r => new AwrSnapshotLine(r.SnapId, SnapTime(r.SnapTime), r.PlanHashValue, r.Executions, PerExec(r.ElapsedMicros / 1000d, r.Executions), PerExec(r.BufferGets, r.Executions)))
            .ToList();
        return new AwrPlanHistoryDescription(true, null, sqlId, plans, lines, omitted);
    }

    /// <summary><c>get_awr_plan</c>: AWR(dba_hist_sql_plan, Tibero 는 TPR)에 남은 한 계획의 줄. 읽기만 한다.</summary>
    public async Task<AwrPlanDescription> GetAwrPlanAsync(ISession s, string sqlId, long planHashValue, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentException.ThrowIfNullOrWhiteSpace(sqlId);
        if (AwrUnavailable(s) is { } reason)
        {
            return new AwrPlanDescription(false, reason, sqlId, planHashValue, false, []);
        }

        Oranda.Core.Plans.ExecutionPlan plan;
        try
        {
            plan = await tuning.GetAwrPlanAsync(s, sqlId, planHashValue, ct).ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException)
        {
            return new AwrPlanDescription(false, AwrPackMissing, sqlId, planHashValue, false, []);
        }
        catch (NotSupportedException)
        {
            return new AwrPlanDescription(false, AwrNotAvailable, sqlId, planHashValue, false, []);
        }

        var steps = Describe(plan, null).Steps;
        return new AwrPlanDescription(true, null, sqlId, planHashValue, steps.Count > 0, steps);
    }

    /// <summary>DBMS 기능 표가 AWR 계획 이력을 끈 DBMS 면 그 까닭(보내지 않는다). 팩 선언은 카탈로그 관문이 본다.</summary>
    private static string? AwrUnavailable(ISession s) =>
        DbmsSupport.Supports(s.Profile.Dbms, DbmsFeature.AwrReport) ? null : AwrNotAvailable;

    private static string SnapTime(DateTime time) => time.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    private static double? PerExec(double total, long executions) => executions > 0 ? Math.Round(total / executions, 3) : null;
}
