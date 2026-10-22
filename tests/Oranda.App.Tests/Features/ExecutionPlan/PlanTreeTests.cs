using System.ComponentModel;
using Oranda.App.Features.ExecutionPlan;
using Oranda.App.Shell.Icons;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Plans;
using CorePlan = Oranda.Core.Plans.ExecutionPlan;

namespace Oranda.App.Tests.Features.ExecutionPlan;

/// <summary>TSK-13-14 계획 트리 모양: 들여쓰기 안내선, 연산 아이콘, 조건절 분리, 고른 단계, 형식 글자.</summary>
public class PlanTreeTests
{
    /// <summary>
    /// 0 SELECT ─ 1 SORT ─ 2 HASH JOIN ─┬ 3 VIEW ─ 4 SORT GROUP BY
    ///                                  └ 5 NESTED LOOPS ─┬ 6 INDEX
    ///                                                    └ 7 TABLE ACCESS
    /// </summary>
    [Fact]
    public void Tree_lines_mark_middle_and_last_children_and_keep_open_ancestor_lines()
    {
        var lines = PlanTreeLines.Compute([0, 1, 2, 3, 4, 3, 4, 4]);

        Assert.Equal(8, lines.Count);
        Assert.Equal(0, lines[0].Depth);
        Assert.True(lines[0].HasChildren);
        Assert.True(lines[1].IsLast);
        Assert.False(lines[3].IsLast); // VIEW 뒤에 형제 NESTED LOOPS 가 있다
        Assert.True(lines[3].HasChildren);
        Assert.True(lines[5].IsLast);
        Assert.False(lines[6].IsLast);
        Assert.True(lines[7].IsLast);
        Assert.False(lines[7].HasChildren);

        // 단계 4 의 조상 칸: SORT(깊이 1)·HASH JOIN(2) 은 끝난 가지, VIEW(3) 는 아래에 형제가 남아 선이 이어진다.
        Assert.Equal([false, false, true], lines[4].Continues);
        Assert.Equal([false, false, false], lines[6].Continues);
        Assert.Empty(lines[0].Continues);
        Assert.Empty(lines[1].Continues);
    }

    [Fact]
    public void Tree_lines_close_a_branch_when_a_shallower_step_comes_first()
    {
        // 0 ─┬ 1 ─ 2
        //    └ 3 ─ 4   : 단계 2 는 형제 없음(뒤의 3 은 더 얕다), 단계 2 의 조상 1 은 형제 3 이 있어 선이 이어진다.
        var lines = PlanTreeLines.Compute([0, 1, 2, 1, 2]);

        Assert.True(lines[2].IsLast);
        Assert.Equal([true], lines[2].Continues);
        Assert.Equal([false], lines[4].Continues);
        Assert.True(lines[3].IsLast);
    }

    [Theory]
    [InlineData("SELECT STATEMENT", null, "fileSql")]
    [InlineData("UPDATE STATEMENT", null, "fileSql")]
    [InlineData("TABLE ACCESS", "FULL", "table")]
    [InlineData("TABLE ACCESS", "BY INDEX ROWID BATCHED", "table")]
    [InlineData("MAT_VIEW ACCESS", "FULL", "objMview")]
    [InlineData("INDEX", "RANGE SCAN", "objIndex")]
    [InlineData("BITMAP INDEX", "SINGLE VALUE", "objIndex")]
    [InlineData("HASH JOIN", "OUTER", "join")]
    [InlineData("NESTED LOOPS", null, "join")]
    [InlineData("MERGE JOIN", "CARTESIAN", "join")]
    [InlineData("SORT", "ORDER BY", "sort")]
    [InlineData("BUFFER", "SORT", "sort")]
    [InlineData("SORT", "GROUP BY", "aggregate")]
    [InlineData("HASH", "GROUP BY", "aggregate")]
    [InlineData("SORT", "AGGREGATE", "aggregate")]
    [InlineData("COUNT", "STOPKEY", "aggregate")]
    [InlineData("VIEW", null, "objView")]
    [InlineData("FILTER", null, "filter")]
    [InlineData("PARTITION RANGE", "ALL", "folder")]
    [InlineData("REMOTE", null, "objDbLink")]
    [InlineData("SEQUENCE", null, "objSequence")]
    [InlineData("UNION-ALL", null, "objOther")]
    public Task Each_operation_family_gets_an_icon(string operation, string? options, string key) => HeadlessHost.Run(() =>
    {
        Assert.Equal(key, PlanOperationIcons.KeyFor(new PlanStep(1, 0, 1, operation) { Options = options }));
        Assert.True(ShellIcons.Contains(key), key); // 아이콘 경로 해석은 Avalonia 플랫폼이 있어야 한다
    });

    [Fact]
    public void A_row_names_the_operation_without_indent_and_keeps_access_and_filter_apart()
    {
        var row = new PlanRowItem(new PlanStep(3, 2, 2, "INDEX") { Options = "RANGE SCAN", ObjectName = "IX", AccessPredicates = "\"A\"=1", FilterPredicates = string.Empty });

        Assert.Equal("INDEX RANGE SCAN", row.OperationText);
        Assert.Equal(2, row.Depth);
        Assert.Equal("objIndex", row.IconKey);
        Assert.Equal("\"A\"=1", row.AccessPredicates);
        Assert.Null(row.FilterPredicates);
        Assert.True(row.HasPredicates);
        Assert.False(new PlanRowItem(new PlanStep(0, null, 0, "SELECT STATEMENT")).HasPredicates);
    }

    [Fact]
    public void Apply_selects_the_hottest_step_draws_tree_lines_and_tells_whether_actuals_exist()
    {
        var vm = new PlanViewModel();
        vm.Apply(new CorePlan(PlanMetricsTests.HashJoin(), string.Empty), "예상");

        Assert.Same(vm.Rows[3], vm.SelectedRow); // 자체 비용이 가장 큰 B
        Assert.False(vm.HasActuals);
        Assert.False(vm.Rows[0].HasActuals);
        Assert.True(vm.Rows[1].TreeLines.HasChildren);
        Assert.False(vm.Rows[2].TreeLines.IsLast);
        Assert.True(vm.Rows[3].TreeLines.IsLast);

        vm.Apply(new CorePlan(
        [
            new PlanStep(0, null, 0, "SELECT STATEMENT") { Cost = 3, ActualRows = 3 },
            new PlanStep(1, 0, 1, "TABLE ACCESS") { Options = "FULL", ObjectName = "T", Cost = 3, ActualRows = 3 },
        ], string.Empty), "실제");

        Assert.True(vm.HasActuals);
        Assert.True(vm.Rows[1].HasActuals);
        Assert.Same(vm.Rows[1], vm.SelectedRow);

        vm.Apply(new CorePlan([new PlanStep(0, null, 0, "SELECT STATEMENT")], string.Empty), "예상");
        Assert.Null(vm.SelectedRow); // 가장 비싼 단계가 없으면 고르지 않는다
    }

    [Fact]
    public void Format_text_follows_the_options_and_announces_each_change()
    {
        var vm = new PlanViewModel();
        var changed = new List<string?>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Assert.Equal("ALLSTATS LAST", vm.FormatText);
        vm.Alias = true;
        Assert.Equal("ALLSTATS LAST +ALIAS", vm.FormatText);
        Assert.Contains(nameof(PlanViewModel.FormatText), changed);

        changed.Clear();
        vm.BaseFormat = "TYPICAL";
        Assert.Equal("TYPICAL +ALIAS", vm.FormatText);
        Assert.Contains(nameof(PlanViewModel.FormatText), changed);

        foreach (var set in new Action[] { () => vm.LastOnly = false, () => vm.PeekedBinds = true, () => vm.Outline = true })
        {
            changed.Clear();
            set();
            Assert.Contains(nameof(PlanViewModel.FormatText), changed);
        }
    }

    [Fact]
    public void A_changed_compare_row_shows_the_current_operation_and_the_previous_one_apart()
    {
        var before = new CorePlan([new PlanStep(0, null, 0, "SELECT STATEMENT"), new PlanStep(1, 0, 1, "TABLE ACCESS") { Options = "FULL", ObjectName = "T" }], string.Empty);
        var after = new CorePlan([new PlanStep(0, null, 0, "SELECT STATEMENT"), new PlanStep(1, 0, 1, "TABLE ACCESS") { Options = "BY INDEX ROWID", ObjectName = "T" }], string.Empty);
        var cmp = PlanComparer.Compare(before, after);

        var changed = new PlanCompareRowItem(cmp.Rows[1]);
        Assert.Equal("TABLE ACCESS BY INDEX ROWID", changed.OperationText);
        Assert.Equal("TABLE ACCESS FULL", changed.PreviousOperationText);
        Assert.True(changed.HasPreviousOperation);
        Assert.Equal(1, changed.Depth);
        Assert.Equal("table", changed.IconKey);

        var same = new PlanCompareRowItem(cmp.Rows[0]);
        Assert.Equal(string.Empty, same.PreviousOperationText);
        Assert.False(same.HasPreviousOperation);
    }
}
