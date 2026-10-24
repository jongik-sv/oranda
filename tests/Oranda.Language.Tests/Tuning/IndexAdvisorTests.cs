using Oranda.Core.Plans;
using Oranda.Core.Tuning;
using Oranda.Language.Tuning;

namespace Oranda.Language.Tests.Tuning;

/// <summary>TSK-06-05 TN-09: 규칙 기반 인덱스 후보.</summary>
public class IndexAdvisorTests
{
    [Fact]
    public void Full_scan_filter_gives_equality_first_then_range_and_extends_the_existing_index()
    {
        var advice = IndexAdvisor.Suggest(TuningFixtures.JoinPlan(), TuningFixtures.Existing);
        var first = advice[0];
        Assert.Equal(("MES", "LOT_HIST"), (first.Candidate.Owner, first.Candidate.TableName));
        Assert.Equal(["EQP_ID", "EVT_TIME"], first.Candidate.Columns);
        Assert.Equal(IndexBasis.Filter, first.Basis);
        Assert.Equal(IndexOverlapKind.Extends, first.Overlap);
        Assert.Equal("LOT_HIST_IX2", first.Candidate.OverlappingIndex);
        Assert.Equal(3, first.Candidate.BasisStepId);
        Assert.Equal("LH", first.Alias);
        Assert.True(first.ShareIsTime);
        Assert.Equal(1600d / 1842d, first.Share!.Value, 3);
        Assert.Equal("Id 3 FULL · filter", first.BasisText);
        Assert.Contains("시간 87 %", first.Reason, StringComparison.Ordinal);
        Assert.Contains("LOT_HIST_IX2(EQP_ID) 를 넓히는", first.Reason, StringComparison.Ordinal);
        Assert.Equal(
            "-- 후보 1 · 실행하면 DB 에 씁니다. 공간·DML 영향을 검토한 뒤 실행하세요\nCREATE INDEX MES.LOT_HIST_IXC1\n    ON MES.LOT_HIST (EQP_ID, EVT_TIME) ONLINE;",
            first.Candidate.Ddl);
    }

    [Fact]
    public void Join_columns_already_covered_are_listed_last_and_cannot_be_created()
    {
        var advice = IndexAdvisor.Suggest(TuningFixtures.JoinPlan(), TuningFixtures.Existing);
        var mst = Assert.Single(advice, a => a.Candidate.TableName == "LOT_MST");
        Assert.Equal(IndexBasis.Join, mst.Basis);
        Assert.Equal(IndexOverlapKind.Covered, mst.Overlap);
        Assert.False(mst.CanCreate);
        Assert.Equal("LOT_MST_PK", mst.Candidate.OverlappingIndex);
        Assert.StartsWith("-- 기존 인덱스 LOT_MST_PK", mst.Candidate.Ddl, StringComparison.Ordinal);
        Assert.DoesNotContain("CREATE INDEX", mst.Candidate.Ddl, StringComparison.Ordinal);
        Assert.Equal("Id 1 조인 열", mst.BasisText);
        Assert.True(advice.ToList().IndexOf(mst) > advice.ToList().FindIndex(a => a.CanCreate));
    }

    [Fact]
    public void Without_existing_index_lookup_the_overlap_is_unknown()
    {
        var advice = IndexAdvisor.Suggest(TuningFixtures.JoinPlan(), existing: null);
        Assert.All(advice, a => Assert.Equal(IndexOverlapKind.Unknown, a.Overlap));
        Assert.All(advice, a => Assert.Contains("CREATE INDEX", a.Candidate.Ddl, StringComparison.Ordinal));
        Assert.Contains(advice, a => a.Reason.Contains("확인하지 못했습니다", StringComparison.Ordinal));
    }

    [Fact]
    public void No_full_scan_means_no_candidate()
    {
        var plan = new ExecutionPlan(
            [
                new PlanStep(0, null, 0, "SELECT STATEMENT"),
                new PlanStep(1, 0, 1, "TABLE ACCESS") { Options = "BY INDEX ROWID", ObjectOwner = "S", ObjectName = "EMP" },
                new PlanStep(2, 1, 2, "INDEX") { Options = "UNIQUE SCAN", ObjectOwner = "S", ObjectName = "EMP_PK", AccessPredicates = "\"EMPNO\"=7839" },
            ], string.Empty);
        Assert.Empty(IndexAdvisor.Suggest(plan, TuningFixtures.Existing));
        Assert.Empty(IndexAdvisor.TablesToCheck(plan));
    }

    [Fact]
    public void Index_full_scan_uses_the_parent_table_and_its_filter()
    {
        var plan = new ExecutionPlan(
            [
                new PlanStep(0, null, 0, "SELECT STATEMENT") { Cost = 100 },
                new PlanStep(1, 0, 1, "TABLE ACCESS") { Options = "BY INDEX ROWID", ObjectOwner = "S", ObjectName = "ORDERS", Cost = 100, FilterPredicates = "\"O\".\"STATUS\"='OPEN'" },
                new PlanStep(2, 1, 2, "INDEX") { Options = "FULL SCAN", ObjectOwner = "S", ObjectName = "ORDERS_IX9", Cost = 80 },
            ], string.Empty);
        var a = Assert.Single(IndexAdvisor.Suggest(plan, (_, _) => []));
        Assert.Equal(("ORDERS", "STATUS", 2), (a.Candidate.TableName, a.Candidate.Columns[0], a.Candidate.BasisStepId));
        Assert.Equal(IndexOverlapKind.None, a.Overlap);
        Assert.False(a.ShareIsTime);
        Assert.Equal(0.8, a.Share!.Value, 3);
        Assert.Equal("Id 2 INDEX FULL SCAN · filter", a.BasisText);
        Assert.Equal([new TableTarget("S", "ORDERS")], IndexAdvisor.TablesToCheck(plan));
    }

    [Fact]
    public void Candidate_columns_are_capped_and_only_one_range_column_is_kept()
    {
        var plan = new ExecutionPlan(
            [
                new PlanStep(0, null, 0, "SELECT STATEMENT"),
                new PlanStep(1, 0, 1, "TABLE ACCESS")
                {
                    Options = "FULL", ObjectName = "T",
                    FilterPredicates = "(\"A\"=1 AND \"B\"=2 AND \"C\"=3 AND \"D\"=4 AND \"E\"=5 AND \"F\">1 AND \"G\"<9)",
                },
            ], string.Empty);
        var a = Assert.Single(IndexAdvisor.Suggest(plan, (_, _) => [], defaultOwner: "SCOTT"));
        Assert.Equal(["A", "B", "C", "D"], a.Candidate.Columns); // 등호가 넘치면 범위 열은 들어갈 자리가 없다
        Assert.Empty(a.RangeColumns);
        Assert.Equal("SCOTT", a.Candidate.Owner);

        var two = plan with { Steps = [plan.Steps[0], plan.Steps[1] with { FilterPredicates = "(\"A\"=1 AND \"F\">1 AND \"G\"<9)" }] };
        var b = Assert.Single(IndexAdvisor.Suggest(two, (_, _) => []));
        Assert.Equal(["A", "F"], b.Candidate.Columns);
        Assert.Equal(["F"], b.RangeColumns);
    }

    [Fact]
    public void Overlap_rules_cover_extend_share_and_none()
    {
        IndexColumnsRow[] ix = [new("IX_AB", ["A", "B"]), new("IX_A", ["A"]), new("IX_C", ["C", "Z"])];
        Assert.Equal(IndexOverlapKind.Covered, IndexAdvisor.Overlap(["A", "B"], allEquality: false, ix).Kind);
        Assert.Equal(IndexOverlapKind.Covered, IndexAdvisor.Overlap(["B", "A"], allEquality: true, ix).Kind);
        Assert.Equal(IndexOverlapKind.SharesLeading, IndexAdvisor.Overlap(["B", "A"], allEquality: false, [new("IX_B", ["B", "X"])]).Kind);
        var (kind, index) = IndexAdvisor.Overlap(["A", "B", "C"], allEquality: false, ix);
        Assert.Equal((IndexOverlapKind.Extends, "IX_AB"), (kind, index!.IndexName));
        Assert.Equal(IndexOverlapKind.SharesLeading, IndexAdvisor.Overlap(["C", "D"], allEquality: false, ix).Kind);
        Assert.Equal(IndexOverlapKind.None, IndexAdvisor.Overlap(["Q"], allEquality: true, ix).Kind);
        Assert.Equal(IndexOverlapKind.None, IndexAdvisor.Overlap([], allEquality: true, ix).Kind);
    }

    [Fact]
    public void Ddl_names_fit_eleven_g_and_quote_mixed_case_names()
    {
        var ddl = IndexAdvisor.Ddl("app", "VERY_LONG_TABLE_NAME_FOR_TESTING_X", ["Mixed", "COL1"], 12);
        Assert.Contains("CREATE INDEX \"app\".VERY_LONG_TABLE_NAME_FOR_IXC12", ddl, StringComparison.Ordinal);
        Assert.Contains("(\"Mixed\", COL1)", ddl, StringComparison.Ordinal);
        Assert.Equal(30, "VERY_LONG_TABLE_NAME_FOR_IXC12".Length);
        Assert.StartsWith("-- Candidate 12 · running this writes to the database", IndexAdvisor.Ddl(string.Empty, "T", ["A"], 12, english: true), StringComparison.Ordinal);
        Assert.Contains("ON T (A) ONLINE;", IndexAdvisor.Ddl(string.Empty, "T", ["A"], 1), StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_without_alias_section_infers_the_alias_from_predicates_and_english_reason()
    {
        var advice = IndexAdvisor.Suggest(TuningFixtures.JoinPlan(text: null), TuningFixtures.Existing, english: true);
        var first = advice[0];
        Assert.Equal("LH", first.Alias);
        Assert.Contains("Equality on EQP_ID and range on EVT_TIME", first.Reason, StringComparison.Ordinal);
        Assert.Contains("takes 87 % of the time", first.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_plan_gives_nothing()
    {
        Assert.Empty(IndexAdvisor.Suggest(new ExecutionPlan([], string.Empty), TuningFixtures.Existing));
    }
}
