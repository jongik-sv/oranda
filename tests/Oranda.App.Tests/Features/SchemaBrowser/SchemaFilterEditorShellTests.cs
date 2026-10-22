using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.SchemaBrowser;
using Oranda.App.Shell.Docking;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Shell;

namespace Oranda.App.Tests.Features.SchemaBrowser;

/// <summary>
/// 저장 필터 ▾ 메뉴와 「필터 편집…」 을 앱과 같은 경로(조립·도킹 도구 창·명령 레지스트리)로 확인한다(TSK-13-10).
/// 뷰 모델을 뷰에 바로 붙이는 시험은 도킹 호스트에서만 생기는 차이를 놓친다.
/// </summary>
public sealed class SchemaFilterEditorShellTests
{
    private const string PanelId = "oranda.panel.schemaBrowser";

    private static SchemaBrowserView FindView(ShellHarness h)
    {
        h.Get<DockWorkspace>().ShowTool(PanelId);
        Dispatcher.UIThread.RunJobs();
        return h.Window.GetVisualDescendants().OfType<SchemaBrowserView>().Single();
    }

    [Fact]
    public Task Docked_view_is_bound_to_the_view_model_the_commands_use() => HeadlessHost.Run(() =>
    {
        using var h = new ShellHarness();
        var view = FindView(h);

        Assert.Same(view.DataContext, Oranda.App.Features.ObjectDetail.ObjectDetailCommandHandler.FindSchemaBrowser(h.Services));
    });

    [Fact]
    public Task Filter_button_flyout_lists_the_choices_when_opened() => HeadlessHost.Run(() =>
    {
        using var h = new ShellHarness();
        var view = FindView(h);
        var button = view.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "SavedFilterButton");

        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        var flyout = view.SavedFilterFlyout!;
        var lazy = typeof(Avalonia.Controls.Primitives.PopupFlyoutBase)
            .GetField("_popupLazy", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(flyout)!;
        var popup = (Avalonia.Controls.Primitives.Popup)lazy.GetType().GetProperty("Value")!.GetValue(lazy)!;
        var presenter = Assert.IsType<MenuFlyoutPresenter>(popup.Child);
        Dispatcher.UIThread.RunJobs();
        var drawn = presenter.GetVisualDescendants().OfType<MenuItem>().Select(m => (string?)m.Header).ToList();
        Assert.Equal(["필터 없음", "필터 편집…"], drawn);
        flyout.Hide();
    });

    [Fact]
    public Task Edit_filters_command_opens_the_dialog_over_the_main_window() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        var view = FindView(h);
        Assert.IsType<SchemaBrowserViewModel>(view.DataContext);

        var ran = await h.Get<ICommandRegistry>().ExecuteAsync("oranda.schema.editFilters", h.Get<IContextKeys>());
        for (var i = 0; i < 50 && !OpenDialogs(h).Any(); i++)
        {
            await Task.Delay(20);
            Dispatcher.UIThread.RunJobs();
        }

        Assert.True(ran);
        var dialog = Assert.Single(OpenDialogs(h));
        dialog.Close(false);
        Dispatcher.UIThread.RunJobs();
    });

    [Fact]
    public Task Edit_item_in_the_filter_menu_opens_the_dialog() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        var view = FindView(h);
        var button = view.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "SavedFilterButton");
        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        var edit = view.SavedFilterFlyout!.Items.OfType<MenuItem>().Single(m => (string?)m.Header == "필터 편집…");
        edit.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        for (var i = 0; i < 50 && !OpenDialogs(h).Any(); i++)
        {
            await Task.Delay(20);
            Dispatcher.UIThread.RunJobs();
        }

        var dialog = Assert.Single(OpenDialogs(h));
        dialog.Close(false);
        Dispatcher.UIThread.RunJobs();
    });

    private static IEnumerable<SchemaFilterEditorWindow> OpenDialogs(ShellHarness h) => h.Window.OwnedWindows.OfType<SchemaFilterEditorWindow>();
}
