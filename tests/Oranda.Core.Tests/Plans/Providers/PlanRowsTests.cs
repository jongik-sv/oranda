using Oranda.Core.Plans;
using Oranda.Core.Plans.Providers;

namespace Oranda.Core.Tests.Plans.Providers;

// 소유: TSK-07-01. 실행 계획 행 조립(DB 없음). Oracle.IntegrationTests ExecutionPlanLogicTests 에서 옮겼다(P0a).
public class PlanRowsTests
{
    private static RawPlanStep Step(string op, string? options = null, long? est = null, long? actual = null, long? starts = null) =>
        new(1, null, op, options, null, null, null, est, actual, starts, null, null, null);

    [Fact]
    public void Full_table_scan_and_cartesian_join_are_flagged()
    {
        Assert.Equal(PlanRisk.FullScan, PlanRows.ClassifyRisk(Step("TABLE ACCESS", "FULL")));
        Assert.Equal(PlanRisk.None, PlanRows.ClassifyRisk(Step("INDEX", "FULL SCAN")));
        Assert.Equal(PlanRisk.None, PlanRows.ClassifyRisk(Step("TABLE ACCESS", "BY INDEX ROWID")));
        Assert.Equal(PlanRisk.Cartesian, PlanRows.ClassifyRisk(Step("MERGE JOIN", "CARTESIAN")));
    }

    [Fact]
    public void Cardinality_gap_is_flagged_both_ways_per_start_and_ignores_small_numbers()
    {
        Assert.Equal(PlanRisk.CardinalityMisestimate, PlanRows.ClassifyRisk(Step("FILTER", null, est: 10, actual: 10_000, starts: 1)));
        Assert.Equal(PlanRisk.CardinalityMisestimate, PlanRows.ClassifyRisk(Step("FILTER", null, est: 100_000, actual: 5, starts: 1)));
        Assert.Equal(PlanRisk.None, PlanRows.ClassifyRisk(Step("FILTER", null, est: 1000, actual: 1500, starts: 1)));

        // 시작 횟수당으로 보면 맞는 추정(예: 1행 × 5000회).
        Assert.Equal(PlanRisk.None, PlanRows.ClassifyRisk(Step("INDEX", "UNIQUE SCAN", est: 1, actual: 5000, starts: 5000)));
        Assert.Equal(PlanRisk.None, PlanRows.ClassifyRisk(Step("FILTER", null, est: 1, actual: 8, starts: 1)));
        Assert.Equal(PlanRisk.None, PlanRows.ClassifyRisk(Step("FILTER", null, est: 1000, actual: null)));
    }

    [Fact]
    public void Steps_are_ordered_by_id_with_depth_from_parent_chain()
    {
        var steps = PlanRows.BuildSteps(
        [
            new(2, 1, "TABLE ACCESS", "FULL", "S", "T", 5, 100, 90, 1, 2000, "a", "f"),
            new(0, null, "SELECT STATEMENT", null, null, null, 7, null, null, null, null, null, null),
            new(1, 0, "SORT", "AGGREGATE", null, null, null, 1, 1, 1, null, null, null),
        ]);

        Assert.Equal([0, 1, 2], steps.Select(x => x.Id));
        Assert.Equal([0, 1, 2], steps.Select(x => x.Depth));
        Assert.Equal(PlanRisk.FullScan, steps[2].Risk);
        Assert.Equal(TimeSpan.FromMilliseconds(2), steps[2].ActualTime);
        Assert.Equal("a", steps[2].AccessPredicates);
        Assert.Equal("f", steps[2].FilterPredicates);
    }

    // 소유: TSK-03-07. 11g 차이(옛 PLAN_TABLE 열 이름 읽기). Oracle.Tests Oracle11ReaderTests 에서 옮겼다(P0a).
    [Fact]
    public void Plan_table_rows_are_read_by_column_name_in_any_order()
    {
        string[] names = ["STATEMENT_ID", "PLAN_ID", "FILTER_PREDICATES", "ID", "PARENT_ID", "OPERATION", "OPTIONS",
            "OBJECT_OWNER", "OBJECT_NAME", "COST", "CARDINALITY", "ACCESS_PREDICATES"];
        object?[] row = ["S1", 7m, "X > 1", 1m, 0m, "TABLE ACCESS", "FULL", "HR", "EMP", 3m, 14m, null];

        var step = Assert.Single(PlanRows.ReadPlanTableRows(names, [row]));

        Assert.Equal(1, step.Id);
        Assert.Equal(0, step.ParentId);
        Assert.Equal("TABLE ACCESS", step.Operation);
        Assert.Equal("FULL", step.Options);
        Assert.Equal("HR", step.Owner);
        Assert.Equal("EMP", step.Name);
        Assert.Equal(3, step.Cost);
        Assert.Equal(14, step.EstimatedRows);
        Assert.Null(step.Access);
        Assert.Equal("X > 1", step.Filter);
    }

    [Fact]
    public void Old_plan_table_without_plan_id_and_predicates_still_reads()
    {
        // 옛 개인 PLAN_TABLE: plan_id·access/filter_predicates·depth 가 없다. 열 이름은 소문자로 와도 된다.
        string[] names = ["statement_id", "operation", "options", "object_owner", "object_name", "id", "parent_id", "cost", "cardinality"];
        List<object?[]> rows =
        [
            ["S1", "SELECT STATEMENT", null, null, null, 0m, null, 2m, 1m],
            ["S1", "TABLE ACCESS", "FULL", "SYS", "DUAL", 1m, 0m, 2m, 1m],
        ];

        var steps = PlanRows.BuildSteps(PlanRows.ReadPlanTableRows(names, rows));

        Assert.Equal(2, steps.Count);
        Assert.Equal("SELECT STATEMENT", steps[0].Operation);
        Assert.Equal(1, steps[1].Depth);
        Assert.Null(steps[1].AccessPredicates);
        Assert.Null(steps[1].FilterPredicates);
    }

    [Fact]
    public void Plan_table_without_operation_gives_empty_operation_not_an_error()
    {
        var step = Assert.Single(PlanRows.ReadPlanTableRows(["ID"], [[0m]]));

        Assert.Equal(0, step.Id);
        Assert.Equal(string.Empty, step.Operation);
        Assert.Null(step.ParentId);
    }
}
