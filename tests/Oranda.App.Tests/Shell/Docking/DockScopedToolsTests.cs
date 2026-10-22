using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dock.Model.Core;
using Oranda.App.Shell.Docking;
using Oranda.App.Shell.Layout;
using Oranda.App.Tests.Shell.Headless;

namespace Oranda.App.Tests.Shell.Docking;

// TSK-13-34 D1: SQL 창에 딸린 도구 창(결과·메시지 …)은 활성 문서가 SQL 편집기가 아니면 숨고, 돌아오면 같은 자리·크기로 보인다.
public class DockScopedToolsTests
{
    private const string Results = "oranda.panel.results";
    private const string Messages = "oranda.panel.messages";
    private const string History = "oranda.panel.history";
    private const string Detail = "oranda.panel.objectDetail";

    private static void Pump()
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>결과·메시지·실행 이력을 아래 묶음에 함께 두고(결과 활성) SQL 문서 하나, 상세 문서 하나를 연다.</summary>
    private static (PanelDocument Sql, PanelDocument Detail) Setup(DockWorkspace ws)
    {
        ws.ShowTool(History);
        ws.ShowTool(Messages);
        ws.ShowTool(Results);
        var sql = ws.OpenDocument("oranda.panel.sqlEditor")!;
        var detail = ws.OpenDocument(Detail)!;
        ws.Activate(sql);
        Pump();
        return (sql, detail);
    }

    private static Control? ViewOf(Window window, object dock) =>
        window.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => ReferenceEquals(c.DataContext, dock) && c.GetType().Name == "ToolDockControl");

    [Fact]
    public Task Detail_tab_hides_sql_tools_and_sql_tab_restores_same_place_and_size() => HeadlessHost.Run(() =>
    {
        using var h = new ShellHarness();
        var ws = h.Get<DockWorkspace>();
        var (sql, detail) = Setup(ws);
        Assert.Contains(Results, ws.VisibleToolIds);
        Assert.Contains(History, ws.VisibleToolIds);
        var results = ws.Factory.FindDockable(ws.Layout!, d => d.Id == Results)!;
        var group = (IDock)results.Owner!;
        var order = group.VisibleDockables!.Select(d => d.Id).ToList();
        var view = ViewOf(h.Window, group);
        var size = view?.Bounds.Size;
        var before = ws.Capture();

        for (var round = 0; round < 4; round++)
        {
            ws.Activate(detail);
            Pump();
            Assert.DoesNotContain(Results, ws.VisibleToolIds);
            Assert.DoesNotContain(Messages, ws.VisibleToolIds);
            Assert.Contains(History, ws.VisibleToolIds); // 실행 이력은 그대로
            Assert.Equal(order.Where(id => !DockWorkspace.SqlScopedToolIds.Contains(id!)).ToList(), group.VisibleDockables!.Select(d => d.Id).ToList());
            Assert.Contains(Results, ws.ScopedHiddenToolIds);

            // 숨긴 동안 저장해도 배치 파일에는 숨김이 없다.
            var saved = ws.Capture();
            Assert.Equal(LayoutPlanner.ToolIds(before.Root!), LayoutPlanner.ToolIds(saved.Root!));
            Assert.DoesNotContain(Results, saved.HiddenTools ?? []);
            Assert.Equal(System.Text.Json.JsonSerializer.Serialize(before.Root), System.Text.Json.JsonSerializer.Serialize(saved.Root));

            ws.Activate(sql);
            Pump();
            Assert.Empty(ws.ScopedHiddenToolIds);
            Assert.Equal(order, group.VisibleDockables!.Select(d => d.Id).ToList());
            Assert.Same(results, group.ActiveDockable);
            if (size is { } s)
            {
                Assert.Equal(s, ViewOf(h.Window, group)!.Bounds.Size);
            }
        }

        Assert.NotNull(size); // 화면 트리에서 실제 크기를 견줬다
    });

    [Fact]
    public Task Group_of_only_sql_tools_collapses_and_comes_back_with_same_proportions() => HeadlessHost.Run(() =>
    {
        using var h = new ShellHarness();
        var ws = h.Get<DockWorkspace>();
        var registry = h.Get<Oranda.Core.Shell.IToolPanelRegistry>();

        // 문서 영역(0.7) 아래에 SQL 창 도구 창만 든 묶음(0.3) 하나, 왼쪽에 스키마 브라우저.
        var root = new DockNode
        {
            Kind = DockNodeKind.Split,
            Orientation = DockOrientation.Horizontal,
            Children =
            [
                new DockNode { Kind = DockNodeKind.ToolGroup, Proportion = 0.25, Tools = ["oranda.panel.schemaBrowser"] },
                new DockNode
                {
                    Kind = DockNodeKind.Split,
                    Orientation = DockOrientation.Vertical,
                    Proportion = 0.75,
                    Children =
                    [
                        new DockNode { Kind = DockNodeKind.Documents, Proportion = 0.7 },
                        new DockNode { Kind = DockNodeKind.ToolGroup, Proportion = 0.3, Tools = [Results, Messages], ActiveTool = Messages },
                    ],
                },
            ],
        };
        var shown = new HashSet<string>(["oranda.panel.schemaBrowser", Results, Messages]);
        ws.Apply(new LayoutDocument
        {
            Root = root,
            HiddenTools = registry.Panels.Where(p => p.Kind == Oranda.Core.Shell.ToolPanelKind.Tool && !shown.Contains(p.Id)).Select(p => p.Id).ToList(),
            Migrations = [LayoutPlanner.RightToolsOpenMigration],
        });
        var sql = ws.OpenDocument("oranda.panel.sqlEditor")!;
        var detail = ws.OpenDocument(Detail)!;
        ws.Activate(sql);
        Pump();
        Assert.Equal(shown.Order(StringComparer.Ordinal).ToList(), ws.VisibleToolIds.Order(StringComparer.Ordinal).ToList());

        var group = (IDock)ws.Factory.FindDockable(ws.Layout!, d => d.Id == Results)!.Owner!;
        var docs = ws.Factory.FindDockable(ws.Layout!, d => d.Id == DockWorkspace.DocumentsId)!;
        var height = ViewOf(h.Window, group)!.Bounds.Height;
        var docsHeight = DocsView(h.Window, docs).Bounds.Height;
        Assert.True(height > 50, "도구 묶음 높이 " + height);
        var before = System.Text.Json.JsonSerializer.Serialize(ws.Capture().Root);

        for (var round = 0; round < 4; round++)
        {
            ws.Activate(detail);
            Pump();
            Assert.True(group.IsEmpty);
            Assert.True(ViewOf(h.Window, group) is not { IsEffectivelyVisible: true, Bounds.Height: > 1 }, "빈 묶음이 접히지 않았다.");
            Assert.True(DocsView(h.Window, docs).Bounds.Height > docsHeight + height / 2, "문서 영역이 늘어나지 않았다.");
            Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(ws.Capture().Root)); // 저장 내용은 숨기기 전 그대로

            ws.Activate(sql);
            Pump();
            Assert.False(group.IsEmpty);
            Assert.Equal(Messages, group.ActiveDockable?.Id);
            Assert.Equal(height, ViewOf(h.Window, group)!.Bounds.Height, 1);
            Assert.Equal(docsHeight, DocsView(h.Window, docs).Bounds.Height, 1);
        }

        Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(ws.Capture().Root));

        // 숨긴 동안 상관없는 칸(왼쪽 스키마 브라우저 폭)을 바꾸면 그 값은 저장·복원 뒤에도 남는다.
        ws.Activate(detail);
        Pump();
        var left = ws.Factory.FindDockable(ws.Layout!, d => d.Id == "oranda.panel.schemaBrowser")!.Owner!;
        left.Proportion = 0.4;
        Pump();
        var leftNow = left.Proportion;
        Assert.Equal(Math.Round(leftNow, 4), ws.Capture().Root!.Children[0].Proportion);
        ws.Activate(sql);
        Pump();
        Assert.Equal(leftNow, left.Proportion, 3);
        Assert.Equal(height, ViewOf(h.Window, group)!.Bounds.Height, 1);
    });

    private static Control DocsView(Window window, object dock) =>
        window.GetVisualDescendants().OfType<Control>().First(c => ReferenceEquals(c.DataContext, dock) && c.GetType().Name == "DocumentDockControl");

    [Fact]
    public Task Panel_closed_by_user_is_not_reopened() => HeadlessHost.Run(() =>
    {
        using var h = new ShellHarness();
        var ws = h.Get<DockWorkspace>();
        var (sql, detail) = Setup(ws);
        ws.HideTool(Messages);
        ws.Activate(detail);
        Pump();
        Assert.DoesNotContain(Messages, ws.ScopedHiddenToolIds);
        ws.Activate(sql);
        Pump();
        Assert.Contains(Results, ws.VisibleToolIds);
        Assert.DoesNotContain(Messages, ws.VisibleToolIds);
        Assert.Contains(Messages, ws.Capture().HiddenTools ?? []);
    });

    [Fact]
    public Task Saved_layout_while_hidden_restores_tools_after_restart() => HeadlessHost.Run(() =>
    {
        LayoutDocument saved;
        using (var h = new ShellHarness())
        {
            var ws = h.Get<DockWorkspace>();
            var (_, detail) = Setup(ws);
            ws.Activate(detail);
            Pump();
            Assert.NotEmpty(ws.ScopedHiddenToolIds);
            saved = ws.Capture();
        }

        using var again = new ShellHarness();
        var ws2 = again.Get<DockWorkspace>();
        ws2.Apply(saved);
        Pump();
        Assert.Contains(Results, ws2.VisibleToolIds);
        Assert.Contains(Messages, ws2.VisibleToolIds);
        Assert.Empty(ws2.ScopedHiddenToolIds);
    });

    [Fact]
    public Task No_active_document_keeps_state_and_show_tool_brings_panel_back() => HeadlessHost.Run(() =>
    {
        using var h = new ShellHarness();
        var ws = h.Get<DockWorkspace>();
        var (sql, detail) = Setup(ws);
        ws.Activate(detail);
        Pump();
        ws.CloseDocument(sql);
        ws.CloseDocument(detail);
        Pump();
        Assert.Null(ws.ActiveDocument);
        Assert.Contains(Results, ws.ScopedHiddenToolIds); // 문서가 없으면 그대로

        // 메뉴의 「결과 보기」(ToggleTool) 는 숨긴 창을 보이게 한다(숨긴 창은 「보임」으로 세지 않는다).
        Assert.False(ws.IsToolVisible(Results));
        ws.ToggleTool(Results);
        Pump();
        Assert.True(ws.IsToolVisible(Results));
        Assert.Contains(Messages, ws.VisibleToolIds);
        Assert.Empty(ws.ScopedHiddenToolIds);
    });

    [Fact]
    public Task Activate_tool_on_hidden_panel_restores_only_that_panel_without_rebuild() => HeadlessHost.Run(() =>
    {
        using var h = new ShellHarness();
        var ws = h.Get<DockWorkspace>();
        var (sql, detail) = Setup(ws);
        var group = (IDock)ws.Factory.FindDockable(ws.Layout!, d => d.Id == Results)!.Owner!;
        var order = group.VisibleDockables!.Select(d => d.Id).ToList();
        ws.Activate(detail);
        Pump();
        var rebuilt = 0;
        ws.LayoutRebuilt += (_, _) => rebuilt++;
        Assert.True(ws.ActivateTool(Messages)); // 예: 뒤에서 끝난 실행이 메시지 창을 앞으로
        Pump();
        Assert.True(ws.IsToolVisible(Messages));
        Assert.False(ws.IsToolVisible(Results)); // 나머지는 숨긴 채
        Assert.Equal(0, rebuilt);
        Assert.Equal(Messages, group.ActiveDockable?.Id);

        // SQL 탭으로 돌아오면 나머지도 원래 순서로 돌아온다.
        ws.Activate(sql);
        Pump();
        Assert.Equal(order, group.VisibleDockables!.Select(d => d.Id).ToList());
    });

    [Fact]
    public Task Hide_tool_on_a_hidden_panel_closes_it_for_real_and_keeps_the_rest_hidden() => HeadlessHost.Run(() =>
    {
        using var h = new ShellHarness();
        var ws = h.Get<DockWorkspace>();
        var (sql, detail) = Setup(ws);
        ws.Activate(detail);
        Pump();
        var changed = 0;
        ws.LayoutChanged += (_, _) => changed++;
        Assert.True(ws.HideTool(Messages));
        Assert.True(changed > 0);
        Assert.Contains(Results, ws.ScopedHiddenToolIds);
        Assert.Contains(Messages, ws.Capture().HiddenTools ?? []);
        Assert.DoesNotContain(Results, ws.Capture().HiddenTools ?? []);

        ws.Activate(sql);
        Pump();
        Assert.Contains(Results, ws.VisibleToolIds);
        Assert.DoesNotContain(Messages, ws.VisibleToolIds);
    });

    [Fact]
    public Task Hiding_in_two_steps_keeps_the_original_tab_order() => HeadlessHost.Run(() =>
    {
        using var h = new ShellHarness();
        var ws = h.Get<DockWorkspace>();
        var (sql, detail) = Setup(ws);
        var detail2 = ws.OpenDocument(Detail)!;
        ws.Activate(sql);
        Pump();
        var group = (IDock)ws.Factory.FindDockable(ws.Layout!, d => d.Id == Results)!.Owner!;
        var order = group.VisibleDockables!.Select(d => d.Id).ToList();

        ws.Activate(detail);
        Pump();
        ws.ActivateTool("oranda.panel.output"); // 상세 문서에서 출력 창만 앞으로(뒤에서 끝난 실행 등)
        Pump();
        Assert.True(ws.IsToolVisible("oranda.panel.output"));
        Assert.False(ws.IsToolVisible(Results));

        ws.Activate(detail2); // 다른 상세 문서 → 출력 창도 숨는다(두 번째 숨기기)
        Pump();
        Assert.False(ws.IsToolVisible("oranda.panel.output"));

        ws.Activate(sql);
        Pump();
        group = (IDock)ws.Factory.FindDockable(ws.Layout!, d => d.Id == Results)!.Owner!;
        Assert.Equal(order, group.VisibleDockables!.Select(d => d.Id).ToList());
    });

    [Fact]
    public Task Hidden_panel_whose_group_disappeared_is_not_saved_as_closed_and_comes_back() => HeadlessHost.Run(() =>
    {
        using var h = new ShellHarness();
        var ws = h.Get<DockWorkspace>();
        var (sql, detail) = Setup(ws);
        ws.Activate(detail);
        Pump();

        // 숨긴 동안 사용자가 그 묶음의 남은 창(실행 이력 등)을 모두 다른 곳으로 옮겨 묶음이 트리에서 사라졌다.
        var group = (IDock)ws.Factory.FindDockable(ws.Layout!, d => d.Id == History)!.Owner!;
        foreach (var t in group.VisibleDockables!.ToList())
        {
            ws.Factory.RemoveDockable(t, collapse: true);
        }

        Pump();
        var saved = ws.Capture();
        Assert.DoesNotContain(Results, saved.HiddenTools ?? []);
        Assert.DoesNotContain(Messages, saved.HiddenTools ?? []);

        // 이 셸에서도 SQL 탭으로 돌아오면 배치 규칙대로 다시 보인다.
        ws.Activate(sql);
        Pump();
        Assert.Contains(Results, ws.VisibleToolIds);
        Assert.Empty(ws.ScopedHiddenToolIds);

        // 저장한 배치를 다시 열면 기본 자리에 보인다.
        using var again = new ShellHarness();
        var ws2 = again.Get<DockWorkspace>();
        ws2.Apply(saved);
        Pump();
        Assert.Contains(Results, ws2.VisibleToolIds);
        Assert.Contains(Messages, ws2.VisibleToolIds);

    });
}
