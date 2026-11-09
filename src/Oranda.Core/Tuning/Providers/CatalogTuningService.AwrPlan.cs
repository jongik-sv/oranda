using Oranda.Core.Catalog.Providers;
using Oranda.Core.Plans;
using Oranda.Core.Sessions;
using static Oranda.Core.Tuning.Providers.TuningRows;

namespace Oranda.Core.Tuning.Providers;

/// <summary>AWR 에 남은 계획 단계(TSK-06-06 두 plan_hash 비교). 카탈로그 <c>tuning.awr_plan_steps</c>(license: diagnostics) — 선언이 모자라면 보내지 않는다.</summary>
public abstract partial class CatalogTuningService
{
    public async Task<ExecutionPlan> GetAwrPlanAsync(ISession s, string sqlId, long planHashValue, CancellationToken ct)
    {
        var id = CheckSqlId(sqlId);
        ArgumentOutOfRangeException.ThrowIfNegative(planHashValue);
        var rows = (await QueryAsync(s, "tuning.awr_plan_steps",
            [CatalogBinds.Trimmed("sql_id", id), CatalogBinds.Long("plan_hash_value", planHashValue)], TuningQueryOptions, ct)).Rows;
        return ToAwrPlan(rows, planHashValue);
    }
}
