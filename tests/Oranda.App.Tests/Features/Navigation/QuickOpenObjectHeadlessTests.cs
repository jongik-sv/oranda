using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.Navigation;
using Oranda.App.Features.SchemaBrowser;
using Oranda.App.Shell;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;

namespace Oranda.App.Tests.Features.Navigation;

/// <summary>앱 전체(Headless)에서 단축키로 DB 객체로 이동 창을 열고 Alt+Enter 로 스키마 브라우저 트리의 노드를 고른다.</summary>
public class QuickOpenObjectHeadlessTests
{
    [Fact]
    public Task Primary_p_opens_the_window_and_alt_enter_selects_the_object_in_the_schema_browser_tree() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        var meta = new NavFakeMeta();
        meta.Schemas.AddRange(["HR", "SCOTT"]);
        meta.Objects.AddRange(
        [
            new DbObjectRef("SCOTT", "EMP", "TABLE"),
            new DbObjectRef("SCOTT", "DEPT", "TABLE"),
            new DbObjectRef("HR", "JOBS", "TABLE"),
            new DbObjectRef("HR", "JOB_HISTORY", "TABLE"),
        ]);
        var sessions = new NavFakeSessions();
        sessions.Sessions.Add(new NavFakeSession());

        // 지연 어셈블리(Oracle·Language)를 먼저 훑어 둔다 — 뒤에 넣은 가짜가 마지막 등록으로 이기게.
        h.Services.ScanDeferred();
        h.Services.AddInstance<IMetadataProvider>(meta);
        h.Services.AddInstance<ISessionManager>(sessions);
        h.Get<ContextKeyService>().Set("hasSession", true);
        h.Window.Activate();

        try
        {
            var primary = OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;
            h.Window.KeyPress(Key.P, primary, PhysicalKey.P, "p");
            await HeadlessHost.WaitUntil(() => ObjectNavigationCommandHandler.OpenWindow is not null);

            var window = ObjectNavigationCommandHandler.OpenWindow!;
            var vm = Assert.IsType<QuickOpenObjectViewModel>(window.DataContext);
            await HeadlessHost.WaitUntil(() => vm.ObjectCount == 4);
            Assert.Equal(1, meta.ListObjectsCalls);

            vm.Query = "job_h";
            await HeadlessHost.WaitUntil(() => vm.Selected?.Display == "HR.JOB_HISTORY");
            Assert.Equal(1, meta.ListObjectsCalls); // 입력마다 DB 를 묻지 않는다

            window.KeyPress(Key.Enter, RawInputModifiers.Alt, PhysicalKey.Enter, null);
            await HeadlessHost.WaitUntil(() => ObjectNavigationCommandHandler.OpenWindow is null);

            var view = h.Window.GetVisualDescendants().OfType<SchemaBrowserView>().First();
            var browser = Assert.IsType<SchemaBrowserViewModel>(view.DataContext);
            await HeadlessHost.WaitUntil(() => browser.SelectedNode?.Ref == new DbObjectRef("HR", "JOB_HISTORY", "TABLE"));
            var tree = view.FindControl<TreeView>("Tree")!;
            await HeadlessHost.WaitUntil(() => ReferenceEquals(tree.SelectedItem, browser.SelectedNode));
            Assert.True(browser.SelectedNode!.Parent!.IsExpanded);
        }
        finally
        {
            ObjectNavigationCommandHandler.OpenWindow?.Close();
        }
    });

    [Theory]
    [InlineData("closed")]
    [InlineData("hidden")]
    [InlineData("behindScripts")]
    public Task Reveal_brings_the_schema_browser_to_the_front_when_it_is_not_shown(string how) => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        var meta = new NavFakeMeta();
        meta.Schemas.AddRange(["HR", "SCOTT"]);
        meta.Objects.AddRange([new DbObjectRef("SCOTT", "EMP", "TABLE"), new DbObjectRef("HR", "JOBS", "TABLE")]);
        var sessions = new NavFakeSessions();
        sessions.Sessions.Add(new NavFakeSession());
        h.Services.ScanDeferred();
        h.Services.AddInstance<IMetadataProvider>(meta);
        h.Services.AddInstance<ISessionManager>(sessions);
        var keys = h.Get<ContextKeyService>();
        keys.Set("hasSession", true);
        var docking = h.Get<IShellUi>().Docking;
        const string Panel = "oranda.panel.schemaBrowser";

        switch (how)
        {
            case "closed":
                docking.Factory.CloseDockable(SchemaTool(docking)!);
                break;
            case "hidden":
                Assert.True(docking.HideTool(Panel));
                break;
            default:
                Assert.True(docking.ShowTool("oranda.panel.scripts"));
                Assert.NotSame(SchemaTool(docking), SchemaTool(docking)?.Owner is Dock.Model.Core.IDock { ActiveDockable: var a } ? a : null);
                break;
        }

        Dispatcher.UIThread.RunJobs();
        var ran = await h.Get<Oranda.Core.Shell.ICommandRegistry>().ExecuteAsync("oranda.schema.reveal", keys, new DbObjectRef("HR", "JOBS", "TABLE"));
        Assert.True(ran);

        Assert.True(docking.IsToolVisible(Panel));
        var tool = SchemaTool(docking)!;
        Assert.Same(tool, Assert.IsAssignableFrom<Dock.Model.Core.IDock>(tool.Owner).ActiveDockable);
        var browser = Assert.IsType<SchemaBrowserViewModel>(tool.Panel);
        await HeadlessHost.WaitUntil(() => browser.SelectedNode?.Ref == new DbObjectRef("HR", "JOBS", "TABLE"));
    });

    private static Oranda.App.Shell.Docking.PanelTool? SchemaTool(Oranda.App.Shell.Docking.DockWorkspace docking)
    {
        var stack = new Stack<Dock.Model.Core.IDockable>();
        if (docking.Layout is { } root)
        {
            stack.Push(root);
        }

        while (stack.Count > 0)
        {
            var d = stack.Pop();
            if (d is Oranda.App.Shell.Docking.PanelTool { Definition.Id: "oranda.panel.schemaBrowser" } tool)
            {
                return tool;
            }

            foreach (var k in (d as Dock.Model.Core.IDock)?.VisibleDockables ?? [])
            {
                stack.Push(k);
            }
        }

        return null;
    }
}
