using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.LogicalTree;
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
    private static readonly string[] SharedColumns = ["c-id", "c-op", "c-obj", "c-cost", "c-share", "c-erows", "c-risk"];
    private static readonly string[] ActualColumns = ["c-arows", "c-starts", "c-time", "c-ashare"];

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
    public Task Rows_are_compact_and_header_cells_line_up_with_row_cells() => HeadlessHost.Run(() =>
    {
        var vm = new PlanViewModel();
        var view = new PlanView { DataContext = vm };
        var window = new Window { Content = view, Width = 1600, Height = 600 };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            Assert.True(view.FindControl<Control>("EmptyHint")!.IsVisible); // 계획이 없으면 안내

            vm.Apply(new CorePlan(PlanMetricsTests.HashJoin(), string.Empty), "예상");
            Dispatcher.UIThread.RunJobs();
            Assert.False(view.FindControl<Control>("EmptyHint")!.IsVisible);

            var list = view.FindControl<ListBox>("PlanList")!;
            var items = list.GetVisualDescendants().OfType<ListBoxItem>().ToList();
            Assert.Equal(4, items.Count);
            Assert.All(items, i => Assert.InRange(i.Bounds.Height, 18, 26));

            var header = view.FindControl<Border>("TreeHeader")!;
            foreach (var column in SharedColumns)
            {
                var head = header.GetVisualDescendants().OfType<Control>().First(c => c.Classes.Contains(column));
                var cell = items[2].GetVisualDescendants().OfType<Control>().First(c => c.Classes.Contains(column));
                Assert.Equal(head.TranslatePoint(default, view)!.Value.X, cell.TranslatePoint(default, view)!.Value.X, 1);
                Assert.Equal(head.Bounds.Width, cell.Bounds.Width, 1);
            }

            // 예상 계획이면 실제 열(A-Rows·Starts·시간·실제 비중)은 머리글·줄 모두 숨는다.
            foreach (var column in ActualColumns)
            {
                Assert.False(header.GetVisualDescendants().OfType<Control>().First(c => c.Classes.Contains(column)).IsVisible);
                Assert.False(items[2].GetVisualDescendants().OfType<Control>().First(c => c.Classes.Contains(column)).IsVisible);
            }
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Details_pane_shows_the_selected_steps_predicates_in_full() => HeadlessHost.Run(() =>
    {
        var vm = new PlanViewModel();
        PlanStep[] steps =
        [
            new(0, null, 0, "SELECT STATEMENT") { Cost = 5 },
            new(1, 0, 1, "INDEX") { Options = "RANGE SCAN", ObjectOwner = "S", ObjectName = "IX", Cost = 5, AccessPredicates = "\"O\".\"ORDER_DATE\">=:B1", FilterPredicates = "\"O\".\"QTY\">0" },
            new(2, 0, 1, "TABLE ACCESS") { Options = "FULL", ObjectName = "T", Cost = 1 },
        ];
        vm.Apply(new CorePlan(steps, string.Empty), "예상");
        var view = new PlanView { DataContext = vm };
        var window = new Window { Content = view, Width = 1600, Height = 600 };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var list = view.FindControl<ListBox>("PlanList")!;
            Assert.Same(vm.Rows[1], list.SelectedItem); // 가장 비싼 단계가 처음부터 골라져 있다

            var pane = view.FindControl<Control>("DetailsPane")!;
            List<string?> Shown() => [.. pane.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text)];
            Assert.Contains("INDEX RANGE SCAN", Shown());
            Assert.Contains("\"O\".\"ORDER_DATE\">=:B1", Shown());
            Assert.Contains("\"O\".\"QTY\">0", Shown());

            list.SelectedItem = vm.Rows[2];
            Dispatcher.UIThread.RunJobs();
            Assert.Same(vm.Rows[2], vm.SelectedRow);
            Assert.Contains("TABLE ACCESS FULL", Shown());
            Assert.DoesNotContain("\"O\".\"QTY\">0", Shown());
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Toolbar_buttons_are_icons_and_every_format_option_sits_in_the_format_flyout() => HeadlessHost.Run(() =>
    {
        var vm = new PlanViewModel();
        var view = new PlanView { DataContext = vm };
        var window = new Window { Content = view, Width = 1200, Height = 500 };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            foreach (var (name, label) in new[] { ("PinButton", "이 계획 고정"), ("CompareButton", "비교"), ("UnpinButton", "고정 해제") })
            {
                var button = view.FindControl<Button>(name)!;
                Assert.IsNotType<string>(button.Content);
                Assert.Equal(label, AutomationProperties.GetName(button));
            }

            var format = view.FindControl<Button>("FormatButton")!;
            Assert.Contains("ALLSTATS LAST", format.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
            var flyout = Assert.IsType<Flyout>(format.Flyout);
            flyout.ShowAt(format);
            Dispatcher.UIThread.RunJobs();
            var content = Assert.IsAssignableFrom<Control>(flyout.Content);
            var boxes = content.GetLogicalDescendants().OfType<CheckBox>().ToList();
            Assert.Equal(4, boxes.Count);
            Assert.Single(content.GetLogicalDescendants().OfType<ComboBox>());

            boxes[1].IsChecked = true; // +PEEKED_BINDS
            Dispatcher.UIThread.RunJobs();
            Assert.True(vm.PeekedBinds);
            Assert.Equal("ALLSTATS LAST +PEEKED_BINDS", vm.FormatText);
            Assert.Contains("ALLSTATS LAST +PEEKED_BINDS", format.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
            flyout.Hide();
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
