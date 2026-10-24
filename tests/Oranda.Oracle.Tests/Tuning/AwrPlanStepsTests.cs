using Oranda.Core.Catalog;
using Oranda.Oracle.Catalog;
using Oranda.Oracle.Tuning;

namespace Oranda.Oracle.Tests.Tuning;

// 소유: TSK-06-06. AWR 계획 단계(두 plan_hash 비교) — 줄 → 계획 변환과 카탈로그 머리(팩·바인드).
public class AwrPlanStepsTests
{
    [Fact]
    public void Rows_become_a_plan_in_id_order_with_hash_and_indented_text()
    {
        object?[][] rows =
        [
            [1m, 0m, 1m, "TABLE ACCESS", "FULL", "MES", "LOT_HIST", 9000m, 120000m, null, "\"EQP_ID\"=:1"],
            [0m, null, 0m, "SELECT STATEMENT", null, null, null, 9000m, null, null, null],
        ];

        var plan = OracleTuningService.ToAwrPlan(rows, 1838229974);

        Assert.Equal(1838229974, plan.PlanHashValue);
        Assert.Equal([0, 1], plan.Steps.Select(s => s.Id));
        var full = plan.Steps[1];
        Assert.Equal(0, full.ParentId);
        Assert.Equal(1, full.Depth);
        Assert.Equal("FULL", full.Options);
        Assert.Equal("LOT_HIST", full.ObjectName);
        Assert.Equal(9000, full.Cost);
        Assert.Equal(120000, full.EstimatedRows);
        Assert.Equal("\"EQP_ID\"=:1", full.FilterPredicates);
        Assert.Contains("Plan hash value: 1838229974", plan.Text, StringComparison.Ordinal);
        Assert.Contains("   1 |  TABLE ACCESS FULL | MES.LOT_HIST | cost 9000", plan.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void No_rows_give_an_empty_plan()
    {
        var plan = OracleTuningService.ToAwrPlan([], 1);
        Assert.Empty(plan.Steps);
        Assert.Equal(1, plan.PlanHashValue);
    }

    [Fact]
    public void Awr_plan_steps_query_needs_the_diagnostics_pack_and_binds_sql_id_and_hash()
    {
        var q = EmbeddedCatalog.Load().Get("tuning.awr_plan_steps", 19);
        Assert.Equal(CatalogLicense.Diagnostics, q.Header.License);
        Assert.Contains(":sql_id", q.Sql, StringComparison.Ordinal);
        Assert.Contains(":plan_hash_value", q.Sql, StringComparison.Ordinal);
        Assert.Contains("dba_hist_sql_plan", q.Sql, StringComparison.Ordinal);
    }
}
