using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dock.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell.Docking;
using Oranda.App.Shell.Layout;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Shell;

namespace Oranda.App.Tests.Shell.Docking;

// 결함(2026-11-05 사용자 신고): 작업 공간 둘을 나란히 나눠 둔 화면이, 다른 일(도구 창 열기·닫기, 세션 수 변화로 세션 창 자동 표시, 결과 창 토글)이
// 생기면 풀려 한 영역으로 합쳐졌다. 도구 창을 보이고 숨기는 길은 배치를 다시 짓는데, 그때 문서 영역 하나만 남기고 문서를 모두 그리로 옮겼기 때문이다.
public sealed class DockDocumentSplitTests
{
    private const string Sessions = "oranda.panel.sessions";
    private const string Messages = "oranda.panel.messages";

    private static async Task<(ShellHarness H, DockWorkspace Dock, PanelDocument Left, PanelDocument Right)> SplitAsync(bool right = true)
    {
        var h = new ShellHarness();
        var wiring = h.Services.GetService<IEnumerable<Oranda.Core.Composition.IStartupTask>>()!.OfType<SqlEditorDockWiring>().Single();
        await wiring.RunAsync(default);
        Dispatcher.UIThread.RunJobs();
        var dock = h.Get<DockWorkspace>();
        var registry = h.Get<ICommandRegistry>();
        var keys = h.Get<IContextKeys>();
        await registry.ExecuteAsync("oranda.file.newWorkspace", keys);
        await registry.ExecuteAsync("oranda.file.newWorkspace", keys);
        Dispatcher.UIThread.RunJobs();
        var first = dock.Documents[0];
        var second = dock.Documents[^1];
        Assert.True(dock.SplitActiveDocument(right));
        Dispatcher.UIThread.RunJobs();
        Assert.NotSame(first.Owner, second.Owner);
        return (h, dock, first, second);
    }

    private static List<IDocumentDock> DocumentDocks(DockWorkspace dock)
    {
        var found = new List<IDocumentDock>();
        void Walk(IDockable node)
        {
            if (node is IDocumentDock d)
            {
                found.Add(d);
            }

            if (node is IDock owner)
            {
                foreach (var child in owner.VisibleDockables ?? [])
                {
                    Walk(child);
                }
            }
        }

        Walk(dock.Layout!);
        return found;
    }

    private static void AssertStillSplit(DockWorkspace dock, PanelDocument left, PanelDocument right)
    {
        Assert.Equal(2, DocumentDocks(dock).Count);
        Assert.NotSame(left.Owner, right.Owner);
        Assert.Same(left, ((IDock)left.Owner!).ActiveDockable);
        Assert.Same(right, ((IDock)right.Owner!).ActiveDockable);
        Assert.Contains(left, dock.Documents);
        Assert.Contains(right, dock.Documents);
        AssertOwnersConsistent(dock.Layout!);
        AssertSingleActiveDock(dock.Layout!);
    }

    // 포커스를 가진 칸은 하나뿐이다. 다시 지을 때 옛 활성 표시를 남기면 두 탭 줄이 모두 활성으로 보인다.
    private static void AssertSingleActiveDock(IDockable root)
    {
        var active = new List<IDock>();
        void Walk(IDockable node)
        {
            if (node is IDock d)
            {
                if (d.IsActive)
                {
                    active.Add(d);
                }

                foreach (var child in d.VisibleDockables ?? [])
                {
                    Walk(child);
                }
            }
        }

        Walk(root);
        Assert.True(active.Count <= 1, $"활성 표시가 {active.Count}곳에 남았다.");
    }

    // 같은 도킹 개체는 트리에 한 번만 나온다(두 부모 밑에 들어가면 닫기·끌기가 어긋난다).
    private static void AssertNoDockableTwice(IDockable root)
    {
        var seen = new HashSet<IDockable>(ReferenceEqualityComparer.Instance);
        void Walk(IDockable node)
        {
            Assert.True(seen.Add(node), $"{node.Id} 가 트리에 두 번 들어 있다.");
            if (node is IDock d)
            {
                foreach (var child in d.VisibleDockables ?? [])
                {
                    Walk(child);
                }
            }
        }

        Walk(root);
    }

    private static PanelTool? FindTool(DockWorkspace dock, string id)
    {
        PanelTool? found = null;
        void Walk(IDockable node)
        {
            if (node is PanelTool t && t.Id == id)
            {
                found = t;
            }

            if (node is IDock d)
            {
                foreach (var child in d.VisibleDockables ?? [])
                {
                    Walk(child);
                }
            }
        }

        Walk(dock.Layout!);
        return found;
    }

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

    // 살아 있는 칸을 새 트리에 옮겨 달기 때문에, 모든 자식의 Owner 가 실제 부모인지 확인한다(어긋나면 닫기·끌기가 엉뚱한 곳을 건드린다).
    private static void AssertOwnersConsistent(IDockable node)
    {
        if (node is not IDock owner)
        {
            return;
        }

        foreach (var child in owner.VisibleDockables ?? [])
        {
            Assert.Same(owner, child.Owner);
            AssertOwnersConsistent(child);
        }
    }

    private static IToolDock BottomTools(DockWorkspace dock)
    {
        IToolDock? found = null;
        void Walk(IDockable node)
        {
            if (node is IToolDock group && (group.VisibleDockables ?? []).OfType<PanelTool>().Any(t => t.Id == "oranda.panel.results"))
            {
                found = group;
            }

            if (node is IDock owner)
            {
                foreach (var child in owner.VisibleDockables ?? [])
                {
                    Walk(child);
                }
            }
        }

        Walk(dock.Layout!);
        return found!;
    }

    private static DockNode? ResultsNode(DockNode? node) =>
        node is null ? null : node.Kind == DockNodeKind.ToolGroup && (node.Tools ?? []).Contains("oranda.panel.results") ? node : (node.Children ?? []).Select(ResultsNode).FirstOrDefault(n => n is not null);

    private static DockNode? DocumentsNode(DockNode? node) =>
        node is null ? null : node.Kind == DockNodeKind.Documents ? node : (node.Children ?? []).Select(DocumentsNode).FirstOrDefault(n => n is not null);

    [Fact]
    public Task Opening_a_closed_tool_window_keeps_the_document_split() => HeadlessHost.Run(async () =>
    {
        var (h, dock, left, right) = await SplitAsync();
        using (h)
        {
            Assert.False(dock.IsToolVisible(Sessions));

            Assert.True(dock.ShowTool(Sessions)); // 세션이 2개가 되면 세션 창이 저절로 열린다(SessionsAutoShow)
            Dispatcher.UIThread.RunJobs();

            Assert.True(dock.IsToolVisible(Sessions));
            AssertStillSplit(dock, left, right);
        }
    });

    [Fact]
    public Task Closing_a_tool_window_keeps_the_document_split() => HeadlessHost.Run(async () =>
    {
        var (h, dock, left, right) = await SplitAsync();
        using (h)
        {
            Assert.True(dock.ShowTool(Sessions));
            Assert.True(dock.HideTool(Sessions)); // 세션이 다시 1개가 되면 저절로 닫힌다
            Dispatcher.UIThread.RunJobs();

            Assert.False(dock.IsToolVisible(Sessions));
            AssertStillSplit(dock, left, right);
        }
    });

    [Fact]
    public Task Toggling_the_result_pane_keeps_the_document_split() => HeadlessHost.Run(async () =>
    {
        var (h, dock, left, right) = await SplitAsync();
        using (h)
        {
            dock.ToggleTool(Messages);
            dock.ToggleTool(Messages);
            Dispatcher.UIThread.RunJobs();

            AssertStillSplit(dock, left, right);
        }
    });

    [Fact]
    public Task A_vertical_split_and_the_split_widths_survive_a_tool_window_change() => HeadlessHost.Run(async () =>
    {
        var (h, dock, left, right) = await SplitAsync(right: false);
        using (h)
        {
            var area = (IProportionalDock)left.Owner!.Owner!;
            Assert.Equal(Orientation.Vertical, area.Orientation);
            left.Owner.Proportion = 0.3;
            right.Owner!.Proportion = 0.7;

            Assert.True(dock.ShowTool(Sessions));
            Dispatcher.UIThread.RunJobs();

            AssertStillSplit(dock, left, right);
            var after = (IProportionalDock)left.Owner!.Owner!;
            Assert.Equal(Orientation.Vertical, after.Orientation);
            Assert.Equal(0.3, left.Owner.Proportion, 3);
            Assert.Equal(0.7, right.Owner!.Proportion, 3);
        }
    });

    [Fact]
    public Task A_vertical_split_keeps_its_shape_and_the_tool_window_below_keeps_its_height() => HeadlessHost.Run(async () =>
    {
        var (h, dock, left, right) = await SplitAsync(right: false);
        using (h)
        {
            var bottom = BottomTools(dock);
            var before = bottom.Proportion;
            Assert.InRange(before, 0.05, 0.6);

            Assert.True(dock.ShowTool(Sessions));
            Dispatcher.UIThread.RunJobs();
            AssertStillSplit(dock, left, right);

            // 위아래 칸은 감싼 분할 하나(세로)에 들고, 그 분할과 아래 창이 같은 열에 나란히 놓인다.
            var area = (IProportionalDock)left.Owner!.Owner!;
            Assert.Equal(Orientation.Vertical, area.Orientation);
            Assert.Same(area, right.Owner!.Owner);
            Assert.Contains(BottomTools(dock), ((IDock)area.Owner!).VisibleDockables!);

            // 다시 짓는다고 아래 창이 커지지 않는다(문서 칸 비율을 합쳐 적지 않으면 둘 다 Rebalance 몫이 되어 약 .44 까지 커진다).
            Assert.Equal(before, BottomTools(dock).Proportion, 2);
            Assert.True(dock.HideTool(Sessions));
            Assert.Equal(before, BottomTools(dock).Proportion, 2);
        }
    });

    [Fact]
    public Task The_saved_layout_of_a_vertical_split_sums_the_document_cells_and_restores_to_the_same_shape() => HeadlessHost.Run(async () =>
    {
        var (h, dock, left, right) = await SplitAsync(right: false);
        using (h)
        {
            var cells = left.Owner!.Proportion + right.Owner!.Proportion;
            var bottom = BottomTools(dock).Proportion;
            var saved = dock.Capture();

            Assert.Equal(1, CountDocuments(saved.Root));
            Assert.Equal(cells, DocumentsNode(saved.Root)!.Proportion!.Value, 2);
            Assert.Equal(bottom, ResultsNode(saved.Root)!.Proportion!.Value, 2);
            var reconciled = LayoutPlanner.Reconcile(saved, h.Get<IToolPanelRegistry>().Panels);
            Assert.True(LayoutPlanner.SameShape(saved.Root, reconciled.Root, 0.01));
        }
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task A_split_without_a_tool_window_below_survives_a_tool_window_change(bool right) => HeadlessHost.Run(async () =>
    {
        var h = new ShellHarness();
        using (h)
        {
            var wiring = h.Services.GetService<IEnumerable<Oranda.Core.Composition.IStartupTask>>()!.OfType<SqlEditorDockWiring>().Single();
            await wiring.RunAsync(default);
            Dispatcher.UIThread.RunJobs();
            var dock = h.Get<DockWorkspace>();
            foreach (var id in dock.VisibleToolIds.Where(id => LayoutPlanner.DefaultBottomTools.Contains(id)).ToList())
            {
                Assert.True(dock.HideTool(id)); // 아래 묶음을 모두 닫으면 문서 칸이 가로 부모 바로 밑에 놓일 수 있다
            }

            Dispatcher.UIThread.RunJobs();
            Assert.DoesNotContain(dock.VisibleToolIds, id => LayoutPlanner.DefaultBottomTools.Contains(id));

            var registry = h.Get<ICommandRegistry>();
            var keys = h.Get<IContextKeys>();
            await registry.ExecuteAsync("oranda.file.newWorkspace", keys);
            await registry.ExecuteAsync("oranda.file.newWorkspace", keys);
            Dispatcher.UIThread.RunJobs();
            var left = dock.Documents[0];
            var second = dock.Documents[^1];
            Assert.True(dock.SplitActiveDocument(right));
            Dispatcher.UIThread.RunJobs();

            Assert.True(dock.ShowTool(Sessions));
            Dispatcher.UIThread.RunJobs();

            AssertStillSplit(dock, left, second);
        }
    });

    [Fact]
    public Task Closing_the_last_tab_of_an_area_after_a_rebuild_leaves_one_working_area() => HeadlessHost.Run(async () =>
    {
        var (h, dock, left, right) = await SplitAsync(right: false);
        using (h)
        {
            Assert.True(dock.ShowTool(Sessions));
            Dispatcher.UIThread.RunJobs();
            AssertStillSplit(dock, left, right);

            Assert.True(dock.CloseDocument(right)); // 화면의 탭 닫기와 같은 길
            Dispatcher.UIThread.RunJobs();
            Assert.Equal([left], dock.Documents);

            Assert.True(dock.HideTool(Sessions)); // 닫은 뒤에도 다음 재구성이 문제없이 돈다
            Dispatcher.UIThread.RunJobs();

            Assert.Equal([left], dock.Documents);
            Assert.Equal(1, CountDocuments(dock.Capture().Root));
            AssertOwnersConsistent(dock.Layout!);
        }
    });

    [Fact]
    public Task A_tool_window_dropped_into_a_document_cell_is_not_kept_there_after_a_rebuild() => HeadlessHost.Run(async () =>
    {
        var (h, dock, left, right) = await SplitAsync();
        using (h)
        {
            Assert.True(dock.ShowTool("oranda.panel.binds"));
            Dispatcher.UIThread.RunJobs();
            var tool = FindTool(dock, "oranda.panel.binds")!;
            ((IDock)tool.Owner!).VisibleDockables!.Remove(tool); // 화면에서 문서 칸의 탭 줄로 끌어 놓은 것과 같다
            var cell = (IDock)right.Owner!;
            cell.VisibleDockables!.Add(tool);
            tool.Owner = cell;
            cell.ActiveDockable = tool; // 끌어 놓으면 그 탭이 활성이 된다(빠진 뒤 칸의 활성 탭이 남은 문서로 바뀌어야 한다)

            Assert.True(dock.ShowTool(Sessions)); // 재구성
            Dispatcher.UIThread.RunJobs();
            Assert.False(dock.IsToolVisible("oranda.panel.binds"));
            Assert.DoesNotContain(DocumentDocks(dock).SelectMany(d => d.VisibleDockables ?? []), d => d is PanelTool);

            Assert.True(dock.ToggleTool("oranda.panel.binds")); // 보기 메뉴로 다시 연다
            Dispatcher.UIThread.RunJobs();

            AssertStillSplit(dock, left, right); // Owner 일관성 포함
            AssertNoDockableTwice(dock.Layout!);
            Assert.True(dock.IsToolVisible("oranda.panel.binds"));
            Assert.IsType<PanelDocument>(((IDock)right.Owner!).ActiveDockable);
        }
    });

    [Fact]
    public Task Only_one_tab_strip_looks_active_after_a_rebuild_and_a_click_on_the_other_area() => HeadlessHost.Run(async () =>
    {
        var (h, dock, left, right) = await SplitAsync();
        using (h)
        {
            dock.Factory.SetFocusedDockable((IDock)right.Owner!, right);
            Dispatcher.UIThread.RunJobs();
            Assert.True(((IDock)right.Owner!).IsActive); // 전제: 포커스를 가진 칸이 활성으로 표시된다

            Assert.True(dock.ShowTool(Sessions));
            Dispatcher.UIThread.RunJobs();
            dock.Factory.SetFocusedDockable((IDock)left.Owner!, left); // 다른 칸을 누른다
            Dispatcher.UIThread.RunJobs();

            AssertStillSplit(dock, left, right);
            Assert.True(((IDock)left.Owner!).IsActive);
            Assert.False(((IDock)right.Owner!).IsActive);
            Assert.Same(left, dock.ActiveDocument);
        }
    });

    [Fact]
    public Task A_floating_document_joins_the_first_area_and_the_focused_document_stays_active() => HeadlessHost.Run(async () =>
    {
        var (h, dock, left, right) = await SplitAsync();
        using (h)
        {
            var floating = dock.OpenDocument("oranda.panel.sessionMonitor")!;
            Dispatcher.UIThread.RunJobs();
            var tab = h.Window.GetVisualDescendants().OfType<DocumentTabStripItem>().First(t => ReferenceEquals(t.DataContext, floating));
            DoubleClick(h.Window, tab);
            await HeadlessHost.WaitUntil(() => ((IRootDock)dock.Layout!).Windows?.Count == 1);
            dock.Factory.SetFocusedDockable((IDock)right.Owner!, right);
            Dispatcher.UIThread.RunJobs();
            Assert.Same(right, dock.ActiveDocument);

            Assert.True(dock.ShowTool(Sessions));
            Dispatcher.UIThread.RunJobs();

            AssertStillSplit(dock, left, right);
            Assert.Same(right, dock.ActiveDocument);
            Assert.Contains(floating, dock.Documents);
            Assert.Same(left.Owner, floating.Owner); // 떠 있던 문서는 첫 영역으로 들어온다
        }
    });

    [Fact]
    public Task Document_cells_separated_by_a_tool_window_merge_into_the_first_area() => HeadlessHost.Run(async () =>
    {
        var (h, dock, left, right) = await SplitAsync(right: false);
        using (h)
        {
            var column = (IDock)left.Owner!.Owner!;
            var list = column.VisibleDockables!; // [왼쪽 칸, 분할선, 오른쪽 칸, 분할선, 아래 묶음] -> [왼쪽 칸, 분할선, 아래 묶음, 분할선, 오른쪽 칸]
            (list[2], list[4]) = (list[4], list[2]);

            Assert.True(dock.ShowTool(Sessions));
            Dispatcher.UIThread.RunJobs();

            Assert.Single(DocumentDocks(dock)); // 인접하지 않은 칸은 첫 영역으로 합친다
            Assert.Same(left.Owner, right.Owner);
            Assert.Equal(2, dock.Documents.Count);
            AssertOwnersConsistent(dock.Layout!);
            AssertNoDockableTwice(dock.Layout!);
        }
    });

    [Fact]
    public Task Applying_a_layout_through_the_public_path_merges_the_split() => HeadlessHost.Run(async () =>
    {
        var (h, dock, left, right) = await SplitAsync();
        using (h)
        {
            dock.Apply(dock.Capture()); // 시작 복원·이름 붙인 레이아웃 적용이 가는 길
            Dispatcher.UIThread.RunJobs();

            Assert.Single(DocumentDocks(dock));
            Assert.Same(left.Owner, right.Owner);
            Assert.Equal(2, dock.Documents.Count);
        }
    });

    [Fact]
    public Task The_focused_document_of_the_second_area_stays_active_after_a_tool_window_change() => HeadlessHost.Run(async () =>
    {
        var (h, dock, left, right) = await SplitAsync();
        using (h)
        {
            dock.Factory.SetFocusedDockable((IDock)right.Owner!, right);
            Dispatcher.UIThread.RunJobs();
            Assert.Same(right, dock.ActiveDocument);

            Assert.True(dock.ShowTool(Sessions));
            Dispatcher.UIThread.RunJobs();

            AssertStillSplit(dock, left, right);
            Assert.Same(right, dock.ActiveDocument);
        }
    });

    [Fact]
    public Task A_tool_window_opened_on_a_split_goes_outside_the_document_area() => HeadlessHost.Run(async () =>
    {
        var (h, dock, left, right) = await SplitAsync();
        using (h)
        {
            Assert.True(dock.ShowTool("oranda.panel.binds")); // 오른쪽 기본 위치
            Dispatcher.UIThread.RunJobs();

            AssertStillSplit(dock, left, right);
            Assert.Same(left.Owner!.Owner, right.Owner!.Owner); // 두 영역 사이에 끼지 않는다
        }
    });

    [Fact]
    public Task The_saved_layout_keeps_one_document_area_so_restoring_it_still_works() => HeadlessHost.Run(async () =>
    {
        var (h, dock, _, _) = await SplitAsync();
        using (h)
        {
            var saved = dock.Capture();

            Assert.Equal(1, CountDocuments(saved.Root));
            var reconciled = LayoutPlanner.Reconcile(saved, h.Get<IToolPanelRegistry>().Panels);
            Assert.True(LayoutPlanner.SameShape(saved.Root, reconciled.Root, 0.01));
        }
    });

    [Fact]
    public Task Resetting_the_layout_merges_the_split_back_into_one_area() => HeadlessHost.Run(async () =>
    {
        var (h, dock, left, right) = await SplitAsync();
        using (h)
        {
            dock.Reset(); // 사용자가 기본 배치로 되돌리라고 한 것이다
            Dispatcher.UIThread.RunJobs();

            Assert.Single(DocumentDocks(dock));
            Assert.Same(left.Owner, right.Owner);
            Assert.Equal(2, dock.Documents.Count);
        }
    });

    private static int CountDocuments(DockNode? node) =>
        node is null ? 0 : (node.Kind == DockNodeKind.Documents ? 1 : 0) + (node.Children ?? []).Sum(CountDocuments);
}
