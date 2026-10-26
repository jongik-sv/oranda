using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell.Docking;
using Oranda.Core.Shell;

namespace Oranda.App.Tests.Shell.Headless;

/// <summary>SQL 탭 줄의 가로 스크롤 막대(TAB-1): 탭이 넘칠 때만 보이고, 끌면 줄이 옮겨 간다.</summary>
public class SqlTabScrollBarTests
{
    [Fact]
    public Task Horizontal_scrollbar_shows_only_when_tabs_overflow_and_dragging_moves_the_strip() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        var (view, window, ws, registry, keys) = await OpenAsync(h);
        var strip = view.FindControl<ListBox>("SqlTabs")!;

        var oneRow = strip.Bounds.Height;
        Assert.False(HorizontalBar(strip).IsVisible); // 다 들어가면 막대가 없다

        for (var i = 0; i < 24; i++)
        {
            await registry.ExecuteAsync("oranda.file.newSqlEditor", keys);
        }

        Dispatcher.UIThread.RunJobs();
        var bar = HorizontalBar(strip);
        Assert.True(bar.IsVisible);
        Assert.True(strip.Bounds.Height - oneRow <= 10, "막대는 탭 줄 높이를 크게 늘리지 않는다");

        // 자동 숨김이 꺼져 있어 포인터를 올리지 않아도 늘 보인다.
        Assert.False(ScrollViewer.GetAllowAutoHide(strip));

        // 막대를 끌어 앞뒤로 옮긴다: 막대 값이 곧 스크롤 오프셋이다.
        var scroller = strip.GetVisualDescendants().OfType<ScrollViewer>().First();
        ws.ActiveTab = ws.Tabs[0];
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, scroller.Offset.X, 0.5);
        bar.Value = bar.Maximum;
        Dispatcher.UIThread.RunJobs();
        Assert.True(scroller.Offset.X > 0);
        Assert.Equal(scroller.Extent.Width - scroller.Viewport.Width, scroller.Offset.X, 1);

        // 탭을 닫아 다시 다 들어가면 막대도 사라진다.
        while (ws.Tabs.Count > 1)
        {
            ws.Tabs.RemoveAt(ws.Tabs.Count - 1);
        }

        Dispatcher.UIThread.RunJobs();
        Assert.False(HorizontalBar(strip).IsVisible);
        Assert.Equal(oneRow, strip.Bounds.Height);
        window.Close();
    });

    [Fact]
    public Task Render_overflowing_tab_strip_in_light_and_dark() => HeadlessHost.Run(async () =>
    {
        var dir = Environment.GetEnvironmentVariable("ORANDA_RENDER_DIR");
        if (string.IsNullOrEmpty(dir))
        {
            return;
        }

        using var h = new ShellHarness();
        var (view, window, ws, registry, keys) = await OpenAsync(h);
        for (var i = 0; i < 24; i++)
        {
            await registry.ExecuteAsync("oranda.file.newSqlEditor", keys);
        }

        ws.ActiveTab = ws.Tabs[3];
        Dispatcher.UIThread.RunJobs();
        Directory.CreateDirectory(dir);
        try
        {
            foreach (var (variant, tone) in new[] { (Avalonia.Styling.ThemeVariant.Light, "light"), (Avalonia.Styling.ThemeVariant.Dark, "dark") })
            {
                Application.Current!.RequestedThemeVariant = variant;
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                using var bmp = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height));
                bmp.Render(window);
#pragma warning disable CS0618 // 렌더 확인용: 새 오버로드가 없는 헤드리스 비트맵
                bmp.Save(Path.Combine(dir, $"sql-tabs-scrollbar-{tone}.png"));
#pragma warning restore CS0618
            }
        }
        finally
        {
            Application.Current!.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Default;
            Dispatcher.UIThread.RunJobs();
            window.Close();
        }
    });

    private static ScrollBar HorizontalBar(ListBox strip) =>
        strip.GetVisualDescendants().OfType<ScrollBar>().Single(b => b.Orientation == Avalonia.Layout.Orientation.Horizontal);

    private static async Task<(SqlWorkspaceView View, Window Window, SqlWorkspaceViewModel Ws, ICommandRegistry Registry, IContextKeys Keys)> OpenAsync(ShellHarness h)
    {
        var wiring = h.Services.GetService<IEnumerable<Oranda.Core.Composition.IStartupTask>>()!.OfType<SqlEditorDockWiring>().Single();
        await wiring.RunAsync(default);
        Dispatcher.UIThread.RunJobs();
        var registry = h.Get<ICommandRegistry>();
        var keys = h.Get<IContextKeys>();
        await registry.ExecuteAsync("oranda.file.newSqlEditor", keys);
        Dispatcher.UIThread.RunJobs();
        var ws = Assert.IsType<SqlWorkspaceViewModel>(h.Get<DockWorkspace>().ActiveDocument!.Panel);
        var view = new SqlWorkspaceView { DataContext = ws };
        var window = new Window { Content = view, Width = 600, Height = 400 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (view, window, ws, registry, keys);
    }
}
