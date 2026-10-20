using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dock.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;
using Oranda.App.Shell.Docking;
using Oranda.App.Tests.Shell.Headless;

namespace Oranda.App.Tests.Shell.Docking;

// 떠 있는 창의 문서 탭을 두 번 누르면 메인 문서 영역으로 돌아오고, 새 창이 생기지 않는다.
public class DockFloatRedockTests
{
    private static void DoubleClick(TopLevel window, Control target)
    {
        var center = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window)!.Value;
        for (var i = 0; i < 2; i++)
        {
            ((Window)window).MouseDown(center, MouseButton.Left);
            try
            {
                ((Window)window).MouseUp(center, MouseButton.Left);
            }
            catch (ObjectDisposedException)
            {
                // 두 번째 떼기에서 문서가 돌아가며 떠 있는 창이 닫힌다.
            }
        }

        Dispatcher.UIThread.RunJobs();
    }

    private static DocumentTabStripItem TabOf(Control root, object doc) =>
        root.GetVisualDescendants().OfType<DocumentTabStripItem>().First(t => ReferenceEquals(t.DataContext, doc));

    [Fact]
    public Task Double_tap_in_floating_window_returns_document_to_main_dock() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        var ws = h.Get<DockWorkspace>();
        var a = ws.OpenDocument("oranda.panel.sqlEditor")!;
        var b = ws.OpenDocument("oranda.panel.sqlEditor")!;
        Dispatcher.UIThread.RunJobs();

        // 메인 창: 두 번 누르면 기존대로 떼어 낸다.
        DoubleClick(h.Window, TabOf(h.Window, b));
        await HeadlessHost.WaitUntil(() => ((IRootDock)ws.Layout!).Windows?.Count == 1);
        var floating = ((IRootDock)ws.Layout!).Windows![0];

        // 떠 있는 창: 두 번 누르면 메인 문서 영역으로 돌아온다.
        var host = floating.Host as Window ?? throw new Xunit.Sdk.XunitException("떠 있는 창 호스트가 없다.");
        var changed = 0;
        ws.LayoutChanged += (_, _) => changed++;
        DoubleClick(host, TabOf(host, b));
        await HeadlessHost.WaitUntil(() => ((IRootDock)ws.Layout!).Windows?.Count == 0);

        Assert.Contains(b, ws.Documents);
        Assert.Equal(2, ws.Documents.Count);
        Assert.Same(a.Owner, b.Owner);
        Assert.True(changed > 0, "배치 저장이 예약되지 않았다.");
    });

    private sealed class FakeTabDocument : IDockTabActions
    {
        public int Renamed { get; private set; }

        public Task RenameFromTabAsync(CancellationToken ct)
        {
            Renamed++;
            return Task.CompletedTask;
        }

        public Task CloseFromTabAsync(CancellationToken ct) => Task.CompletedTask;
    }

    // 탭 머리 동작을 가진 문서(SQL 작업 공간): 떼어 낼 수 없고, 두 번 누르면 이름 바꾸기이며 떠 있는 창이 생기지 않는다.
    [Fact]
    public Task Double_tap_on_inner_tabs_document_renames_instead_of_floating() => HeadlessHost.Run(() =>
    {
        using var h = new ShellHarness();
        var ws = h.Get<DockWorkspace>();
        var fake = new FakeTabDocument();
        var doc = ws.OpenDocument("oranda.panel.sqlEditor", fake, "작업")!;
        Dispatcher.UIThread.RunJobs();

        Assert.False(doc.CanFloat);
        DoubleClick(h.Window, TabOf(h.Window, doc));

        Assert.Equal(1, fake.Renamed);
        Assert.Empty(((IRootDock)ws.Layout!).Windows ?? []);
        return Task.CompletedTask;
    });

    // 떠 있던 문서가 든 배치를 다시 지어도(Apply) 문서는 메인 문서 영역으로 들어가고 탭 순서가 유지된다.
    [Fact]
    public Task Applying_layout_puts_floating_document_back_in_main_dock_in_order() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        var ws = h.Get<DockWorkspace>();
        var a = ws.OpenDocument("oranda.panel.sqlEditor")!;
        var b = ws.OpenDocument("oranda.panel.sqlEditor")!;
        var c = ws.OpenDocument("oranda.panel.sqlEditor")!;
        Dispatcher.UIThread.RunJobs();
        DoubleClick(h.Window, TabOf(h.Window, b));
        await HeadlessHost.WaitUntil(() => ((IRootDock)ws.Layout!).Windows?.Count == 1);

        ws.Apply(ws.Capture());

        Assert.Empty(((IRootDock)ws.Layout!).Windows ?? []);
        Assert.Equal(3, ws.Documents.Count);
        Assert.Equal([a, c, b], ws.Documents);
    });
}
