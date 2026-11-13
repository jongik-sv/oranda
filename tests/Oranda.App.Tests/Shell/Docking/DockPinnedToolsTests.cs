using Avalonia.Threading;
using Dock.Model.Controls;
using Dock.Model.Core;
using Oranda.App.Shell.Docking;
using Oranda.App.Tests.Shell.Headless;

namespace Oranda.App.Tests.Shell.Docking;

// 2026-11-13 사용자 신고:
// - 도구 창 탭 메뉴 「Dock as Tabbed Document」 로 스키마 브라우저가 문서 탭 자리에 붙어 쓰기 곤란했다 → 도구 창은 문서로 넣지 않는다.
// - 핀을 빼 사이드바로 접은 창이 배치 저장에서 빠져 닫힌 창이 되었다(다음 실행에 사라짐, 보기 메뉴도 꺼짐) → 핀 상태를 저장·복원한다.
public class DockPinnedToolsTests
{
    private const string Schema = "oranda.panel.schemaBrowser";
    private const string Results = "oranda.panel.results";

    private static PanelTool Tool(DockWorkspace ws, string id) => (PanelTool)ws.Factory.FindDockable(ws.Layout!, d => d.Id == id)!;

    private static IRootDock Root(DockWorkspace ws) => (IRootDock)ws.Layout!;

    private static bool IsPinned(DockWorkspace ws, string id) =>
        new[] { Root(ws).LeftPinnedDockables, Root(ws).RightPinnedDockables, Root(ws).TopPinnedDockables, Root(ws).BottomPinnedDockables }
            .Any(l => l?.Any(d => d.Id == id) == true);

    private static DockWorkspace PinSchema(ShellHarness h)
    {
        var ws = h.Get<DockWorkspace>();
        Assert.True(ws.ShowTool(Schema));
        Dispatcher.UIThread.RunJobs();
        ws.Factory.PinDockable(Tool(ws, Schema));
        Dispatcher.UIThread.RunJobs();
        Assert.True(IsPinned(ws, Schema));
        return ws;
    }

    [Fact]
    public Task Tool_windows_cannot_be_docked_as_documents() => HeadlessHost.Run(() =>
    {
        using var h = new ShellHarness();
        var ws = h.Get<DockWorkspace>();
        Assert.True(ws.ShowTool(Schema));

        Assert.False(Tool(ws, Schema).CanDockAsDocument);
    });

    [Fact]
    public Task A_pinned_tool_counts_as_open_and_is_saved_as_pinned_not_hidden() => HeadlessHost.Run(() =>
    {
        using var h = new ShellHarness();
        var ws = PinSchema(h);

        Assert.True(ws.IsToolVisible(Schema)); // 보기 메뉴에 체크로 보인다
        var saved = ws.Capture();
        Assert.Contains(Schema, saved.PinnedTools);
        Assert.DoesNotContain(Schema, saved.HiddenTools);
        Assert.Contains(Schema, Oranda.App.Shell.Layout.LayoutPlanner.ToolIds(saved.Root));
    });

    [Fact]
    public Task A_pinned_tool_comes_back_pinned_on_the_next_start() => HeadlessHost.Run(() =>
    {
        Oranda.App.Shell.Layout.LayoutDocument saved;
        using (var first = new ShellHarness())
        {
            saved = PinSchema(first).Capture();
        }

        using var h = new ShellHarness();
        var ws = h.Get<DockWorkspace>();
        ws.Apply(saved);
        Dispatcher.UIThread.RunJobs();

        Assert.True(IsPinned(ws, Schema));
        Assert.True(ws.IsToolVisible(Schema));
        Assert.Contains(Schema, ws.Capture().PinnedTools);
    });

    [Fact]
    public Task Rebuilding_the_layout_for_another_tool_keeps_the_pin() => HeadlessHost.Run(() =>
    {
        using var h = new ShellHarness();
        var ws = PinSchema(h);
        Assert.True(ws.HideTool(Results));

        Assert.True(ws.ShowTool(Results)); // 닫힌 창 보이기 = 배치 다시 짓기
        Dispatcher.UIThread.RunJobs();

        Assert.True(IsPinned(ws, Schema));
        Assert.True(ws.IsToolVisible(Results));
    });

    [Fact]
    public Task Showing_a_pinned_tool_keeps_it_pinned_without_rebuilding() => HeadlessHost.Run(() =>
    {
        using var h = new ShellHarness();
        var ws = PinSchema(h);
        var layout = ws.Layout;

        Assert.True(ws.ShowTool(Schema));
        Assert.True(ws.ActivateTool(Schema));

        Assert.Same(layout, ws.Layout);
        Assert.True(IsPinned(ws, Schema));
    });

    [Fact]
    public Task Closing_a_pinned_tool_from_the_view_menu_closes_it() => HeadlessHost.Run(() =>
    {
        using var h = new ShellHarness();
        var ws = PinSchema(h);

        Assert.False(ws.ToggleTool(Schema)); // 보기 메뉴 체크 해제
        Dispatcher.UIThread.RunJobs();

        Assert.False(ws.IsToolVisible(Schema));
        Assert.False(IsPinned(ws, Schema));
        var saved = ws.Capture();
        Assert.Contains(Schema, saved.HiddenTools);
        Assert.DoesNotContain(Schema, saved.PinnedTools);

        Assert.True(ws.ToggleTool(Schema)); // 다시 체크하면 보인다
        Assert.True(ws.IsToolVisible(Schema));
        Assert.False(IsPinned(ws, Schema));
    });
}
