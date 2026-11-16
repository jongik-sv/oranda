using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dock.Model.Controls;
using Dock.Model.Core;
using Oranda.App.Shell.Docking;
using Oranda.App.Shell.Layout;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Shell;

namespace Oranda.App.Tests.Shell.Docking;

// DK2(2026-11-16 사용자 신고): 「도구 창을 추가할 때마다 화면 전체가 껌벅이고, 아래쪽 도구 창 핀을 빼면 아래에 붙는데 그때는 도구 창을 추가해도 안 된다」.
// 켜기·끄기는 언제나 그 창 하나만 넣고 뺀다(배치를 다시 짓지 않는다). 핀으로 모두 접힌 묶음이 비율 0 으로 저장되어 높이 0 으로 지어지지 않게 한다.
public class DockToolToggleTests
{
    /// <summary>
    /// 사용자 확인 앱의 배치(2026-11-16 사본, 창 위치는 뺐다): 아래 묶음이 비율 0(문서 칸 1)으로 저장되어 있고 메시지·DBMS 출력·컴파일 오류는 핀.
    /// </summary>
    private const string UserLayoutJson = """
        {
          "schemaVersion": 2,
          "root": {
            "kind": "Split", "orientation": "Horizontal",
            "children": [
              { "kind": "ToolGroup", "proportion": 0.22,
                "tools": ["oranda.panel.schemaBrowser", "oranda.panel.scripts", "oranda.panel.mybatisStatements"], "activeTool": "oranda.panel.schemaBrowser" },
              { "kind": "Split", "orientation": "Vertical", "proportion": 0.55,
                "children": [
                  { "kind": "Documents", "proportion": 1 },
                  { "kind": "ToolGroup", "proportion": 0,
                    "tools": ["oranda.panel.mybatisCombos", "oranda.panel.results", "oranda.panel.history", "oranda.panel.messages", "oranda.panel.output", "oranda.panel.compileErrors"],
                    "activeTool": "oranda.panel.history" }
                ] },
              { "kind": "ToolGroup", "proportion": 0.23, "tools": ["oranda.panel.binds"], "activeTool": "oranda.panel.binds" }
            ]
          },
          "hiddenTools": ["oranda.panel.sessions", "oranda.panel.plan", "oranda.panel.changeJournal", "oranda.panel.outline", "oranda.panel.templates", "oranda.panel.rowView", "oranda.panel.aiChat"],
          "pinnedTools": ["oranda.panel.messages", "oranda.panel.output", "oranda.panel.compileErrors"],
          "migrations": ["rightToolsOpen", "rightToolsFullHeight"]
        }
        """;

    private static readonly string[] RightTools = ["oranda.panel.binds", "oranda.panel.extensionTest"];

    private static readonly string[] LeftTools = ["oranda.panel.schemaBrowser", "oranda.panel.scripts"];

    private static void Pump()
    {
        for (var i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static ShellHarness UserLayoutShell()
    {
        var dir = Path.Combine(Path.GetTempPath(), "oranda-shell-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "layout.json"), UserLayoutJson);
        File.WriteAllText(Path.Combine(dir, "settings.json"), ShellHarness.AllFeaturesSettingsJson);
        return new ShellHarness(dir);
    }

    private static IRootDock Root(DockWorkspace ws) => (IRootDock)ws.Layout!;

    private static List<IDockable> Pinned(DockWorkspace ws) =>
        [.. new[] { Root(ws).LeftPinnedDockables, Root(ws).RightPinnedDockables, Root(ws).TopPinnedDockables, Root(ws).BottomPinnedDockables }.SelectMany(l => l ?? [])];

    /// <summary>메인 배치의 도킹(분할·묶음·문서 칸)마다 그것을 그리는 컨트롤.</summary>
    private static Dictionary<IDock, Control> DockViews(ShellHarness h) =>
        h.Window.GetVisualDescendants().OfType<Control>()
            .Where(c => c.DataContext is IDock && c.GetType().Name.EndsWith("DockControl", StringComparison.Ordinal))
            .GroupBy(c => (IDock)c.DataContext!)
            .ToDictionary(g => g.Key, g => g.First());

    private static List<IDockable> TreeOf(DockWorkspace ws)
    {
        var all = new List<IDockable>();
        void Walk(IDockable d)
        {
            all.Add(d);
            foreach (var c in (d as IDock)?.VisibleDockables ?? [])
            {
                Walk(c);
            }
        }

        Walk(ws.Layout!);
        return all;
    }

    /// <summary>창이 메인 배치 묶음의 활성 탭이고, 그 묶음 컨트롤이 화면에 보이는 크기다.</summary>
    private static void AssertShown(ShellHarness h, DockWorkspace ws, string id)
    {
        Assert.True(ws.IsToolVisible(id), id);
        var tool = ws.Factory.FindDockable(ws.Layout!, d => d.Id == id);
        Assert.NotNull(tool);
        var group = Assert.IsAssignableFrom<IToolDock>(tool!.Owner);
        Assert.Same(tool, group.ActiveDockable);
        Assert.True(DockViews(h).TryGetValue(group, out var view), id + ": 묶음 컨트롤이 없다");
        Assert.True(view.Bounds.Height > 40 && view.Bounds.Width > 40, $"{id}: 묶음이 {view.Bounds.Width}x{view.Bounds.Height} 로 보이지 않는다");
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("oranda.panel.sqlEditor", false)]
    [InlineData("oranda.panel.objectDetail", false)]
    [InlineData("oranda.panel.sqlEditor", true)]
    [InlineData("oranda.panel.objectDetail", true)]
    public Task Every_tool_window_turns_on_and_off_alone_in_the_user_layout(string? documentId, bool unpinBottom) => HeadlessHost.Run(() =>
    {
        using var h = UserLayoutShell();
        var ws = h.Get<DockWorkspace>();
        if (documentId is not null)
        {
            ws.Activate(ws.OpenDocument(documentId)!); // SQL 아닌 문서면 SQL 전용 창이 묶음째 잠시 숨는다(전에는 이때 켜기마다 다시 지었다)
        }

        Pump();
        if (unpinBottom)
        {
            foreach (var t in Pinned(ws))
            {
                ws.Factory.PinDockable(t); // 핀 빼기(고정) = 신고 2 의 상태
                Pump();
            }

            Assert.Empty(Pinned(ws));
            AssertShown(h, ws, ((IDock)ws.Factory.FindDockable(ws.Layout!, d => d.Id == "oranda.panel.messages")!.Owner!).ActiveDockable!.Id);
        }

        var layout = ws.Layout;
        var rebuilt = 0;
        ws.LayoutRebuilt += (_, _) => rebuilt++;
        var top = DockViews(h)[(IDock)Root(ws).VisibleDockables![0]]; // 루트 배치 컨트롤(DK2-04: 켜기·끄기 동안 다시 만들어지면 화면 전체가 깜박인다)
        var documents = (IDock)TreeOf(ws).First(d => d is IDocumentDock);
        var documentView = DockViews(h)[documents];

        foreach (var def in h.Get<IToolPanelRegistry>().Panels.Where(p => p.Kind == ToolPanelKind.Tool).ToList())
        {
            for (var round = 0; round < 2; round++)
            {
                var views = DockViews(h);
                var wasVisible = ws.IsToolVisible(def.Id);
                ws.ToggleTool(def.Id);
                Pump();
                if (wasVisible)
                {
                    Assert.False(ws.IsToolVisible(def.Id), def.Id);
                }
                else if (documentId is not null || !DockWorkspace.MyBatisToolIds.Contains(def.Id))
                {
                    AssertShown(h, ws, def.Id); // MyBatis 창은 문서가 없으면(시작 화면) 규칙상 늘 숨는다(빈 창이 새지 않게)
                }

                // 문서 칸·남아 있는 다른 도킹의 컨트롤은 같은 인스턴스(그 창이 든 묶음만 탭이 바뀐다)
                var now = DockViews(h);
                var tree = TreeOf(ws);
                foreach (var (dock, view) in views.Where(v => tree.Contains(v.Key)))
                {
                    Assert.True(now.TryGetValue(dock, out var after) && ReferenceEquals(after, view), $"{def.Id}: {dock.Id} 컨트롤을 다시 만들었다");
                }
            }
        }

        Assert.Equal(0, rebuilt);
        Assert.Same(layout, ws.Layout);
        Assert.Same(top, DockViews(h)[(IDock)Root(ws).VisibleDockables![0]]);
        Assert.Same(documentView, DockViews(h)[documents]);
    });

    [Fact]
    public Task A_group_saved_folded_comes_back_at_its_default_size() => HeadlessHost.Run(() =>
    {
        using var h = UserLayoutShell();
        var ws = h.Get<DockWorkspace>();
        ws.Activate(ws.OpenDocument("oranda.panel.sqlEditor")!);
        Pump();

        AssertShown(h, ws, "oranda.panel.history"); // 보기 메뉴에 체크인데 높이 0 이던 창
        Assert.True(ws.ToggleTool("oranda.panel.plan")); // 「켜도 안 열림」이던 창
        Pump();
        AssertShown(h, ws, "oranda.panel.plan");

        static DockNode BottomOf(LayoutDocument d) =>
            d.Root!.Children.Single(c => c.Kind == DockNodeKind.Split).Children.Single(c => c.Kind == DockNodeKind.ToolGroup);
        Assert.True(BottomOf(ws.Capture()).Proportion > 0.2);
    });

    [Fact]
    public Task Tools_pinned_in_a_saved_layout_stay_in_their_group_after_start() => HeadlessHost.Run(() =>
    {
        // 보기가 붙기 전에 핀을 다시 빼면, 보기가 붙을 때 Dock 이 핀 창 Owner 를 root 로 바꿨다: 체크가 꺼지고 다시 지으면 사라졌다.
        using var h = UserLayoutShell();
        var ws = h.Get<DockWorkspace>();
        Pump();
        var bottom = (IDock)ws.Factory.FindDockable(ws.Layout!, d => d.Id == "oranda.panel.results")!.Owner!;
        foreach (var id in PinnedIds)
        {
            Assert.Same(bottom, Pinned(ws).Single(d => d.Id == id).Owner);
            Assert.True(ws.IsToolVisible(id), id); // 보기 메뉴 체크
        }

        ws.Rebuild();
        Pump();
        Assert.Equal(PinnedIds.Order(), Pinned(ws).Select(d => d.Id).Order());
        Assert.Equal(PinnedIds.Order(), (ws.Capture().PinnedTools ?? []).Order());

        var messages = Pinned(ws).Single(d => d.Id == "oranda.panel.messages");
        ws.Factory.PinDockable(messages); // 핀 빼기 = 아래 묶음의 탭으로
        Pump();
        AssertShown(h, ws, "oranda.panel.messages");
    });

    private static readonly string[] PinnedIds = ["oranda.panel.messages", "oranda.panel.output", "oranda.panel.compileErrors"];

    [Fact]
    public Task Pinning_every_tab_of_a_group_does_not_save_it_folded() => HeadlessHost.Run(() =>
    {
        LayoutDocument saved;
        using (var first = new ShellHarness())
        {
            var ws0 = first.Get<DockWorkspace>();
            ws0.Activate(ws0.OpenDocument("oranda.panel.sqlEditor")!);
            Pump();
            var group = (IDock)ws0.Factory.FindDockable(ws0.Layout!, d => d.Id == "oranda.panel.results")!.Owner!;
            foreach (var t in group.VisibleDockables!.OfType<PanelTool>().ToList())
            {
                ws0.Factory.PinDockable(t);
                Pump();
            }

            saved = ws0.Capture();
        }

        var column = saved.Root!.Children.Single(c => c.Kind == DockNodeKind.Split);
        var bottom = column.Children.Single(c => c.Kind == DockNodeKind.ToolGroup);
        var documents = column.Children.Single(c => c.Kind == DockNodeKind.Documents);
        Assert.Equal(LayoutPlanner.BottomProportion, bottom.Proportion!.Value, 2); // 전에는 0(문서 칸 1)으로 저장되었다
        Assert.Equal(1 - LayoutPlanner.BottomProportion, documents.Proportion!.Value, 2);

        using var h = new ShellHarness(); // 다음 실행에서 핀을 빼면 보이는 크기로 나온다
        var ws = h.Get<DockWorkspace>();
        ws.Apply(saved);
        ws.Activate(ws.OpenDocument("oranda.panel.sqlEditor")!);
        Pump();
        var results = Pinned(ws).Single(d => d.Id == "oranda.panel.results");
        ws.Factory.PinDockable(results);
        Pump();
        AssertShown(h, ws, "oranda.panel.results");
    });

    [Fact]
    public Task Closing_the_only_tab_of_a_group_removes_just_that_group_and_turning_it_on_adds_one_back() => HeadlessHost.Run(() =>
    {
        using var h = UserLayoutShell();
        var ws = h.Get<DockWorkspace>();
        ws.Activate(ws.OpenDocument("oranda.panel.sqlEditor")!);
        Pump();
        var rebuilt = 0;
        ws.LayoutRebuilt += (_, _) => rebuilt++;
        var documents = ws.Documents[0].Owner!;
        var documentView = DockViews(h)[(IDock)documents];
        var left = (IDock)ws.Factory.FindDockable(ws.Layout!, d => d.Id == "oranda.panel.schemaBrowser")!.Owner!;
        var leftView = DockViews(h)[left];

        foreach (var id in RightTools.Where(ws.IsToolVisible))
        {
            Assert.True(ws.HideTool(id)); // 오른쪽 묶음을 비운다
            Pump();
        }

        Assert.DoesNotContain(TreeOf(ws), d => d is IToolDock { Alignment: Alignment.Right }); // 빈 묶음은 치운다(분할선도)
        AssertSplittersBetweenCells(ws);

        Assert.True(ws.ShowTool("oranda.panel.outline")); // 오른쪽에 묶음이 없다: 새 묶음 하나만 끼운다
        Pump();
        AssertShown(h, ws, "oranda.panel.outline");
        var placedRight = ws.Capture().Root!;
        Assert.Equal(DockLocation.Right, LayoutPlanner.LocationOf(placedRight, Groups(placedRight).Single(g => g.Tools.Contains("oranda.panel.outline"))));

        foreach (var id in LeftTools)
        {
            Assert.True(ws.HideTool(id)); // 왼쪽 묶음도(MyBatis 창은 SQL 문서에서 잠시 숨김이라 묶음은 접힌 채 남는다)
            Pump();
        }

        Assert.True(ws.ShowTool("oranda.panel.sessions")); // 왼쪽 묶음에 끼워 펼친다
        Pump();
        AssertShown(h, ws, "oranda.panel.sessions");

        Assert.Equal(0, rebuilt);
        Assert.Same(documentView, DockViews(h)[(IDock)documents]);
        Assert.Same(leftView, DockViews(h)[left]);
        var saved = ws.Capture();
        Assert.Contains("oranda.panel.binds", saved.HiddenTools);
        Assert.Contains("oranda.panel.mybatisStatements", LayoutPlanner.ToolIds(saved.Root)); // 잠시 숨긴 창은 닫힌 창이 아니다
    });

    /// <summary>분할선은 늘 두 칸 사이에만 있다(맨 앞·맨 뒤·연달아 남지 않는다).</summary>
    private static void AssertSplittersBetweenCells(DockWorkspace ws)
    {
        foreach (var split in TreeOf(ws).OfType<IProportionalDock>())
        {
            var kids = split.VisibleDockables!;
            for (var i = 0; i < kids.Count; i++)
            {
                if (kids[i] is IProportionalDockSplitter)
                {
                    Assert.True(i > 0 && i < kids.Count - 1 && kids[i - 1] is not IProportionalDockSplitter && kids[i + 1] is not IProportionalDockSplitter, split.Id);
                }
            }
        }
    }

    private static IEnumerable<DockNode> Groups(DockNode n) => n.Kind == DockNodeKind.ToolGroup ? [n] : n.Children.SelectMany(Groups);

    [Fact]
    public Task A_bottom_group_is_added_under_the_documents_without_touching_other_views() => HeadlessHost.Run(() =>
    {
        using var h = new ShellHarness();
        var ws = h.Get<DockWorkspace>();
        ws.Activate(ws.OpenDocument("oranda.panel.sqlEditor")!);
        Pump();
        foreach (var id in ws.VisibleToolIds.Where(id => LayoutPlanner.ToolIds(ws.Capture().Root).Contains(id)
                     && h.Get<IToolPanelRegistry>().Find(id)!.DefaultLocation == DockLocation.Bottom).ToList())
        {
            Assert.True(ws.HideTool(id));
            Pump();
        }

        // 기본 배치의 MyBatis 조합 검사(SQL 문서에서 잠시 숨김)까지 닫으면 아래 묶음 자체가 없어진다.
        Assert.True(ws.HideTool("oranda.panel.mybatisCombos"));
        Pump();
        Assert.DoesNotContain(TreeOf(ws), d => d is IToolDock { Alignment: Alignment.Bottom });
        var rebuilt = 0;
        ws.LayoutRebuilt += (_, _) => rebuilt++;
        var views = DockViews(h);

        Assert.True(ws.ToggleTool("oranda.panel.history"));
        Pump();

        AssertShown(h, ws, "oranda.panel.history");
        Assert.Equal(0, rebuilt);
        var now = DockViews(h);
        Assert.All(views, v => Assert.Same(v.Value, now[v.Key])); // 문서 칸은 세로 분할 안에 남아 있어 형제로만 끼운다
        var root = ws.Capture().Root!;
        Assert.Equal(DockLocation.Bottom, LayoutPlanner.LocationOf(root, Groups(root).Single(g => g.Tools.Contains("oranda.panel.history"))));
    });

    [Fact]
    public Task Turning_off_a_pinned_tool_takes_it_off_the_sidebar_only() => HeadlessHost.Run(() =>
    {
        using var h = UserLayoutShell();
        var ws = h.Get<DockWorkspace>();
        ws.Activate(ws.OpenDocument("oranda.panel.sqlEditor")!);
        Pump();
        var rebuilt = 0;
        ws.LayoutRebuilt += (_, _) => rebuilt++;
        Assert.Contains(Pinned(ws), d => d.Id == "oranda.panel.output");

        Assert.False(ws.ToggleTool("oranda.panel.output"));
        Pump();

        Assert.Equal(0, rebuilt);
        Assert.False(ws.IsToolVisible("oranda.panel.output"));
        Assert.DoesNotContain(Pinned(ws), d => d.Id == "oranda.panel.output");
        Assert.Contains(Pinned(ws), d => d.Id == "oranda.panel.messages"); // 다른 핀 창은 그대로
        var saved = ws.Capture();
        Assert.Contains("oranda.panel.output", saved.HiddenTools);
        Assert.Contains("oranda.panel.messages", saved.PinnedTools);
    });

    [Fact]
    public Task Turning_off_one_tool_of_a_fully_pinned_group_keeps_the_group_size() => HeadlessHost.Run(() =>
    {
        using var h = new ShellHarness();
        var ws = h.Get<DockWorkspace>();
        ws.Activate(ws.OpenDocument("oranda.panel.sqlEditor")!);
        Pump();
        var group = (IDock)ws.Factory.FindDockable(ws.Layout!, d => d.Id == "oranda.panel.results")!.Owner!;
        foreach (var t in group.VisibleDockables!.OfType<PanelTool>().ToList())
        {
            ws.Factory.PinDockable(t);
            Pump();
        }

        Assert.False(ws.ToggleTool("oranda.panel.output")); // 핀 창 하나 끄기: 묶음은 다른 핀 창 때문에 접힌 채 남는다
        Pump();

        var column = ws.Capture().Root!.Children.Single(c => c.Kind == DockNodeKind.Split);
        Assert.Equal(LayoutPlanner.BottomProportion, column.Children.Single(c => c.Kind == DockNodeKind.ToolGroup).Proportion!.Value, 2);

        ws.Factory.PinDockable(Pinned(ws).Single(d => d.Id == "oranda.panel.messages")); // 사이드바에서 핀 빼기: 묶음이 다시 보인다
        Pump();
        AssertShown(h, ws, "oranda.panel.messages");
        column = ws.Capture().Root!.Children.Single(c => c.Kind == DockNodeKind.Split);
        Assert.Equal(LayoutPlanner.BottomProportion, column.Children.Single(c => c.Kind == DockNodeKind.ToolGroup).Proportion!.Value, 2); // 끌 때의 접힘 기록(0)이 남아 저장되지 않는다
        ws.Factory.PinDockable(ws.Factory.FindDockable(ws.Layout!, d => d.Id == "oranda.panel.messages")!); // 다시 모두 핀
        Pump();

        Assert.True(ws.ToggleTool("oranda.panel.plan")); // 접힌 아래 묶음에 켜면 보이는 크기로 펼친다(핀 창은 사이드바에 그대로)
        Pump();
        AssertShown(h, ws, "oranda.panel.plan");
        Assert.Contains(Pinned(ws), d => d.Id == "oranda.panel.results");
    });

    [Fact]
    public Task Pinning_a_whole_group_in_a_three_cell_split_keeps_the_other_cells_in_proportion() => HeadlessHost.Run(() =>
    {
        using var h = new ShellHarness();
        var ws = h.Get<DockWorkspace>();
        Assert.True(ws.ShowTool("oranda.panel.binds")); // 바깥 가로 분할 = [왼쪽, 문서 열, 오른쪽]
        Pump();
        static List<double> Outer(LayoutDocument d) => [.. d.Root!.Children.Select(c => c.Proportion ?? double.NaN)];
        var before = Outer(ws.Capture());
        Assert.Equal(3, before.Count);
        var left = (IDock)ws.Factory.FindDockable(ws.Layout!, d => d.Id == "oranda.panel.schemaBrowser")!.Owner!;
        foreach (var t in left.VisibleDockables!.OfType<PanelTool>().ToList())
        {
            ws.Factory.PinDockable(t);
            Pump();
        }

        var after = Outer(ws.Capture()); // 전에는 접힌 몫을 문서 열에서만 빼 저장할 때마다 오른쪽이 넓어졌다
        for (var i = 0; i < before.Count; i++)
        {
            Assert.Equal(before[i], after[i], 2);
        }
    });

    [Fact]
    public Task A_tool_closed_with_its_tab_button_comes_back_into_the_group_left_behind() => HeadlessHost.Run(() =>
    {
        using var h = new ShellHarness();
        var ws = h.Get<DockWorkspace>();
        Assert.True(ws.ShowTool("oranda.panel.binds"));
        Pump();
        for (var round = 0; round < 2; round++)
        {
            ws.Factory.CloseDockable(ws.Factory.FindDockable(ws.Layout!, d => d.Id == "oranda.panel.binds")!); // 오른쪽 묶음의 하나뿐인 탭을 × 로(Dock 은 빈 묶음을 남긴다)
            Pump();
            Assert.False(ws.IsToolVisible("oranda.panel.binds"));
            Assert.True(ws.ToggleTool("oranda.panel.binds"));
            Pump();
            AssertShown(h, ws, "oranda.panel.binds");
            Assert.Single(TreeOf(ws), d => d is IToolDock { Alignment: Alignment.Right }); // 묶음이 쌓이지 않는다
        }

        AssertSplittersBetweenCells(ws);
    });

    /// <summary>문서 칸만 있거나 [왼쪽, 문서] 처럼 문서 칸이 세로 분할 안이 아닌 배치(아래 창을 모두 닫고 다시 시작한 모양).</summary>
    public static TheoryData<string> FramelessLayouts => new()
    {
        """{ "schemaVersion": 2, "root": { "kind": "Documents" }, "migrations": ["rightToolsOpen", "rightToolsFullHeight"] }""",
        """{ "schemaVersion": 2, "root": { "kind": "Split", "orientation": "Horizontal", "children": [ { "kind": "ToolGroup", "proportion": 0.22, "tools": ["oranda.panel.schemaBrowser"], "activeTool": "oranda.panel.schemaBrowser" }, { "kind": "Documents", "proportion": 0.78 } ] }, "migrations": ["rightToolsOpen", "rightToolsFullHeight"] }""",
    };

    [Theory]
    [MemberData(nameof(FramelessLayouts))]
    public Task Tool_windows_added_after_a_restart_do_not_recreate_the_documents_view(string json) => HeadlessHost.Run(() =>
    {
        var dir = Path.Combine(Path.GetTempPath(), "oranda-shell-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "layout.json"), json);
        File.WriteAllText(Path.Combine(dir, "settings.json"), ShellHarness.AllFeaturesSettingsJson);
        using var h = new ShellHarness(dir);
        var ws = h.Get<DockWorkspace>();
        ws.Activate(ws.OpenDocument("oranda.panel.sqlEditor")!);
        Pump();
        foreach (var id in ws.VisibleToolIds.ToList())
        {
            ws.HideTool(id); // 배치에 새로 생긴 창(새 등록 창 기본 위치)도 닫아 문서 칸만 남긴다
            Pump();
        }

        var rebuilt = 0;
        ws.LayoutRebuilt += (_, _) => rebuilt++;
        var documents = (IDock)TreeOf(ws).First(d => d is IDocumentDock);
        var documentView = DockViews(h)[documents];
        foreach (var id in AroundTools)
        {
            Assert.True(ws.ToggleTool(id));
            Pump();
            AssertShown(h, ws, id);
        }

        Assert.Equal(0, rebuilt);
        Assert.Same(documentView, DockViews(h)[documents]);
        var root = ws.Capture().Root!;
        Assert.Equal(DockLocation.Bottom, LayoutPlanner.LocationOf(root, Groups(root).Single(g => g.Tools.Contains("oranda.panel.results"))));
        Assert.Equal(DockLocation.Right, LayoutPlanner.LocationOf(root, Groups(root).Single(g => g.Tools.Contains("oranda.panel.binds"))));
        Assert.Equal(DockLocation.Left, LayoutPlanner.LocationOf(root, Groups(root).Single(g => g.Tools.Contains("oranda.panel.scripts"))));
        AssertSplittersBetweenCells(ws);
    });

    private static readonly string[] AroundTools = ["oranda.panel.results", "oranda.panel.binds", "oranda.panel.scripts"];

    [Fact]
    public Task Turning_on_a_floating_tool_from_the_view_menu_moves_it_back_into_the_main_window() => HeadlessHost.Run(() =>
    {
        using var h = new ShellHarness();
        var ws = h.Get<DockWorkspace>();
        ws.ShowTool("oranda.panel.messages");
        ws.ShowTool("oranda.panel.results");
        ws.ShowTool("oranda.panel.history");
        Pump();
        ws.Factory.FloatDockable((PanelTool)ws.Factory.FindDockable(ws.Layout!, d => d.Id == "oranda.panel.history")!);
        Pump();
        Assert.Single(Root(ws).Windows!);
        Assert.False(ws.IsToolVisible("oranda.panel.history"));
        var rebuilt = 0;
        ws.LayoutRebuilt += (_, _) => rebuilt++;

        Assert.True(ws.ToggleTool("oranda.panel.history"));
        Pump();

        Assert.Equal(0, rebuilt);
        AssertShown(h, ws, "oranda.panel.history");
        Assert.Empty(Root(ws).Windows ?? []); // 비게 된 뜬 창은 닫는다
    });
}
