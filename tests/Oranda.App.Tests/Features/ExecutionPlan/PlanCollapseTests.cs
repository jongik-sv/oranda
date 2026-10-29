using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.ExecutionPlan;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Plans;
using CorePlan = Oranda.Core.Plans.ExecutionPlan;

namespace Oranda.App.Tests.Features.ExecutionPlan;

/// <summary>계획 트리 접기·펼치기(p4): 숨은 줄·단계 수·안내선·순서·가장 비싼 단계 표시·키·모두 펼치기/접기.</summary>
public class PlanCollapseTests
{
    /// <summary>0 SELECT › 1 HASH JOIN › (2 TABLE A, 3 NESTED LOOPS › (4 INDEX, 5 TABLE B)). 가장 비싼 단계 = 5.</summary>
    private static CorePlan Deep() => new(
        [
            new(0, null, 0, "SELECT STATEMENT") { Cost = 140 },
            new(1, 0, 1, "HASH JOIN") { Cost = 140 },
            new(2, 1, 2, "TABLE ACCESS") { Options = "FULL", ObjectName = "A", Cost = 20 },
            new(3, 1, 2, "NESTED LOOPS") { Cost = 90 },
            new(4, 3, 3, "INDEX") { Options = "RANGE SCAN", ObjectName = "B_IX", Cost = 10 },
            new(5, 3, 3, "TABLE ACCESS") { Options = "BY INDEX ROWID", ObjectName = "B", Cost = 70 },
        ],
        string.Empty);

    private static PlanViewModel Loaded()
    {
        var vm = new PlanViewModel();
        vm.Apply(Deep(), "예상");
        return vm;
    }

    private static int[] Ids(PlanViewModel vm) => [.. vm.Rows.Select(r => r.Id)];

    [Fact]
    public void Collapsing_hides_descendants_counts_them_and_keeps_order_and_guides()
    {
        var vm = Loaded();
        Assert.Equal([0, 1, 2, 3, 4, 5], Ids(vm));
        var orders = vm.AllRows.Select(r => r.ExecutionOrderText).ToArray();
        var nested = vm.AllRows[3];
        Assert.True(nested.TreeLines.HasChildren);

        vm.SetCollapsed(nested, true);

        Assert.Equal([0, 1, 2, 3], Ids(vm));
        Assert.True(nested.IsCollapsed);
        Assert.Equal(2, nested.HiddenCount);
        Assert.True(nested.HiddenHottest); // 가장 비싼 단계(5)가 숨었다
        Assert.False(nested.TreeLines.HasChildren); // 보이는 자식이 없으니 아래로 내리는 안내선이 없다
        Assert.Equal(orders, vm.AllRows.Select(r => r.ExecutionOrderText)); // 실행 순서는 그대로
        Assert.False(vm.AllRows[1].IsCollapsed);
        Assert.False(vm.AllRows[1].HiddenHottest); // 펼친 줄은 표시하지 않는다

        vm.ToggleRow(nested);
        Assert.Equal([0, 1, 2, 3, 4, 5], Ids(vm));
        Assert.True(nested.TreeLines.HasChildren);
        Assert.False(nested.HiddenHottest);
    }

    [Fact]
    public void Nested_collapse_stays_collapsed_when_the_outer_row_is_expanded_again()
    {
        var vm = Loaded();
        vm.SetCollapsed(vm.AllRows[3], true);
        vm.SetCollapsed(vm.AllRows[1], true);
        Assert.Equal([0, 1], Ids(vm));
        Assert.Equal(4, vm.AllRows[1].HiddenCount);

        vm.SetCollapsed(vm.AllRows[1], false);
        Assert.Equal([0, 1, 2, 3], Ids(vm)); // 안쪽 접힘(3)은 유지
        Assert.True(vm.HasCollapsed);
    }

    [Fact]
    public void Collapse_all_and_expand_all_move_the_selection_off_hidden_rows()
    {
        var vm = Loaded();
        Assert.Equal(5, vm.SelectedRow!.Id); // 처음에는 가장 비싼 단계
        Assert.True(vm.HasCollapsible);
        Assert.False(vm.HasCollapsed);

        vm.CollapseAll();
        Assert.Equal([0], Ids(vm));
        Assert.Equal(0, vm.SelectedRow!.Id); // 고른 줄이 숨으면 보이는 부모로
        Assert.True(vm.HasCollapsed);
        Assert.False(vm.HasCollapsible);

        vm.ExpandAll();
        Assert.Equal([0, 1, 2, 3, 4, 5], Ids(vm));
        Assert.False(vm.HasCollapsed);
    }

    [Fact]
    public void Left_collapses_then_selects_the_parent_and_right_expands()
    {
        var vm = Loaded();
        vm.SelectedRow = vm.AllRows[3];

        Assert.True(vm.CollapseOrSelectParent());
        Assert.True(vm.AllRows[3].IsCollapsed);
        Assert.Equal(3, vm.SelectedRow!.Id);

        Assert.True(vm.CollapseOrSelectParent()); // 이미 접힘 → 부모
        Assert.Equal(1, vm.SelectedRow!.Id);

        vm.SelectedRow = vm.AllRows[3];
        Assert.True(vm.ExpandSelected());
        Assert.Equal([0, 1, 2, 3, 4, 5], Ids(vm));
        Assert.False(vm.ExpandSelected()); // 펼쳐 있으면 할 일 없음

        vm.SelectedRow = vm.AllRows[2]; // 자식 없는 줄: ← = 부모 고르기
        Assert.True(vm.CollapseOrSelectParent());
        Assert.Equal(1, vm.SelectedRow!.Id);
    }

    [Fact]
    public void A_new_plan_starts_expanded_and_a_graph_click_reveals_a_hidden_step()
    {
        var vm = Loaded();
        vm.CollapseAll();

        vm.SelectGraphStep(4);
        Assert.Equal(4, vm.SelectedRow!.Id);
        Assert.Equal([0, 1, 2, 3, 4, 5], Ids(vm)); // 가려진 부모들을 폈다

        vm.CollapseAll();
        vm.Apply(Deep(), "예상");
        Assert.Equal([0, 1, 2, 3, 4, 5], Ids(vm));
        Assert.False(vm.HasCollapsed);
    }

    [Fact]
    public Task Toggle_button_and_arrow_keys_drive_the_tree_view() => HeadlessHost.Run(() =>
    {
        var vm = Loaded();
        var view = new PlanView { DataContext = vm };
        var window = new Window { Content = view, Width = 1400, Height = 400 };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var list = view.FindControl<ListBox>("PlanList")!;
            IEnumerable<Button> Toggles() => list.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("tree-toggle") && b.IsEffectivelyVisible);
            Assert.Equal(3, Toggles().Count()); // 자식이 있는 줄: 0·1·3

            // 단추 누르기: 줄 3 접기.
            var toggle = Toggles().First(b => ((PlanRowItem)b.DataContext!).Id == 3);
            var at = toggle.TranslatePoint(new Point(toggle.Bounds.Width / 2, toggle.Bounds.Height / 2), window)!.Value;
            window.MouseDown(at, MouseButton.Left);
            window.MouseUp(at, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal([0, 1, 2, 3], Ids(vm));
            Assert.Equal(4, list.GetVisualDescendants().OfType<ListBoxItem>().Count());
            Assert.Contains(list.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "+2");

            // 키: 줄 1 을 골라 ← 접기, → 펼치기.
            vm.SelectedRow = vm.AllRows[1];
            Dispatcher.UIThread.RunJobs();
            list.Focus();
            window.KeyPress(Key.Left, RawInputModifiers.None, PhysicalKey.ArrowLeft, null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal([0, 1], Ids(vm));
            Assert.Equal(1, vm.SelectedRow?.Id);
            Assert.True(vm.AllRows[1].IsCollapsed);
            Dispatcher.UIThread.RunJobs();
            Assert.True(list.IsKeyboardFocusWithin);
            window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal([0, 1, 2, 3], Ids(vm));

            // 툴바: 모두 접기 → 모두 펼치기.
            view.FindControl<Button>("TreeCollapseAllButton")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal([0], Ids(vm));
            view.FindControl<Button>("TreeExpandAllButton")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(6, vm.Rows.Count);

            vm.SetCollapsed(vm.AllRows[3], true);
            Dispatcher.UIThread.RunJobs();
            Render(window);
        }
        finally
        {
            window.Close();
        }

        return Task.CompletedTask;
    });

    private static void Render(Window window)
    {
        if (Environment.GetEnvironmentVariable("ORANDA_RENDER_DIR") is not { Length: > 0 } dir)
        {
            return;
        }

        Directory.CreateDirectory(dir);
        window.UpdateLayout();
        using var bmp = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height));
        bmp.Render(window);
#pragma warning disable CS0618 // 렌더 확인용: 새 오버로드가 없는 헤드리스 비트맵
        bmp.Save(Path.Combine(dir, "plan-cols-collapse.png"));
#pragma warning restore CS0618
    }
}
