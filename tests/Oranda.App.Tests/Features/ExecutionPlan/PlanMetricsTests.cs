using Oranda.App.Features.ExecutionPlan;
using Oranda.Core.Plans;
using CorePlan = Oranda.Core.Plans.ExecutionPlan;

namespace Oranda.App.Tests.Features.ExecutionPlan;

/// <summary>TSK-07-01 계획 트리 비중 막대·실행 순서·가장 비싼 단계(PLAN-03 보강).</summary>
public class PlanMetricsTests
{
    /// <summary>
    /// 0 SELECT STATEMENT(100) ─ 1 HASH JOIN(100) ─┬ 2 TABLE ACCESS FULL A(30)
    ///                                            └ 3 TABLE ACCESS FULL B(60)
    /// </summary>
    internal static PlanStep[] HashJoin() =>
    [
        new(0, null, 0, "SELECT STATEMENT") { Cost = 100 },
        new(1, 0, 1, "HASH JOIN") { Cost = 100, EstimatedRows = 1000 },
        new(2, 1, 2, "TABLE ACCESS") { Options = "FULL", ObjectOwner = "S", ObjectName = "A", Cost = 30, EstimatedRows = 100, Risk = PlanRisk.FullScan },
        new(3, 1, 2, "TABLE ACCESS") { Options = "FULL", ObjectOwner = "S", ObjectName = "B", Cost = 60, EstimatedRows = 5000, Risk = PlanRisk.FullScan },
    ];

    [Fact]
    public void Self_cost_subtracts_direct_children_and_the_hottest_step_is_not_the_root()
    {
        var m = PlanMetrics.Compute(HashJoin());

        Assert.Equal(100, m.TotalCost);
        Assert.Equal([0L, 10L, 30L, 60L], m.Steps.Select(s => s.SelfCost!.Value));
        Assert.Equal(1.0, m.Steps[1].CostShare);
        Assert.Equal(0.1, m.Steps[1].SelfCostShare, 6);
        Assert.Equal(0.6, m.Steps[3].SelfCostShare, 6);
        Assert.Equal(3, m.HottestIndex);
        Assert.Equal(PlanActualMetric.None, m.Actual);
    }

    [Fact]
    public void Execution_order_visits_children_before_parent_and_siblings_in_id_order()
    {
        var m = PlanMetrics.Compute(HashJoin());

        // A(2) → B(3) → HASH JOIN(1) → SELECT(0)
        Assert.Equal([4, 3, 1, 2], m.Steps.Select(s => s.ExecutionOrder));
    }

    [Fact]
    public void Actual_time_picks_the_hottest_step_by_self_time_instead_of_cost()
    {
        PlanStep[] steps =
        [
            new(0, null, 0, "SELECT STATEMENT") { Cost = 100, ActualTime = TimeSpan.FromSeconds(10) },
            new(1, 0, 1, "NESTED LOOPS") { Cost = 100, ActualTime = TimeSpan.FromSeconds(10) },
            new(2, 1, 2, "TABLE ACCESS") { Options = "FULL", ObjectName = "A", Cost = 90, ActualTime = TimeSpan.FromSeconds(1) },
            new(3, 1, 2, "INDEX") { Options = "RANGE SCAN", ObjectName = "IX", Cost = 2, ActualTime = TimeSpan.FromSeconds(8) },
        ];

        var m = PlanMetrics.Compute(steps);

        Assert.Equal(PlanActualMetric.Time, m.Actual);
        Assert.Equal(3, m.HottestIndex);
        Assert.Equal(TimeSpan.FromSeconds(1), m.Steps[1].SelfTime);
        Assert.Equal(0.8, m.Steps[3].SelfActualShare, 6);
        Assert.Equal(1.0, m.Steps[1].ActualShare, 6);
    }

    [Fact]
    public void Without_time_statistics_the_actual_bar_falls_back_to_a_rows_of_the_largest_step()
    {
        PlanStep[] steps =
        [
            new(0, null, 0, "SELECT STATEMENT") { ActualRows = 10 },
            new(1, 0, 1, "TABLE ACCESS") { Options = "FULL", ObjectName = "T", ActualRows = 40 },
        ];

        var m = PlanMetrics.Compute(steps);

        Assert.Equal(PlanActualMetric.Rows, m.Actual);
        Assert.Equal(0.25, m.Steps[0].ActualShare, 6);
        Assert.Equal(m.Steps[0].ActualShare, m.Steps[0].SelfActualShare);
        Assert.Equal(1.0, m.Steps[1].ActualShare, 6);

        // 비용이 없으면 비중도 가장 비싼 단계도 없다.
        Assert.Null(m.TotalCost);
        Assert.Equal(-1, m.HottestIndex);
    }

    [Fact]
    public void Missing_costs_count_as_zero_and_children_costs_above_the_parent_clamp_to_zero()
    {
        PlanStep[] steps =
        [
            new(0, null, 0, "SELECT STATEMENT") { Cost = 5 },
            new(1, 0, 1, "COUNT") { Options = "STOPKEY" },
            new(2, 1, 2, "VIEW") { Cost = 3 },
            new(3, 2, 3, "TABLE ACCESS") { Options = "FULL", ObjectName = "T", Cost = 8 },
        ];

        var m = PlanMetrics.Compute(steps);

        Assert.Equal(5, m.Steps[0].SelfCost);
        Assert.Null(m.Steps[1].SelfCost);
        Assert.Equal(0, m.Steps[1].CostShare);
        Assert.Equal(0, m.Steps[2].SelfCost);
        Assert.Equal(8, m.TotalCost);
        Assert.Equal(3, m.HottestIndex);
    }

    [Fact]
    public void Deep_and_orphan_plans_are_ordered_without_recursion()
    {
        const int depth = 20_000;
        var steps = Enumerable.Range(0, depth).Select(i => new PlanStep(i, i == 0 ? null : i - 1, i, "VIEW") { Cost = depth - i }).ToList();
        steps.Add(new PlanStep(depth, 999_999, 0, "ORPHAN"));

        var m = PlanMetrics.Compute(steps);

        Assert.Equal(depth, m.Steps[0].ExecutionOrder);
        Assert.Equal(1, m.Steps[depth - 1].ExecutionOrder);
        Assert.Equal(depth + 1, m.Steps[depth].ExecutionOrder);
    }

    [Fact]
    public void Single_step_plan_has_no_hottest_highlight_and_empty_plans_are_fine()
    {
        Assert.Equal(-1, PlanMetrics.Compute([new PlanStep(0, null, 0, "SELECT STATEMENT") { Cost = 3 }]).HottestIndex);
        Assert.Empty(PlanMetrics.Compute([]).Steps);
    }

    [Fact]
    public void Apply_precomputes_bars_order_and_hottest_highlight_on_rows()
    {
        var vm = new PlanViewModel();

        vm.Apply(new CorePlan(HashJoin(), "text"), "예상");

        var rows = vm.Rows;
        Assert.Equal(PlanRowItem.BarWidth, rows[0].CostBarWidth);
        Assert.Equal(0, rows[0].SelfCostBarWidth);
        Assert.Equal("100%", rows[1].CostShareText);
        Assert.Equal(6, rows[1].SelfCostBarWidth);
        Assert.Equal("4", rows[0].ExecutionOrderText);
        Assert.Equal("1", rows[2].ExecutionOrderText);
        Assert.True(rows[3].IsHottest);
        Assert.False(rows[0].IsHottest);
        Assert.Contains("자체 비용 60 (60%)", rows[3].CostTip, StringComparison.Ordinal);
        Assert.Equal("가장 비싼 단계(자체 비용 기준)", rows[3].HotTip);
        Assert.Null(rows[0].ActualTip);
        Assert.Equal("전체 스캔", rows[3].RiskText);
        Assert.Contains("가장 비싼 단계 Id 3(자체 비용 60%)", vm.StatusText, StringComparison.Ordinal);
        Assert.Equal(3, vm.Metrics!.HottestIndex);
        Assert.Equal("    TABLE ACCESS FULL", rows[3].Operation);
        Assert.Equal("S.B", rows[3].ObjectText);
    }

    [Theory]
    [InlineData(0.0, "0%")]
    [InlineData(0.004, "<1%")]
    [InlineData(0.456, "46%")]
    [InlineData(1.0, "100%")]
    public void Percent_text_rounds_and_marks_tiny_shares(double share, string expected) =>
        Assert.Equal(expected, PlanRowItem.Percent(share));
}
