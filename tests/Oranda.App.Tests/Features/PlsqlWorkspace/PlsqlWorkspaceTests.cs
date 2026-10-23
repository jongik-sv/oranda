using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.PlsqlEditor;
using Oranda.App.Features.PlsqlWorkspace;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell;
using Oranda.App.Shell.Docking;
using Oranda.App.Tests.Features.PlsqlEditor;
using Oranda.App.Tests.Features.SchemaBrowser;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Shell;

namespace Oranda.App.Tests.Features.PlsqlWorkspace;

/// <summary>
/// PL/SQL 작업 공간(결정 59 안 B, B-1): 탭 들어가기(가까운 → 활성 → 최근 → 새로), 안쪽 탭부터 닫기·빈 작업 공간 남김, 닫기 확인,
/// 활성 탭을 따르는 문맥 키·컴파일 오류 창, 접속 섞임 표시, View 를 새로 만들어도 편집기 View 를 나눠 갖지 않음.
/// 스키마 브라우저에서 여는 길(미리보기·이미 열린 객체·스펙/바디)은 <see cref="SchemaOpenTests"/> 에 있다.
/// </summary>
[Collection(PlsqlStatics.Name)]
public class PlsqlWorkspaceTests
{
    [Fact]
    public Task New_plsql_twice_puts_two_tabs_in_one_workspace_document_and_plus_adds_to_its_own_workspace() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        var registry = h.Get<ICommandRegistry>();
        var keys = h.Get<IContextKeys>();
        var docking = h.Get<DockWorkspace>();

        await registry.ExecuteAsync("oranda.file.newPlsqlEditor", keys);
        await registry.ExecuteAsync("oranda.file.newPlsqlEditor", keys);
        Dispatcher.UIThread.RunJobs();

        var doc = Assert.Single(docking.Documents);
        Assert.Equal(PlsqlWorkspaceRouter.PanelId, doc.Definition.Id);
        Assert.Equal("PL/SQL 1", doc.Title);
        var first = Assert.IsType<PlsqlWorkspaceViewModel>(doc.Panel);
        Assert.Equal(2, first.Tabs.Count);
        Assert.Same(first.Tabs[1], first.ActiveTab);

        // 다른 작업 공간이 활성이어도 탭 줄의 + 는 그 작업 공간에 넣는다.
        var second = new PlsqlWorkspaceViewModel { Name = "PL/SQL 2" };
        docking.OpenDocument(PlsqlWorkspaceRouter.PanelId, second, second.Name);
        await first.NewTabCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(3, first.Tabs.Count);
        Assert.Empty(second.Tabs);
        Assert.Same(doc, docking.ActiveDocument);
    });

    [Fact]
    public Task A_plsql_tab_goes_to_the_last_active_workspace_when_another_document_is_active() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        var registry = h.Get<ICommandRegistry>();
        var keys = h.Get<IContextKeys>();
        var docking = h.Get<DockWorkspace>();
        await registry.ExecuteAsync("oranda.file.newPlsqlEditor", keys);
        OpenOtherDocument(docking);
        Assert.IsNotType<PlsqlWorkspaceViewModel>(docking.ActiveDocument?.Panel);

        await registry.ExecuteAsync("oranda.file.newPlsqlEditor", keys);
        Dispatcher.UIThread.RunJobs();

        var workspace = Assert.Single(docking.Documents.Select(d => d.Panel).OfType<PlsqlWorkspaceViewModel>());
        Assert.Equal(2, workspace.Tabs.Count);
        Assert.Same(workspace, docking.ActiveDocument?.Panel);
    });

    [Fact]
    public Task Close_closes_inner_tabs_first_and_the_last_tab_leaves_an_empty_workspace() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        var registry = h.Get<ICommandRegistry>();
        var keys = h.Get<IContextKeys>();
        var docking = h.Get<DockWorkspace>();
        await registry.ExecuteAsync("oranda.file.newPlsqlEditor", keys);
        await registry.ExecuteAsync("oranda.file.newPlsqlEditor", keys);
        Dispatcher.UIThread.RunJobs();
        var doc = Assert.Single(docking.Documents);
        var workspace = (PlsqlWorkspaceViewModel)doc.Panel;

        await registry.ExecuteAsync("oranda.file.close", keys);
        Assert.Single(workspace.Tabs);
        await registry.ExecuteAsync("oranda.file.close", keys);
        Assert.True(workspace.IsEmpty);
        Assert.Same(doc, Assert.Single(docking.Documents)); // 빈 작업 공간은 남는다(결정 59)
        Assert.Null(PlsqlEditorViewModel.Active);

        await registry.ExecuteAsync("oranda.file.close", keys);
        Assert.Empty(docking.Documents);
    });

    [Fact]
    public Task Closing_a_workspace_with_an_edited_tab_asks_and_a_clean_one_does_not() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        var registry = h.Get<ICommandRegistry>();
        var keys = h.Get<IContextKeys>();
        var docking = h.Get<DockWorkspace>();
        await registry.ExecuteAsync("oranda.file.newPlsqlEditor", keys);
        Dispatcher.UIThread.RunJobs();
        var doc = Assert.Single(docking.Documents);
        var workspace = (PlsqlWorkspaceViewModel)doc.Panel;

        Assert.Null(docking.CloseConfirm!(doc)); // 템플릿 그대로인 새 탭은 묻지 않는다
        workspace.Tabs[0].Editor.Document.Insert(0, "-- mine\n");
        Assert.NotNull(docking.CloseConfirm(doc));
    });

    [Fact]
    public Task Closing_asks_for_each_edited_tab_in_turn_and_a_cancel_keeps_the_tab() => HeadlessHost.Run(async () =>
    {
        var dialogs = new AnswerDialogs(SaveChoice.DontSave, SaveChoice.Cancel, SaveChoice.DontSave, SaveChoice.DontSave, SaveChoice.Cancel);
        var host = new SqlEditorHost(dialogs: dialogs);
        var workspace = new PlsqlWorkspaceViewModel { Host = () => host };
        var a = Edited("A");
        var clean = new PlsqlEditorViewModel();
        clean.StartNew(PlsqlEditorCommandHandler.NewTemplate, null);
        var b = Edited("B");
        workspace.Add(a);
        workspace.Add(clean);
        workspace.Add(b);

        // 작업 공간 문서 닫기: 고친 탭마다 그 탭을 보이고 차례로 묻는다. 처음 취소에서 멈춘다.
        Assert.False(await workspace.ConfirmCloseAllAsync());
        Assert.Equal(["A", "B"], dialogs.Asked);
        Assert.Same(b, workspace.ActiveTab);
        Assert.True(await workspace.ConfirmCloseAllAsync());
        Assert.Equal(["A", "B", "A", "B"], dialogs.Asked);

        // Ctrl+W: 고친 안쪽 탭을 닫다 취소하면 탭은 남고, 셸이 문서까지 닫지 않게 true.
        Assert.True(await workspace.CloseActiveInnerTabAsync(default));
        Assert.Contains(b, workspace.Tabs);
        Assert.Equal(3, workspace.Tabs.Count);

        // 탭 줄에서 선택이 풀려도(ActiveTab null) 탭이 남아 있으면 안쪽 탭을 닫는다.
        workspace.ActiveTab = null;
        Assert.True(await workspace.CloseActiveInnerTabAsync(default));
        Assert.Equal(3, workspace.Tabs.Count); // 마지막 탭 B 를 물었고 위에서 준 답이 다 떨어져 취소(NoDialogs 와 같은 기본)
    });

    [Fact]
    public Task Removing_or_replacing_the_active_tab_with_the_tab_strip_drawn_shows_the_next_tab() => HeadlessHost.Run(() =>
    {
        // 탭 줄(ListBox)의 SelectedItem 양방향 바인딩이 항목이 빠질 때 ActiveTab 을 null 로 밀어도 옆 탭이 보인다.
        using var h = new ShellHarness();
        var docking = h.Get<DockWorkspace>();
        var router = PlsqlWorkspaceRouter.For(docking, h.Services);
        var tabs = Enumerable.Range(0, 3).Select(i => New($"T{i}")).ToList();
        try
        {
            var doc = router.Place(tabs[0])!;
            router.Place(tabs[1]);
            router.Place(tabs[2]);
            var workspace = (PlsqlWorkspaceViewModel)doc.Panel;
            Dispatcher.UIThread.RunJobs();
            Assert.Single(h.Window.GetVisualDescendants().OfType<PlsqlWorkspaceView>());

            workspace.ActiveTab = tabs[1];
            Dispatcher.UIThread.RunJobs();
            workspace.Remove(tabs[1]);
            Dispatcher.UIThread.RunJobs();
            Assert.Same(tabs[2], workspace.ActiveTab);
            Assert.Same(tabs[2], PlsqlEditorViewModel.Active);

            var p1 = New("P1");
            var p2 = New("P2");
            router.Place(p1, preview: true);
            Dispatcher.UIThread.RunJobs();
            router.Place(p2, preview: true);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal([tabs[0], tabs[2], p2], workspace.Tabs);
            Assert.Same(p2, workspace.ActiveTab);
            Assert.Same(p2, PlsqlEditorViewModel.Active);
        }
        finally
        {
            PlsqlEditorViewModel.Active = null;
        }
    });

    [Fact]
    public Task Closing_the_only_tab_of_a_preview_only_workspace_closes_the_document() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        var docking = h.Get<DockWorkspace>();
        var router = PlsqlWorkspaceRouter.For(docking, h.Services);
        var preview = New("P");
        var doc = router.Place(preview, preview: true)!;
        var workspace = (PlsqlWorkspaceViewModel)doc.Panel;
        Assert.True(workspace.IsTransient);

        Assert.True(await workspace.CloseTabAsync(preview));

        Assert.Empty(docking.Documents);
        Assert.Null(PlsqlEditorViewModel.Active);

        // 보통 탭이 들어온 작업 공간은 마지막 탭을 닫아도 남는다.
        var pinned = New("Q");
        var kept = router.Place(pinned)!;
        Assert.True(await ((PlsqlWorkspaceViewModel)kept.Panel).CloseTabAsync(pinned));
        Assert.Same(kept, Assert.Single(docking.Documents));
    });

    [Fact]
    public async Task Reveal_in_schema_uses_the_visible_tab_of_the_active_plsql_workspace()
    {
        var b = new Oranda.App.Tests.Features.Navigation.NavBrowserHarness();
        var spec = new PlsqlEditorViewModel(new SchemaOpenTests.OpenFakeMeta(), null!);
        Assert.True(await spec.OpenAsync(new SchemaOpenTests.OpenFakeSession(), new Oranda.Core.Metadata.DbObjectRef("SCOTT", "EMP_PKG", "PACKAGE")));
        var workspace = new PlsqlWorkspaceViewModel();
        workspace.Add(spec);
        var handler = new Oranda.App.Features.Navigation.ObjectNavigationCommandHandler
        {
            Metadata = b.Meta,
            Editor = b.Editor,
            SessionProvider = () => b.Session,
            ActiveDocumentProvider = () => workspace,
            BrowserProvider = () => b.Vm,
        };

        await handler.ExecuteAsync(new CommandContext("oranda.schema.reveal", Oranda.App.Tests.Shell.FakeContextKeys.AllTrue), default);

        Assert.Equal(new Oranda.Core.Metadata.DbObjectRef("SCOTT", "EMP_PKG", "PACKAGE"), b.Vm.SelectedNode!.Ref);
    }

    private static PlsqlEditorViewModel New(string text)
    {
        var vm = new PlsqlEditorViewModel();
        vm.StartNew(text, null);
        return vm;
    }

    private static PlsqlEditorViewModel Edited(string title)
    {
        var vm = New(PlsqlEditorCommandHandler.NewTemplate);
        vm.Title = title;
        vm.Editor.Document.Insert(0, "-- mine\n");
        return vm;
    }

    [Fact]
    public Task Keys_follow_the_active_tab_and_turn_off_on_a_non_plsql_document_or_when_the_tab_closes() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        var keys = h.Get<IContextKeys>();
        var docking = h.Get<DockWorkspace>();
        var router = PlsqlWorkspaceRouter.For(docking, h.Services);
        var body = await PlsqlEditorTests.OpenedPackageBody();
        var procedure = await PlsqlEditorTests.OpenedProcedure();
        try
        {
            var doc = router.Place(body)!;
            router.Place(procedure);
            var workspace = (PlsqlWorkspaceViewModel)doc.Panel;
            Assert.Same(procedure, PlsqlEditorViewModel.Active);
            Assert.True(keys.Evaluate("plsqlRunnable"));
            Assert.False(keys.Evaluate("isPackageDoc"));
            Assert.Same(procedure, CompileErrorsViewModel.LastSource);

            workspace.ActiveTab = body; // 안쪽 탭을 바꾸면 활성 탭·키·컴파일 오류 창이 따라간다
            Assert.Same(body, PlsqlEditorViewModel.Active);
            Assert.True(keys.Evaluate("isPackageDoc"));
            Assert.Same(body, CompileErrorsViewModel.LastSource);

            // PL/SQL 이 아닌 문서로 옮겨 가면 세 키가 꺼진다.
            OpenOtherDocument(docking);
            Assert.Null(PlsqlEditorViewModel.Active);
            Assert.False(keys.Evaluate("isPackageDoc"));
            Assert.False(keys.Evaluate("plsqlRunnable"));
            Assert.False(keys.Evaluate("serverSourceChanged"));

            docking.Activate(doc);
            Dispatcher.UIThread.RunJobs();
            Assert.Same(body, PlsqlEditorViewModel.Active);
            Assert.True(keys.Evaluate("isPackageDoc"));

            // 닫은 탭을 활성 탭·컴파일 오류 창이 가리키지 않는다.
            workspace.Remove(body);
            Assert.Same(procedure, PlsqlEditorViewModel.Active);
            Assert.False(keys.Evaluate("isPackageDoc"));
            workspace.Remove(procedure);
            Assert.Null(PlsqlEditorViewModel.Active);
            Assert.Null(CompileErrorsViewModel.LastSource);
            Assert.False(keys.Evaluate("plsqlRunnable"));
        }
        finally
        {
            PlsqlEditorViewModel.Active = null;
        }
    });

    /// <summary>PL/SQL 이 아닌 문서(객체 상세)를 열어 활성으로 만든다.</summary>
    private static void OpenOtherDocument(DockWorkspace docking)
    {
        docking.OpenDocument("oranda.panel.objectDetail", new Oranda.App.Features.ObjectDetail.ObjectDetailViewModel(), "SCOTT.EMP");
        Dispatcher.UIThread.RunJobs();
    }

    [Fact]
    public void Connection_names_show_only_when_tabs_of_one_workspace_use_different_connections()
    {
        var workspace = new PlsqlWorkspaceViewModel();
        var dev = new SchemaOpenTests.OpenFakeSession();
        var a = new PlsqlEditorViewModel();
        a.StartNew("x", dev);
        var b = new PlsqlEditorViewModel();
        b.StartNew("y", dev);
        workspace.Add(a);
        workspace.Add(b);
        Assert.False(workspace.MixedConnections);

        var c = new PlsqlEditorViewModel();
        c.StartNew("z", new SchemaOpenTests.OpenFakeSession());
        workspace.Add(c);
        Assert.True(workspace.MixedConnections);
        Assert.Equal("t", c.ConnectionName);

        workspace.Remove(c);
        Assert.False(workspace.MixedConnections);
    }

    [Fact]
    public Task Two_views_of_one_workspace_build_their_own_editor_views() => HeadlessHost.Run(() =>
    {
        // 도킹이 문서를 옮기거나 문서 영역 템플릿을 다시 만들면 작업 공간 View 가 새로 생긴다. 편집기 View 를 ViewModel 이 들고 있으면
        // 같은 Control 이 부모 둘을 갖는다(「already has a visual parent」 크래시).
        var workspace = new PlsqlWorkspaceViewModel();
        var tab = new PlsqlEditorViewModel();
        tab.StartNew(PlsqlEditorCommandHandler.NewTemplate, null);
        workspace.Add(tab);
        var first = new PlsqlWorkspaceView { DataContext = workspace };
        var second = new PlsqlWorkspaceView { DataContext = workspace };
        var panel = new StackPanel();
        panel.Children.Add(first);
        panel.Children.Add(second);
        var window = new Window { Content = panel, Width = 800, Height = 600 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var editors = window.GetVisualDescendants().OfType<PlsqlEditorView>().ToList();
        Assert.Equal(2, editors.Count);
        Assert.NotSame(editors[0], editors[1]);

        // 떼었다 다시 붙여도(문서 옮기기) 예외 없이 자기 편집기를 보인다.
        panel.Children.Remove(first);
        Dispatcher.UIThread.RunJobs();
        panel.Children.Add(first);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, window.GetVisualDescendants().OfType<PlsqlEditorView>().Distinct().Count());
        window.Close();
    });

    // 렌더 점검(ORANDA_RENDER_DIR 이 있을 때만 PNG 를 남긴다): 탭 3개(고정 2·미리보기 1, 접속 섞임), 라이트·다크.
    [Fact]
    public Task Three_tabs_show_in_the_tab_strip_with_the_preview_in_italics() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        var docking = h.Get<DockWorkspace>();
        var router = PlsqlWorkspaceRouter.For(docking, h.Services);
        var body = await PlsqlEditorTests.OpenedPackageBody();
        var procedure = await PlsqlEditorTests.OpenedProcedure();
        var preview = new PlsqlEditorViewModel();
        preview.StartNew(PlsqlEditorCommandHandler.NewTemplate, new SchemaOpenTests.OpenFakeSession());
        try
        {
            router.Place(body);
            router.Place(procedure);
            router.Place(preview, preview: true);
            Dispatcher.UIThread.RunJobs();
            h.Window.UpdateLayout();

            var view = Assert.Single(h.Window.GetVisualDescendants().OfType<PlsqlWorkspaceView>());
            var titles = view.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Classes.Contains("tab-title")).ToList();
            Assert.Equal(3, titles.Count);
            Assert.Equal(Avalonia.Media.FontStyle.Italic, titles[2].FontStyle);
            Assert.Equal(Avalonia.Media.FontStyle.Normal, titles[0].FontStyle);
            Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), t => t.Classes.Contains("tab-conn") && t.IsEffectivelyVisible);
            Oranda.App.Tests.Features.MyBatisWorkspace.Flow.MyBatisFlowRig.Render(h.Window, "plsql-workspace-3tabs",
                (int)h.Window.Bounds.Width, (int)h.Window.Bounds.Height);
        }
        finally
        {
            PlsqlEditorViewModel.Active = null;
        }
    });

    [Fact]
    public void Ctrl_tab_walks_inner_tabs_and_stops_at_the_ends()
    {
        var workspace = new PlsqlWorkspaceViewModel();
        var tabs = Enumerable.Range(0, 3).Select(_ => new PlsqlEditorViewModel()).ToList();
        tabs.ForEach(t => workspace.Add(t));

        Assert.False(workspace.CycleInnerTab(1));
        Assert.True(workspace.CycleInnerTab(-1));
        Assert.Same(tabs[1], workspace.ActiveTab);
        workspace.SelectEdgeInnerTab(first: true);
        Assert.Same(tabs[0], workspace.ActiveTab);
        Assert.False(workspace.CycleInnerTab(-1));
    }
}

/// <summary>정적 활성 탭(<see cref="PlsqlEditorViewModel.Active"/>)·컴파일 오류 창·공유 문맥 키를 쓰는 시험 묶음: 섞여 돌면 서로의 활성 탭을 바꾼다.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PlsqlStatics
{
    public const string Name = "PlsqlStatics";
}

/// <summary>저장 확인에 정해진 순서로 답하고 물은 탭 이름을 남긴다. 답이 떨어지면 취소. 나머지 대화상자는 취소·거절.</summary>
internal sealed class AnswerDialogs(params SaveChoice[] answers) : IEditorDialogs
{
    private int _next;

    public List<string> Asked { get; } = [];

    public Task<string?> PickOpenFileAsync(CancellationToken ct) => Task.FromResult<string?>(null);

    public Task<string?> PickSaveFileAsync(string suggestedName, CancellationToken ct) => Task.FromResult<string?>(null);

    public Task<string?> PromptAsync(string title, string initial, CancellationToken ct) => Task.FromResult<string?>(null);

    public Task<string?> ChooseAsync(string title, IReadOnlyList<string> items, CancellationToken ct) => Task.FromResult<string?>(null);

    public Task<bool> ConfirmAsync(string message, CancellationToken ct) => Task.FromResult(false);

    public Task<bool> PreviewEditAsync(string title, string before, string after, CancellationToken ct) => Task.FromResult(false);

    public Task<SaveChoice> AskSaveChangesAsync(string tabName, CancellationToken ct)
    {
        Asked.Add(tabName);
        return Task.FromResult(_next < answers.Length ? answers[_next++] : SaveChoice.Cancel);
    }
}
