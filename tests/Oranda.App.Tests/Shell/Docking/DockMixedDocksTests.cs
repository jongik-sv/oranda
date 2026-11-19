using Avalonia.Threading;
using DockManager = Dock.Model.DockManager;
using DockService = Dock.Model.DockService;
using Dock.Model.Controls;
using Dock.Model.Core;
using Factory = Dock.Model.Mvvm.Factory;
using Dock.Model.Mvvm.Controls;
using Oranda.App.Shell.Docking;
using Oranda.App.Shell.Layout;
using Oranda.App.Tests.Shell.Headless;

namespace Oranda.App.Tests.Shell.Docking;

// 2026-11-20 사용자 신고: 문서 탭 줄에 「스키마 브라우저」 도구 창이 탭으로 섞여 들어가 탭 머리가 세로로 늘어나고 내용이 겹쳐 그려졌다.
// 원인: 도구 창 제목 줄(ToolChrome)을 끌면 Dock 은 묶음(IToolDock) 통째를 끌고, 묶음은 도구 창의 CanDockAsDocument=false 를 보지 않아
// 문서 영역에 채우기로 놓으면 도구 창들이 문서 영역으로 옮겨졌다. 도구 창은 도구 묶음에만, 문서는 문서 영역에만 들어가야 한다.
public class DockMixedDocksTests
{
    private const string SchemaBrowser = "oranda.panel.schemaBrowser";
    private const string Results = "oranda.panel.results";
    private const string SqlEditor = "oranda.panel.sqlEditor";

    private static DockManager Manager() => new(new DockService());

    private static PanelTool ToolOf(DockWorkspace ws, string id) =>
        (PanelTool)ws.Factory.FindDockable(ws.Layout!, d => d.Id == id)!;

    private static bool HasToolTab(IDockable node) =>
        node is IDocumentDock { VisibleDockables: { } tabs } && tabs.OfType<ITool>().Any()
        || node is IDock dock && (dock.VisibleDockables ?? []).Any(HasToolTab);

    // 원인 재현: Dock 기본 팩터리는 도구 묶음 통째를 문서 영역에 채우기로 놓는 것을 허용해 도구 창이 문서 탭 사이로 들어간다.
    [Fact]
    public void Plain_dock_factory_lets_a_dragged_tool_group_fill_the_document_area()
    {
        var factory = new Factory();
        var tool = new Tool { Id = "tool", CanDockAsDocument = false }; // 탭 하나를 끌 때는 이 값이 막는다
        var group = new ToolDock { Id = "group", VisibleDockables = factory.CreateList<IDockable>(tool), ActiveDockable = tool };
        var doc = new Document { Id = "doc" };
        var documents = new DocumentDock { Id = "documents", VisibleDockables = factory.CreateList<IDockable>(doc), ActiveDockable = doc };
        var split = new ProportionalDock { Id = "split", VisibleDockables = factory.CreateList<IDockable>(group, new ProportionalDockSplitter(), documents) };
        var root = factory.CreateRootDock();
        root.VisibleDockables = factory.CreateList<IDockable>(split);
        factory.InitLayout(root);
        var manager = Manager();

        Assert.False(manager.ValidateTool(tool, documents, DragAction.Move, DockOperation.Fill, bExecute: false));
        Assert.True(manager.ValidateDock(group, documents, DragAction.Move, DockOperation.Fill, bExecute: true));

        Assert.Same(documents, tool.Owner);
    }

    [Fact]
    public Task Tool_group_and_tool_tab_cannot_be_dropped_into_the_document_area() => HeadlessHost.Run(() =>
    {
        using var h = new ShellHarness();
        var ws = h.Get<DockWorkspace>();
        var doc = ws.OpenDocument(SqlEditor)!;
        Assert.True(ws.ShowTool(SchemaBrowser));
        Dispatcher.UIThread.RunJobs();
        var tool = ToolOf(ws, SchemaBrowser);
        var group = (IToolDock)tool.Owner!;
        var documents = (IDocumentDock)doc.Owner!;
        var manager = Manager();

        foreach (var op in new[] { DockOperation.Fill, DockOperation.Left, DockOperation.Right, DockOperation.Top, DockOperation.Bottom })
        {
            Assert.False(manager.ValidateDock(group, documents, DragAction.Move, op, bExecute: true), $"묶음 → 문서 영역 {op}");
            Assert.False(manager.ValidateDock(group, doc, DragAction.Move, op, bExecute: true), $"묶음 → 문서 탭 {op}");
            Assert.False(manager.ValidateTool(tool, documents, DragAction.Move, op, bExecute: true), $"탭 → 문서 영역 {op}");
            Assert.False(manager.ValidateTool(tool, doc, DragAction.Move, op, bExecute: true), $"탭 → 문서 탭 {op}");
            Assert.False(manager.IsDockTargetVisible(group, documents, op), $"놓을 자리 표시 {op}");
        }

        Assert.False(manager.ValidateDock(group, documents, DragAction.Link, DockOperation.Fill, bExecute: true)); // 맞바꾸기도 막는다
        Assert.Same(group, tool.Owner);
        Assert.False(HasToolTab(ws.Layout!));
    });

    [Fact]
    public Task Menu_and_direct_factory_moves_do_not_put_a_tool_among_documents() => HeadlessHost.Run(() =>
    {
        using var h = new ShellHarness();
        var ws = h.Get<DockWorkspace>();
        var doc = ws.OpenDocument(SqlEditor)!;
        Assert.True(ws.ShowTool(SchemaBrowser));
        Dispatcher.UIThread.RunJobs();
        var tool = ToolOf(ws, SchemaBrowser);
        var group = (IToolDock)tool.Owner!;
        var documents = (IDocumentDock)doc.Owner!;

        ws.Factory.DockAsDocument(tool); // 도구 창 메뉴 「Dock as Tabbed Document」
        ws.Factory.MoveDockable(group, documents, tool, doc);
        ws.Factory.SwapDockable(group, documents, tool, doc);
        ws.Factory.MoveDockable(documents, group, doc, tool);

        Assert.Same(group, tool.Owner);
        Assert.Same(documents, doc.Owner);
        Assert.False(HasToolTab(ws.Layout!));
    });

    [Fact]
    public Task Documents_cannot_be_dropped_into_a_tool_group() => HeadlessHost.Run(() =>
    {
        using var h = new ShellHarness();
        var ws = h.Get<DockWorkspace>();
        var doc = ws.OpenDocument(SqlEditor)!;
        Assert.True(ws.ShowTool(SchemaBrowser));
        Dispatcher.UIThread.RunJobs();
        var tool = ToolOf(ws, SchemaBrowser);
        var group = (IToolDock)tool.Owner!;
        var documents = (IDocumentDock)doc.Owner!;
        var manager = Manager();

        Assert.False(manager.ValidateDocument(doc, group, DragAction.Move, DockOperation.Fill, bExecute: true));
        Assert.False(manager.ValidateDocument(doc, tool, DragAction.Move, DockOperation.Fill, bExecute: true));
        Assert.False(manager.ValidateDock(documents, group, DragAction.Move, DockOperation.Fill, bExecute: true));
        Assert.Same(documents, doc.Owner);
    });

    [Fact]
    public Task Tools_still_move_and_split_among_tool_groups() => HeadlessHost.Run(() =>
    {
        using var h = new ShellHarness();
        var ws = h.Get<DockWorkspace>();
        Assert.True(ws.ShowTool(SchemaBrowser));
        Assert.True(ws.ShowTool(Results));
        Dispatcher.UIThread.RunJobs();
        var schema = ToolOf(ws, SchemaBrowser);
        var results = ToolOf(ws, Results);
        var bottom = (IToolDock)results.Owner!;
        Assert.NotSame(bottom, schema.Owner);
        var manager = Manager();

        Assert.True(manager.IsDockTargetVisible(schema, bottom, DockOperation.Fill));
        Assert.True(manager.ValidateTool(schema, bottom, DragAction.Move, DockOperation.Fill, bExecute: true));
        Assert.Same(bottom, schema.Owner);

        Assert.True(manager.ValidateTool(schema, bottom, DragAction.Move, DockOperation.Right, bExecute: true)); // 묶음 옆으로 나누기
        Assert.IsAssignableFrom<IToolDock>(schema.Owner);
        Assert.NotSame(bottom, schema.Owner);
        Assert.False(HasToolTab(ws.Layout!));
    });

    [Fact]
    public Task Documents_still_move_and_split_among_document_areas() => HeadlessHost.Run(() =>
    {
        using var h = new ShellHarness();
        var ws = h.Get<DockWorkspace>();
        var a = ws.OpenDocument(SqlEditor)!;
        var b = ws.OpenDocument(SqlEditor)!;
        Dispatcher.UIThread.RunJobs();
        var first = (IDocumentDock)a.Owner!;
        var manager = Manager();

        Assert.True(manager.IsDockTargetVisible(b, first, DockOperation.Right));
        Assert.True(manager.ValidateDocument(b, first, DragAction.Move, DockOperation.Right, bExecute: true)); // 문서 영역 나누기
        var second = (IDocumentDock)b.Owner!;
        Assert.NotSame(first, second);

        Assert.True(manager.ValidateDocument(b, a, DragAction.Move, DockOperation.Fill, bExecute: true)); // 다른 칸의 탭 사이로 되옮기기
        Assert.Same(first, b.Owner);
        Assert.Equal(2, ws.Documents.Count);
    });

    // 막기 전 버전에서 이미 섞였거나 다른 길로 섞여도, 저장할 때 도구 창은 기본 위치 묶음으로·문서는 문서 영역으로 돌아가 다음 시작에 깨진 채 나오지 않는다.
    [Fact]
    public Task Saving_a_mixed_layout_moves_tools_and_documents_back_home() => HeadlessHost.Run(() =>
    {
        using var h = new ShellHarness();
        var ws = h.Get<DockWorkspace>();
        var doc = ws.OpenDocument(SqlEditor)!;
        var stray = ws.OpenDocument(SqlEditor)!;
        Assert.True(ws.ShowTool(SchemaBrowser));
        Assert.True(ws.ShowTool(Results));
        Dispatcher.UIThread.RunJobs();
        var tool = ToolOf(ws, SchemaBrowser);
        var group = (IToolDock)tool.Owner!;
        var bottom = (IToolDock)ToolOf(ws, Results).Owner!;
        var documents = (IDocumentDock)doc.Owner!;

        // 팩터리를 거치지 않고 섞는다(옛 버전에서 섞인 채 떠 있는 모습).
        group.VisibleDockables!.Remove(tool);
        documents.VisibleDockables!.Add(tool);
        tool.Owner = documents;
        documents.ActiveDockable = tool;
        documents.VisibleDockables.Remove(stray);
        bottom.VisibleDockables!.Add(stray);
        stray.Owner = bottom;

        var saved = ws.Capture();

        Assert.False(HasToolTab(ws.Layout!));
        Assert.IsAssignableFrom<IToolDock>(tool.Owner);
        Assert.Contains(SchemaBrowser, LayoutPlanner.ToolIds(saved.Root));
        Assert.DoesNotContain(SchemaBrowser, saved.HiddenTools); // 닫힌 창이 되지 않는다
        Assert.Same(documents, stray.Owner);
        Assert.Contains(stray, ws.Documents);
        Assert.DoesNotContain(bottom.VisibleDockables!, d => d is PanelDocument);
    });

    private const string MixedLayoutJson = """
        {
          "schemaVersion": 2,
          "root": {
            "kind": "Split", "orientation": "Horizontal",
            "children": [
              { "kind": "ToolGroup", "proportion": 0.22, "tools": ["oranda.panel.scripts", "oranda.panel.sqlEditor"], "activeTool": "oranda.panel.sqlEditor" },
              { "kind": "Documents", "proportion": 0.78, "tools": ["oranda.panel.schemaBrowser"], "activeTool": "oranda.panel.schemaBrowser" }
            ]
          },
          "hiddenTools": ["oranda.panel.schemaBrowser"],
          "migrations": ["rightToolsOpen", "rightToolsFullHeight"]
        }
        """;

    // 저장 파일에 섞인 상태(문서 영역 노드의 도구 창, 도구 묶음의 문서 종류 창)가 적혀 있어도 복원하면 바로잡힌다.
    [Fact]
    public Task Restoring_a_mixed_layout_file_puts_tools_back_at_their_default_place() => HeadlessHost.Run(() =>
    {
        var dir = Path.Combine(Path.GetTempPath(), "oranda-shell-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "layout.json"), MixedLayoutJson);
        File.WriteAllText(Path.Combine(dir, "settings.json"), ShellHarness.AllFeaturesSettingsJson);
        using var h = new ShellHarness(dir);
        var ws = h.Get<DockWorkspace>();
        Dispatcher.UIThread.RunJobs();

        Assert.True(ws.IsToolVisible(SchemaBrowser));
        Assert.IsAssignableFrom<IToolDock>(ToolOf(ws, SchemaBrowser).Owner);
        Assert.False(HasToolTab(ws.Layout!));
        var saved = ws.Capture();
        Assert.DoesNotContain(SqlEditor, LayoutPlanner.ToolIds(saved.Root));
        Assert.DoesNotContain(SchemaBrowser, saved.HiddenTools);
    });
}
