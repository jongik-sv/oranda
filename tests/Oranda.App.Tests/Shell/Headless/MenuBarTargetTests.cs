using Avalonia.Controls;
using Avalonia.Threading;
using Dock.Model.Core;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell.Docking;
using Oranda.App.Shell.Menus;
using Oranda.Core.Shell;

namespace Oranda.App.Tests.Shell.Headless;

/// <summary>
/// TSK-13-22: 메뉴 막대 명령의 대상. 앱과 같은 도킹·등록 경로로 화면을 만들어 확인한다.
/// </summary>
public class MenuBarTargetTests
{
    private static async Task<(ShellHarness H, SqlEditorHost Host, DockWorkspace Dock)> StartAsync()
    {
        var h = new ShellHarness();
        var wiring = h.Services.GetService<IEnumerable<Oranda.Core.Composition.IStartupTask>>()!.OfType<SqlEditorDockWiring>().Single();
        await wiring.RunAsync(default);
        Dispatcher.UIThread.RunJobs();
        return (h, SqlEditorHost.Current, h.Get<DockWorkspace>());
    }

    /// <summary>탭 줄에서 고른 문서가 보이는 문서다. 포커스 기록(CurrentDockable)이 옛 문서에 남아도 활성 문서는 보이는 쪽이다.</summary>
    [Fact]
    public Task Active_document_is_the_visible_tab_even_when_the_focus_record_is_stale() => HeadlessHost.Run(async () =>
    {
        var (h, host, dock) = await StartAsync();
        using (h)
        {
            var registry = h.Get<ICommandRegistry>();
            var keys = h.Get<IContextKeys>();
            await registry.ExecuteAsync("oranda.file.newSqlEditor", keys);
            await registry.ExecuteAsync("oranda.file.newWorkspace", keys);
            await registry.ExecuteAsync("oranda.file.newWorkspace", keys);
            Dispatcher.UIThread.RunJobs();

            var owner = (IDock)dock.Documents[0].Owner!;
            owner.ActiveDockable = dock.Documents[0]; // 탭 줄에서 첫 문서를 골랐다
            Dispatcher.UIThread.RunJobs();
            dock.Factory.SetFocusedDockable(owner, dock.Documents[2]); // 포커스 기록은 마지막에 연 문서에 남아 있다
            Dispatcher.UIThread.RunJobs();

            Assert.Same(dock.Documents[0], dock.ActiveDocument);
            Assert.Same(dock.Documents[0].Panel, host.ActiveWorkspace);
        }
    });

    /// <summary>시스템 메뉴 막대 명령은 포커스가 메뉴로 옮겨 간 뒤에도, 작업 공간이 여럿이어도 보이는 작업 공간·탭에서 실행된다.</summary>
    [Fact]
    public Task Menu_bar_command_runs_on_the_visible_workspace_and_tab_after_focus_leaves() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness(nativeMenu: true);
        var wiring = h.Services.GetService<IEnumerable<Oranda.Core.Composition.IStartupTask>>()!.OfType<SqlEditorDockWiring>().Single();
        await wiring.RunAsync(default);
        Dispatcher.UIThread.RunJobs();
        var host = SqlEditorHost.Current;
        var dock = h.Get<DockWorkspace>();
        var registry = h.Get<ICommandRegistry>();
        var keys = h.Get<IContextKeys>();
        await registry.ExecuteAsync("oranda.file.newSqlEditor", keys);
        await registry.ExecuteAsync("oranda.file.newWorkspace", keys);
        await registry.ExecuteAsync("oranda.file.newWorkspace", keys);
        Dispatcher.UIThread.RunJobs();

        var first = Assert.IsType<SqlWorkspaceViewModel>(dock.Documents[0].Panel);
        var visibleTab = first.ActiveTab;
        var owner = (IDock)dock.Documents[0].Owner!;
        owner.ActiveDockable = dock.Documents[0];
        Dispatcher.UIThread.RunJobs();
        dock.Factory.SetFocusedDockable(owner, dock.Documents[2]); // 낡은 포커스 기록
        h.Window.Focus(); // 포커스가 편집기를 떠나 메뉴 쪽으로
        Dispatcher.UIThread.RunJobs();

        var item = FindItem(h.Window.NativeMenuBar!, "oranda.exec.runScript");
        var command = Assert.IsType<NativeMenuCommand>(item.Command);
        command.Execute(null);
        await Task.Delay(50);
        Dispatcher.UIThread.RunJobs();

        Assert.Same(first, host.ActiveWorkspace);
        Assert.Same(visibleTab, host.Active);
        Assert.Same(dock.Documents[0], dock.ActiveDocument);
    });

    private static NativeMenuItem FindItem(NativeMenu menu, string commandId)
    {
        foreach (var item in menu.Items.OfType<NativeMenuItem>())
        {
            if (item.Command is NativeMenuCommand c && c.CommandId == commandId)
            {
                return item;
            }

            if (item.Menu is { } sub)
            {
                try
                {
                    return FindItem(sub, commandId);
                }
                catch (Xunit.Sdk.XunitException)
                {
                }
            }
        }

        throw new Xunit.Sdk.XunitException($"시스템 메뉴에 명령 {commandId} 항목이 없다.");
    }
}
