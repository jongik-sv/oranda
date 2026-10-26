using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Avalonia.Threading;
using Oranda.App.Features.Sessions;
using Oranda.App.Shell.Docking;
using Oranda.App.Shell.Layout;
using Oranda.App.Tests.Features.SqlEditor;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Events;
using Oranda.Core.Shell;

namespace Oranda.App.Tests.Features.Sessions;

// 소유: TSK-14-04. 「세션」 도구 창 화면(Headless): 목록·우클릭 메뉴·라이트/다크 PNG, 셸 등록과 기본 배치.
// ORANDA_RENDER_DIR 가 있으면 sessions-list-{light,dark}.png · sessions-menu-{light,dark}.png 를 쓴다.
public sealed class SessionsViewTests
{
    /// <summary>접속 둘(TESTDB 에 세션 하나 더) = 세션 셋. 활성 작업 공간은 마지막에 연 ORA11G 를 쓰고, 더 연 세션은 미커밋이 있고, TESTDB 는 실행 중이다.</summary>
    private static async Task<(SessionsViewModelTests.Rig Rig, Window Window, SessionsView View)> OpenAsync()
    {
        var rig = new SessionsViewModelTests.Rig();
        var a = await rig.ConnectAsync("TESTDB");
        var extra = await rig.OpenExtraAsync(a);
        await rig.ConnectAsync("ORA11G");
        var ws = rig.Host.EnsureWorkspace();
        var tab = rig.Host.NewDocument();
        Assert.True(await rig.Router.PrepareAsync(tab));
        rig.Host.Active = tab;
        SessionsViewModelTests.Pump();
        rig.Vm.Refresh();
        rig.State.ApplyTransaction(extra.Id, true);
        rig.State.BeginRun(a.Id);
        SessionsViewModelTests.Pump();
        _ = ws;

        var view = new SessionsView { DataContext = rig.Vm };
        var window = new Window { Width = 280, Height = 330, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        return (rig, window, view);
    }

    private static void Render(Window window, string name)
    {
        var dir = Environment.GetEnvironmentVariable("ORANDA_RENDER_DIR");
        if (string.IsNullOrEmpty(dir))
        {
            return;
        }

        Directory.CreateDirectory(dir);
        try
        {
            foreach (var (variant, tone) in new[] { (ThemeVariant.Light, "light"), (ThemeVariant.Dark, "dark") })
            {
                Application.Current!.RequestedThemeVariant = variant;
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                using var bmp = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height));
                bmp.Render(window);
#pragma warning disable CS0618 // 렌더 확인용: 새 오버로드가 없는 헤드리스 비트맵
                bmp.Save(Path.Combine(dir, $"{name}-{tone}.png"));
#pragma warning restore CS0618
            }
        }
        finally
        {
            Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
            Dispatcher.UIThread.RunJobs();
        }
    }

    [Fact]
    public Task The_view_lists_three_sessions_with_label_sid_and_state() => Ui.Run(async () =>
    {
        var (rig, window, view) = await OpenAsync();
        using (rig)
        {
            var list = view.FindControl<ListBox>("SessionList")!;
            Assert.True(list.IsVisible);
            Assert.Equal(3, list.ItemCount);
            Assert.False(view.FindControl<TextBlock>("EmptyHintText")!.IsVisible);
            var texts = AllText(view);
            Assert.Contains("1 - scott@TESTDB", texts);
            Assert.Contains("1 - scott@TESTDB (2)", texts);
            Assert.Contains("2 - scott@ORA11G", texts);
            Assert.Contains(texts, t => t.StartsWith("SID ", StringComparison.Ordinal));
            Assert.Contains(texts, t => t.Contains("실행 중", StringComparison.Ordinal));
            Assert.Contains(texts, t => t.Contains("미커밋", StringComparison.Ordinal));
            Assert.Contains("열린 세션 3", texts);
            Render(window, "sessions-list");
            window.Close();
        }
    });

    [Fact]
    public Task The_empty_state_shows_a_hint_instead_of_a_list() => Ui.Run(() =>
    {
        using var rig = new SessionsViewModelTests.Rig();
        rig.Vm.Refresh();
        var view = new SessionsView { DataContext = rig.Vm };
        var window = new Window { Width = 280, Height = 200, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.False(view.FindControl<ListBox>("SessionList")!.IsVisible);
        Assert.True(view.FindControl<TextBlock>("EmptyHintText")!.IsVisible);
        window.Close();
    });

    [Fact]
    public Task The_context_menu_has_four_commands_enabled_for_the_selected_row() => Ui.Run(async () =>
    {
        var (rig, window, view) = await OpenAsync();
        using (rig)
        {
            var menu = view.FindControl<ContextMenu>("SessionMenu")!;
            Assert.Equal(["이 세션 쓰기", "커밋", "롤백", "세션 닫기"], menu.Items.OfType<MenuItem>().Select(m => m.Header));

            // 더 연 세션(미커밋, 어느 작업 공간도 안 씀): 쓰기·커밋·롤백·닫기 모두 가능.
            rig.Vm.Selected = rig.Vm.Rows[1];
            var list = view.FindControl<ListBox>("SessionList")!;
            menu.VerticalOffset = list.ContainerFromIndex(1)?.Bounds.Y ?? 0; // PNG 에서 고른 줄 곁에 보이게
            menu.Open(list);
            Dispatcher.UIThread.RunJobs();
            Assert.True(Enabled(menu, "UseMenu"));
            Assert.True(Enabled(menu, "CommitMenu"));
            Assert.True(Enabled(menu, "RollbackMenu"));
            Assert.True(Enabled(menu, "CloseMenu"));
            Render(window, "sessions-menu");
            menu.Close();
            Dispatcher.UIThread.RunJobs();

            // 활성 작업 공간이 쓰는 세션(미커밋 없음): 쓰기 불가(이미 씀), 커밋·롤백 불가.
            rig.Vm.Selected = rig.Vm.Rows[2];
            menu.Open(view.FindControl<ListBox>("SessionList")!);
            Dispatcher.UIThread.RunJobs();
            Assert.False(Enabled(menu, "UseMenu"));
            Assert.False(Enabled(menu, "CommitMenu"));
            Assert.False(Enabled(menu, "RollbackMenu"));
            Assert.True(Enabled(menu, "CloseMenu"));
            menu.Close();
            Dispatcher.UIThread.RunJobs();
            window.Close();
        }
    });

    [Fact]
    public Task Clicking_commit_and_use_this_session_run_the_commands() => Ui.Run(async () =>
    {
        var (rig, window, view) = await OpenAsync();
        using (rig)
        {
            var menu = view.FindControl<ContextMenu>("SessionMenu")!;
            var list = view.FindControl<ListBox>("SessionList")!;
            rig.Vm.Selected = rig.Vm.Rows[1];
            menu.Open(list);
            Dispatcher.UIThread.RunJobs();
            Click(menu, "CommitMenu");
            await WaitAsync(() => rig.Ops.Calls.Contains("commit"));
            Assert.Contains("commit", rig.Ops.Calls);
            menu.Close();
            Dispatcher.UIThread.RunJobs();

            // 미커밋이 있는 세션으로 옮기려 하면 그 트랜잭션을 놓치지 않게 먼저 막힌다(옮기기 전에 커밋한 뒤에는 가능).
            menu.Open(list);
            Dispatcher.UIThread.RunJobs();
            Click(menu, "UseMenu");
            await WaitAsync(() => rig.Router.SessionFor(rig.Host.Active) == rig.Vm.Rows[1].Session);
            Assert.Same(rig.Vm.Rows[1].Session, rig.Router.SessionFor(rig.Host.Active));
            Assert.Equal(1, rig.Vm.Rows[1].WorkspaceCount);
            menu.Close();
            Dispatcher.UIThread.RunJobs();
            window.Close();
        }
    });

    [Fact]
    public Task The_window_is_registered_on_the_left_closed_by_default_and_opens_as_a_left_tab() => HeadlessHost.Run(() =>
    {
        using var h = new ShellHarness();
        var ws = h.Get<DockWorkspace>();
        var registry = h.Get<Oranda.Core.Shell.IToolPanelRegistry>();

        var def = registry.Find(SessionsAutoShow.PanelId);
        Assert.NotNull(def);
        Assert.Equal(DockLocation.Left, def.DefaultLocation);
        Assert.Equal(ToolPanelKind.Tool, def.Kind);
        Assert.DoesNotContain(SessionsAutoShow.PanelId, ws.VisibleToolIds); // 세션이 하나뿐이면 기본 배치에 없다
        Assert.Contains(SessionsAutoShow.PanelId, ws.Capture().HiddenTools!);

        // 자동으로 보일 때는 왼쪽 묶음의 탭으로 더하되 보던 탭(스키마 브라우저)을 가로채지 않는다.
        new SessionsAutoShow(ws, h.Get<IEventBus>(), h.Services).Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Contains(SessionsAutoShow.PanelId, ws.VisibleToolIds);
        var root = ws.Capture().Root!;
        var group = Flatten(root).Single(n => n.Kind == DockNodeKind.ToolGroup && (n.Tools ?? []).Contains(SessionsAutoShow.PanelId));
        Assert.Equal(DockLocation.Left, LayoutPlanner.LocationOf(root, group));
        Assert.Equal("oranda.panel.schemaBrowser", group.ActiveTool);

        Assert.True(ws.HideTool(SessionsAutoShow.PanelId));
        Assert.DoesNotContain(SessionsAutoShow.PanelId, ws.VisibleToolIds);
    });

    private static IEnumerable<DockNode> Flatten(DockNode node) => new[] { node }.Concat((node.Children ?? []).SelectMany(Flatten));

    private static bool Enabled(ContextMenu menu, string name) => menu.Items.OfType<MenuItem>().Single(m => m.Name == name).IsEnabled;

    private static void Click(ContextMenu menu, string name) =>
        menu.Items.OfType<MenuItem>().Single(m => m.Name == name).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));

    private static async Task WaitAsync(Func<bool> done)
    {
        for (var i = 0; i < 100 && !done(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
    }

    private static List<string> AllText(Control root) =>
        [.. root.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text)).Select(t => t.Text!)];
}
