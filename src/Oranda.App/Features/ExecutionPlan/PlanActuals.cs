using Oranda.Core.Connection;
using Oranda.Core.Tuning;
using CorePlan = Oranda.Core.Plans.ExecutionPlan;

namespace Oranda.App.Features.ExecutionPlan;

/// <summary>
/// Oracle 이 아닌 DBMS(Tibero)의 실제 계획에서 「통계를 안 모았을 때 실제 열이 null 이 아니라 0」인 경우를 걸러
/// 화면이 0 을 실제 값처럼 보이지 않게 한다(P6b, tb-base 실측: 안 모으면 ActualRows·Starts·ActualTime 이 0).
/// 판정 근거: 세션의 환경 값 <c>gather_sql_plan_stat</c>(<c>tuning.report_env</c>)을 읽었으면 그것을 따르고,
/// 못 읽었으면(<c>null</c>) 서비스 결과만으로 본다 — 모든 단계의 세 실제 열이 0(또는 null)이고 0 이 하나라도 있으면 「실제 통계 없음」.
/// Oracle 은 건드리지 않는다(Oracle 은 안 모으면 null 이다).
/// </summary>
public static class PlanActuals
{
    /// <summary>환경 값 이름(<c>tuning.report_env</c> 의 PARAMETER 행, 소문자).</summary>
    public const string GatherParameter = "gather_sql_plan_stat";

    /// <summary>화면 상태 줄에 붙이는 이유(값을 못 읽고 0 으로 추정했을 때).</summary>
    public static string Reason(Dbms dbms) =>
        $"실제 통계 없음 — {dbms} 는 SQL 실행 통계를 모으지 않아 실제 열이 모두 0 이라 숨겼습니다(GATHER_SQL_PLAN_STAT 를 켠 뒤 다시 실행)";

    /// <summary>환경 값으로 확인했을 때의 이유(GATHER_SQL_PLAN_STAT 가 꺼져 있음).</summary>
    public static string ReasonOff(Dbms dbms) =>
        $"실제 통계 없음 — {dbms} 의 GATHER_SQL_PLAN_STAT 가 꺼져 있어 SQL 실행 통계를 모으지 않았습니다(켠 뒤 다시 실행)";

    /// <summary>
    /// 환경 값(<c>tuning.report_env</c> 결과)에서 SQL 실행 통계 수집 여부. 켜짐 true, 꺼짐 false, 행이 없거나 뜻을 모르는 값이면 null.
    /// </summary>
    public static bool? GatherStatsFrom(IReadOnlyList<TuningEnvRow>? env)
    {
        var value = env?.FirstOrDefault(r => r.Kind == TuningEnvKind.Parameter
            && string.Equals(r.Name, GatherParameter, StringComparison.OrdinalIgnoreCase))?.Value?.Trim();
        return value?.ToUpperInvariant() switch
        {
            "Y" or "YES" or "TRUE" or "ON" or "1" => true,
            "N" or "NO" or "FALSE" or "OFF" or "0" => false,
            _ => null,
        };
    }

    /// <summary>
    /// 환경 값을 물어볼 만한 계획인가: Oracle 이 아니고 실제 계획이며, 실제 열이 하나도 없거나 0 뿐이다(값이 하나라도 있으면 통계가 모인 것이라 물을 필요가 없다).
    /// </summary>
    public static bool NeedsEnvironmentCheck(CorePlan plan, Dbms dbms)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return dbms != Dbms.Oracle && plan.Cursor is not null && plan.Steps.Count > 0 && plan.Steps.All(IsZeroOrEmpty);
    }

    /// <summary>
    /// 실제 열이 수집되지 않은 것으로 판단되면 그 열을 비운 계획(<paramref name="reason"/> 에 이유)을, 아니면 <paramref name="plan"/> 그대로(<paramref name="reason"/> null).
    /// <paramref name="gatherStats"/>: 환경 값으로 읽은 수집 여부(<see cref="GatherStatsFrom"/>), 모르면 null(0 값 추정).
    /// 켜져 있다고 읽었으면(true) 0 도 실제 값이므로 그대로 둔다.
    /// </summary>
    public static CorePlan Normalize(CorePlan plan, Dbms dbms, out string? reason, bool? gatherStats = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        reason = null;
        if (!NeedsEnvironmentCheck(plan, dbms) || gatherStats == true)
        {
            return plan;
        }

        var anyZero = plan.Steps.Any(s => s.ActualRows is 0 || s.Starts is 0 || s.ActualTime is { } t && t == TimeSpan.Zero);
        if (gatherStats == false)
        {
            reason = ReasonOff(dbms); // 꺼져 있다고 읽었으면 0 이 있든 없든 이유를 보인다
        }
        else if (anyZero)
        {
            reason = Reason(dbms);
        }
        else
        {
            return plan; // 값도 모르고 이미 모두 null — 화면이 「A-Rows 없음」 띠로 다룬다
        }

        return anyZero
            ? plan with { Steps = [.. plan.Steps.Select(s => s with { ActualRows = null, Starts = null, ActualTime = null })] }
            : plan;
    }

    private static bool IsZeroOrEmpty(Oranda.Core.Plans.PlanStep s) =>
        !(s.ActualRows is > 0 || s.Starts is > 0 || s.ActualTime is { } t && t > TimeSpan.Zero);
}
