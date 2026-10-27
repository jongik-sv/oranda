using Oranda.App.Shell;
using Oranda.App.Shell.Layout;
using Oranda.Core.Shell;

namespace Oranda.App.Tests.Shell;

// TSK-03-01: 도킹 배치 계획(기본 배치, 복원할 때 맞추기, 보이기·숨기기)과 layout.json 형식.
public class LayoutPlannerTests
{
    private static ToolPanelDefinition Tool(string name, DockLocation at, int order = 0) => new()
    {
        Id = "oranda.panel." + name,
        Title = "Panel." + name,
        Kind = ToolPanelKind.Tool,
        DefaultLocation = at,
        ViewModelType = typeof(object),
        Order = order,
    };

    private static ToolPanelDefinition Doc(string name) => new()
    {
        Id = "oranda.panel." + name,
        Title = "Panel." + name,
        Kind = ToolPanelKind.Document,
        DefaultLocation = DockLocation.Document,
        ViewModelType = typeof(object),
        AllowMultiple = true,
    };

    public static readonly ToolPanelDefinition[] Standard =
    [
        Tool("schemaBrowser", DockLocation.Left, 10),
        Tool("scripts", DockLocation.Left, 20),
        Tool("output", DockLocation.Bottom, 10),
        Tool("messages", DockLocation.Bottom, 20),
        Tool("outline", DockLocation.Right, 10),
        Tool("binds", DockLocation.Right, 20),
        Tool("templates", DockLocation.Right, 20),
        Doc("sqlEditor"),
    ];

    /// <summary>실제 등록과 같은 도구 창 구성(TSK-09-05 기본 배치 시험용, 순서 = 각 [OrandaToolPanel] 의 Order).</summary>
    public static readonly ToolPanelDefinition[] Real =
    [
        Tool("schemaBrowser", DockLocation.Left, 10),
        Tool("scripts", DockLocation.Left, 20),
        Tool("mybatisStatements", DockLocation.Left, 30),
        Tool("results", DockLocation.Bottom, 5),
        Tool("mybatisCombos", DockLocation.Bottom, 7),
        Tool("messages", DockLocation.Bottom, 8),
        Tool("output", DockLocation.Bottom, 10),
        Tool("plan", DockLocation.Bottom, 30),
        Tool("history", DockLocation.Bottom, 40),
        Tool("changeJournal", DockLocation.Bottom, 45),
        Tool("compileErrors", DockLocation.Bottom, 50),
        Tool("outline", DockLocation.Right, 10),
        Tool("binds", DockLocation.Right, 20),
        Tool("templates", DockLocation.Right, 20),
        Tool("rowView", DockLocation.Right, 30),
        Doc("sqlEditor"),
    ];

    private static string Id(string name) => "oranda.panel." + name;

    [Fact]
    public void Default_layout_follows_ui_shell_2_1()
    {
        var layout = LayoutPlanner.CreateDefault(Standard);

        // 가로: [왼쪽 묶음(창 전체 높이), 세로: [문서, 아래 묶음]]
        var root = layout.Root!;
        Assert.Equal(DockNodeKind.Split, root.Kind);
        Assert.Equal(DockOrientation.Horizontal, root.Orientation);
        var left = root.Children[0];
        var column = root.Children[1];
        Assert.Equal([Id("schemaBrowser"), Id("scripts")], left.Tools);
        Assert.Equal(DockOrientation.Vertical, column.Orientation);
        Assert.Equal(1, CountDocuments(column.Children[0])); // 문서 영역은 오른쪽 열과 가로로 나란히 놓인다.
        Assert.Equal([Id("output"), Id("messages")], column.Children[1].Tools);

        // 오른쪽 도구 창은 바인드 변수·템플릿까지 모두 기본 닫힘(D-019, TSK-13-16 되돌림): 트리에 없고 hiddenTools 에 있다.
        // 이주 표지는 남아 옛 이주가 다시 돌지 않는다. 문서 종류는 트리에 넣지 않는다.
        Assert.Equal([Id("outline"), Id("binds"), Id("templates")], layout.HiddenTools);
        Assert.Equal([Id("schemaBrowser"), Id("scripts"), Id("output"), Id("messages")], LayoutPlanner.ToolIds(root));
        Assert.Contains(LayoutPlanner.RightToolsOpenMigration, layout.Migrations!);
        Assert.DoesNotContain(Id("sqlEditor"), LayoutPlanner.ToolIds(root));
        Assert.Equal(1, CountDocuments(root));
    }

    [Fact]
    public void Default_layout_without_side_tools_is_just_the_documents_area()
    {
        var layout = LayoutPlanner.CreateDefault([Doc("sqlEditor")]);

        Assert.Equal(DockNodeKind.Documents, layout.Root!.Kind);
        Assert.Empty(layout.HiddenTools);
    }

    // TSK-09-05(D-019): 기본 배치 덜어내기 — 아래 3개(+ MyBatis 문서 범위 창), 오른쪽 0개, 나머지는 필요할 때 기본 위치에.
    [Fact]
    public void Default_layout_opens_only_results_messages_and_output_below_and_nothing_on_the_right()
    {
        var layout = LayoutPlanner.CreateDefault(Real);
        var root = layout.Root!;

        var bottom = FindGroup(root, Id("results"));
        Assert.Equal([Id("results"), Id("mybatisCombos"), Id("messages"), Id("output")], bottom.Tools); // 조합 검사는 MyBatis 문서에서만 보인다
        Assert.Equal(Id("results"), bottom.ActiveTool);
        Assert.Equal(DockLocation.Bottom, LayoutPlanner.LocationOf(root, bottom));
        Assert.DoesNotContain(FindGroupsOf(root), g => LayoutPlanner.LocationOf(root, g) == DockLocation.Right);
        Assert.Equal(
            [Id("plan"), Id("history"), Id("changeJournal"), Id("compileErrors"), Id("outline"), Id("binds"), Id("templates"), Id("rowView")],
            layout.HiddenTools);
        Assert.Contains(LayoutPlanner.RightToolsOpenMigration, layout.Migrations!);

        // SQL 탭과 함께 보이는 창(MyBatis 범위 창 2개 제외): 조사 11개 → 왼쪽 2 + 아래 3 = 5.
        var onSqlTab = LayoutPlanner.ToolIds(root).Where(id => id is not ("oranda.panel.mybatisStatements" or "oranda.panel.mybatisCombos")).ToList();
        Assert.Equal([Id("schemaBrowser"), Id("scripts"), Id("results"), Id("messages"), Id("output")], onSqlTab);
        Assert.Equal(["oranda.panel.results", "oranda.panel.messages", "oranda.panel.output"], LayoutPlanner.DefaultBottomTools);
    }

    [Fact]
    public void Default_layout_without_the_three_bottom_tools_has_no_bottom_group()
    {
        // 경계: 아래 기본 3개가 하나도 등록되지 않으면(기능 꺼짐 등) 아래 묶음이 없고 나머지 아래 창은 닫혀 있다.
        var panels = new[] { Tool("schemaBrowser", DockLocation.Left, 10), Tool("plan", DockLocation.Bottom, 30), Tool("binds", DockLocation.Right, 20), Doc("sqlEditor") };

        var layout = LayoutPlanner.CreateDefault(panels);

        Assert.Equal([Id("schemaBrowser")], LayoutPlanner.ToolIds(layout.Root));
        Assert.Equal([Id("plan"), Id("binds")], layout.HiddenTools);
        Assert.Equal(1, CountDocuments(layout.Root!));
    }

    [Theory]
    [InlineData("plan", DockLocation.Bottom)]
    [InlineData("compileErrors", DockLocation.Bottom)]
    [InlineData("history", DockLocation.Bottom)]
    [InlineData("binds", DockLocation.Right)]
    [InlineData("templates", DockLocation.Right)]
    public void A_closed_default_tool_opens_at_its_default_location(string name, DockLocation expected)
    {
        var layout = LayoutPlanner.Reconcile(LayoutPlanner.CreateDefault(Real), Real);

        var shown = LayoutPlanner.ShowTool(layout, Real.First(p => p.Id == Id(name)));

        var group = FindGroup(shown.Root!, Id(name));
        Assert.Equal(Id(name), group.ActiveTool);
        Assert.Equal(expected, LayoutPlanner.LocationOf(shown.Root!, group));
        Assert.DoesNotContain(Id(name), shown.HiddenTools);
        Assert.Equal(1, CountDocuments(shown.Root!));
        if (expected == DockLocation.Bottom)
        {
            Assert.Contains(Id("results"), group.Tools); // 새 묶음이 아니라 아래 묶음의 탭으로 붙는다
        }

        // 되돌림: 다시 닫으면 기본 배치와 같은 모양이다.
        var closed = LayoutPlanner.HideTool(shown, Id(name));
        Assert.Equal(LayoutPlanner.ToolIds(layout.Root), LayoutPlanner.ToolIds(closed.Root));
        Assert.Contains(Id(name), closed.HiddenTools);
    }

    [Fact]
    public void A_saved_layout_with_the_marker_is_read_as_before()
    {
        // TSK-13-16 시절 기본 배치(아래 7개 + 오른쪽 바인드·템플릿, 표지 있음)를 저장한 사용자: 그대로 읽힌다(D-019 저장 배치 유지).
        var saved = OldDefault(Real) with { Migrations = [LayoutPlanner.RightToolsOpenMigration] };

        var r = LayoutPlanner.Reconcile(saved, Real);

        Assert.True(LayoutPlanner.SameShape(saved.Root, r.Root));
        Assert.Equal(saved.HiddenTools, r.HiddenTools);
        Assert.Contains(Id("plan"), LayoutPlanner.ToolIds(r.Root));
        Assert.Contains(Id("binds"), LayoutPlanner.ToolIds(r.Root));
        Assert.Equal([LayoutPlanner.RightToolsOpenMigration, LayoutPlanner.RightToolsFullHeightMigration], r.Migrations);
    }

    [Fact]
    public void A_saved_layout_without_the_marker_still_gets_the_old_one_time_migration()
    {
        // 표지 없는 옛 배치에서 바인드·템플릿을 닫아 둔 사용자: 예전처럼 한 번 열리고 표지가 붙는다(새 이주 표지를 만들지 않는다).
        var saved = LayoutPlanner.HideTool(LayoutPlanner.HideTool(OldDefault(Real), Id("binds")), Id("templates")) with { Migrations = null };

        var r = LayoutPlanner.Reconcile(saved, Real);

        Assert.Contains(Id("binds"), LayoutPlanner.ToolIds(r.Root));
        Assert.Contains(Id("templates"), LayoutPlanner.ToolIds(r.Root));
        Assert.Contains(Id("plan"), LayoutPlanner.ToolIds(r.Root)); // 아래 7개는 그대로
        Assert.Equal([LayoutPlanner.RightToolsOpenMigration, LayoutPlanner.RightToolsFullHeightMigration], r.Migrations);

        var again = LayoutPlanner.Reconcile(r, Real);
        Assert.True(LayoutPlanner.SameShape(r.Root, again.Root));
    }

    [Fact]
    public void Default_layout_reset_after_a_customized_layout_drops_the_extra_tools()
    {
        // 레이아웃 초기화 = CreateDefault: 사용자가 열어 둔 계획·바인드 창은 닫히고 아래 3개로 돌아간다.
        var customized = LayoutPlanner.ShowTool(LayoutPlanner.ShowTool(LayoutPlanner.CreateDefault(Real), Real.First(p => p.Id == Id("plan"))), Real.First(p => p.Id == Id("binds")));

        var reset = LayoutPlanner.Reconcile(LayoutPlanner.CreateDefault(Real), Real);

        Assert.Contains(Id("binds"), LayoutPlanner.ToolIds(customized.Root));
        Assert.DoesNotContain(Id("binds"), LayoutPlanner.ToolIds(reset.Root));
        Assert.DoesNotContain(Id("plan"), LayoutPlanner.ToolIds(reset.Root));
        Assert.True(LayoutPlanner.SameShape(LayoutPlanner.CreateDefault(Real).Root, reset.Root));
    }

    /// <summary>TSK-13-16 의 기본 배치 모양: 왼쪽 전부, 아래 전부, 오른쪽 바인드·템플릿 한 열.</summary>
    private static LayoutDocument OldDefault(ToolPanelDefinition[] panels)
    {
        var layout = LayoutPlanner.CreateDefault(panels);
        foreach (var name in new[] { "plan", "history", "changeJournal", "compileErrors", "binds", "templates" })
        {
            layout = LayoutPlanner.ShowTool(layout, panels.First(p => p.Id == Id(name)));
        }

        return layout with { Root = LayoutPlanner.Rebalance(layout.Root!) };
    }

    private static List<DockNode> FindGroupsOf(DockNode n) =>
        [.. (n.Kind == DockNodeKind.ToolGroup ? [n] : Array.Empty<DockNode>()), .. (n.Children ?? []).SelectMany(FindGroupsOf)];

    [Fact]
    public void Reconcile_of_a_default_layout_is_a_no_op()
    {
        var layout = LayoutPlanner.CreateDefault(Standard);

        var again = LayoutPlanner.Reconcile(layout, Standard);

        Assert.True(LayoutPlanner.SameShape(layout.Root, again.Root));
        Assert.Equal(layout.HiddenTools, again.HiddenTools);
    }

    [Fact]
    public void Reconcile_drops_unregistered_tools_and_collapses_empty_groups()
    {
        var layout = LayoutPlanner.CreateDefault(Standard);
        var remaining = Standard.Where(p => p.Id is not ("oranda.panel.scripts" or "oranda.panel.output" or "oranda.panel.messages")).ToArray();

        var r = LayoutPlanner.Reconcile(layout, remaining);

        var ids = LayoutPlanner.ToolIds(r.Root);
        Assert.Equal([Id("schemaBrowser")], ids);
        // 아래 묶음이 사라지면 문서 쪽 세로 분할도 없어진다: 가로 [왼쪽, 문서].
        Assert.Equal(DockOrientation.Horizontal, r.Root!.Orientation);
        Assert.Equal(DockNodeKind.Documents, r.Root.Children[1].Kind);
        Assert.Equal(1, CountDocuments(r.Root));
    }

    [Fact]
    public void Reconcile_adds_newly_registered_tool_at_its_default_location()
    {
        var saved = LayoutPlanner.CreateDefault(Standard);
        var withNew = Standard.Append(Tool("history", DockLocation.Bottom, 30)).Append(Tool("rowView", DockLocation.Left, 5)).ToArray();

        var r = LayoutPlanner.Reconcile(saved, withNew);

        var bottom = FindGroup(r.Root!, Id("output"));
        Assert.Equal([Id("output"), Id("messages"), Id("history")], bottom.Tools);
        var left = FindGroup(r.Root!, Id("schemaBrowser"));
        Assert.Contains(Id("rowView"), left.Tools);
    }

    [Fact]
    public void Reconcile_puts_a_newly_registered_tool_at_its_order_within_the_group()
    {
        var saved = LayoutPlanner.CreateDefault(Standard);
        var withNew = Standard.Append(Tool("results", DockLocation.Bottom, 5)).Append(Tool("plan", DockLocation.Bottom, 15)).ToArray();

        var r = LayoutPlanner.Reconcile(saved, withNew);

        var bottom = FindGroup(r.Root!, Id("output"));
        Assert.Equal([Id("results"), Id("output"), Id("plan"), Id("messages")], bottom.Tools);
        Assert.Equal(saved.Root!.Children[1].Children[1].ActiveTool, bottom.ActiveTool);
    }

    [Fact]
    public void Reconcile_creates_a_group_for_a_new_location_with_no_group()
    {
        var saved = LayoutPlanner.CreateDefault([Doc("sqlEditor")]);
        var tools = new[] { Doc("sqlEditor"), Tool("output", DockLocation.Bottom), Tool("outline", DockLocation.Right) };

        var r = LayoutPlanner.Reconcile(saved, tools);

        var ids = LayoutPlanner.ToolIds(r.Root);
        Assert.Contains(Id("output"), ids);
        Assert.Contains(Id("outline"), ids);
        Assert.Equal(DockLocation.Bottom, LayoutPlanner.LocationOf(r.Root!, FindGroup(r.Root!, Id("output"))));
        Assert.Equal(DockLocation.Right, LayoutPlanner.LocationOf(r.Root!, FindGroup(r.Root!, Id("outline"))));
        Assert.Equal(1, CountDocuments(r.Root!));
    }

    [Fact]
    public void Reconcile_keeps_user_hidden_tools_hidden()
    {
        var saved = LayoutPlanner.CreateDefault(Standard);
        saved = LayoutPlanner.HideTool(saved, Id("scripts"));

        var r = LayoutPlanner.Reconcile(saved, Standard);

        Assert.DoesNotContain(Id("scripts"), LayoutPlanner.ToolIds(r.Root));
        Assert.Contains(Id("scripts"), r.HiddenTools);
    }

    [Fact]
    public void Reconcile_opens_binds_and_templates_once_for_a_layout_without_the_marker()
    {
        // 표지 없는 옛 배치(TSK-13-16 전)는 지금도 한 번 연다: 저장된 배치를 읽는 규칙은 D-019 에서 바꾸지 않는다.
        var old = LayoutPlanner.CreateDefault(Standard) with { Migrations = null };

        var migrated = LayoutPlanner.Reconcile(old, Standard);

        Assert.Contains(Id("binds"), LayoutPlanner.ToolIds(migrated.Root));
        Assert.Contains(Id("templates"), LayoutPlanner.ToolIds(migrated.Root));
        Assert.Equal([Id("outline")], migrated.HiddenTools);
        Assert.Contains(LayoutPlanner.RightToolsOpenMigration, migrated.Migrations!);
        Assert.Equal(1, CountDocuments(migrated.Root!));
    }

    [Fact]
    public void Reconcile_keeps_binds_closed_after_the_migration_marker_is_set()
    {
        var old = LayoutPlanner.CreateDefault(Standard) with { Migrations = null };
        var migrated = LayoutPlanner.Reconcile(old, Standard);
        var closed = LayoutPlanner.HideTool(migrated, Id("binds"));

        var again = LayoutPlanner.Reconcile(closed, Standard);

        Assert.DoesNotContain(Id("binds"), LayoutPlanner.ToolIds(again.Root));
        Assert.Contains(Id("binds"), again.HiddenTools);
    }

    [Fact]
    public void Reset_to_default_and_broken_layout_paths_leave_the_marker()
    {
        Assert.Contains(LayoutPlanner.RightToolsOpenMigration, LayoutPlanner.CreateDefault(Standard).Migrations!);

        var broken = new LayoutDocument { Root = new DockNode { Kind = DockNodeKind.ToolGroup, Tools = [Id("output")] } };
        var fixedUp = LayoutPlanner.Reconcile(broken, Standard);
        Assert.Contains(LayoutPlanner.RightToolsOpenMigration, fixedUp.Migrations!);
        Assert.DoesNotContain(Id("binds"), LayoutPlanner.ToolIds(fixedUp.Root)); // 깨진 배치는 새 기본 배치(바인드 닫힘)로
        Assert.Contains(Id("binds"), fixedUp.HiddenTools);

        var noRoot = LayoutPlanner.Reconcile(new LayoutDocument(), Standard);
        Assert.Contains(LayoutPlanner.RightToolsOpenMigration, noRoot.Migrations!);
    }

    [Fact]
    public void Reconcile_forgets_hidden_ids_that_are_no_longer_registered()
    {
        var saved = LayoutPlanner.CreateDefault(Standard) with { HiddenTools = [Id("outline"), "oranda.panel.removedPlugin"] };

        var r = LayoutPlanner.Reconcile(saved, Standard);

        Assert.DoesNotContain("oranda.panel.removedPlugin", r.HiddenTools);
    }

    [Fact]
    public void Reconcile_resets_a_layout_without_exactly_one_documents_area()
    {
        var broken = new LayoutDocument { Root = new DockNode { Kind = DockNodeKind.ToolGroup, Tools = [Id("output")] } };
        var twice = new LayoutDocument
        {
            Root = new DockNode
            {
                Kind = DockNodeKind.Split,
                Children = [new DockNode { Kind = DockNodeKind.Documents }, new DockNode { Kind = DockNodeKind.Documents }],
            },
        };

        foreach (var layout in new[] { broken, twice })
        {
            var r = LayoutPlanner.Reconcile(layout, Standard);
            Assert.Equal(1, CountDocuments(r.Root!));
            Assert.Contains(Id("schemaBrowser"), LayoutPlanner.ToolIds(r.Root));
        }
    }

    [Fact]
    public void Reconcile_of_empty_layout_gives_default_and_keeps_window()
    {
        var window = new WindowPlacement { X = 10, Y = 20, Width = 1000, Height = 700, State = WindowStateKind.Maximized };

        var r = LayoutPlanner.Reconcile(new LayoutDocument { Window = window }, Standard);

        Assert.Equal(window, r.Window);
        Assert.NotNull(r.Root);
    }

    [Fact]
    public void Reconcile_dedups_a_tool_listed_twice()
    {
        var layout = LayoutPlanner.CreateDefault(Standard);
        var left = FindGroup(layout.Root!, Id("schemaBrowser"));
        var dup = layout with
        {
            Root = layout.Root! with
            {
                Children = [left with { Tools = [Id("schemaBrowser"), Id("schemaBrowser"), Id("scripts")] }, layout.Root.Children[1]],
            },
        };

        var r = LayoutPlanner.Reconcile(dup, Standard);

        Assert.Equal(1, LayoutPlanner.ToolIds(r.Root).Count(i => i == Id("schemaBrowser")));
    }

    [Fact]
    public void ShowTool_activates_a_visible_tool_and_adds_a_hidden_one_at_its_location()
    {
        var layout = LayoutPlanner.CreateDefault(Standard);

        var shown = LayoutPlanner.ShowTool(layout, Standard.First(p => p.Id == Id("scripts")));
        Assert.Equal(Id("scripts"), FindGroup(shown.Root!, Id("scripts")).ActiveTool);

        var outline = LayoutPlanner.ShowTool(layout, Standard.First(p => p.Id == Id("outline")));
        Assert.Contains(Id("outline"), LayoutPlanner.ToolIds(outline.Root));
        Assert.DoesNotContain(Id("outline"), outline.HiddenTools);
        Assert.Equal(DockLocation.Right, LayoutPlanner.LocationOf(outline.Root!, FindGroup(outline.Root!, Id("outline"))));
        Assert.Equal(1, CountDocuments(outline.Root!));
    }

    [Fact]
    public void HideTool_moves_it_to_hidden_and_removes_empty_groups()
    {
        var layout = LayoutPlanner.CreateDefault(Standard);

        var one = LayoutPlanner.HideTool(layout, Id("scripts"));
        var both = LayoutPlanner.HideTool(one, Id("schemaBrowser"));

        Assert.Equal([Id("schemaBrowser")], LayoutPlanner.ToolIds(one.Root).Where(i => i.Contains("schema", StringComparison.Ordinal)));
        Assert.Contains(Id("scripts"), one.HiddenTools);
        Assert.DoesNotContain(Id("schemaBrowser"), LayoutPlanner.ToolIds(both.Root));
        Assert.Equal(1, CountDocuments(both.Root!));
    }

    [Fact]
    public void Layout_file_round_trips_through_json_and_is_not_dock_library_shaped()
    {
        var layout = LayoutPlanner.CreateDefault(Standard) with
        {
            Window = new WindowPlacement { X = 100, Y = 50, Width = 1280, Height = 800, State = WindowStateKind.Maximized, Screen = "DISPLAY1" },
        };

        var json = System.Text.Json.JsonSerializer.Serialize(layout, ShellJsonContext.Default.LayoutDocument);
        var back = System.Text.Json.JsonSerializer.Deserialize(json, ShellJsonContext.Default.LayoutDocument)!;

        Assert.Contains("\"schemaVersion\": 2", json);
        Assert.Contains("\"kind\": \"ToolGroup\"", json);
        Assert.DoesNotContain("Dock.", json);
        Assert.Equal(layout.Window, back.Window);
        Assert.True(LayoutPlanner.SameShape(layout.Root, back.Root));
        Assert.Equal(layout.HiddenTools, back.HiddenTools);
    }

    // layout.json 1 → 2: 예전 기본 모양(왼쪽 묶음이 아래 묶음 위에서 끝남)을 왼쪽 전체 높이 모양으로 옮긴다.
    private const string OldDefaultRoot = """
        {
          "kind": "Split", "orientation": "Vertical", "tools": [],
          "children": [
            { "kind": "Split", "orientation": "Horizontal", "proportion": 0.7, "tools": [],
              "children": [
                { "kind": "ToolGroup", "proportion": 0.25, "tools": ["oranda.panel.schemaBrowser", "oranda.panel.scripts"], "activeTool": "oranda.panel.scripts" },
                { "kind": "Documents", "proportion": 0.75, "tools": [] }
              ] },
            { "kind": "ToolGroup", "proportion": 0.3, "tools": ["oranda.panel.output", "oranda.panel.messages"] }
          ]
        }
        """;

    private static LayoutDocument Migrate(string root)
    {
        var doc = System.Text.Json.Nodes.JsonNode.Parse("{\"schemaVersion\": 1, \"hiddenTools\": [\"oranda.panel.outline\"], \"root\": " + root + "}")!;
        var migrated = new LayoutLeftFullHeightMigration().Apply(doc);
        return System.Text.Json.JsonSerializer.Deserialize(migrated, ShellJsonContext.Default.LayoutDocument)!;
    }

    [Fact]
    public void Migration_moves_old_default_layout_to_full_height_left_column()
    {
        var r = Migrate(OldDefaultRoot);

        var root = r.Root!;
        Assert.Equal(DockOrientation.Horizontal, root.Orientation);
        var left = root.Children[0];
        Assert.Equal([Id("schemaBrowser"), Id("scripts")], left.Tools);
        Assert.Equal(Id("scripts"), left.ActiveTool);
        Assert.Equal(0.25, left.Proportion);
        var column = root.Children[1];
        Assert.Equal(DockOrientation.Vertical, column.Orientation);
        Assert.Equal(0.75, column.Proportion);
        Assert.Equal(DockNodeKind.Documents, column.Children[0].Kind);
        Assert.Equal(0.7, column.Children[0].Proportion!.Value, 6);
        Assert.Equal([Id("output"), Id("messages")], column.Children[1].Tools);
        Assert.Equal(0.3, column.Children[1].Proportion);
        Assert.Equal([Id("outline")], r.HiddenTools);
        Assert.Equal(DockLocation.Left, LayoutPlanner.LocationOf(root, left));
        Assert.Equal(DockLocation.Bottom, LayoutPlanner.LocationOf(root, column.Children[1]));
    }

    [Fact]
    public void Migration_keeps_a_right_group_beside_the_documents()
    {
        var old = OldDefaultRoot.Replace(
            """{ "kind": "Documents", "proportion": 0.75, "tools": [] }""",
            """{ "kind": "Documents", "proportion": 0.55, "tools": [] }, { "kind": "ToolGroup", "proportion": 0.2, "tools": ["oranda.panel.outline"] }""",
            StringComparison.Ordinal);

        var root = Migrate(old).Root!;

        var top = root.Children[1].Children[0];
        Assert.Equal(DockOrientation.Horizontal, top.Orientation);
        Assert.Equal(DockNodeKind.Documents, top.Children[0].Kind);
        Assert.Equal(0.55 / 0.75, top.Children[0].Proportion!.Value, 3);
        Assert.Equal(0.2 / 0.75, top.Children[1].Proportion!.Value, 3);
        Assert.Equal(DockLocation.Right, LayoutPlanner.LocationOf(root, top.Children[1]));
        Assert.Equal(1, CountDocuments(root));
    }

    [Fact]
    public void Migration_leaves_other_shapes_alone()
    {
        const string custom = """
            { "kind": "Split", "orientation": "Horizontal", "tools": [],
              "children": [
                { "kind": "Documents", "proportion": 0.7, "tools": [] },
                { "kind": "ToolGroup", "proportion": 0.3, "tools": ["oranda.panel.schemaBrowser"] }
              ] }
            """;

        var root = Migrate(custom).Root!;

        Assert.Equal(DockOrientation.Horizontal, root.Orientation);
        Assert.Equal(DockNodeKind.Documents, root.Children[0].Kind);
        Assert.Equal([Id("schemaBrowser")], root.Children[1].Tools);
    }

    // D-048: 오른쪽 도구 창은 [문서 / 아래 묶음] 열의 오른쪽(맨 바깥 가로 분할의 마지막 칸)에 붙어 세로 전체를 쓴다.
    [Fact]
    public void Right_tool_opens_beside_the_documents_and_bottom_column_not_above_the_bottom_group()
    {
        var layout = LayoutPlanner.CreateDefault(Real);

        var shown = LayoutPlanner.ShowTool(layout, Real.First(p => p.Id == Id("templates")));

        var root = shown.Root!;
        Assert.Equal(DockOrientation.Horizontal, root.Orientation);
        Assert.Equal(3, root.Children.Count);
        Assert.Equal(Id("schemaBrowser"), root.Children[0].Tools[0]);
        var column = root.Children[1];
        Assert.Equal(DockOrientation.Vertical, column.Orientation);
        Assert.Equal(DockNodeKind.Documents, column.Children[0].Kind);
        Assert.Contains(Id("results"), column.Children[1].Tools);
        Assert.Equal([Id("templates")], root.Children[2].Tools);
        Assert.Equal(DockLocation.Right, LayoutPlanner.LocationOf(root, root.Children[2]));
        Assert.Equal(LayoutPlanner.LeftProportion, root.Children[0].Proportion);
        Assert.Equal(1.0, root.Children.Sum(c => c.Proportion!.Value), 3);

        // 바인드 변수도 같은 오른쪽 묶음의 탭으로 붙는다(패널 사이 전환).
        var both = LayoutPlanner.ShowTool(shown, Real.First(p => p.Id == Id("binds")));
        Assert.Equal([Id("templates"), Id("binds")], both.Root!.Children[2].Tools);
        Assert.Equal(Id("binds"), both.Root.Children[2].ActiveTool);
    }

    [Fact]
    public void Closing_the_right_tools_removes_the_column_and_gives_its_width_back_to_the_documents()
    {
        var layout = LayoutPlanner.CreateDefault(Real);
        var shown = LayoutPlanner.ShowTool(LayoutPlanner.ShowTool(layout, Real.First(p => p.Id == Id("templates"))), Real.First(p => p.Id == Id("binds")));

        var closed = LayoutPlanner.HideTool(LayoutPlanner.HideTool(shown, Id("binds")), Id("templates"));

        Assert.True(LayoutPlanner.SameShape(layout.Root, closed.Root, 1e-3)); // 빈 열·칸이 남지 않고 왼쪽 폭도 그대로
        Assert.Contains(Id("templates"), closed.HiddenTools);
        Assert.Contains(Id("binds"), closed.HiddenTools);
    }

    [Fact]
    public void Bottom_group_added_while_a_right_tool_is_open_goes_under_the_documents_only()
    {
        var noBottom = LayoutPlanner.CreateDefault(Standard);
        foreach (var id in new[] { "output", "messages" })
        {
            noBottom = LayoutPlanner.HideTool(noBottom, Id(id));
        }

        var right = LayoutPlanner.ShowTool(noBottom, Standard.First(p => p.Id == Id("templates")));
        var withBottom = LayoutPlanner.ShowTool(right, Standard.First(p => p.Id == Id("output")));

        var root = withBottom.Root!;
        Assert.Equal(DockOrientation.Horizontal, root.Orientation);
        Assert.Equal([Id("templates")], root.Children[^1].Tools);
        var column = root.Children[1];
        Assert.Equal(DockOrientation.Vertical, column.Orientation);
        Assert.Equal([Id("output")], column.Children[1].Tools);
        Assert.Equal(DockLocation.Bottom, LayoutPlanner.LocationOf(root, column.Children[1]));
    }

    /// <summary>D-048 전 모양: 가로[왼쪽, 세로[가로[문서, 오른쪽], 아래]] — 오른쪽 창이 아래 묶음 위에서 끝난다.</summary>
    private static LayoutDocument BeforeFullHeight(IReadOnlyList<string>? migrations) => new()
    {
        Root = new DockNode
        {
            Kind = DockNodeKind.Split,
            Orientation = DockOrientation.Horizontal,
            Children =
            [
                new DockNode { Kind = DockNodeKind.ToolGroup, Tools = [Id("schemaBrowser"), Id("scripts")], ActiveTool = Id("schemaBrowser"), Proportion = 0.2 },
                new DockNode
                {
                    Kind = DockNodeKind.Split,
                    Orientation = DockOrientation.Vertical,
                    Proportion = 0.8,
                    Children =
                    [
                        new DockNode
                        {
                            Kind = DockNodeKind.Split,
                            Orientation = DockOrientation.Horizontal,
                            Proportion = 0.7,
                            Children =
                            [
                                new DockNode { Kind = DockNodeKind.Documents, Proportion = 0.75 },
                                new DockNode { Kind = DockNodeKind.ToolGroup, Tools = [Id("binds"), Id("templates")], ActiveTool = Id("templates"), Proportion = 0.25 },
                            ],
                        },
                        new DockNode { Kind = DockNodeKind.ToolGroup, Tools = [Id("output"), Id("messages")], ActiveTool = Id("output"), Proportion = 0.3 },
                    ],
                },
            ],
        },
        HiddenTools = [Id("outline")],
        Migrations = migrations,
    };

    [Fact]
    public void A_saved_layout_with_the_right_tools_above_the_bottom_group_moves_them_to_full_height_once()
    {
        var saved = BeforeFullHeight([LayoutPlanner.RightToolsOpenMigration]);

        var r = LayoutPlanner.Reconcile(saved, Standard);

        var root = r.Root!;
        Assert.Equal(3, root.Children.Count);
        Assert.Equal(0.2, root.Children[0].Proportion!.Value, 3);
        var column = root.Children[1];
        Assert.Equal(DockOrientation.Vertical, column.Orientation);
        Assert.Equal(DockNodeKind.Documents, column.Children[0].Kind);
        Assert.Equal(0.7, column.Children[0].Proportion!.Value, 3);
        Assert.Equal([Id("output"), Id("messages")], column.Children[1].Tools);
        Assert.Equal(0.8 * 0.75, column.Proportion!.Value, 3); // 창 전체 기준 폭은 그대로
        Assert.Equal([Id("binds"), Id("templates")], root.Children[2].Tools);
        Assert.Equal(Id("templates"), root.Children[2].ActiveTool);
        Assert.Equal(0.8 * 0.25, root.Children[2].Proportion!.Value, 3);
        Assert.Contains(LayoutPlanner.RightToolsFullHeightMigration, r.Migrations!);
        Assert.Equal([Id("outline")], r.HiddenTools);

        // 표지가 붙은 뒤 사용자가 다시 아래 묶음 위로 끌어 놓은 배치는 그대로 둔다.
        var dragged = BeforeFullHeight(r.Migrations);
        Assert.True(LayoutPlanner.SameShape(dragged.Root, LayoutPlanner.Reconcile(dragged, Standard).Root));
    }

    [Fact]
    public void Full_height_migration_without_a_left_column_wraps_the_documents_column()
    {
        var saved = BeforeFullHeight([LayoutPlanner.RightToolsOpenMigration]);
        var withoutLeft = saved with { Root = saved.Root!.Children[1] with { Proportion = null } };
        var tools = Standard.Where(p => p.DefaultLocation != DockLocation.Left).ToArray();

        var root = LayoutPlanner.Reconcile(withoutLeft, tools).Root!;

        Assert.Equal(DockOrientation.Horizontal, root.Orientation);
        Assert.Equal(DockOrientation.Vertical, root.Children[0].Orientation);
        Assert.Equal(0.75, root.Children[0].Proportion!.Value, 3);
        Assert.Equal([Id("binds"), Id("templates")], root.Children[1].Tools);
        Assert.Equal(DockLocation.Right, LayoutPlanner.LocationOf(root, root.Children[1]));
    }

    private static int CountDocuments(DockNode n) =>
        (n.Kind == DockNodeKind.Documents ? 1 : 0) + (n.Children ?? []).Sum(CountDocuments);

    private static DockNode FindGroup(DockNode root, string toolId)
    {
        if (root.Kind == DockNodeKind.ToolGroup && (root.Tools ?? []).Contains(toolId))
        {
            return root;
        }

        foreach (var c in root.Children ?? [])
        {
            var found = TryFind(c, toolId);
            if (found is not null)
            {
                return found;
            }
        }

        throw new Xunit.Sdk.XunitException($"도구 창 {toolId} 의 묶음이 트리에 없다.");
    }

    private static DockNode? TryFind(DockNode n, string toolId)
    {
        if (n.Kind == DockNodeKind.ToolGroup && (n.Tools ?? []).Contains(toolId))
        {
            return n;
        }

        return (n.Children ?? []).Select(c => TryFind(c, toolId)).FirstOrDefault(x => x is not null);
    }
}
