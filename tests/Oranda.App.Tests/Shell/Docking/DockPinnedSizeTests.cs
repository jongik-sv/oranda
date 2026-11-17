using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Dock.Model.Controls;
using Oranda.App.Shell.Docking;
using Oranda.App.Shell.Layout;
using Oranda.App.Tests.Shell.Headless;

namespace Oranda.App.Tests.Shell.Docking;

// 2026-11-17 사용자 신고: 왼쪽·오른쪽 도구 창을 핀으로 접은 채 앱을 다시 시작하면 사이드바에서 펼친 창이 아주 좁았다(글자가 한 자씩 줄바꿈).
// Dock 은 핀을 뺄 때 그 창이 보이던 크기를 PinnedBounds 에 적고 펼칠 때 그 크기를 쓴다. 다시 시작하면 화면에 붙기 전에 핀을 빼서
// 보이던 크기가 없었고, 펼친 칸이 내용 폭(최소 50)으로 줄었다 → 펼침 크기를 배치에 저장하고, 기록이 없으면 기본 크기로 채운다.
public class DockPinnedSizeTests
{
    private const string Sessions = "oranda.panel.sessions";

    private static void Pump() => HeadlessHost.Pump(5);

    private static IRootDock Root(DockWorkspace ws) => (IRootDock)ws.Layout!;

    private static PanelTool Pinned(DockWorkspace ws, string id) =>
        (PanelTool)new[] { Root(ws).LeftPinnedDockables, Root(ws).RightPinnedDockables, Root(ws).TopPinnedDockables, Root(ws).BottomPinnedDockables }
            .SelectMany(l => l ?? [])
            .First(d => d.Id == id);

    /// <summary>세션 창(왼쪽 묶음)을 보이고 화면에 그린 뒤 핀으로 접는다.</summary>
    private static DockWorkspace PinSessions(ShellHarness h)
    {
        var ws = h.Get<DockWorkspace>();
        Assert.True(ws.ShowTool(Sessions));
        Pump();
        ws.Factory.PinDockable((PanelTool)ws.Factory.FindDockable(ws.Layout!, d => d.Id == Sessions)!);
        Pump();
        return ws;
    }

    /// <summary>사이드바에서 펼친 칸의 폭.</summary>
    private static double PreviewWidth(DockWorkspace ws, Window window)
    {
        Assert.True(ws.ShowTool(Sessions)); // 핀 창을 부르면 사이드바에서 펼친다
        Pump();
        Assert.True(Root(ws).PinnedDock?.VisibleDockables?.Any(d => d.Id == Sessions) == true);
        var preview = window.GetVisualDescendants().OfType<ContentControl>()
            .First(c => c.Name == "PART_PinnedDock" && c.IsEffectivelyVisible && c.Bounds.Width > 0);
        return preview.Bounds.Width;
    }

    [Fact]
    public Task A_pinned_tool_slides_out_at_its_saved_size_after_a_restart() => HeadlessHost.Run(() =>
    {
        LayoutDocument saved;
        double before;
        using (var first = new ShellHarness())
        {
            var ws0 = PinSessions(first);
            before = PreviewWidth(ws0, first.Window);
            Assert.True(before > 150, $"첫 실행 펼침 폭 {before}");
            saved = ws0.Capture();
        }

        Assert.NotNull(saved.PinnedSizes);
        Assert.True(saved.PinnedSizes!.ContainsKey(Sessions));
        var json = System.Text.Json.JsonSerializer.Serialize(saved, Oranda.App.Shell.ShellJsonContext.Default.LayoutDocument); // layout.json 으로 저장·읽기
        var loaded = System.Text.Json.JsonSerializer.Deserialize(json, Oranda.App.Shell.ShellJsonContext.Default.LayoutDocument)!;

        using var h = new ShellHarness();
        var ws = h.Get<DockWorkspace>();
        ws.Apply(loaded);
        Pump();

        Assert.Equal(before, PreviewWidth(ws, h.Window), 0);
    });

    [Fact]
    public Task A_pinned_tool_without_a_saved_size_slides_out_at_the_default_size() => HeadlessHost.Run(() =>
    {
        LayoutDocument saved;
        using (var first = new ShellHarness())
        {
            saved = PinSessions(first).Capture() with { PinnedSizes = null }; // 크기를 적지 않던 옛 배치
        }

        using var h = new ShellHarness();
        var ws = h.Get<DockWorkspace>();
        ws.Apply(saved);
        Pump();

        Pinned(ws, Sessions).GetPinnedBounds(out _, out _, out var width, out _);
        Assert.True(width > 150, $"기본 펼침 폭 {width}");
        Assert.True(PreviewWidth(ws, h.Window) > 150);
    });
}
