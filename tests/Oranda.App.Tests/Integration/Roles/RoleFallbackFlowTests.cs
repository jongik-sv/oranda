using System.Security.Cryptography;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.BlockerAlert;
using Oranda.App.Features.GridEdit;
using Oranda.App.Features.MyBatisWorkspace.Hooks;
using Oranda.App.Features.PlsqlEditor;
using Oranda.App.Features.PlsqlWorkspace;
using Oranda.App.Features.SchemaBrowser;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell;
using Oranda.App.Shell.Docking;
using Oranda.App.Shell.Features;
using Oranda.App.Shell.Settings;
using Oranda.App.Tests.Features.Navigation;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Composition;
using Oranda.Core.Features;
using Oranda.Core.Metadata;
using Oranda.Core.Settings;
using Oranda.Core.Shell;
using Oranda.Testing.MyBatis;
using OpenFakeMeta = Oranda.App.Tests.Features.SchemaBrowser.SchemaOpenTests.OpenFakeMeta;
using OpenFakeSession = Oranda.App.Tests.Features.SchemaBrowser.SchemaOpenTests.OpenFakeSession;

namespace Oranda.App.Tests.Integration.Roles;

/// <summary>
/// TSK-07-08 통합 3: 꺼진 기능의 대체 경로를 <b>실제 FeatureGate</b> 로 다시 본다(TSK-07-06 은 시험 게이트를 컨테이너에 갈아 끼워 확인했다 — followups 「실게이트로 재확인」).
/// 설정 파일의 Role → 앱 조립 → 실제 시작 작업 실행 → 대체 경로. DB 는 가짜 메타데이터·세션(대체 경로는 서버 소스를 읽기만 한다).
/// </summary>
[Trait("Category", "Integration")]
[Collection(Oranda.App.Tests.Features.PlsqlWorkspace.PlsqlStatics.Name)]
public sealed class RoleFallbackFlowTests
{
    private const string PlsqlOffNotice = "PL/SQL 편집기 기능이 꺼져 있어 읽기 전용으로 열었습니다";
    private const string MyBatisOffNotice = "MyBatis 기능이 꺼져 있어 매퍼를 일반 SQL 탭으로 펼쳐 열었습니다(매퍼 파일에는 저장하지 않습니다)";

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    /// <summary>앱 전체 + 스키마 브라우저에 가짜 메타데이터·세션을 넣은 한 벌. 게이트는 건드리지 않는다(설정 파일로만 정한다).</summary>
    private sealed class BrowserRig : IDisposable
    {
        private readonly string _dir;

        private BrowserRig(string dir, RoleApp app, SchemaBrowserViewModel vm)
        {
            _dir = dir;
            App = app;
            Vm = vm;
        }

        public RoleApp App { get; }

        public SchemaBrowserViewModel Vm { get; }

        public DockWorkspace Ws => App.Get<IShellUi>().Docking;

        public SqlEditorHost Host { get; } = SqlEditorHost.Current;

        public OpenFakeSession Session { get; } = new();

        public static async Task<BrowserRig> StartAsync(string featuresJson)
        {
            var dir = RoleApp.NewDir();
            var app = new RoleApp(dir, featuresJson);
            var view = app.Window.GetVisualDescendants().OfType<SchemaBrowserView>().First();
            var rig = new BrowserRig(dir, app, (SchemaBrowserViewModel)view.DataContext!);
            var sessions = new NavFakeSessions();
            sessions.Sessions.Add(rig.Session);
            app.Services.ScanDeferred();
            app.Services.AddInstance<IMetadataProvider>(new OpenFakeMeta());
            app.Services.AddInstance<Oranda.Core.Sessions.ISessionManager>(sessions);
            app.Window.Activate();
            await app.Services.GetService<IEnumerable<IStartupTask>>()!.OfType<SqlEditorDockWiring>().Single().RunAsync(default); // SQL 작업 공간 ↔ 문서
            Dispatcher.UIThread.RunJobs();
            rig.Vm.PreviewDelayMilliseconds = 0;
            await rig.Vm.SetSessionAsync(rig.Session);
            return rig;
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

        public void Dispose()
        {
            App.Dispose();
            RoleApp.DeleteDir(_dir);
        }
    }

    // ── PL/SQL 끔 ──────────────────────────────────────────────────

    [Fact]
    public Task Plsql_off_by_role_opens_the_package_from_the_schema_browser_as_a_read_only_sql_tab() => HeadlessHost.Run(async () =>
    {
        // R1(SQL 개발자)은 plsqlEditor 가 꺼져 있다: 설정 파일 → 실제 게이트 → 스키마 브라우저.
        using var rig = await BrowserRig.StartAsync(PrdMatrix.FeaturesJson(["sqlDev"]));
        Assert.True(rig.App.Gate.IsEnabled(FeatureIds.PlsqlEditor) == false);
        Assert.True(SchemaObjectOpener.IsPlsqlEditorOff(rig.App.Services));

        rig.Vm.SchedulePreview(await rig.NodeAsync("PACKAGE BODY", "EMP_PKG"));
        var tab = await rig.WaitReadOnlyAsync("[읽기 전용] SCOTT.EMP_PKG BODY");

        Assert.True(tab.IsReadOnly);
        Assert.False(tab.IsDirty);
        Assert.Null(tab.FilePath);
        Assert.Contains("pkg body", tab.Text, StringComparison.Ordinal);
        Assert.Equal(PlsqlOffNotice, rig.Vm.StatusText);
        Assert.DoesNotContain(rig.Ws.Documents, d => d.Panel is PlsqlWorkspaceViewModel); // PL/SQL 작업 공간 문서를 만들지 않는다
        Assert.Contains(rig.Ws.Documents, d => d.Panel is SqlWorkspaceViewModel ws && ws.Tabs.Contains(tab));
        Assert.Same(tab, rig.Host.Active);
    });

    [Fact]
    public Task Plsql_on_by_role_keeps_opening_the_package_in_the_plsql_workspace() => HeadlessHost.Run(async () =>
    {
        using var rig = await BrowserRig.StartAsync(PrdMatrix.FeaturesJson(["plsqlDev"]));
        Assert.False(SchemaObjectOpener.IsPlsqlEditorOff(rig.App.Services));

        Assert.True(await rig.Vm.OpenPinnedAsync(await rig.NodeAsync("PACKAGE", "EMP_PKG")));
        PlsqlEditorViewModel? plsql = null;
        await HeadlessHost.WaitUntil(() => (plsql = rig.Ws.Documents.Select(d => d.Panel).OfType<PlsqlWorkspaceViewModel>()
            .SelectMany(w => w.Tabs).FirstOrDefault(p => p.Target?.ObjectType == "PACKAGE")) is not null);

        Assert.NotNull(plsql);
        Assert.DoesNotContain(rig.Host.Tabs, t => SchemaObjectOpener.ReadOnlySourceOf(t) is not null);
    });

    [Fact]
    public Task Plsql_off_then_on_again_in_the_settings_returns_to_the_plsql_workspace_without_restart() => HeadlessHost.Run(async () =>
    {
        // plsqlEditor 는 시작 때 도는 기능이 아니다(StartupBound 아님): 설정에서 켜면 바로 PL/SQL 작업 공간으로 열린다.
        using var rig = await BrowserRig.StartAsync(PrdMatrix.FeaturesJson(["sqlDev"]));
        Assert.True(await rig.Vm.OpenPinnedAsync(await rig.NodeAsync("PROCEDURE", "PROC1")));
        await rig.WaitReadOnlyAsync("[읽기 전용] SCOTT.PROC1");

        await rig.App.Get<SettingsService>().UpdateAsync(s => s with { Features = new FeaturesSettings { Roles = [RoleIds.SqlDev, RoleIds.PlsqlDev] } });
        await HeadlessHost.WaitUntil(() => rig.App.Gate.IsEnabled(FeatureIds.PlsqlEditor));
        Assert.False(SchemaObjectOpener.IsPlsqlEditorOff(rig.App.Services));

        Assert.True(await rig.Vm.OpenPinnedAsync(await rig.NodeAsync("PACKAGE", "EMP_PKG")));
        await HeadlessHost.WaitUntil(() => rig.Ws.Documents.Select(d => d.Panel).OfType<PlsqlWorkspaceViewModel>().Any(w => w.Tabs.Any(p => p.Target?.ObjectType == "PACKAGE")));
    });

    // ── PL/SQL 작업 공간: 복원·자동 저장은 plsqlWorkspace 만 (TRD 10.7.3) ──

    [Theory]
    [InlineData("plsqlDev", true)] // R2: PL/SQL 편집기 + 작업 공간 → 복원·자동 저장 켬
    [InlineData("tuner", false)] // R4: PL/SQL 편집기만 → 라우터는 있고 복원·자동 저장·종료 저장은 없다
    [InlineData("dba", false)]
    public Task Plsql_workspace_restore_and_autosave_follow_the_role_through_the_real_gate(string role, bool restores) => HeadlessHost.Run(async () =>
    {
        var dir = RoleApp.NewDir();
        try
        {
            // 앞 실행이 남긴 PL/SQL 작업 공간 하나
            var store = new PlsqlWorkspaceStore(Path.Combine(dir, "plsql-workspaces"), Path.Combine(dir, "backup", "plsql-workspaces"));
            await store.SaveAsync(new PlsqlWorkspaceFile { Id = "a1", Name = "PL/SQL 1", Tabs = [new PlsqlTabFile { Title = "새 PL/SQL", Text = "-- mine" }] });
            await store.SaveIndexAsync(new PlsqlWorkspaceIndex { OpenIds = ["a1"], LastId = "a1" });
            var indexBefore = await File.ReadAllTextAsync(Path.Combine(dir, "plsql-workspaces", "open.json"));

            using (var app = new RoleApp(dir, PrdMatrix.FeaturesJson([role])))
            {
                var persistence = Assert.Single(app.Services.GetStartupTasks().OfType<PlsqlWorkspacePersistence>());
                await persistence.RunAsync(CancellationToken.None);
                await HeadlessHost.WaitUntil(() => persistence.Persisting is not null);

                var router = PlsqlWorkspaceRouter.Of(app.Services);
                Assert.NotNull(router); // PL/SQL 편집 탭이 열리는 자리는 늘 있다
                Assert.Equal(restores, persistence.Persisting);
                Assert.Equal(restores, persistence.AutosaveRunning);
                if (restores)
                {
                    await HeadlessHost.WaitUntil(() => router!.Workspaces.Count == 1);
                    Assert.Equal("a1", router!.Workspaces[0].Id);
                }
                else
                {
                    Dispatcher.UIThread.RunJobs();
                    Assert.Empty(router!.Workspaces);
                }
            }

            // 끔이면 종료 저장도 없어 앞 실행의 목록이 그대로 남는다(작업 공간을 다시 켜면 되살아난다).
            if (!restores)
            {
                Assert.Equal(indexBefore, await File.ReadAllTextAsync(Path.Combine(dir, "plsql-workspaces", "open.json")));
                Assert.True(File.Exists(Path.Combine(dir, "plsql-workspaces", "a1.json")));
            }
        }
        finally
        {
            PlsqlEditorViewModel.Active = null;
            RoleApp.DeleteDir(dir);
        }
    });

    // ── MyBatis 끔 ─────────────────────────────────────────────────

    private sealed class TempMapper : IDisposable
    {
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "oranda-roleit-mapper-" + Guid.NewGuid().ToString("N"));

        public TempMapper(string text)
        {
            Directory.CreateDirectory(Dir);
            Path_ = System.IO.Path.Combine(Dir, "EmpMapper.xml");
            File.WriteAllText(Path_, text);
        }

        public string Path_ { get; }

        public void Dispose() => RoleApp.DeleteDir(Dir);
    }

    /// <summary>앱이 시작 때 하는 대로 시작 작업을 돌린다(꺼진 기능의 작업은 건너뛴다).</summary>
    private static async Task RunStartupAsync(RoleApp app)
    {
        var run = new Oranda.App.App.StartupRun();
        await Oranda.App.App.RunStartupTasksAsync(app.Services, run, StartupPhase.AfterFirstFrame);
        await Oranda.App.App.RunStartupTasksAsync(app.Services, run, null);
    }

    [Fact]
    public Task Mybatis_off_by_role_opens_a_mapper_as_sql_tabs_and_never_writes_the_mapper_file() => HeadlessHost.Run(async () =>
    {
        var dir = RoleApp.NewDir();
        using var mapper = new TempMapper(MapperSamples.EmpMapper);
        try
        {
            using var app = new RoleApp(dir, PrdMatrix.FeaturesJson(["sqlDev"]));
            await RunStartupAsync(app);
            var host = SqlEditorHost.Current;
            Assert.Same(MyBatisHooks.Empty, host.MyBatisHooks); // 배선(MyBatisWorkspaceWiring)이 돌지 않았다
            var before = Hash(mapper.Path_);
            SaveFeedback.Clear();

            var opened = await host.OpenFileAsync(mapper.Path_, default);

            Assert.NotNull(opened);
            var ws = Assert.Single(host.Workspaces, w => w.SourcePath == Path.GetFullPath(mapper.Path_));
            Assert.Equal(SqlWorkspaceKind.Sql, ws.DocumentKind);
            Assert.True(ws.Tabs.Count > 1);
            Assert.All(ws.Tabs, t => Assert.Null(t.FilePath));
            Assert.Contains(ws.Tabs, t => t.Text.Contains(":empNo", StringComparison.Ordinal) && !t.Text.Contains("#{", StringComparison.Ordinal));
            Assert.Equal(MyBatisOffNotice, SaveFeedback.Current?.Text);

            // 탭을 고쳐도 매퍼 파일은 그대로다(탭에는 파일 경로가 없고, 저장은 새 .sql/.md 로만 간다 — 저장 흐름은 FeatureFallbackTests 가 대화상자 대역으로 본다).
            ws.Tabs[0].Document.Insert(0, "-- 고침\n");
            Assert.Equal(before, Hash(mapper.Path_));
            Assert.Equal(["EmpMapper.xml"], Directory.GetFiles(mapper.Dir).Select(Path.GetFileName).ToArray());
        }
        finally
        {
            RoleApp.DeleteDir(dir);
        }
    });

    [Fact]
    public Task Mybatis_on_by_role_wires_the_hooks_and_opens_the_same_mapper_as_a_mybatis_document() => HeadlessHost.Run(async () =>
    {
        var dir = RoleApp.NewDir();
        using var mapper = new TempMapper(MapperSamples.EmpMapper);
        try
        {
            using var app = new RoleApp(dir, PrdMatrix.FeaturesJson(["myBatisDev"]));
            await RunStartupAsync(app);
            var host = SqlEditorHost.Current;
            Assert.NotSame(MyBatisHooks.Empty, host.MyBatisHooks); // 배선이 돌았다
            SaveFeedback.Clear();

            await host.OpenFileAsync(mapper.Path_, default);

            var ws = Assert.Single(host.Workspaces, w => w.SourcePath == Path.GetFullPath(mapper.Path_));
            Assert.Equal(SqlWorkspaceKind.MyBatis, ws.DocumentKind);
            Assert.Null(SaveFeedback.Current); // 「꺼져 있어…」 안내는 MyBatis 가 켜졌을 때 나오지 않는다
        }
        finally
        {
            RoleApp.DeleteDir(dir);
        }
    });

    [Fact]
    public Task Broken_mapper_cannot_be_opened_the_same_way_with_mybatis_on_or_off() => HeadlessHost.Run(async () =>
    {
        // followups(07-06): MyBatis 끔 + 깨진 매퍼 xml = 열 수 없음(오류 알림만). 켠 쪽도 같은지 견준다.
        using var mapper = new TempMapper("<mapper namespace=\"x\"><select id=\"a\">");
        var results = new List<(string Role, int Tabs, int Workspaces)>();
        foreach (var (roleName, roles) in new[] { ("R1 (MyBatis 끔)", "sqlDev"), ("R3 (MyBatis 켬)", "myBatisDev") })
        {
            var dir = RoleApp.NewDir();
            try
            {
                using var app = new RoleApp(dir, PrdMatrix.FeaturesJson([roles]));
                await RunStartupAsync(app);
                var host = SqlEditorHost.Current;
                var before = (host.Tabs.Count, host.Workspaces.Count);

                await host.OpenFileAsync(mapper.Path_, default);

                results.Add((roleName, host.Tabs.Count - before.Item1, host.Workspaces.Count - before.Item2));
                Assert.DoesNotContain(host.Tabs, t => t.FilePath == Path.GetFullPath(mapper.Path_)); // 글자 탭으로 열려 저장이 매퍼를 덮어쓰는 일은 없다
            }
            finally
            {
                RoleApp.DeleteDir(dir);
            }
        }

        Assert.Equal(results[0].Tabs, results[1].Tabs);
        Assert.Equal(results[0].Workspaces, results[1].Workspaces);
    });

    // ── Lock 모니터 끔: 블로커 칸 ──────────────────────────────────

    [Fact]
    public Task Lock_monitor_off_by_role_turns_the_blocker_chip_click_into_a_notice_and_adding_r4_brings_the_monitor_back() => HeadlessHost.Run(async () =>
    {
        var dir = RoleApp.NewDir();
        var runs = new List<string>();
        var oldRunner = ShellCommandRunner.Runner;
        try
        {
            using var app = new RoleApp(dir, PrdMatrix.FeaturesJson(["sqlDev"])); // R1: 블로커 알림 켬, Lock 모니터 꺼짐
            var chip = app.BlockerChip();
            Assert.NotNull(chip);
            Assert.False(chip.LockMonitorEnabled);
            var asked = 0;
            chip.LockMonitorOffRequested += (_, _) => asked++;
            var shell = app.Get<ShellController>();

            // 칸을 누르면 Lock 모니터 대신 안내(플라이아웃)를 청한다.
            await chip.OnClickAsync();
            Assert.Equal(1, asked);
            Assert.Equal("Lock 모니터 기능이 꺼져 있습니다. 설정 > 기능에서 켤 수 있습니다.", BlockerStatusViewModel.LockMonitorOffMessage);
            Assert.Equal("설정 열기", BlockerStatusViewModel.OpenSettingsText);

            // 「설정 열기」 = 실제 oranda.tools.roles → 설정 창이 「기능」 탭으로 열린다.
            await BlockerStatusViewModel.OpenFeatureSettingsAsync();
            var window = Assert.IsType<SettingsWindow>(shell.LastWindow);
            var vm = Assert.IsType<SettingsViewModel>(window.DataContext);
            Assert.Equal(SettingsViewModel.FeaturesTabIndex, vm.SelectedTab);

            // 주 업무를 튜닝(R4)으로 바꾸면 칸 클릭이 Lock 모니터를 연다(메뉴처럼 즉시, 칸은 그대로 — 블로커 알림은 시작 때 켜져 있었다).
            vm.Features.Primary = PrimaryWorkKind.Tuner;

            await vm.SaveAsync();
            await HeadlessHost.WaitUntil(() => chip.LockMonitorEnabled);
            ShellCommandRunner.Runner = id =>
            {
                runs.Add(id);
                return Task.CompletedTask;
            };
            await chip.OnClickAsync();
            Assert.Equal(1, asked);
            Assert.Equal(["oranda.monitor.locks"], runs);
            window.Close();
        }
        finally
        {
            ShellCommandRunner.Runner = oldRunner;
            RoleApp.DeleteDir(dir);
        }
    });
}
