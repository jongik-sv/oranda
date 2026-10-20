using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dock.Avalonia.Controls;
using Dock.Model.Controls;
using Oranda.App.Shell.Docking;
using Oranda.App.Tests.Shell.Headless;

namespace Oranda.App.Tests.Shell.Docking;

// 떠 있는 창 안에서도 문서 본문(패널 View)이 그려지고, 메인 창과 서로 막지 않는다.
public class DockFloatContentTests
{
    private static void DoubleClick(Window window, Control target)
    {
        var center = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window)!.Value;
        for (var i = 0; i < 2; i++)
        {
            window.MouseDown(center, MouseButton.Left);
            try
            {
                window.MouseUp(center, MouseButton.Left);
            }
            catch (ObjectDisposedException)
            {
            }
        }

        Dispatcher.UIThread.RunJobs();
    }

    private static Window Float(ShellHarness h, DockWorkspace ws, PanelDocument doc)
    {
        var tab = h.Window.GetVisualDescendants().OfType<DocumentTabStripItem>().First(t => ReferenceEquals(t.DataContext, doc));
        DoubleClick(h.Window, tab);
        return null!;
    }

    private static bool HasPanelView(Visual root, object panel) =>
        root.GetVisualDescendants().OfType<Control>().Any(c =>
            ReferenceEquals(c.DataContext, panel) && c.GetType() != typeof(ContentControl) && c.GetType() != typeof(ContentPresenter)
            && c.Bounds.Width > 0 && c.Bounds.Height > 0);

    [Fact]
    public Task Floating_document_window_renders_panel_view() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        var ws = h.Get<DockWorkspace>();
        var doc = ws.OpenDocument("oranda.panel.sessionMonitor")!;
        Dispatcher.UIThread.RunJobs();
        Float(h, ws, doc);
        await HeadlessHost.WaitUntil(() => ((IRootDock)ws.Layout!).Windows?.Count == 1);
        var host = ((IRootDock)ws.Layout!).Windows![0].Host as Window ?? throw new Xunit.Sdk.XunitException("떠 있는 창 호스트가 없다.");
        Dispatcher.UIThread.RunJobs();

        Assert.True(HasPanelView(host, doc.Panel), "떠 있는 창 안에 패널 View 가 없다.");
    });

    // 보통 창: 소유자·항상 위 없음, 메인 창을 덮지 않는 크기, 메인 창은 입력을 받는다.
    [Fact]
    public Task Floating_window_is_ordinary_window_and_main_stays_interactive() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        var ws = h.Get<DockWorkspace>();
        var doc = ws.OpenDocument("oranda.panel.sessionMonitor")!;
        Dispatcher.UIThread.RunJobs();
        Float(h, ws, doc);
        await HeadlessHost.WaitUntil(() => ((IRootDock)ws.Layout!).Windows?.Count == 1);
        var host = (Window)((IRootDock)ws.Layout!).Windows![0].Host!;

        Assert.IsType<FloatHostWindow>(host);
        Assert.False(host.Topmost);
        Assert.Null(host.Owner);
        Assert.True(host.Width <= h.Window.Bounds.Width * 2 / 3 + 1);
        Assert.True(host.Height <= h.Window.Bounds.Height * 2 / 3 + 1);
        Assert.True(h.Window.IsEnabled);
    });

    // 다시 붙인 뒤에도 같은 패널 ViewModel 이고, 본문 View 가 메인 창에 그려진다.
    [Fact]
    public Task Redocked_document_keeps_panel_and_renders_in_main() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        var ws = h.Get<DockWorkspace>();
        var doc = ws.OpenDocument("oranda.panel.sessionMonitor")!;
        var panel = doc.Panel;
        Dispatcher.UIThread.RunJobs();
        Float(h, ws, doc);
        await HeadlessHost.WaitUntil(() => ((IRootDock)ws.Layout!).Windows?.Count == 1);
        var host = (Window)((IRootDock)ws.Layout!).Windows![0].Host!;
        Dispatcher.UIThread.RunJobs();
        var tab = host.GetVisualDescendants().OfType<DocumentTabStripItem>().First(t => ReferenceEquals(t.DataContext, doc));
        DoubleClick(host, tab);
        await HeadlessHost.WaitUntil(() => ((IRootDock)ws.Layout!).Windows?.Count == 0);
        Dispatcher.UIThread.RunJobs();

        Assert.Same(panel, doc.Panel);
        Assert.Contains(doc, ws.Documents);
        Assert.True(HasPanelView(h.Window, panel), "다시 붙인 뒤 메인 창에 패널 View 가 없다.");
    });

    // 소유자가 없으므로 메인 창을 닫으면 떠 있는 창도 함께 닫혀야 앱이 끝난다.
    [Fact]
    public Task Closing_main_window_closes_floating_window() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        var ws = h.Get<DockWorkspace>();
        var doc = ws.OpenDocument("oranda.panel.sessionMonitor")!;
        Dispatcher.UIThread.RunJobs();
        Float(h, ws, doc);
        await HeadlessHost.WaitUntil(() => ((IRootDock)ws.Layout!).Windows?.Count == 1);
        var host = (Window)((IRootDock)ws.Layout!).Windows![0].Host!;
        var closed = false;
        host.Closed += (_, _) => closed = true;

        h.Window.CloseWithoutSaving();
        await HeadlessHost.WaitUntil(() => closed);
    });
}
