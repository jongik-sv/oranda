using System.Text.Json;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.ConnectionManager;
using Oranda.App.Features.DataTransfer;
using Oranda.App.Features.ExecutionPlan;
using Oranda.App.Features.GridEdit;
using Oranda.App.Features.ResultGrid;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell;
using Oranda.App.Shell.Docking;
using Oranda.App.Shell.Features;
using Oranda.App.Shell.Keymaps;
using Oranda.App.Shell.Palette;
using Oranda.App.Shell.Settings;
using Oranda.App.Tests.Features.BlockerAlert;
using Oranda.App.Tests.Features.DataTransfer;
using Oranda.App.Tests.Integration.Roles;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Composition;
using Oranda.Core.Events;
using Oranda.Core.Features;
using Oranda.Core.Sessions;
using Oranda.Core.Shell;
using Oranda.Core.Transfer;
using Xunit.Abstractions;

namespace Oranda.App.Tests.Integration.Ux;

/// <summary>
/// TSK-09-10 통합 2: 주 흐름을 <b>실제 FeatureGate</b>·설정 저장소·레지스트리·메인 창·키맵으로 끝까지 간다(PRD 13.5, TRD 11.12).
/// 새 설치 → 시작 화면 카드 → 「조회만」 → 메뉴 즉시 바뀜·커밋/롤백 숨김(미커밋이면 보임) → 「Excel 로 내보내기…」 → 하나로 합친 대화상자 → 저장 알림 →
/// 설정 「기능」 탭 「SQL 개발」 + MyBatis → 재시작 → MyBatis 문서 Ctrl+Enter → 레이아웃 초기화 → 아래 3탭 → 예상 계획 → 계획 창 → 「모든 기능」 → 원래 명령 집합.
/// 「나중에」 → R1 → 접속 0개 「접속 추가…」 → 첫 접속 SQL 탭, 묶이거나 메뉴에서 빠진 명령을 단축키·팔레트로 실행하는 길도 본다.
/// 실제 Oracle 은 쓰지 않는다(접속은 이벤트로, 결과는 메모리 결과 집합) — DB 경로는 Integration/Roles·TransferFlowTests 가 본다.
/// </summary>
[Trait("Category", "Integration")]
[Collection("ExcelExportStatics")]
public sealed class UxFlowTests(ITestOutputHelper output)
{
    private const string Commit = "oranda.txn.commit";
    private const string Rollback = "oranda.txn.rollback";

    /// <summary>기본 배치에서 SQL 탭과 함께 보이는 창(왼쪽 2 + 아래 3, D-019).</summary>
    private static readonly string[] DefaultVisible =
        ["oranda.panel.schemaBrowser", "oranda.panel.scripts", "oranda.panel.results", "oranda.panel.messages", "oranda.panel.output"];

    private static readonly (Key, KeyModifiers)[] CtrlEnter = [(Key.Enter, KeyModifiers.Control)];

    private static StartPage Page(RoleApp app) => app.Window.GetVisualDescendants().OfType<StartPage>().First();

    private static bool HasTop(RoleApp app, string id) => UxWidth.Top(app, id) is not null;

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static Button Choice(StartPage page, PrimaryWorkKind kind) =>
        page.GetVisualDescendants().OfType<Button>().Single(b => b.Tag is PrimaryWorkKind k && k == kind);

    private static string SettingsFile(string dir) => Path.Combine(dir, "settings.json");

    private static List<string> SqlMenuVisible(RoleApp app) => UxWidth.VisibleCommandIds(UxWidth.Top(app, "sql")!, UxWidth.MenuKeys(app));

    /// <summary>실제 명령(oranda.tools.roles)으로 설정 창을 「기능」 탭으로 연다.</summary>
    private static async Task<(SettingsWindow Window, SettingsViewModel Vm)> OpenFeaturesTabAsync(RoleApp app)
    {
        Assert.True(await app.Get<ICommandRegistry>().ExecuteAsync(FeatureCommandIds.Roles, app.Get<ContextKeyService>()));
        var window = Assert.IsType<SettingsWindow>(app.Get<ShellController>().LastWindow);
        var vm = Assert.IsType<SettingsViewModel>(window.DataContext);
        Assert.Equal(SettingsViewModel.FeaturesTabIndex, vm.SelectedTab);
        return (window, vm);
    }

    private static async Task<T> OwnedWindowAsync<T>(RoleApp app)
        where T : Window
    {
        await RoleApp.SettleAsync(() => app.Window.OwnedWindows.OfType<T>().Any(w => w.IsVisible));
        return app.Window.OwnedWindows.OfType<T>().Last(w => w.IsVisible);
    }

    private static string[]? SavedRoles(string dir)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(SettingsFile(dir)));
        return doc.RootElement.TryGetProperty("features", out var f) && f.TryGetProperty("roles", out var r) && r.ValueKind == JsonValueKind.Array
            ? [.. r.EnumerateArray().Select(e => e.GetString()!)]
            : null;
    }

    [Fact]
    public Task New_install_viewer_export_sql_dev_with_mybatis_layout_plan_and_back_to_all_features() => HeadlessHost.Run(async () =>
    {
        // 기준: 「모든 기능」 의 면(마지막 단계에서 같아야 한다).
        RoleSurface all;
        var allDir = RoleApp.NewDir();
        try
        {
            using var a = new RoleApp(allDir, PrdMatrix.FeaturesJson(null, allFeatures: true));
            await RoleApp.SettleAsync();
            all = a.Capture();
        }
        finally
        {
            RoleApp.DeleteDir(allDir);
        }

        var dir = RoleApp.NewDir();
        var exportDir = Path.Combine(Path.GetTempPath(), "oranda-uxit-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(exportDir);
        var opened = new List<string>();
        var oldOpen = ExportRunner.OpenFile;
        var oldFolder = ExcelExportPlan.LastFolder;
        var oldFormat = ExcelExportPlan.LastFormat;
        var oldOpenAfter = ExcelExportPlan.LastOpenAfter;
        var oldSource = DataTransferCommands.ActiveExportSource;
        var oldGrid = ResultGridCommands.Active;
        ExportRunner.OpenFile = p =>
        {
            opened.Add(p);
            return true;
        };
        ExcelExportPlan.LastFolder = exportDir;
        ExcelExportPlan.LastFormat = null;
        ExcelExportPlan.LastOpenAfter = null;
        try
        {
            using (var app = new RoleApp(dir, null))
            {
                await RoleApp.SettleAsync();
                var settings = app.Get<SettingsService>();
                var keys = app.Get<ContextKeyService>();
                var registry = app.Get<ICommandRegistry>();
                var page = Page(app);

                // ── 1. 새 설치: 카드, 아직 파일 없음, 기본 Role(R1) 메뉴 ──
                Assert.True(settings.IsFirstRun);
                Assert.True(page.ShowsFirstRunCard);
                Assert.False(File.Exists(SettingsFile(dir)));
                Assert.True(PrdMatrix.Enabled(["sqlDev"]).SetEquals(app.Gate.EnabledFeatures));
                Assert.True(HasTop(app, "tuning"));
                Assert.False(HasTop(app, "monitor"));

                // ── 2. 「조회만」 → 재시작 없이 튜닝·모니터 메뉴 없음, 커밋·롤백 숨김 ──
                Click(Choice(page, PrimaryWorkKind.Viewer));
                await RoleApp.SettleAsync(() => app.Gate.ReadOnlyRole && !page.ShowsFirstRunCard);
                await RoleApp.SettleAsync(() => !HasTop(app, "tuning"));
                Assert.False(HasTop(app, "monitor"));
                Assert.Equal([RoleIds.Viewer], SavedRoles(dir));
                Assert.Equal(9, app.Window.Menus.ItemsSource!.Cast<MenuItem>().Count());
                Assert.DoesNotContain(Commit, UxWidth.ShownToolbar(app));
                Assert.DoesNotContain(Rollback, UxWidth.ShownToolbar(app));
                Assert.DoesNotContain(Commit, SqlMenuVisible(app));
                Assert.DoesNotContain(Rollback, SqlMenuVisible(app));

                // 미커밋이 생기면(Role 을 바꾸기 전 DML 등) 툴바·SQL 메뉴에 끝낼 길이 보인다(D-022).
                keys.Set("txnPending", true);
                await RoleApp.SettleAsync(() => UxWidth.ShownToolbar(app).Contains(Commit));
                Assert.Contains(Rollback, UxWidth.ShownToolbar(app));
                Assert.Contains(Commit, SqlMenuVisible(app));
                Assert.Contains(Rollback, SqlMenuVisible(app));
                keys.Set("txnPending", false);
                await RoleApp.SettleAsync(() => !UxWidth.ShownToolbar(app).Contains(Commit));

                // ── 3. SELECT 결과 → 「Excel 로 내보내기…」 → 하나로 합친 대화상자(Excel 골라짐) → 저장 알림 ──
                await UxWidth.OpenSqlTabAsync(app, "select id, name, sal from emp");
                await using var grid = await ExportDialogTests.GridAsync(10);
                ResultGridCommands.Active = grid;
                DataTransferCommands.ActiveExportSource = () => null;
                keys.Set("gridFocus", true);
                keys.Set("gridHasResult", true);
                var gridMenu = UxWidth.GridContext(app);
                Assert.Contains(gridMenu, n => n.CommandId == "oranda.grid.export"); // 최상위 줄(하위 메뉴 안이 아니다)
                Assert.Contains(gridMenu, n => n.CommandId == "oranda.grid.exportExcel");
                Assert.True(UxWidth.Lines(gridMenu) <= UxWidth.WideLimit);

                SaveFeedback.Clear();
                var excelRun = registry.ExecuteAsync("oranda.grid.exportExcel", keys);
                var dialog = await OwnedWindowAsync<ExportDialogWindow>(app);
                var vm = Assert.IsType<ExportDialogViewModel>(dialog.DataContext);
                Assert.Equal(TransferFormat.Xlsx, vm.Format);
                Assert.True(vm.HasGrid);
                Assert.True(vm.OpenAfter); // Excel 은 내보낸 뒤 열기가 기본
                Assert.StartsWith(exportDir, vm.FilePath, StringComparison.OrdinalIgnoreCase); // 저장 위치가 미리 채워짐
                Assert.EndsWith(".xlsx", vm.FilePath, StringComparison.Ordinal);
                Assert.True(vm.StartCommand.CanExecute(null)); // 그대로 Enter
                var path = vm.FilePath;
                await vm.StartAsync();
                await RoleApp.SettleAsync(() => !dialog.IsVisible); // 끝나면 창이 닫힌다
                await excelRun;
                Assert.True(File.Exists(path));
                Assert.True(new FileInfo(path).Length > 0);
                Assert.Equal([path], opened);
                Assert.NotNull(SaveFeedback.Current); // 저장 알림(열기·폴더 열기)
                Assert.Contains("SaveStatusViewModel", app.Capture().StatusTypes); // 조회 전용에서도 알림 칸이 있다(TSK-07-09)

                // 「파일로 내보내기…」 도 같은 대화상자(마지막에 쓴 형식 = Excel).
                var fileRun = registry.ExecuteAsync("oranda.grid.export", keys);
                var again = await OwnedWindowAsync<ExportDialogWindow>(app);
                Assert.NotSame(dialog, again);
                Assert.Equal(TransferFormat.Xlsx, Assert.IsType<ExportDialogViewModel>(again.DataContext).Format);
                again.Close();
                await fileRun;
                ResultGridCommands.Active = oldGrid;
                keys.Set("gridFocus", false);
                keys.Set("gridHasResult", false);

                // ── 4. 설정 「기능」 탭: 「SQL 개발」 + MyBatis → 메뉴 즉시, MyBatis 시작 작업은 재시작 안내 ──
                var (window, svm) = await OpenFeaturesTabAsync(app);
                Assert.Equal(PrimaryWorkKind.Viewer, svm.Features.Primary);
                svm.Features.Primary = PrimaryWorkKind.SqlDev;
                svm.Features.MyBatisAddon = true;
                await svm.SaveAsync();
                await RoleApp.SettleAsync(() => !app.Gate.ReadOnlyRole);
                Assert.Equal([RoleIds.SqlDev, RoleIds.MyBatisDev], SavedRoles(dir));
                Assert.Contains(FeatureIds.MyBatis, app.Gate.PendingRestart);
                Assert.True(svm.Features.HasRestartBanner);
                window.Close();
                await RoleApp.SettleAsync(() => HasTop(app, "tuning"));
                Assert.Contains(Commit, UxWidth.ShownToolbar(app)); // 조회 전용이 풀려 커밋·롤백이 늘 보인다
                Assert.DoesNotContain("MapperSaveStatusViewModel", app.Capture().StatusTypes); // 이번 실행에서는 아직
            }

            SaveFeedback.Clear();

            // ── 5. 재시작: MyBatis 가 켜짐 → MyBatis 문서 Ctrl+Enter = 시나리오 실행, SQL 문서 Ctrl+Enter = 현재 문장 ──
            using (var app = new RoleApp(dir, null))
            {
                await RoleApp.SettleAsync();
                var settings = app.Get<SettingsService>();
                var keys = app.Get<ContextKeyService>();
                var registry = (CommandRegistry)app.Get<ICommandRegistry>();
                var keymap = app.Get<KeymapService>();
                var page = Page(app);
                Assert.False(settings.IsFirstRun);
                Assert.False(page.ShowsRoleLink); // 골랐으니 카드 없음
                Assert.Empty(app.Gate.PendingRestart);
                var surface = app.Capture();
                Assert.Contains("MapperWatchStartup", surface.StartupTasks);
                Assert.Contains("MapperSaveStatusViewModel", surface.StatusTypes);

                bool Available(string id) => !registry.IsFeatureDisabled(id);
                keys.SetFocusSlot("mybatisText");
                Assert.Equal("oranda.mybatis.runScenario", keymap.Table.Find(CtrlEnter, keys, Available).CommandId);
                Assert.Equal("oranda.mybatis.runScenario", keymap.Table.Find([(Key.F9, KeyModifiers.None)], keys, Available).CommandId);
                var dispatcher = new KeyDispatcher(() => keymap.Table, registry, keys);
                Assert.True(dispatcher.Handle(Key.Enter, KeyModifiers.Control)); // 키를 삼키고 시나리오 실행 명령을 부른다(열린 매퍼가 없으면 아무 일 없음)
                await dispatcher.LastRun!;
                keys.SetFocusSlot("sqlText");
                // macOS 키맵은 Ctrl+Enter 를 runForEdit 에 묶고 runStatement 는 Cmd+Enter 다(default.json 의 "mac").
                Assert.Equal(
                    OperatingSystem.IsMacOS() ? "oranda.exec.runForEdit" : "oranda.exec.runStatement",
                    keymap.Table.Find(CtrlEnter, keys, Available).CommandId);
                Assert.Equal("oranda.exec.runStatement", keymap.Table.Find([(Key.Enter, OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control)], keys, Available).CommandId);
                keys.SetFocusSlot(null);
                Assert.Equal("Ctrl+Enter", keymap.ShortcutText("oranda.mybatis.runScenario")?.Split(',')[0].Trim());

                // ── 6. 레이아웃 초기화 → 아래 3탭·오른쪽 0 ──
                await UxWidth.OpenSqlTabAsync(app);
                var ws = app.Get<DockWorkspace>();
                ws.ShowTool("oranda.panel.history");
                ws.ShowTool("oranda.panel.binds");
                ws.ShowTool("oranda.panel.templates");
                Dispatcher.UIThread.RunJobs();
                Assert.Contains("oranda.panel.binds", ws.VisibleToolIds);
                Assert.True(await registry.ExecuteAsync("oranda.view.resetLayout", keys));
                await RoleApp.SettleAsync();
                Assert.Equal(DefaultVisible.Order(), ws.VisibleToolIds.Order());
                Assert.NotNull(SqlEditorHost.Current.Active);

                // ── 7. 예상 계획 → 닫혀 있던 실행 계획 창이 아래에 열림 ──
                Assert.DoesNotContain("oranda.panel.plan", ws.VisibleToolIds);
                await new ExecutionPlanCommandHandler(app.Services).ExecuteAsync(new CommandContext("oranda.plan.explain", keys), default);
                Dispatcher.UIThread.RunJobs();
                Assert.Contains("oranda.panel.plan", ws.VisibleToolIds);

                // ── 8. 「모든 기능」 → 메뉴는 즉시(모니터·튜닝·PL/SQL), 시작 때 꺼져 있던 기능(PL/SQL 작업 공간)은 재시작 안내 ──
                var (window, svm) = await OpenFeaturesTabAsync(app);
                Assert.Equal(PrimaryWorkKind.SqlDev, svm.Features.Primary);
                Assert.True(svm.Features.MyBatisAddon);
                svm.Features.Primary = PrimaryWorkKind.All;
                await svm.SaveAsync();
                window.Close();
                await RoleApp.SettleAsync(() => HasTop(app, "monitor"));
                Assert.True(settings.Current.Features.AllFeatures);
                Assert.Null(SavedRoles(dir));
                Assert.Contains(FeatureIds.PlsqlWorkspace, app.Gate.PendingRestart);
                Assert.NotNull(registry.Find("oranda.plsql.compile"));
                Assert.Null(registry.Find("oranda.plsql.saveWorkspaceFile")); // 재시작 뒤에 돌아온다(RL-06)
            }

            // ── 9. 재시작 → 처음 「모든 기능」 과 같은 면(명령·메뉴·툴바·우클릭·팔레트·최상위 메뉴) ──
            using (var app = new RoleApp(dir, null))
            {
                await RoleApp.SettleAsync();
                var now = app.Capture();
                Assert.Empty(app.Gate.PendingRestart);
                Assert.True(all.RegistryCommands.SetEquals(now.RegistryCommands), string.Join(", ", all.RegistryCommands.Except(now.RegistryCommands).Concat(now.RegistryCommands.Except(all.RegistryCommands))));
                Assert.Equal(all.TopMenus, now.TopMenus);
                Assert.True(all.MenuCommands.SetEquals(now.MenuCommands));
                Assert.True(all.ToolbarCommands.SetEquals(now.ToolbarCommands));
                Assert.True(all.PaletteCommands.SetEquals(now.PaletteCommands));
                Assert.True(all.ContextCommands.SetEquals(now.ContextCommands));
                Assert.Equal(all.StartupTasks.Order(), now.StartupTasks.Order());
                Assert.Equal(all.StatusTypes.Order(), now.StatusTypes.Order());
                output.WriteLine($"모든 기능으로 되돌림(재시작 뒤): 명령 {now.RegistryCommands.Count}, 메뉴 {now.MenuCommands.Count}, 팔레트 {now.PaletteCommands.Count}, 최상위 {now.TopMenus.Count}, 시작 작업 {now.StartupTasks.Count}");
            }
        }
        finally
        {
            ExportRunner.OpenFile = oldOpen;
            ExcelExportPlan.LastFolder = oldFolder;
            ExcelExportPlan.LastFormat = oldFormat;
            ExcelExportPlan.LastOpenAfter = oldOpenAfter;
            DataTransferCommands.ActiveExportSource = oldSource;
            ResultGridCommands.Active = oldGrid;
            SaveFeedback.Clear();
            RoleApp.DeleteDir(dir);
            RoleApp.DeleteDir(exportDir);
        }
    });

    [Fact]
    public Task Later_keeps_sql_developer_and_add_connection_leads_to_a_sql_tab_after_the_first_connection() => HeadlessHost.Run(async () =>
    {
        var dir = RoleApp.NewDir();
        try
        {
            using (var app = new RoleApp(dir, null))
            {
                await RoleApp.SettleAsync();
                var page = Page(app);
                Assert.True(page.ShowsFirstRunCard);
                Click(page.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "나중에")));
                await RoleApp.SettleAsync(() => File.Exists(SettingsFile(dir)));
                Assert.False(page.ShowsFirstRunCard);
                Assert.True(page.ShowsDefaultRoleCard);
                Assert.True(FeatureCatalog.IsUnchosen(app.Get<SettingsService>().Current.Features));
                Assert.True(PrdMatrix.Enabled(["sqlDev"]).SetEquals(app.Gate.EnabledFeatures)); // 「나중에」 = R1(D-013)
            }

            using (var app = new RoleApp(dir, null))
            {
                await RoleApp.SettleAsync();
                var page = Page(app);
                Assert.False(app.Get<SettingsService>().IsFirstRun);
                Assert.False(page.ShowsFirstRunCard);
                Assert.True(page.ShowsDefaultRoleCard); // 다음 실행 = D-013 한 줄 카드
                Assert.Equal(10, (await UxWidth.MeasureAsync(app)).TopMenus);

                // 접속 0개 → 첫 줄 「접속 추가…」(강조) → 접속 관리자가 「새 접속」 상태로 열린다.
                await RoleApp.SettleAsync(() => page.ShowsAddConnection);
                var add = page.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b) == "접속 추가…");
                Assert.Contains("accent", add.Classes);
                Click(add);
                var manager = await OwnedWindowAsync<ConnectionManagerWindow>(app);
                var cm = Assert.IsType<ConnectionManagerViewModel>(manager.DataContext);
                await RoleApp.SettleAsync(() => cm.Editor is not null);
                Assert.True(cm.Editor!.IsNew);
                manager.Close();
                await RoleApp.SettleAsync();

                // 접속 성공(작업 세션) + 열린 문서 없음 → 그 접속으로 SQL 탭 하나.
                await app.Services.GetService<IEnumerable<IStartupTask>>()!.OfType<SqlEditorDockWiring>().Single().RunAsync(default);
                var docking = app.Get<DockWorkspace>();
                foreach (var d in docking.Documents.ToList())
                {
                    docking.CloseDocument(d);
                }

                await RoleApp.SettleAsync(() => docking.Documents.Count == 0);
                app.Get<IEventBus>().Publish(new SessionOpened(Guid.NewGuid(), BlockerTestData.Profile(), BlockerTestData.Server(BlockerTestData.NoPrivilege), SessionPurpose.Work));
                await RoleApp.SettleAsync(() => docking.Documents.Count == 1);
                Assert.Equal("oranda.panel.sqlEditor", docking.Documents.Single().Definition.Id);
            }
        }
        finally
        {
            RoleApp.DeleteDir(dir);
        }
    });

    [Fact]
    public Task Viewer_toolbar_follows_a_real_pending_transaction_event() => HeadlessHost.Run(async () =>
    {
        var dir = RoleApp.NewDir();
        try
        {
            using var app = new RoleApp(dir, PrdMatrix.FeaturesJson([RoleIds.Viewer]));
            await RoleApp.SettleAsync();
            var bus = app.Get<IEventBus>();
            var keys = app.Get<ContextKeyService>();
            Assert.Equal(13, UxWidth.ShownToolbar(app).Count);

            // 접속(작업 세션) → 미커밋 생김(실행 엔진이 내는 이벤트) → 커밋·롤백 단추가 나타나고, 끝나면 다시 숨는다.
            var session = Guid.NewGuid();
            bus.Publish(new SessionOpened(session, BlockerTestData.Profile(), BlockerTestData.Server(BlockerTestData.NoPrivilege), SessionPurpose.Work));
            await RoleApp.SettleAsync();
            bus.Publish(new TransactionStateChanged(session, true));
            await RoleApp.SettleAsync(() => keys.Get("txnPending"));
            await RoleApp.SettleAsync(() => UxWidth.ShownToolbar(app).Contains(Commit));
            Assert.Contains(Rollback, UxWidth.ShownToolbar(app));
            Assert.Equal(15, UxWidth.ShownToolbar(app).Count);

            bus.Publish(new TransactionStateChanged(session, false));
            await RoleApp.SettleAsync(() => !UxWidth.ShownToolbar(app).Contains(Commit));
            Assert.Equal(13, UxWidth.ShownToolbar(app).Count);
            bus.Publish(new SessionClosed(session));
        }
        finally
        {
            RoleApp.DeleteDir(dir);
        }
    });

    /// <summary>
    /// 하위 메뉴로 묶였거나(대소문자·줄·이동·복사 형식) 메뉴 자리를 뺀(보기 중복 7쌍) 명령도 단축키·팔레트로 그대로 실행된다(PRD 13.5 「명령 id·키맵·팔레트 그대로」).
    /// 팔레트에서 숨긴 것은 저장소 작업 공간 2줄뿐이다.
    /// </summary>
    [Theory]
    [InlineData("all-features")]
    [InlineData("none(default-R1)")]
    public Task Grouped_and_unplaced_commands_still_run_from_shortcuts_and_the_palette(string config) => HeadlessHost.Run(async () =>
    {
        var dir = RoleApp.NewDir();
        try
        {
            using var app = new RoleApp(dir, config == "all-features" ? PrdMatrix.FeaturesJson(null, allFeatures: true) : null);
            await RoleApp.SettleAsync();
            var registry = (CommandRegistry)app.Get<ICommandRegistry>();
            var keys = app.Get<ContextKeyService>();
            var keymap = app.Get<KeymapService>();
            var shell = app.Get<ShellController>();
            bool Available(string id) => !registry.IsFeatureDisabled(id);

            // 메뉴 자리를 뺀 보기 명령: 메뉴 모델에 없고 팔레트·레지스트리에는 있다.
            var bar = UxWidth.MenuBar(app);
            var menuIds = bar.SelectMany(b => Flatten(b.Children)).ToHashSet(StringComparer.Ordinal);
            string[] unplaced = ["oranda.view.outline", "oranda.view.output", "oranda.view.messages", "oranda.view.plan", "oranda.view.templates"];
            var palette = new CommandPaletteViewModel(registry, keys, shell.Text, keymap.ShortcutText);
            var paletteIds = palette.Search(string.Empty).Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var id in unplaced)
            {
                Assert.DoesNotContain(id, menuIds);
                Assert.Contains(id, paletteIds);
            }

            Assert.DoesNotContain("oranda.file.saveWorkspace", paletteIds);
            Assert.DoesNotContain("oranda.file.openWorkspace", paletteIds);
            Assert.NotNull(registry.Find("oranda.file.saveWorkspace")); // 명령 id 는 남는다
            Assert.Equal(registry.Commands.Count - 2, paletteIds.Count);

            // 하위 메뉴 안 명령의 단축키가 그대로 그 명령을 찾는다.
            var primary = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
            keys.SetFocusSlot("sqlText");
            Assert.Equal("oranda.edit.upperCase", keymap.Table.Find([(Key.U, primary | KeyModifiers.Shift)], keys, Available).CommandId);
            Assert.Equal("oranda.edit.toggleLineComment", keymap.Table.Find([(Key.Oem2, primary)], keys, Available).CommandId);
            Assert.Equal("oranda.nav.quickOpenObject", keymap.Table.Find([(Key.P, primary)], keys, Available).CommandId);
            keys.SetFocusSlot("sqlGrid");
            Assert.Equal("oranda.grid.copyWithHeaders", keymap.Table.Find([(Key.C, primary | KeyModifiers.Shift)], keys, Available).CommandId);
            keys.SetFocusSlot(null);
            Assert.Contains("oranda.edit.upperCase", paletteIds);
            Assert.Contains("oranda.grid.copyWithHeaders", paletteIds);

            // 실제 실행: Alt+4(macOS 는 Ctrl+4, 메뉴에서 뺀 「실행 계획」) → 닫혀 있던 실행 계획 창이 열린다.
            await UxWidth.OpenSqlTabAsync(app);
            var ws = app.Get<DockWorkspace>();
            Assert.DoesNotContain("oranda.panel.plan", ws.VisibleToolIds);
            var dispatcher = new KeyDispatcher(() => keymap.Table, registry, keys);
            Assert.True(dispatcher.Handle(Key.D4, OperatingSystem.IsMacOS() ? KeyModifiers.Control : KeyModifiers.Alt)); // macOS 키맵은 Ctrl+4
            await dispatcher.LastRun!;
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("oranda.panel.plan", ws.VisibleToolIds);

            // 팔레트로 「템플릿」(보기·도구 메뉴에서 뺌, 「도구 창 ▸」 토글만 남음) → 닫혀 있던 오른쪽 창이 열린다.
            Assert.DoesNotContain("oranda.panel.templates", ws.VisibleToolIds);
            palette.Query = shell.Text.Lookup("Cmd.oranda.view.templates");
            palette.Selected = palette.Results.First(r => r.Id == "oranda.view.templates");
            Assert.True(await palette.ExecuteSelectedAsync());
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("oranda.panel.templates", ws.VisibleToolIds);
        }
        finally
        {
            RoleApp.DeleteDir(dir);
        }
    });

    private static IEnumerable<string> Flatten(IReadOnlyList<Oranda.App.Shell.Menus.MenuNode> nodes) =>
        nodes.SelectMany(n => n.CommandId is { } id ? [id] : Flatten(n.Children));
}
