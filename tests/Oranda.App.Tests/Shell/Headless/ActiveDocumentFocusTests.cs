using Avalonia.Threading;
using Dock.Model.Core;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell.Docking;
using Oranda.Core.Shell;

namespace Oranda.App.Tests.Shell.Headless;

/// <summary>
/// 결함(TSK-14-10, review 19): 문서 영역을 둘로 나눈 화면에서 활성 문서(<see cref="DockWorkspace.ActiveDocument"/>)는 마지막으로 포커스를 가진 문서다.
/// 도구 창으로 포커스가 옮겨 가도 그 문서가 남고, 이미 보이는 탭인 다른 영역의 문서를 눌러 포커스만 옮겨도 활성 문서가 바뀐다고 알린다.
/// </summary>
public sealed class ActiveDocumentFocusTests
{
    private static async Task<(ShellHarness H, SqlEditorHost Host, DockWorkspace Dock)> StartAsync()
    {
        var h = new ShellHarness();
        var wiring = h.Services.GetService<IEnumerable<Oranda.Core.Composition.IStartupTask>>()!.OfType<SqlEditorDockWiring>().Single();
        await wiring.RunAsync(default);
        Dispatcher.UIThread.RunJobs();
        return (h, SqlEditorHost.Current, h.Get<DockWorkspace>());
    }

    /// <summary>작업 공간 둘을 열고 둘째를 오른쪽 문서 영역으로 나눈다. (왼쪽 문서, 오른쪽 문서)</summary>
    private static async Task<(PanelDocument Left, PanelDocument Right)> SplitAsync(ShellHarness h, DockWorkspace dock)
    {
        var registry = h.Get<ICommandRegistry>();
        var keys = h.Get<IContextKeys>();
        await registry.ExecuteAsync("oranda.file.newSqlEditor", keys);
        await registry.ExecuteAsync("oranda.file.newWorkspace", keys);
        Dispatcher.UIThread.RunJobs();
        var left = dock.Documents[0];
        var right = dock.Documents[^1];
        dock.Factory.NewHorizontalDocumentDock(right); // 탭을 끌어 오른쪽 새 문서 영역으로 나눈 것과 같다
        Dispatcher.UIThread.RunJobs();
        Assert.NotSame(left.Owner, right.Owner); // 두 문서 영역
        return (left, right);
    }

    [Fact]
    public Task Focusing_a_tool_window_keeps_the_last_focused_document_of_a_split_layout_active() => HeadlessHost.Run(async () =>
    {
        var (h, host, dock) = await StartAsync();
        using (h)
        {
            var (_, right) = await SplitAsync(h, dock);
            dock.Factory.SetFocusedDockable((IDock)right.Owner!, right); // 오른쪽 문서를 눌렀다
            Dispatcher.UIThread.RunJobs();
            Assert.Same(right, dock.ActiveDocument);
            Assert.Same(right.Panel, host.ActiveWorkspace);

            Assert.True(dock.ShowTool("oranda.panel.messages"));
            var tool = AllDockables(dock.Layout!).OfType<PanelTool>().First(t => t.Id == "oranda.panel.messages");
            dock.Factory.SetFocusedDockable((IDock)tool.Owner!, tool); // 메시지 창을 눌렀다
            Dispatcher.UIThread.RunJobs();

            Assert.Same(right, dock.ActiveDocument); // 왼쪽 영역의 탭으로 되돌아가지 않는다
            Assert.Same(right.Panel, host.ActiveWorkspace);
        }
    });

    [Fact]
    public Task Focusing_the_visible_document_of_the_other_split_area_makes_it_the_active_document() => HeadlessHost.Run(async () =>
    {
        var (h, host, dock) = await StartAsync();
        using (h)
        {
            var (left, right) = await SplitAsync(h, dock);
            dock.Factory.SetFocusedDockable((IDock)right.Owner!, right);
            Dispatcher.UIThread.RunJobs();
            Assert.Same(right.Panel, host.ActiveWorkspace);

            var changed = 0;
            dock.ActiveDocumentChanged += (_, _) => changed++;
            dock.Factory.SetFocusedDockable((IDock)left.Owner!, left); // 왼쪽 영역의 이미 보이는 탭을 눌렀다(영역의 활성 탭은 그대로)
            Dispatcher.UIThread.RunJobs();

            Assert.Same(left, dock.ActiveDocument);
            Assert.True(changed > 0);
            Assert.Same(left.Panel, host.ActiveWorkspace);
        }
    });

    private static IEnumerable<IDockable> AllDockables(IDockable root)
    {
        yield return root;
        if (root is IDock { VisibleDockables: { } children })
        {
            foreach (var child in children.ToList())
            {
                foreach (var d in AllDockables(child))
                {
                    yield return d;
                }
            }
        }
    }
}
