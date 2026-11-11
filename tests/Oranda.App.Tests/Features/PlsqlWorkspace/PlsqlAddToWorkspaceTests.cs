using Avalonia.Threading;
using Oranda.App.Features.PlsqlEditor;
using Oranda.App.Features.PlsqlWorkspace;
using Oranda.App.Shell.Docking;
using Oranda.App.Tests.Features.PlsqlEditor;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Metadata;
using Oranda.Core.Shell;

namespace Oranda.App.Tests.Features.PlsqlWorkspace;

/// <summary>「PL/SQL 작업 공간에 추가」(B-3 b2): 고정 탭으로 넣기, 포커스 유지, 패키지는 명세·본문 둘, 이미 열린 객체는 새 탭 없음.</summary>
[Collection(PlsqlStatics.Name)]
public sealed class PlsqlAddToWorkspaceTests
{
    private const string Command = "oranda.plsql.addToWorkspace";

    [Fact]
    public Task A_procedure_is_added_as_a_pinned_tab_and_its_workspace_becomes_the_active_document() => HeadlessHost.Run(async () =>
    {
        using var f = new Fixture();
        await f.AddAsync(new DbObjectRef("SCOTT", "P", "PROCEDURE"));

        var workspace = Assert.Single(f.Workspaces);
        var tab = Assert.Single(workspace.Tabs);
        Assert.Equal(new DbObjectRef("SCOTT", "P", "PROCEDURE"), tab.Target);
        Assert.False(tab.IsPreviewTab);
        Assert.Null(workspace.PreviewTab);
        Assert.False(workspace.IsTransient);
        Assert.StartsWith("CREATE OR REPLACE PROCEDURE", tab.Editor.Text, StringComparison.Ordinal);
        Assert.Same(workspace, f.H.Get<DockWorkspace>().ActiveDocument!.Panel); // 지금 동작: 활성 문서는 PL/SQL 작업 공간이 된다(키보드 포커스는 헤드리스에서 단정하지 않는다)
    });

    [Fact]
    public Task A_package_node_adds_one_tab_with_spec_and_body_and_shows_the_clicked_side() => HeadlessHost.Run(async () =>
    {
        using var f = new Fixture();

        await f.AddAsync(new DbObjectRef("SCOTT", "PK", "PACKAGE BODY"));

        var workspace = Assert.Single(f.Workspaces);
        var tab = Assert.Single(workspace.Tabs); // 명세와 본문은 한 탭(PKG1)
        Assert.Equal(PlsqlSide.Body, tab.Side);
        Assert.Equal("PACKAGE BODY", tab.Target!.ObjectType);
        Assert.True(tab.SpecPart!.Loaded);
        Assert.False(tab.IsPreviewTab);

        await f.AddAsync(new DbObjectRef("SCOTT", "PK", "PACKAGE")); // 이미 열려 있다: 새 탭 없이 명세 쪽을 보인다
        Assert.Same(tab, Assert.Single(workspace.Tabs));
        Assert.Equal(PlsqlSide.Spec, tab.Side);
    });

    [Fact]
    public Task A_package_without_a_body_opens_with_an_empty_body_side_and_the_create_body_hint() => HeadlessHost.Run(async () =>
    {
        using var f = new Fixture();
        f.Meta.FailTypes.Add("PACKAGE BODY");

        await f.AddAsync(new DbObjectRef("SCOTT", "PK", "PACKAGE"));

        var workspace = Assert.Single(f.Workspaces);
        var tab = Assert.Single(workspace.Tabs);
        Assert.Equal("PACKAGE", tab.Target!.ObjectType);
        Assert.True(tab.BodyPart!.Missing);
        Assert.False(tab.ShowCreateBodyHint); // 명세 쪽을 보는 동안은 안내가 없다
        Assert.True(await tab.SwitchSideAsync(PlsqlSide.Body));
        Assert.True(tab.ShowCreateBodyHint);
    });

    [Fact]
    public Task An_object_already_open_in_any_workspace_gets_no_new_tab_and_a_preview_becomes_pinned() => HeadlessHost.Run(async () =>
    {
        using var f = new Fixture();
        var target = new DbObjectRef("SCOTT", "P", "PROCEDURE");
        var router = PlsqlWorkspaceRouter.For(f.H.Get<DockWorkspace>(), f.H.Services);
        var preview = new PlsqlEditorViewModel(f.H.Services);
        router.Place(preview, preview: true);
        Assert.True(await preview.OpenAsync(f.Session, target));
        Assert.True(preview.IsPreviewTab);

        await f.AddAsync(target);
        await f.AddAsync(target);

        var workspace = Assert.Single(f.Workspaces);
        Assert.Same(preview, Assert.Single(workspace.Tabs));
        Assert.False(preview.IsPreviewTab);
        Assert.Null(workspace.PreviewTab);
    });

    [Fact]
    public Task The_other_half_of_an_open_package_shows_that_tab_instead_of_a_new_one() => HeadlessHost.Run(async () =>
    {
        using var f = new Fixture();
        var docking = f.H.Get<DockWorkspace>();
        var router = PlsqlWorkspaceRouter.For(docking, f.H.Services);
        var body = new PlsqlEditorViewModel(f.H.Services);
        var home = (PlsqlWorkspaceViewModel)router.Place(body)!.Panel!;
        Assert.True(await body.OpenAsync(f.Session, new DbObjectRef("SCOTT", "PK", "PACKAGE BODY")));
        var other = new PlsqlWorkspaceViewModel { Name = "다른" };
        docking.OpenDocument(PlsqlWorkspaceRouter.PanelId, other, other.Name);
        router.Activate(other, focus: false); // 활성 문서는 다른 작업 공간

        await f.AddAsync(new DbObjectRef("SCOTT", "PK", "PACKAGE"));

        Assert.Same(body, Assert.Single(home.Tabs));
        Assert.Equal(PlsqlSide.Spec, body.Side);
        Assert.Empty(other.Tabs);
    });

    [Fact]
    public Task Without_a_session_or_for_a_non_source_object_nothing_is_added() => HeadlessHost.Run(async () =>
    {
        using var f = new Fixture();

        await f.AddAsync(new DbObjectRef("SCOTT", "EMP", "TABLE"));
        Assert.Empty(f.Workspaces);

        f.Handler.SessionProvider = () => null;
        await f.AddAsync(new DbObjectRef("SCOTT", "P", "PROCEDURE"));
        Assert.Empty(f.Workspaces);
    });

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            H = new ShellHarness();
            Meta = new PlsqlEditorTests.FakeMeta { Source = "PROCEDURE p IS BEGIN\n  NULL;\nEND;\n" };
            Session = new PlsqlEditorTests.FakeSession();
            H.Services.AddInstance<IMetadataProvider>(Meta);
            Handler = new PlsqlEditorCommandHandler(H.Services) { SessionProvider = () => Session };
        }

        public ShellHarness H { get; }

        public PlsqlEditorTests.FakeMeta Meta { get; }

        public PlsqlEditorTests.FakeSession Session { get; }

        public PlsqlEditorCommandHandler Handler { get; }

        public IReadOnlyList<PlsqlWorkspaceViewModel> Workspaces =>
            H.Get<DockWorkspace>().Documents.Select(d => d.Panel).OfType<PlsqlWorkspaceViewModel>().ToList();

        public async Task AddAsync(DbObjectRef target)
        {
            await Handler.ExecuteAsync(new CommandContext(Command, H.Get<IContextKeys>(), target), default);
            Dispatcher.UIThread.RunJobs();
        }

        public void Dispose()
        {
            PlsqlEditorViewModel.Active = null;
            H.Dispose();
        }
    }
}
