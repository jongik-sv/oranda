using Oranda.Core.Connection;
using CorePlan = Oranda.Core.Plans.ExecutionPlan;

namespace Oranda.App.Features.ExecutionPlan;

/// <summary>
/// Oracle 이 아닌 DBMS(Tibero)의 실제 계획에서 「통계를 안 모았을 때 실제 열이 null 이 아니라 0」인 경우를 걸러
/// 화면이 0 을 실제 값처럼 보이지 않게 한다(P6b, tb-base 실측: GATHER_SQL_PLAN_STAT 를 v$parameter 로 못 읽고, 안 모으면 ActualRows·Starts·ActualTime 이 0).
/// 판정은 서비스 결과만으로: 실제 계획(커서 있음)이고 모든 단계의 세 실제 열이 0(또는 null)이며 하나라도 0 으로 채워져 있으면 「실제 통계 없음」으로 본다.
/// Oracle 은 건드리지 않는다(Oracle 은 안 모으면 null 이다).
/// </summary>
public static class PlanActuals
{
    /// <summary>화면 상태 줄에 붙이는 이유.</summary>
    public static string Reason(Dbms dbms) =>
        $"실제 통계 없음 — {dbms} 는 SQL 실행 통계를 모으지 않아 실제 열이 모두 0 이라 숨겼습니다(GATHER_SQL_PLAN_STAT 를 켠 뒤 다시 실행)";

    /// <summary>
    /// 0 으로 채워진 실제 열이면 그 열을 비운 계획(<paramref name="reason"/> 에 이유)을, 아니면 <paramref name="plan"/> 그대로(<paramref name="reason"/> null).
    /// </summary>
    public static CorePlan Normalize(CorePlan plan, Dbms dbms, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(plan);
        reason = null;
        if (dbms == Dbms.Oracle || plan.Cursor is null || plan.Steps.Count == 0)
        {
            return plan;
        }

        var anyZero = false;
        foreach (var s in plan.Steps)
        {
            if (s.ActualRows is > 0 || s.Starts is > 0 || s.ActualTime is { } t && t > TimeSpan.Zero)
            {
                return plan; // 실제 값이 하나라도 있으면 통계가 모인 것이다
            }

            anyZero |= s.ActualRows is 0 || s.Starts is 0 || s.ActualTime is { } z && z == TimeSpan.Zero;
        }

        if (!anyZero)
        {
            return plan; // 이미 모두 null — 화면이 「A-Rows 없음」 띠로 다룬다
        }

        reason = Reason(dbms);
        return plan with { Steps = [.. plan.Steps.Select(s => s with { ActualRows = null, Starts = null, ActualTime = null })] };
    }
}
