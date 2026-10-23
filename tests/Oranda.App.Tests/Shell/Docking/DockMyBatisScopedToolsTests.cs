using Avalonia.Threading;
using Dock.Model.Core;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell.Docking;
using Oranda.App.Tests.Features.MyBatisWorkspace.Fakes;
using Oranda.App.Shell.Layout;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Shell;

namespace Oranda.App.Tests.Shell.Docking;

// TSK-01-02 B5: 도구 창 보임 규칙이 문서 종류(MyBatis)를 함께 본다(design.md 3.2).
// MyBatis 도구 창은 아직 등록되지 않았으므로, 등록된 SQL 전용 창(템플릿 = MyBatis 문서에서 숨김, 결과 = 보임)으로 전환을 견준다.
// 바인드 변수 창은 MyBatis 문서에서도 보인다(TSK-03-02, 2026-10-23 사용자 결정: 문장 파라미터를 바인드 변수 창에 둔다).
public class DockMyBatisScopedToolsTests
{
    private const string Binds = "oranda.panel.binds";
    private const string Results = "oranda.panel.results";
    private const string Detail = "oranda.panel.objectDetail";
    private const string SqlEditor = "oranda.panel.sqlEditor";

    private static void Pump()
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static SqlWorkspaceViewModel NewWorkspace(string id) => new(new SqlEditorHost(), id, id);

    private static List<string> GroupOrder(DockWorkspace ws, string toolId) =>
        ((IDock)ws.Factory.FindDockable(ws.Layout!, d => d.Id == toolId)!.Owner!).VisibleDockables!.Select(d => d.Id).ToList();

    [Fact]
    public Task Attaching_and_detaching_a_session_inside_the_same_document_hides_and_restores_text_only_tools() => HeadlessHost.Run(() =>
    {
        using var h = new ShellHarness();
        var ws = h.Get<DockWorkspace>();
        ws.ShowTool(Templates);
        ws.ShowTool(Results);
        var vm = NewWorkspace("mb-a");
        var doc = ws.OpenDocument(SqlEditor, vm)!;
        ws.Activate(doc);
        Pump();
        Assert.Equal(SqlWorkspaceKind.Sql, vm.DocumentKind);
        Assert.Null(vm.ToolScope);
        Assert.Contains(Templates, ws.VisibleToolIds);
        var order = GroupOrder(ws, Templates);
        var before = System.Text.Json.JsonSerializer.Serialize(ws.Capture().Root);

        for (var round = 0; round < 3; round++)
        {
            vm.MyBatis = new FakeMyBatisWorkspaceSession();
            Pump();
            Assert.Equal(SqlWorkspaceKind.MyBatis, vm.DocumentKind);
            Assert.Equal("mybatis", vm.ToolScope);
            Assert.DoesNotContain(Templates, ws.VisibleToolIds);
            Assert.Contains(Templates, ws.ScopedHiddenToolIds);
            Assert.Contains(Results, ws.VisibleToolIds);
            Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(ws.Capture().Root)); // layout.json 에 숨김이 남지 않는다

            vm.MyBatis = null;
            Pump();
            Assert.Equal(SqlWorkspaceKind.Sql, vm.DocumentKind);
            Assert.Contains(Templates, ws.VisibleToolIds);
            Assert.Empty(ws.ScopedHiddenToolIds);
            Assert.Equal(order, GroupOrder(ws, Templates));
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task Switching_between_a_sql_workspace_and_a_mybatis_workspace_hides_and_restores_text_only_tools() => HeadlessHost.Run(() =>
    {
        using var h = new ShellHarness();
        var ws = h.Get<DockWorkspace>();
        ws.ShowTool(Templates);
        ws.ShowTool(Results);
        var plainVm = NewWorkspace("plain");
        var mapperVm = NewWorkspace("mapper");
        mapperVm.MyBatis = new FakeMyBatisWorkspaceSession();
        var plain = ws.OpenDocument(SqlEditor, plainVm)!;
        var mapper = ws.OpenDocument(SqlEditor, mapperVm)!;
        ws.Activate(plain);
        Pump();
        var order = GroupOrder(ws, Templates);

        for (var round = 0; round < 3; round++)
        {
            ws.Activate(mapper);
            Pump();
            Assert.DoesNotContain(Templates, ws.VisibleToolIds);
            Assert.Contains(Results, ws.VisibleToolIds);

            ws.Activate(plain);
            Pump();
            Assert.Contains(Templates, ws.VisibleToolIds);
            Assert.Contains(Results, ws.VisibleToolIds);
            Assert.Empty(ws.ScopedHiddenToolIds);
            Assert.Equal(order, GroupOrder(ws, Templates));
        }
    });

    [Fact]
    public Task Detail_document_to_mybatis_document_brings_back_only_the_tools_that_mybatis_shows() => HeadlessHost.Run(() =>
    {
        using var h = new ShellHarness();
        var ws = h.Get<DockWorkspace>();
        ws.ShowTool(Templates);
        ws.ShowTool(Results);
        var mapperVm = NewWorkspace("mapper");
        mapperVm.MyBatis = new FakeMyBatisWorkspaceSession();
        var mapper = ws.OpenDocument(SqlEditor, mapperVm)!;
        var plain = ws.OpenDocument(SqlEditor, NewWorkspace("plain"))!;
        var detail = ws.OpenDocument(Detail)!;
        ws.Activate(plain);
        Pump();
        var resultsOrder = GroupOrder(ws, Results);

        ws.Activate(detail);
        Pump();
        Assert.DoesNotContain(Results, ws.VisibleToolIds);
        Assert.DoesNotContain(Templates, ws.VisibleToolIds);

        ws.Activate(mapper); // 상세 → MyBatis: 결과는 되보이고 템플릿은 숨은 채
        Pump();
        Assert.Contains(Results, ws.VisibleToolIds);
        Assert.DoesNotContain(Templates, ws.VisibleToolIds);
        Assert.Contains(Templates, ws.ScopedHiddenToolIds);
        Assert.Equal(resultsOrder.Where(id => !DockWorkspace.SqlTextOnlyToolIds.Contains(id)).ToList(),
            GroupOrder(ws, Results).Where(id => !DockWorkspace.MyBatisToolIds.Contains(id)).ToList());
        Assert.Contains("oranda.panel.mybatisExpanded", ws.VisibleToolIds); // MyBatis 문서에서는 아래 묶음에 펼친 SQL 이 보인다

        ws.Activate(plain);
        Pump();
        Assert.Contains(Templates, ws.VisibleToolIds);
        Assert.Empty(ws.ScopedHiddenToolIds);
        Assert.Equal(resultsOrder, GroupOrder(ws, Results));
    });

    [Fact]
    public Task Unsubscribes_from_the_previous_document_when_the_active_document_changes() => HeadlessHost.Run(() =>
    {
        using var h = new ShellHarness();
        var ws = h.Get<DockWorkspace>();
        ws.ShowTool(Templates);
        var firstVm = NewWorkspace("first");
        var first = ws.OpenDocument(SqlEditor, firstVm)!;
        var second = ws.OpenDocument(SqlEditor, NewWorkspace("second"))!;
        ws.Activate(first);
        Pump();
        ws.Activate(second);
        Pump();
        Assert.Contains(Templates, ws.VisibleToolIds);

        firstVm.MyBatis = new FakeMyBatisWorkspaceSession(); // 활성이 아닌 문서의 종류 변화는 지금 보이는 창에 영향이 없다
        Pump();
        Assert.Contains(Templates, ws.VisibleToolIds);
        Assert.Empty(ws.ScopedHiddenToolIds);

        ws.Activate(first); // 다시 활성이 되면 그 문서의 종류로 판정한다
        Pump();
        Assert.DoesNotContain(Templates, ws.VisibleToolIds);
    });

    // design.md 3.2: MyBatis 창 ID 는 활성 문서 없음·일반 SQL·MyBatis·상세 문서에서 각각 숨김·숨김·보임·숨김.
    [Fact]
    public Task Tool_shown_decision_for_mybatis_tool_ids_follows_the_document_kind() => HeadlessHost.Run(() =>
    {
        using var h = new ShellHarness();
        var ws = h.Get<DockWorkspace>();
        var mapperVm = NewWorkspace("mapper");
        mapperVm.MyBatis = new FakeMyBatisWorkspaceSession();
        var mapper = ws.OpenDocument(SqlEditor, mapperVm)!;
        var plain = ws.OpenDocument(SqlEditor, NewWorkspace("plain"))!;
        var detail = ws.OpenDocument(Detail)!;

        Assert.Equal(3, DockWorkspace.MyBatisToolIds.Count);
        foreach (var id in DockWorkspace.MyBatisToolIds)
        {
            Assert.False(DockWorkspace.IsToolShownFor(id, (PanelDocument?)null));
            Assert.False(DockWorkspace.IsToolShownFor(id, plain));
            Assert.True(DockWorkspace.IsToolShownFor(id, mapper));
            Assert.False(DockWorkspace.IsToolShownFor(id, detail));
        }

        // SQL 본문 전용 창: MyBatis 문서에서만 숨고, 결과 같은 나머지 SQL 전용 창과 탐색 창은 그대로.
        foreach (var id in DockWorkspace.SqlTextOnlyToolIds)
        {
            Assert.True(DockWorkspace.IsToolShownFor(id, plain));
            Assert.False(DockWorkspace.IsToolShownFor(id, mapper));
            Assert.False(DockWorkspace.IsToolShownFor(id, detail));
            Assert.True(DockWorkspace.IsToolShownFor(id, (PanelDocument?)null)); // 활성 문서 없음 = 지금 상태 유지
        }

        Assert.True(DockWorkspace.IsToolShownFor(Results, mapper));
        Assert.True(DockWorkspace.IsToolShownFor(Binds, mapper));
        Assert.False(DockWorkspace.IsToolShownFor(Results, detail));
        Assert.True(DockWorkspace.IsToolShownFor("oranda.panel.schemaBrowser", mapper));
        Assert.True(DockWorkspace.IsToolShownFor("oranda.panel.schemaBrowser", detail));
        Assert.True(DockWorkspace.SqlTextOnlyToolIds.IsSubsetOf(DockWorkspace.SqlOnlyToolIds));
        Assert.Empty(DockWorkspace.MyBatisToolIds.Intersect(DockWorkspace.SqlOnlyToolIds));
        return Task.CompletedTask;
    });

    private const string Templates = "oranda.panel.templates";
    private const string MbExpanded = "oranda.panel.mybatisExpanded";
    private const string MbStatements = "oranda.panel.mybatisStatements";

    /// <summary>등록되지 않은 MyBatis 도구 창을 시험용으로 등록한다(아직 구현 Task 가 등록하기 전이라).</summary>
    private static void RegisterMyBatisStubs(ShellHarness h)
    {
        var registry = h.Get<IToolPanelRegistry>();
        foreach (var id in DockWorkspace.MyBatisToolIds.Where(id => registry.Find(id) is null).ToList())
        {
            registry.Register(new ToolPanelDefinition
            {
                Id = id,
                Title = id,
                Kind = ToolPanelKind.Tool,
                DefaultLocation = DockLocation.Right,
                ViewModelType = registry.Find(Templates)!.ViewModelType,
            });
        }
    }

    private static void ApplyLayout(ShellHarness h, DockWorkspace ws, DockNode root, params string[] shown)
    {
        var registry = h.Get<IToolPanelRegistry>();
        var set = new HashSet<string>(shown);
        ws.Apply(new LayoutDocument
        {
            Root = root,
            HiddenTools = registry.Panels.Where(p => p.Kind == ToolPanelKind.Tool && !set.Contains(p.Id)).Select(p => p.Id).ToList(),
            Migrations = [LayoutPlanner.RightToolsOpenMigration],
        });
    }

    private static DockNode Group(string tool, double proportion) => new() { Kind = DockNodeKind.ToolGroup, Proportion = proportion, Tools = [tool] };

    // 오른쪽 = 바인드·템플릿·MyBatis 창 하나인 배치에서 SQL 문서일 때 MyBatis 창은 늘 숨김 기록에 남는다.
    // 그 상태에서 칸 비율을 바꾸고 객체 상세 문서를 다녀와도 바꾼 비율이 옛 값으로 돌아가면 안 된다(왕복 2회).
    [Fact]
    public Task Changed_proportions_survive_round_trips_while_a_mybatis_tool_stays_hidden() => HeadlessHost.Run(() =>
    {
        using var h = new ShellHarness();
        RegisterMyBatisStubs(h);
        var ws = h.Get<DockWorkspace>();
        ApplyLayout(
            h,
            ws,
            new DockNode
            {
                Kind = DockNodeKind.Split,
                Orientation = DockOrientation.Horizontal,
                Children =
                [
                    new DockNode { Kind = DockNodeKind.Documents, Proportion = 0.7 },
                    new DockNode
                    {
                        Kind = DockNodeKind.Split,
                        Orientation = DockOrientation.Vertical,
                        Proportion = 0.3,
                        Children = [Group(Binds, 0.4), Group(Templates, 0.3), Group(MbExpanded, 0.3)],
                    },
                ],
            },
            Binds,
            Templates,
            MbExpanded);
        var sql = ws.OpenDocument(SqlEditor, NewWorkspace("plain"))!;
        var detail = ws.OpenDocument(Detail)!;
        ws.Activate(sql);
        Pump();
        Assert.Equal([MbExpanded], ws.MyBatisScopedHiddenToolIds);
        var binds = ws.Factory.FindDockable(ws.Layout!, d => d.Id == Binds)!.Owner!;
        var templates = ws.Factory.FindDockable(ws.Layout!, d => d.Id == Templates)!.Owner!;

        foreach (var (bindsValue, templatesValue) in new[] { (0.55, 0.45), (0.35, 0.65) })
        {
            binds.Proportion = bindsValue;
            templates.Proportion = templatesValue;

            ws.Activate(detail);
            Pump();
            Assert.DoesNotContain(Binds, ws.VisibleToolIds);

            ws.Activate(sql);
            Pump();
            Assert.Contains(Binds, ws.VisibleToolIds);
            Assert.Contains(Templates, ws.VisibleToolIds);
            Assert.Equal(bindsValue, binds.Proportion, 4);
            Assert.Equal(templatesValue, templates.Proportion, 4);
        }

        return Task.CompletedTask;
    });

    // 「문장」 창은 폴더 모드(문서 없이도 보임)에서 쓰도록 문서 종류와 상관없이 보이게 할 수 있다(03-07).
    [Fact]
    public Task A_mybatis_tool_marked_always_shown_stays_visible_on_any_document() => HeadlessHost.Run(() =>
    {
        using var h = new ShellHarness();
        RegisterMyBatisStubs(h);
        var ws = h.Get<DockWorkspace>();
        ApplyLayout(
            h,
            ws,
            new DockNode
            {
                Kind = DockNodeKind.Split,
                Orientation = DockOrientation.Horizontal,
                Children =
                [
                    new DockNode { Kind = DockNodeKind.ToolGroup, Proportion = 0.25, Tools = [MbStatements] },
                    new DockNode { Kind = DockNodeKind.Documents, Proportion = 0.75 },
                ],
            },
            MbStatements);
        var sql = ws.OpenDocument(SqlEditor, NewWorkspace("plain"))!;
        var detail = ws.OpenDocument(Detail)!;
        ws.Activate(sql);
        Pump();
        Assert.True(ws.IsScopedHidden(MbStatements));
        Assert.DoesNotContain(MbStatements, ws.VisibleToolIds);

        ws.SetToolAlwaysShown(MbStatements, true);
        Pump();
        Assert.False(ws.IsScopedHidden(MbStatements));
        Assert.Contains(MbStatements, ws.VisibleToolIds);
        ws.Activate(detail);
        Pump();
        Assert.Contains(MbStatements, ws.VisibleToolIds);
        ws.Activate(sql);
        Pump();
        Assert.Contains(MbStatements, ws.VisibleToolIds);

        ws.SetToolAlwaysShown(MbStatements, false);
        Pump();
        Assert.DoesNotContain(MbStatements, ws.VisibleToolIds);
        Assert.True(ws.IsScopedHidden(MbStatements));

        Assert.Throws<ArgumentException>(() => ws.SetToolAlwaysShown(Results, true)); // MyBatis 도구 창만 받는다
        return Task.CompletedTask;
    });
}
