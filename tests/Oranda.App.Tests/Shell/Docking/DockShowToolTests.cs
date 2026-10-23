using Avalonia.Threading;
using Dock.Model.Controls;
using Dock.Model.Core;
using Oranda.App.Features.ExecutionPlan;
using Oranda.App.Shell.Docking;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Shell;

namespace Oranda.App.Tests.Shell.Docking;

// ShowTool 이 이미 보이는 도구 창에도 배치를 새로 지어(Apply → LayoutRebuilt) 메인 창 전체가 깜박였다(2026-10-24 사용자 신고: 조합 검사·실행 계획).
// 보이는 창은 배치를 그대로 두고 그 탭만 앞으로 가져온다. 닫힌 창은 지금처럼 배치를 새로 지어 보인다.
public class DockShowToolTests
{
    private const string Results = "oranda.panel.results";
    private const string Messages = "oranda.panel.messages";

    [Fact]
    public Task Showing_a_visible_tool_again_keeps_the_layout_and_only_brings_its_tab_forward() => HeadlessHost.Run(() =>
    {
        using var h = new ShellHarness();
        var ws = h.Get<DockWorkspace>();
        Assert.True(ws.ShowTool(Results));
        Assert.True(ws.ShowTool(Messages));
        Dispatcher.UIThread.RunJobs();
        var layout = ws.Layout;
        int rebuilt = 0;
        ws.LayoutRebuilt += (_, _) => rebuilt++;

        Assert.True(ws.ShowTool(Results));
        Assert.True(ws.ShowTool(Results));

        Assert.Same(layout, ws.Layout);
        Assert.Equal(0, rebuilt);
        var tool = ws.Factory.FindDockable(ws.Layout!, d => d.Id == Results)!;
        Assert.Same(tool, ((IDock)tool.Owner!).ActiveDockable);
    });

    [Fact]
    public Task Showing_a_closed_tool_still_rebuilds_the_layout_and_shows_it() => HeadlessHost.Run(() =>
    {
        using var h = new ShellHarness();
        var ws = h.Get<DockWorkspace>();
        Assert.True(ws.ShowTool(Results));
        Assert.True(ws.HideTool(Results));
        Assert.False(ws.IsToolVisible(Results));

        Assert.True(ws.ShowTool(Results));

        Assert.True(ws.IsToolVisible(Results));
    });

    [Fact]
    public Task Showing_a_floating_tool_keeps_it_floating_and_the_main_layout() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        var ws = h.Get<DockWorkspace>();
        Assert.True(ws.ShowTool(Results));
        Dispatcher.UIThread.RunJobs();
        var tool = ws.Factory.FindDockable(ws.Layout!, d => d.Id == Results)!;
        ws.Factory.FloatDockable(tool);
        await HeadlessHost.WaitUntil(() => ((IRootDock)ws.Layout!).Windows?.Count == 1);
        var layout = ws.Layout;

        Assert.True(ws.ShowTool(Results));

        Assert.Same(layout, ws.Layout);
        Assert.Single(((IRootDock)ws.Layout!).Windows!);
        Assert.False(ws.IsToolVisible(Results)); // 메인 배치로 끌어오지 않았다
    });

    [Fact]
    public Task Running_the_plan_command_again_keeps_the_layout() => HeadlessHost.Run(async () =>
    {
        // 실행 계획 명령은 모두 맨 앞에서 ShowTool(ExecutionPlanCommands.cs) — 「실행 계획 창 보기」 로 그 길만 탄다(접속·계획 서비스 없이).
        using var h = new ShellHarness();
        var ws = h.Get<DockWorkspace>();
        var handler = new ExecutionPlanCommandHandler(h.Services);
        var context = new CommandContext("oranda.view.plan", Oranda.App.Tests.Shell.FakeContextKeys.AllTrue);
        await handler.ExecuteAsync(context, default);
        Dispatcher.UIThread.RunJobs();
        Assert.True(ws.IsToolVisible(ExecutionPlanCommands.PanelId));
        var layout = ws.Layout;

        await handler.ExecuteAsync(context, default);

        Assert.Same(layout, ws.Layout);
    });
}
