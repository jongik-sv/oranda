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
        Tool("templates", DockLocation.Right, 20),
        Doc("sqlEditor"),
    ];

    private static string Id(string name) => "oranda.panel." + name;

    [Fact]
    public void Default_layout_follows_ui_shell_2_1()
    {
        var layout = LayoutPlanner.CreateDefault(Standard);

        // 세로: [가로: [왼쪽 묶음, 문서], 아래 묶음]
        var root = layout.Root!;
        Assert.Equal(DockNodeKind.Split, root.Kind);
        Assert.Equal(DockOrientation.Vertical, root.Orientation);
        var middle = root.Children[0];
        var bottom = root.Children[1];
        Assert.Equal(DockOrientation.Horizontal, middle.Orientation);
        Assert.Equal([Id("schemaBrowser"), Id("scripts")], middle.Children[0].Tools);
        Assert.Equal(DockNodeKind.Documents, middle.Children[1].Kind);
        Assert.Equal([Id("output"), Id("messages")], bottom.Tools);

        // 오른쪽 도구 창은 기본 숨김(트리에 없고 hiddenTools 에 있다). 문서 종류는 트리에 넣지 않는다.
        Assert.Equal([Id("outline"), Id("templates")], layout.HiddenTools);
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
        // 아래 묶음이 사라지면 가장 바깥 세로 분할도 없어진다.
        Assert.Equal(DockOrientation.Horizontal, r.Root!.Orientation);
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
                Children =
                [
                    layout.Root.Children[0] with
                    {
                        Children = [left with { Tools = [Id("schemaBrowser"), Id("schemaBrowser"), Id("scripts")] }, layout.Root.Children[0].Children[1]],
                    },
                    layout.Root.Children[1],
                ],
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

        Assert.Contains("\"schemaVersion\": 1", json);
        Assert.Contains("\"kind\": \"ToolGroup\"", json);
        Assert.DoesNotContain("Dock.", json);
        Assert.Equal(layout.Window, back.Window);
        Assert.True(LayoutPlanner.SameShape(layout.Root, back.Root));
        Assert.Equal(layout.HiddenTools, back.HiddenTools);
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
