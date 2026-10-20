using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.ExecutionPlan;
using Oranda.App.Shell.Menus;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Plans;
using CorePlan = Oranda.Core.Plans.ExecutionPlan;

namespace Oranda.App.Tests.Features.ExecutionPlan;

/// <summary>실행 계획 창 화면(Headless): 비중 막대·가장 비싼 단계 강조·비교 탭·오른쪽 클릭 메뉴.</summary>
public class PlanViewHeadlessTests
{
    [Fact]
    public Task Tree_shows_bars_and_one_hot_row_and_the_compare_tab_lists_marked_rows() => HeadlessHost.Run(() =>
    {
        var vm = new PlanViewModel();
        vm.Apply(new CorePlan(PlanMetricsTests.HashJoin(), "Plan hash value: 1"), "예상");
        vm.PinCurrent();
        PlanStep[] next =
        [
            new(0, null, 0, "SELECT STATEMENT") { Cost = 40 },
            new(1, 0, 1, "HASH JOIN") { Cost = 40 },
            new(2, 1, 2, "TABLE ACCESS") { Options = "FULL", ObjectName = "A", Cost = 30 },
            new(3, 1, 2, "INDEX") { Options = "FAST FULL SCAN", ObjectName = "B_IX", Cost = 9 },
            new(4, 3, 3, "SORT") { Options = "AGGREGATE", Cost = 9 },
        ];
        vm.Apply(new CorePlan(next, "Plan hash value: 2"), "예상");

        var view = new PlanView { DataContext = vm };
        var window = new Window { Content = view, Width = 1600, Height = 600 };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var list = view.FindControl<ListBox>("PlanList")!;
            var hot = list.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("hot")).ToList();
            var row = Assert.Single(hot);
            Assert.Same(vm.Rows[2], row.DataContext);
            var bars = list.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("bar") && b.Classes.Contains("cost") && !b.Classes.Contains("total")).ToList();
            Assert.Contains(bars, b => b.Bounds.Width > 40);

            Assert.True(vm.CompareWithPinned());
            Dispatcher.UIThread.RunJobs();
            var tabs = view.GetVisualDescendants().OfType<TabControl>().First();
            Assert.Equal(PlanViewModel.CompareTabIndex, tabs.SelectedIndex);
            var marks = view.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
            Assert.Contains("≠", marks);
            Assert.Contains("+", marks);
            Assert.Contains(view.GetVisualDescendants().OfType<Border>(), b => b.Classes.Contains("added"));
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Right_click_on_the_plan_tree_offers_pin_and_compare() => HeadlessHost.Run(() =>
    {
        using var h = new ShellHarness();
        var items = ShellContextMenus.Factory!("plan");
        var commands = items.OfType<MenuItem>().ToList();
        Assert.Equal(2, commands.Count);
        Assert.Empty(items.OfType<Separator>());
    });
}
