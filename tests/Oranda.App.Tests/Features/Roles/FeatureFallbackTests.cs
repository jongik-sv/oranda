using Avalonia.VisualTree;
using Oranda.App.Features.DbTools;
using Oranda.App.Features.Navigation;
using Oranda.App.Features.PlsqlEditor;
using Oranda.App.Features.PlsqlWorkspace;
using Oranda.App.Features.SchemaBrowser;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell;
using Oranda.App.Shell.Docking;
using Oranda.App.Tests.Features.Navigation;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Features;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Core.Shell;
using Oranda.Testing;
using OpenFakeMeta = Oranda.App.Tests.Features.SchemaBrowser.SchemaOpenTests.OpenFakeMeta;
using OpenFakeSession = Oranda.App.Tests.Features.SchemaBrowser.SchemaOpenTests.OpenFakeSession;

namespace Oranda.App.Tests.Features.Roles;

/// <summary>
/// TSK-07-06 꺼진 기능 대체 경로: PL/SQL 끔 읽기 전용 소스 열기(D-007)·MyBatis 끔 매퍼 펼쳐 열기(파일 불변, D-008)·
/// 계획 창 고급 탭 숨김·화면 안 튜닝 단추·SQL 모니터 바로 가기·시작 화면 「Role 고르기…」(D-011).
/// 게이트는 <see cref="FakeFeatureGate"/> 로 넣는다(실제 게이트는 TSK-07-02).
/// </summary>
[Collection(Oranda.App.Tests.Features.PlsqlWorkspace.PlsqlStatics.Name)]
public sealed partial class FeatureFallbackTests
{
    private const string Notice = "PL/SQL 편집기 기능이 꺼져 있어 읽기 전용으로 열었습니다";

    // ---- PL/SQL 끔: 읽기 전용 소스 (D-007) ------------------------------------------------------

    [Fact]
    public Task Plsql_off_preview_opens_a_read_only_sql_tab_and_the_next_preview_reuses_it() => HeadlessHost.Run(async () =>
    {
        using var f = await PlsqlFixture.StartAsync(new FakeFeatureGate().Disable(FeatureIds.PlsqlEditor, FeatureIds.PlsqlWorkspace));

        f.Vm.SchedulePreview(await f.NodeAsync("PACKAGE BODY", "EMP_PKG"));
        var body = await f.WaitReadOnlyAsync("[읽기 전용] SCOTT.EMP_PKG BODY");
        Assert.True(body.IsReadOnly);
        Assert.False(body.IsDirty);
        Assert.Null(body.FilePath);
        Assert.Contains("pkg body", body.Text, StringComparison.Ordinal);
        Assert.Equal(f.Session.Profile.Id, body.ConnectionProfileId);
        Assert.DoesNotContain(f.Ws.Documents, d => d.Panel is PlsqlWorkspaceViewModel); // PL/SQL 작업 공간을 만들지 않는다
        Assert.Contains(f.Ws.Documents, d => d.Panel is SqlWorkspaceViewModel ws && ws.Tabs.Contains(body));
        Assert.Equal(Notice, f.Vm.StatusText);
        Assert.Same(body, f.Host.Active);

        // 다음 미리보기는 같은 탭을 그 자리에서 바꿔 쓴다(탭이 늘지 않는다).
        f.Vm.SchedulePreview(await f.NodeAsync("PROCEDURE", "PROC1"));
        var proc = await f.WaitReadOnlyAsync("[읽기 전용] SCOTT.PROC1");
        Assert.Same(body, proc);
        Assert.Contains("proc1 body", proc.Text, StringComparison.Ordinal);
        Assert.False(proc.IsDirty);
        Assert.Single(f.Host.Tabs, t => SchemaObjectOpener.ReadOnlySourceOf(t) is not null);
    });

    [Fact]
    public Task Plsql_off_pinned_open_keeps_the_tab_and_an_open_object_comes_to_front_without_a_new_read() => HeadlessHost.Run(async () =>
    {
        using var f = await PlsqlFixture.StartAsync(new FakeFeatureGate().Disable(FeatureIds.PlsqlEditor));
        var spec = await f.NodeAsync("PACKAGE", "EMP_PKG");
        Assert.True(await f.Vm.OpenPinnedAsync(spec));
        var pinned = await f.WaitReadOnlyAsync("[읽기 전용] SCOTT.EMP_PKG");
        var reads = f.Meta.SourceCalls;

        // 고정 탭은 다음 미리보기가 바꿔 쓰지 않는다: 새 탭이 생긴다.
        f.Vm.SchedulePreview(await f.NodeAsync("PROCEDURE", "PROC1"));
        var preview = await f.WaitReadOnlyAsync("[읽기 전용] SCOTT.PROC1");
        Assert.NotSame(pinned, preview);
        Assert.Contains("pkg spec", pinned.Text, StringComparison.Ordinal);

        // 이미 열린 객체는 다시 읽지 않고 그 탭을 앞으로 낸다.
        f.Host.Active = preview;
        Assert.True(await f.Vm.OpenPinnedAsync(spec));
        Assert.Same(pinned, f.Host.Active);
        Assert.Equal(reads + 1, f.Meta.SourceCalls); // PROC1 한 번만 더 읽었다
        Assert.Equal(2, f.Host.Tabs.Count(t => SchemaObjectOpener.ReadOnlySourceOf(t) is not null));

        // 미리보기를 고정으로 다시 열면 그 탭이 고정된다: 다음 미리보기는 또 새 탭.
        Assert.True(await f.Vm.OpenPinnedAsync(await f.NodeAsync("PROCEDURE", "PROC1")));
        f.Vm.SchedulePreview(await f.NodeAsync("PACKAGE BODY", "EMP_PKG"));
        await f.WaitReadOnlyAsync("[읽기 전용] SCOTT.EMP_PKG BODY");
        Assert.Equal(3, f.Host.Tabs.Count(t => SchemaObjectOpener.ReadOnlySourceOf(t) is not null));
        Assert.Contains("proc1 body", preview.Text, StringComparison.Ordinal);
    });

    [Fact]
    public Task Plsql_off_source_read_failure_opens_no_tab_and_says_why() => HeadlessHost.Run(async () =>
    {
        using var f = await PlsqlFixture.StartAsync(new FakeFeatureGate().Disable(FeatureIds.PlsqlEditor));
        var before = f.Host.Tabs.Count;

        Assert.False(await f.Vm.OpenPinnedAsync(await f.NodeAsync("PROCEDURE", "BROKEN")));

        Assert.Equal(before, f.Host.Tabs.Count);
        Assert.StartsWith("소스를 열지 못했다: ORA-04043", f.Vm.StatusText, StringComparison.Ordinal);
        Assert.DoesNotContain(f.Ws.Documents, d => d.Panel is PlsqlWorkspaceViewModel);
    });

    [Fact]
    public Task Plsql_on_keeps_the_plsql_workspace_and_turning_it_back_on_returns_to_it() => HeadlessHost.Run(async () =>
    {
        var gate = new FakeFeatureGate().Disable(FeatureIds.PlsqlEditor);
        using var f = await PlsqlFixture.StartAsync(gate);
        Assert.True(await f.Vm.OpenPinnedAsync(await f.NodeAsync("PROCEDURE", "PROC1")));
        await f.WaitReadOnlyAsync("[읽기 전용] SCOTT.PROC1");

        // 되돌림: 다시 켜면 PL/SQL 작업 공간 탭으로 연다(이미 열린 읽기 전용 탭은 그대로 둔다).
        gate.Enable(FeatureIds.PlsqlEditor).RaiseChanged();
        Assert.True(await f.Vm.OpenPinnedAsync(await f.NodeAsync("PACKAGE", "EMP_PKG")));
        var plsql = await f.WaitPlsqlAsync(p => p.Target?.ObjectType == "PACKAGE");
        await HeadlessHost.WaitUntil(() => plsql.Editor.Text.Contains("pkg spec", StringComparison.Ordinal));
        Assert.Contains(f.Ws.Documents, d => d.Panel is PlsqlWorkspaceViewModel);
        Assert.Single(f.Host.Tabs, t => SchemaObjectOpener.ReadOnlySourceOf(t) is not null);
        Assert.False(SchemaObjectOpener.IsPlsqlEditorOff(f.H.Services));
        Assert.False(SchemaObjectOpener.IsPlsqlEditorOff(null)); // 게이트 없음 = 켬
    });

    [Fact]
    public Task Plsql_off_open_source_command_quick_open_and_object_search_open_read_only_tabs() => HeadlessHost.Run(async () =>
    {
        using var f = await PlsqlFixture.StartAsync(new FakeFeatureGate().Disable(FeatureIds.PlsqlEditor));
        var body = new DbObjectRef("SCOTT", "EMP_PKG", "PACKAGE BODY");

        // oranda.plsql.openSource 처리기(꼬리표 공통, 서버 줄 번호로 커서 이동).
        var messages = new List<string>();
        var handler = new PlsqlEditorCommandHandler(f.H.Services) { SessionProvider = () => f.Session, Notify = messages.Add };
        await handler.ExecuteAsync(new CommandContext("oranda.plsql.openSource", AlwaysKeys.Instance, new PlsqlSourceLocation(body, 2)), default);
        var tab = await f.WaitReadOnlyAsync("[읽기 전용] SCOTT.EMP_PKG BODY");
        Assert.Equal(2, tab.CaretPosition.Line);
        Assert.Equal([Notice], messages);

        // 「PL/SQL 작업 공간에 추가」를 직접 불러도 PL/SQL 탭을 만들지 않는다(같은 탭을 앞으로).
        await handler.ExecuteAsync(new CommandContext("oranda.plsql.addToWorkspace", AlwaysKeys.Instance, body), default);
        Assert.Single(f.Host.Tabs, t => SchemaObjectOpener.ReadOnlySourceOf(t) is not null);

        // DB 객체로 이동(Quick Open).
        var nav = new ObjectNavigationCommandHandler(f.H.Services) { Notify = messages.Add };
        await nav.OpenAsync(new DbObjectRef("SCOTT", "PROC1", "PROCEDURE"), f.Session, default);
        await f.WaitReadOnlyAsync("[읽기 전용] SCOTT.PROC1");
        Assert.Equal(3, messages.Count); // 열기·작업 공간에 추가(앞으로 냄)·DB 객체로 이동

        // 객체 검색의 소스 결과 줄: 안내는 검색 창 상태 줄에.
        var search = new ObjectSearchViewModel(f.H.Services) { Session = f.Session };
        await search.OpenSourceHitAsync(new SourceSearchItem(new SourceSearchHit(new DbObjectRef("SCOTT", "EMP_PKG", "PACKAGE"), 1, "PACKAGE emp_pkg")));
        await f.WaitReadOnlyAsync("[읽기 전용] SCOTT.EMP_PKG");
        Assert.Equal(Notice, search.StatusText);
        Assert.DoesNotContain(f.Ws.Documents, d => d.Panel is PlsqlWorkspaceViewModel);
    });

    [Fact]
    public void Read_only_title_names_owner_object_and_body()
    {
        Assert.Equal("[읽기 전용] SCOTT.EMP_PKG", SchemaObjectOpener.ReadOnlyTitle(new DbObjectRef("SCOTT", "EMP_PKG", "PACKAGE")));
        Assert.Equal("[읽기 전용] SCOTT.T_TYPE BODY", SchemaObjectOpener.ReadOnlyTitle(new DbObjectRef("SCOTT", "T_TYPE", "TYPE BODY")));
        Assert.Throws<ArgumentNullException>(() => SchemaObjectOpener.ReadOnlyTitle(null!));
    }

    private sealed class AlwaysKeys : IContextKeys
    {
        public static readonly AlwaysKeys Instance = new();

        public bool Evaluate(string? expression) => true;
    }

    /// <summary>앱 전체(Headless)에 가짜 메타데이터·세션과 시험 게이트를 넣은 스키마 브라우저.</summary>
    private sealed class PlsqlFixture : IDisposable
    {
        private PlsqlFixture(ShellHarness h, SchemaBrowserViewModel vm)
        {
            H = h;
            Vm = vm;
            Ws = h.Get<IShellUi>().Docking;
        }

        public ShellHarness H { get; }

        public SchemaBrowserViewModel Vm { get; }

        public DockWorkspace Ws { get; }

        public SqlEditorHost Host { get; } = SqlEditorHost.Current;

        public CountingMeta Meta { get; } = new();

        public OpenFakeSession Session { get; } = new();

        public static async Task<PlsqlFixture> StartAsync(FakeFeatureGate gate)
        {
            var h = new ShellHarness();
            var view = h.Window.GetVisualDescendants().OfType<SchemaBrowserView>().First();
            var f = new PlsqlFixture(h, (SchemaBrowserViewModel)view.DataContext!);
            var sessions = new NavFakeSessions();
            sessions.Sessions.Add(f.Session);
            h.Services.ScanDeferred();
            h.Services.AddInstance<IMetadataProvider>(f.Meta);
            h.Services.AddInstance<ISessionManager>(sessions);
            h.Services.AddInstance<IFeatureGate>(gate);
            h.Window.Activate();
            await h.Services.GetService<IEnumerable<Oranda.Core.Composition.IStartupTask>>()!.OfType<SqlEditorDockWiring>().Single().RunAsync(default); // SQL 작업 공간 ↔ 문서
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            f.Vm.PreviewDelayMilliseconds = 0;
            await f.Vm.SetSessionAsync(f.Session);
            return f;
        }

        public async Task<SchemaNode> NodeAsync(string type, string name)
        {
            var category = Vm.Roots.Single(r => r.Title == "SCOTT").Children.Single(c => c.CategoryType == type);
            await Vm.ExpandAsync(category);
            category.IsExpandedSilently = true;
            return category.Children.Single(c => c.Ref?.Name == name);
        }

        public async Task<SqlEditorViewModel> WaitReadOnlyAsync(string title)
        {
            SqlEditorViewModel? found = null;
            await HeadlessHost.WaitUntil(() => (found = Host.Tabs.FirstOrDefault(t => t.Title == title && t.IsReadOnly)) is not null);
            return found!;
        }

        public async Task<PlsqlEditorViewModel> WaitPlsqlAsync(Func<PlsqlEditorViewModel, bool> match)
        {
            PlsqlEditorViewModel? found = null;
            await HeadlessHost.WaitUntil(() => (found = Ws.Documents.Select(d => d.Panel).OfType<PlsqlWorkspaceViewModel>()
                .SelectMany(w => w.Tabs).FirstOrDefault(match)) is not null);
            return found!;
        }

        public void Dispose() => H.Dispose();
    }

    /// <summary>표본 메타데이터에 소스 읽기 횟수를 센다.</summary>
    private sealed class CountingMeta : IMetadataProvider
    {
        private readonly OpenFakeMeta _inner = new();

        public int SourceCalls { get; private set; }

        public Task<IReadOnlyList<string>> ListSchemasAsync(ISession s, CancellationToken ct) => _inner.ListSchemasAsync(s, ct);

        public Task<IReadOnlyList<DbObjectRef>> ListObjectsAsync(ISession s, ObjectQuery q, CancellationToken ct) => _inner.ListObjectsAsync(s, q, ct);

        public Task<TableInfo> GetTableAsync(ISession s, DbObjectRef table, CancellationToken ct) => _inner.GetTableAsync(s, table, ct);

        public Task<IReadOnlyList<ProcedureInfo>> GetProceduresAsync(ISession s, DbObjectRef obj, CancellationToken ct) => _inner.GetProceduresAsync(s, obj, ct);

        public Task<string> GetDdlAsync(ISession s, DbObjectRef obj, DdlOptions o, CancellationToken ct) => _inner.GetDdlAsync(s, obj, o, ct);

        public Task<Oranda.Core.Metadata.ObjectDetail> GetObjectDetailAsync(ISession s, DbObjectRef obj, CancellationToken ct) => _inner.GetObjectDetailAsync(s, obj, ct);

        public Task<string> GetSourceAsync(ISession s, DbObjectRef obj, CancellationToken ct)
        {
            SourceCalls++;
            return _inner.GetSourceAsync(s, obj, ct);
        }

        public Task<IReadOnlyList<CompileError>> GetCompileErrorsAsync(ISession s, DbObjectRef obj, CancellationToken ct) => _inner.GetCompileErrorsAsync(s, obj, ct);
    }
}
