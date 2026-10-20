using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.Execution;
using Oranda.App.Features.ResultGrid;
using Oranda.App.Shell;
using Oranda.App.Tests.Features.SqlEditor;
using Oranda.App.Tests.Shell.Headless;

namespace Oranda.App.Tests.Features.Execution;

/// <summary>「Excel 로 내보내기」 단추가 결과 탭 줄 오른쪽 끝에 있고, 선택한 탭의 결과로 명령을 실행한다.</summary>
[Collection("ExcelExportStatics")]
public sealed class ResultTabsExcelButtonTests : IDisposable
{
    private readonly Func<string, Task>? _oldRunner = ShellCommandRunner.Runner;
    private readonly ResultGridModel? _oldActive = ResultGridCommands.Active;

    public void Dispose()
    {
        ShellCommandRunner.Runner = _oldRunner;
        ResultGridCommands.Active = _oldActive;
    }

    private static (ResultTabsViewModel Vm, ResultTabsView View, Window Window) Show()
    {
        var vm = new ResultTabsViewModel();
        vm.AddQuery(new OneColumnResult("a", "b"), "결과 1", null);
        vm.AddQuery(new OneColumnResult("c"), "자식: EMP(SYS_C0010576)", null);
        vm.AddMessage("메시지", "3행 처리됨", isError: false);
        vm.Selected = vm.Tabs[0];
        var view = new ResultTabsView { DataContext = vm };
        var window = new Window { Width = 600, Height = 300, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (vm, view, window);
    }

    [Fact]
    public Task The_button_sits_at_the_right_end_of_the_tab_header_row_without_growing_it() => Ui.Run(() =>
    {
        var (_, view, window) = Show();
        try
        {
            var button = view.FindControl<Button>("ExcelButton")!;
            var first = view.FindControl<TabControl>("Tabs")!.GetVisualDescendants().OfType<TabItem>().First();
            var headerHeight = first.Bounds.Height;
            Assert.True(button.IsVisible);
            Assert.True(button.Bounds.Right > view.Bounds.Width - 20); // 오른쪽 끝
            Assert.True(button.Bounds.Bottom <= headerHeight + 0.5); // 탭 머리 줄 높이 안에 있다
            var last = view.FindControl<TabControl>("Tabs")!.GetVisualDescendants().OfType<TabItem>().Last();
            var lastRight = last.TranslatePoint(new Point(last.Bounds.Width, 0), view)!.Value.X;
            Assert.True(lastRight <= button.Bounds.Left + 0.5); // 탭이 단추를 가리지 않는다

            // 단추가 줄을 늘리지 않는다: 단추를 숨겨도 탭 머리 높이가 같다.
            button.IsVisible = false;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(headerHeight, first.Bounds.Height);
            Assert.NotNull(ToolTip.GetTip(button));
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Clicking_runs_exportExcel_against_the_selected_tabs_result_and_is_off_for_message_tabs() => Ui.Run(async () =>
    {
        var (vm, view, window) = Show();
        var ran = new List<string>();
        ResultGridModel? target = null;
        ShellCommandRunner.Runner = id =>
        {
            ran.Add(id);
            target = ResultGridCommands.Active;
            return Task.CompletedTask;
        };
        try
        {
            var button = view.FindControl<Button>("ExcelButton")!;
            vm.Selected = vm.Tabs[1];
            Dispatcher.UIThread.RunJobs();
            Assert.True(button.IsEnabled);

            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await HeadlessHost.WaitUntil(() => ran.Count == 1);

            Assert.Equal("oranda.grid.exportExcel", ran[0]);
            Assert.Same(vm.Tabs[1].Pane!.Model, target); // 포커스와 상관없이 선택한 탭의 결과

            vm.Selected = vm.Tabs[2]; // 메시지 탭
            Dispatcher.UIThread.RunJobs();
            Assert.False(button.IsEnabled);
            Assert.True(button.IsVisible); // 숨기지 않는다(자리 흔들림 방지)
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task The_result_pane_toolbar_row_is_visible_only_in_edit_mode_and_has_no_excel_button() => Ui.Run(() =>
    {
        var pane = new SqlResultPaneViewModel();
        pane.SetResult(new OneColumnResult("a"));
        var view = new SqlResultPaneView { DataContext = pane };
        var window = new Window { Width = 500, Height = 300, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var row = view.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("panel-toolbar") && Grid.GetRow(b) == 3);
            Assert.False(pane.IsEditMode);
            Assert.False(row.IsVisible);
            Assert.DoesNotContain(row.GetVisualDescendants().OfType<Button>(), b => b.Tag is "oranda.grid.exportExcel");
            Assert.Contains(row.GetLogicalDescendants().OfType<Button>(), b => b.Tag is "oranda.grid.edit.addRow");
        }
        finally
        {
            window.Close();
            pane.Clear();
        }
    });
}
