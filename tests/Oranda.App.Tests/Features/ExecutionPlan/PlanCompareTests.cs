using Oranda.App.Features.ExecutionPlan;
using Oranda.App.Tests.Shell;
using Oranda.Core.Plans;
using CorePlan = Oranda.Core.Plans.ExecutionPlan;

namespace Oranda.App.Tests.Features.ExecutionPlan;

/// <summary>TSK-07-01 두 계획 비교(PLAN-04): 맞추기 규칙, 수치 강조, plan_hash_value, 고정·비교 흐름, 명령 등록.</summary>
public class PlanCompareTests
{
    private static CorePlan Plan(string hashLine, params PlanStep[] steps) => new(steps, hashLine);

    private static PlanStep Full(int id, int parent, int depth, string table, long cost, long rows = 100) =>
        new(id, parent, depth, "TABLE ACCESS") { Options = "FULL", ObjectOwner = "S", ObjectName = table, Cost = cost, EstimatedRows = rows };

    [Fact]
    public void Identical_plans_align_one_to_one_and_report_the_same_hash()
    {
        var a = Plan("Plan hash value: 111", PlanMetricsTests.HashJoin());
        var b = Plan("Plan hash value: 111", PlanMetricsTests.HashJoin());

        var cmp = PlanComparer.Compare(a, b);

        Assert.All(cmp.Rows, r => Assert.Equal(PlanDiffKind.Same, r.Kind));
        Assert.Equal(4, cmp.Rows.Count);
        Assert.False(cmp.HashDiffers);
        Assert.Equal(100, cmp.PinnedCost);
        Assert.False(cmp.Positional);
    }

    [Fact]
    public void A_step_inserted_above_shifts_depth_but_the_rest_still_aligns()
    {
        var before = Plan("Plan hash value: 1",
            new PlanStep(0, null, 0, "SELECT STATEMENT") { Cost = 10 },
            Full(1, 0, 1, "T", 10));
        var after = Plan("Plan hash value: 2",
            new PlanStep(0, null, 0, "SELECT STATEMENT") { Cost = 12 },
            new PlanStep(1, 0, 1, "SORT") { Options = "ORDER BY", Cost = 12 },
            Full(2, 1, 2, "T", 10));

        var cmp = PlanComparer.Compare(before, after);

        Assert.Equal([PlanDiffKind.Same, PlanDiffKind.Added, PlanDiffKind.Same], cmp.Rows.Select(r => r.Kind));
        Assert.Equal(1, cmp.Rows[2].Pinned!.Id);
        Assert.Equal(2, cmp.Rows[2].Current!.Id);
        Assert.Null(cmp.Rows[1].Pinned);
        Assert.True(cmp.HashDiffers);
    }

    [Fact]
    public void A_full_scan_replaced_by_an_index_access_is_a_changed_row_plus_an_added_row()
    {
        var before = Plan("Plan hash value: 1",
            new PlanStep(0, null, 0, "SELECT STATEMENT") { Cost = 500 },
            Full(1, 0, 1, "T", 500, rows: 1));
        var after = Plan("Plan hash value: 2",
            new PlanStep(0, null, 0, "SELECT STATEMENT") { Cost = 3 },
            new PlanStep(1, 0, 1, "TABLE ACCESS") { Options = "BY INDEX ROWID", ObjectOwner = "S", ObjectName = "T", Cost = 3, EstimatedRows = 1 },
            new PlanStep(2, 1, 2, "INDEX") { Options = "UNIQUE SCAN", ObjectOwner = "S", ObjectName = "T_PK", Cost = 2, EstimatedRows = 1 });

        var cmp = PlanComparer.Compare(before, after);

        Assert.Equal([PlanDiffKind.Same, PlanDiffKind.Changed, PlanDiffKind.Added], cmp.Rows.Select(r => r.Kind));
        Assert.True(cmp.Rows[0].CostChangedBig);
        Assert.True(cmp.Rows[1].CostChangedBig);
        Assert.False(cmp.Rows[1].RowsChangedBig);

        var changed = new PlanCompareRowItem(cmp.Rows[1]);
        Assert.Equal("≠", changed.KindMark);
        Assert.Equal("  TABLE ACCESS BY INDEX ROWID  ← TABLE ACCESS FULL", changed.Operation);
        Assert.Equal("S.T", changed.ObjectText);
        Assert.Equal("500 → 3", changed.CostText);
        Assert.Equal("−99%", changed.CostDeltaText);
        Assert.True(changed.CostBig);
        Assert.Equal("1", changed.RowsText);
        Assert.Equal(string.Empty, changed.RowsDeltaText);
        Assert.Equal(string.Empty, changed.ActualRowsText);

        var added = new PlanCompareRowItem(cmp.Rows[2]);
        Assert.Equal("+", added.KindMark);
        Assert.Equal(string.Empty, added.PinnedIdText);
        Assert.Equal("2", added.CurrentIdText);
        Assert.Equal("2", added.CostText);
    }

    [Fact]
    public void Swapped_join_inputs_show_a_removed_and_an_added_step_around_the_kept_one()
    {
        var before = Plan(string.Empty,
            new PlanStep(0, null, 0, "HASH JOIN") { Cost = 10 },
            Full(1, 0, 1, "A", 4),
            Full(2, 0, 1, "B", 5));
        var after = Plan(string.Empty,
            new PlanStep(0, null, 0, "HASH JOIN") { Cost = 10 },
            Full(1, 0, 1, "B", 5),
            Full(2, 0, 1, "A", 4));

        var cmp = PlanComparer.Compare(before, after);

        // HASH JOIN 과 B 는 짝, A 는 앞에서 빠지고 뒤에서 추가된다(조인 순서가 바뀜).
        Assert.Equal([PlanDiffKind.Same, PlanDiffKind.Removed, PlanDiffKind.Same, PlanDiffKind.Added], cmp.Rows.Select(r => r.Kind));
        Assert.Equal("A", cmp.Rows[1].Pinned!.ObjectName);
        Assert.Equal("A", cmp.Rows[3].Current!.ObjectName);
        Assert.Null(cmp.HashDiffers);
    }

    [Fact]
    public void Very_large_middles_fall_back_to_positional_pairing()
    {
        const int n = 2_000;
        var a = Plan(string.Empty, [.. Enumerable.Range(0, n).Select(i => new PlanStep(i, null, 0, "A" + i))]);
        var b = Plan(string.Empty, [.. Enumerable.Range(0, n).Select(i => new PlanStep(i, null, 0, "B" + i))]);

        var cmp = PlanComparer.Compare(a, b);

        Assert.True(cmp.Positional);
        Assert.Equal(n, cmp.Count(PlanDiffKind.Changed));
    }

    [Theory]
    [InlineData(10L, 100L, true)]
    [InlineData(100L, 10L, true)]
    [InlineData(10L, 15L, false)]
    [InlineData(3L, 9L, false)]
    [InlineData(0L, 20L, true)]
    [InlineData(null, 100L, false)]
    public void Big_change_needs_a_factor_of_two_and_a_meaningful_size(long? before, long? after, bool big) =>
        Assert.Equal(big, PlanComparer.IsBigChange(before, after));

    [Fact]
    public void Plan_hash_is_read_from_the_dbms_xplan_text_before_the_service_value()
    {
        Assert.Equal(42, PlanComparer.PlanHashOf(new CorePlan([], "Plan hash value: 42") { PlanHashValue = 7 }));
        Assert.Equal(7, PlanComparer.PlanHashOf(new CorePlan([], "no header") { PlanHashValue = 7 }));
        Assert.Null(PlanComparer.PlanHashOf(new CorePlan([], string.Empty)));
    }

    [Theory]
    [InlineData(100L, 250L, "+150%")]
    [InlineData(100L, 38L, "−62%")]
    [InlineData(0L, 5L, "+5")]
    [InlineData(5L, 5L, "")]
    [InlineData(null, 5L, "")]
    public void Delta_text_shows_the_relative_change(long? before, long? after, string expected) =>
        Assert.Equal(expected, PlanCompareRowItem.Delta(before, after));

    [Fact]
    public void Pin_then_a_new_plan_fills_the_compare_tab_and_compare_selects_it()
    {
        var vm = new PlanViewModel();
        Assert.False(vm.PinCurrent());
        Assert.False(vm.CompareWithPinned());
        Assert.Contains("먼저", vm.StatusText, StringComparison.Ordinal);

        var first = new CorePlan(PlanMetricsTests.HashJoin(), "Plan hash value: 111");
        vm.Apply(first, "예상");
        Assert.True(vm.PinCurrent());
        Assert.True(vm.HasPinned);
        Assert.Contains("plan hash 111", vm.PinnedText, StringComparison.Ordinal);
        Assert.Empty(vm.CompareRows);
        Assert.Contains("고정한 계획을 보고", vm.CompareHashText, StringComparison.Ordinal);

        PlanStep[] cheaper =
        [
            new(0, null, 0, "SELECT STATEMENT") { Cost = 40 },
            new(1, 0, 1, "HASH JOIN") { Cost = 40, EstimatedRows = 1000 },
            new(2, 1, 2, "TABLE ACCESS") { Options = "FULL", ObjectOwner = "S", ObjectName = "A", Cost = 30, EstimatedRows = 100 },
            new(3, 1, 2, "INDEX") { Options = "FAST FULL SCAN", ObjectOwner = "S", ObjectName = "B_IX", Cost = 9, EstimatedRows = 5000 },
        ];
        vm.Apply(new CorePlan(cheaper, "Plan hash value: 222"), "예상");

        Assert.Equal(0, vm.SelectedTabIndex);
        Assert.Equal(4, vm.CompareRows.Count);
        Assert.True(vm.CompareHashDiffers);
        Assert.Equal("plan_hash_value 다름: 111 → 222 — 계획이 바뀌었습니다", vm.CompareHashText);
        Assert.StartsWith("전체 비용 100 → 40 (−60%) · 같음 3 · 바뀜 1 · 추가 0 · 빠짐 0", vm.CompareSummaryText, StringComparison.Ordinal);
        Assert.Contains("고정 계획과 plan_hash_value 다름", vm.StatusText, StringComparison.Ordinal);

        Assert.True(vm.CompareWithPinned());
        Assert.Equal(PlanViewModel.CompareTabIndex, vm.SelectedTabIndex);

        vm.Unpin();
        Assert.False(vm.HasPinned);
        Assert.Empty(vm.CompareRows);
        Assert.Null(vm.Comparison);
    }

    [Fact]
    public void Same_plan_shape_reports_equal_hashes()
    {
        var vm = new PlanViewModel();
        vm.Apply(new CorePlan(PlanMetricsTests.HashJoin(), "Plan hash value: 9"), "예상");
        vm.PinCurrent();
        vm.Apply(new CorePlan(PlanMetricsTests.HashJoin(), "Plan hash value: 9"), "실제(ALLSTATS LAST)");

        Assert.False(vm.CompareHashDiffers);
        Assert.Equal("plan_hash_value 같음: 9 → 9", vm.CompareHashText);
        Assert.All(vm.CompareRows, r => Assert.Equal("=", r.KindMark));
    }

    [Fact]
    public void Pin_and_compare_commands_are_registered_without_keys_or_session_conditions()
    {
        var registry = ShellTestHelpers.CreateCommandRegistry();
        foreach (var id in ExecutionPlanCommands.Ids)
        {
            Assert.NotNull(registry.Find(id));
        }

        Assert.Equal(6, ExecutionPlanCommands.Ids.Length);
        var pin = registry.Find("oranda.plan.pinForCompare")!;
        var compare = registry.Find("oranda.plan.compare")!;
        Assert.Null(pin.Enablement);
        Assert.Null(compare.Enablement);
        Assert.Null(pin.When);
        Assert.Contains("context:plan/pin@10", pin.Placements);
        Assert.Contains("menu:tuning/comparePlan@50", compare.Placements);

        var handlers = ShellTestHelpers.AppContributions.Commands
            .Where(c => c.HandlerType == typeof(ExecutionPlanCommandHandler))
            .Select(c => c.Definition.Id)
            .Order(StringComparer.Ordinal);
        Assert.Equal(ExecutionPlanCommands.Ids.Order(StringComparer.Ordinal), handlers);
    }

    [Fact]
    public void New_plan_commands_are_listed_in_commands_md_with_matching_conditions()
    {
        var doc = FindCommandsDoc();
        var rows = File.ReadAllLines(doc)
            .Where(l => l.StartsWith("| `oranda.plan.", StringComparison.Ordinal) && l.Contains("TSK-07-01", StringComparison.Ordinal))
            .Select(l => l.Trim('|').Split('|').Select(c => c.Trim()).ToArray())
            .ToDictionary(r => r[0].Trim('`'));

        foreach (var id in new[] { "oranda.plan.pinForCompare", "oranda.plan.compare" })
        {
            Assert.True(rows.TryGetValue(id, out var r), $"commands.md 에 없음: {id}");
            Assert.Equal("-", r[2]);
            Assert.Equal("전역", r[3]);
            Assert.Equal("항상", r[4]);
            Assert.Equal("TSK-07-01", r[5]);
        }
    }

    private static string FindCommandsDoc()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var p = Path.Combine(dir.FullName, "docs", "design", "commands.md");
            if (File.Exists(p))
            {
                return p;
            }
        }

        throw new FileNotFoundException("docs/design/commands.md 를 찾지 못했다.");
    }
}
