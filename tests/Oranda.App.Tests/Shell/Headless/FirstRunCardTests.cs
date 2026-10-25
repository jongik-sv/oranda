using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Shell;
using Oranda.App.Shell.Docking;
using Oranda.App.Shell.Features;
using Oranda.App.Shell.Settings;
using Oranda.App.Tests.Features.BlockerAlert;
using Oranda.App.Tests.Features.Update;
using Oranda.App.Tests.Features.MyBatisWorkspace.Flow;
using Oranda.App.Tests.Integration.Roles;
using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Features;
using Oranda.Core.Sessions;
using Oranda.Core.Shell;
using Oranda.Core.Storage;
using Oranda.Testing;

namespace Oranda.App.Tests.Shell.Headless;

/// <summary>
/// TSK-09-09 새 설치 시작 화면 「주로 하는 일은?」 카드(D-018)와 첫 접속 흐름(UX-13): 새 설치 판정(<see cref="SettingsService.IsFirstRun"/>),
/// 카드 단추 → 저장·즉시 반영, 「나중에」 → 파일 생김·다음 실행은 D-013 한 줄 카드, 새 설치 아님 → 카드 없음, 프로필 0개 → 「접속 추가…」 강조,
/// 접속 → 열린 문서가 없을 때만 SQL 탭 1개(<see cref="FirstConnectEditor"/>), ko/en·라이트/다크·좁은 창 렌더.
/// <c>ORANDA_RENDER_DIR</c> 가 있으면 PNG 를 그 폴더에 쓴다.
/// </summary>
public sealed class FirstRunCardTests
{
    private static StartPage Page(RoleApp app) => app.Window.GetVisualDescendants().OfType<StartPage>().First();

    private static bool HasTop(RoleApp app, string id) =>
        app.Capture().TopMenus.Contains(app.Get<ShellController>().Text.Lookup($"Menu.{id}"));

    private static List<Button> ChoiceButtons(StartPage page) =>
        [.. page.GetVisualDescendants().OfType<Button>().Where(b => b.Tag is PrimaryWorkKind)];

    private static Button Choice(StartPage page, PrimaryWorkKind kind) => ChoiceButtons(page).Single(b => (PrimaryWorkKind)b.Tag! == kind);

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static string SettingsFile(string dir) => Path.Combine(dir, "settings.json");

    // ── 새 설치 → 카드 ────────────────────────────────────────────

    [Fact]
    public Task A_new_install_shows_the_card_on_top_with_five_choices_two_addons_and_later() => HeadlessHost.Run(async () =>
    {
        var dir = RoleApp.NewDir();
        try
        {
            using var app = new RoleApp(dir, null);
            await RoleApp.SettleAsync();
            var settings = app.Get<SettingsService>();
            var page = Page(app);

            Assert.True(settings.IsFirstRun);
            Assert.True(page.ShowsFirstRunCard);
            Assert.False(page.ShowsDefaultRoleCard); // 새 설치에서는 한 줄 카드 대신 이 카드
            Assert.True(page.ShowsRoleLink);
            Assert.False(File.Exists(SettingsFile(dir))); // 보이기만 하고 아직 쓰지 않는다
            Assert.Empty(app.Window.OwnedWindows); // 모달이 아니다

            var card = page.GetVisualDescendants().OfType<Border>().Single(b => AutomationProperties.GetName(b) == "주로 하는 일은?");
            Assert.True(card.IsEffectivelyVisible);
            var buttons = ChoiceButtons(page);
            Assert.Equal(PrimaryWork.Choices, buttons.Select(b => (PrimaryWorkKind)b.Tag!));
            Assert.Equal(["모든 기능 - 튜닝·모니터·PL/SQL·MyBatis 까지 모든 메뉴", "SQL 개발 - 쿼리 작성·실행, 결과 편집, 내보내기"],
                buttons.Take(2).Select(AutomationProperties.GetName));
            Assert.All(buttons, b => Assert.True(b.IsEffectivelyVisible && b.Focusable && b.IsTabStop)); // 키보드로 닿는다
            var addons = card.GetVisualDescendants().OfType<CheckBox>().ToList();
            Assert.Equal(["PL/SQL 패키지·프로시저도 고친다", "MyBatis 매퍼를 쓴다"], addons.Select(AutomationProperties.GetName));
            Assert.All(addons, c => Assert.False(c.IsChecked));
            var later = card.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "나중에"));
            Assert.StartsWith("나중에 — SQL 개발 메뉴로 시작합니다.", AutomationProperties.GetName(later));
            Assert.Contains(page.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "함께 하는 일은 SQL 개발·튜닝·운영 DBA 에 붙습니다." && t.IsEffectivelyVisible);

            // 카드는 목록 위(맨 위)에 있다.
            var cardTop = card.TranslatePoint(default, page)!.Value.Y;
            var listTop = page.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("start-item") && b.IsEffectivelyVisible).TranslatePoint(default, page)!.Value.Y;
            Assert.True(cardTop < listTop, $"{cardTop} < {listTop}");

            MyBatisFlowRig.Render(app.Window, "start-first-run-ko", (int)app.Window.Bounds.Width, (int)app.Window.Bounds.Height);
        }
        finally
        {
            RoleApp.DeleteDir(dir);
        }
    });

    [Fact]
    public Task Choosing_read_only_saves_the_role_and_drops_the_monitor_and_tuning_menus_without_a_restart() => HeadlessHost.Run(async () =>
    {
        var dir = RoleApp.NewDir();
        try
        {
            using var app = new RoleApp(dir, null);
            await RoleApp.SettleAsync();
            var page = Page(app);
            Assert.True(HasTop(app, "tuning")); // R1(기본) 에는 계획 명령이 있는 튜닝 메뉴가 있다

            Click(Choice(page, PrimaryWorkKind.Viewer));
            await RoleApp.SettleAsync(() => app.Gate.ReadOnlyRole && !page.ShowsFirstRunCard);

            var settings = app.Get<SettingsService>();
            Assert.Equal([RoleIds.Viewer], settings.Current.Features.Roles);
            Assert.True(File.Exists(SettingsFile(dir)));
            Assert.False(page.ShowsRoleLink); // 골랐으니 한 줄 카드도 없다
            await RoleApp.SettleAsync(() => !HasTop(app, "tuning"));
            Assert.False(HasTop(app, "monitor"));
            Assert.True(app.Get<ContextKeyService>().Get("feature.tableSpec"));
            Assert.False(app.Get<ContextKeyService>().Get("feature.gridEdit"));

            // 시작 때 도는 기능(변경 일지·블로커 알림)은 재시작 뒤에 멈춘다: 시작 화면이 알린다.
            await RoleApp.SettleAsync(() => page.RestartLineText.Length > 0);
            Assert.StartsWith("재시작하면 적용됩니다: ", page.RestartLineText, StringComparison.Ordinal);
            Assert.Contains("블로커 알림", page.RestartLineText, StringComparison.Ordinal);
            MyBatisFlowRig.Render(app.Window, "start-first-run-chosen-viewer", (int)app.Window.Bounds.Width, (int)app.Window.Bounds.Height);
        }
        finally
        {
            RoleApp.DeleteDir(dir);
        }
    });

    [Fact]
    public Task Choosing_sql_development_with_the_plsql_addon_saves_both_roles() => HeadlessHost.Run(async () =>
    {
        var dir = RoleApp.NewDir();
        try
        {
            using var app = new RoleApp(dir, null);
            await RoleApp.SettleAsync();
            var page = Page(app);
            page.GetVisualDescendants().OfType<CheckBox>().Single(c => AutomationProperties.GetName(c) == "PL/SQL 패키지·프로시저도 고친다").IsChecked = true;

            Click(Choice(page, PrimaryWorkKind.SqlDev));
            await RoleApp.SettleAsync(() => !page.ShowsFirstRunCard);

            Assert.Equal([RoleIds.SqlDev, RoleIds.PlsqlDev], app.Get<SettingsService>().Current.Features.Roles);
            await RoleApp.SettleAsync(() => app.Get<ICommandRegistry>().Find("oranda.plsql.compile") is not null); // 재시작 없이 PL/SQL 명령
            Assert.False(page.ShowsRoleLink);
        }
        finally
        {
            RoleApp.DeleteDir(dir);
        }
    });

    [Fact]
    public Task All_features_ignores_the_addons_and_saves_the_all_features_flag() => HeadlessHost.Run(async () =>
    {
        var dir = RoleApp.NewDir();
        try
        {
            using var app = new RoleApp(dir, null);
            await RoleApp.SettleAsync();
            var page = Page(app);
            page.GetVisualDescendants().OfType<CheckBox>().First().IsChecked = true;

            await page.ChooseAsync(PrimaryWorkKind.All);

            var features = app.Get<SettingsService>().Current.Features;
            Assert.True(features.AllFeatures);
            Assert.Null(features.Roles);
            await RoleApp.SettleAsync(() => HasTop(app, "monitor"));
        }
        finally
        {
            RoleApp.DeleteDir(dir);
        }
    });

    // ── 나중에 → 파일 생김 → 다음 실행은 한 줄 카드 ─────────────────────

    [Fact]
    public Task Later_writes_the_file_shows_the_one_line_card_and_the_next_start_has_no_card() => HeadlessHost.Run(async () =>
    {
        var dir = RoleApp.NewDir();
        try
        {
            using (var app = new RoleApp(dir, null))
            {
                await RoleApp.SettleAsync();
                var page = Page(app);
                var later = page.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "나중에"));

                Click(later);
                await RoleApp.SettleAsync(() => File.Exists(SettingsFile(dir)));

                Assert.False(page.ShowsFirstRunCard);
                Assert.True(page.ShowsDefaultRoleCard); // D-013 한 줄 카드로 바뀐다
                Assert.True(FeatureCatalog.IsUnchosen(app.Get<SettingsService>().Current.Features)); // 기본 Role(SQL 개발자) 그대로
                Assert.True(app.Get<SettingsService>().IsFirstRun); // 이번 실행 동안은 그대로
                MyBatisFlowRig.Render(app.Window, "start-first-run-later", (int)app.Window.Bounds.Width, (int)app.Window.Bounds.Height);
            }

            using var again = new RoleApp(dir, null);
            await RoleApp.SettleAsync();
            Assert.False(again.Get<SettingsService>().IsFirstRun);
            var next = Page(again);
            Assert.False(next.ShowsFirstRunCard);
            Assert.True(next.ShowsDefaultRoleCard);
        }
        finally
        {
            RoleApp.DeleteDir(dir);
        }
    });

    [Fact]
    public Task An_existing_settings_file_is_not_a_new_install_and_keeps_todays_screens() => HeadlessHost.Run(async () =>
    {
        var dir = RoleApp.NewDir();
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(SettingsFile(dir), "{\"schemaVersion\":1}"); // 기존 사용자, Role 은 고른 적 없음
            using (var app = new RoleApp(dir, null))
            {
                await RoleApp.SettleAsync();
                Assert.False(app.Get<SettingsService>().IsFirstRun);
                Assert.False(Page(app).ShowsFirstRunCard);
                Assert.True(Page(app).ShowsDefaultRoleCard);
            }

            var chosen = RoleApp.NewDir();
            try
            {
                using var app = new RoleApp(chosen, PrdMatrix.FeaturesJson(["tuner", "dba"]));
                await RoleApp.SettleAsync();
                Assert.False(Page(app).ShowsRoleLink); // 고른 사람에게는 카드가 없다
            }
            finally
            {
                RoleApp.DeleteDir(chosen);
            }
        }
        finally
        {
            RoleApp.DeleteDir(dir);
        }
    });

    [Fact]
    public async Task Is_first_run_is_decided_once_by_the_first_load()
    {
        var exists = false;
        var settings = new SettingsService(new MemorySettingsStore(), settingsFileExists: () => exists);
        Assert.False(settings.IsFirstRun); // 읽기 전에는 모른다
        await settings.LoadAsync();
        Assert.True(settings.IsFirstRun);

        exists = true; // 다른 프로세스가 뒤에 파일을 만들어도
        await settings.LoadAsync();
        Assert.True(settings.IsFirstRun);

        var plain = new SettingsService(new MemorySettingsStore()); // 파일 위치를 모르면 새 설치로 보지 않는다
        await plain.LoadAsync();
        Assert.False(plain.IsFirstRun);
    }

    // ── 첫 접속 흐름 ───────────────────────────────────────────────

    [Fact]
    public Task With_no_profiles_the_first_line_is_a_highlighted_add_connection_and_a_saved_profile_brings_the_manager_row_back() => HeadlessHost.Run(async () =>
    {
        var dir = RoleApp.NewDir();
        try
        {
            using var app = new RoleApp(dir, PrdMatrix.FeaturesJson(["sqlDev"]));
            await RoleApp.SettleAsync();
            var page = Page(app);
            await RoleApp.SettleAsync(() => page.ShowsAddConnection);

            var add = page.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b) == "접속 추가…");
            Assert.True(add.IsEffectivelyVisible);
            Assert.Contains("accent", add.Classes); // 강조
            Assert.Contains(add.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "접속하면 SQL 탭이 바로 열립니다" && t.IsEffectivelyVisible);
            Assert.Null(ToolTip.GetTip(add)); // 둘째 줄이 이미 보인다(최근 목록 줄만 툴팁에 경로를 단다)
            var visible = page.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible && b.Classes.Contains("start-item") || b == add).ToList();
            Assert.Same(add, visible[0]); // 첫 줄
            Assert.DoesNotContain(visible, b => AutomationProperties.GetName(b) == app.Get<ShellController>().Text.Lookup("Cmd.oranda.conn.manage")); // 같은 일을 하는 줄은 숨긴다
            MyBatisFlowRig.Render(app.Window, "start-add-connection", (int)app.Window.Bounds.Width, (int)app.Window.Bounds.Height);

            await app.Get<IConnectionStore>().SaveAsync(new ConnectionsDocument { Profiles = [BlockerTestData.Profile(Guid.NewGuid())] }, CancellationToken.None);
            await page.RefreshConnectionsAsync();
            Assert.False(page.ShowsAddConnection);
            Assert.Contains(page.GetVisualDescendants().OfType<Button>(), b => b.IsEffectivelyVisible && AutomationProperties.GetName(b) == app.Get<ShellController>().Text.Lookup("Cmd.oranda.conn.manage"));
        }
        finally
        {
            RoleApp.DeleteDir(dir);
        }
    });

    [Fact]
    public Task Connecting_with_no_documents_opens_one_sql_tab_and_a_second_connection_does_not_open_another() => HeadlessHost.Run(async () =>
    {
        var dir = RoleApp.NewDir();
        try
        {
            using var app = new RoleApp(dir, PrdMatrix.FeaturesJson(["sqlDev"]));
            await RoleApp.SettleAsync();
            await app.Services.GetService<IEnumerable<Oranda.Core.Composition.IStartupTask>>()!
                .OfType<Oranda.App.Features.SqlEditor.SqlEditorDockWiring>().Single().RunAsync(default); // SQL 작업 공간 ↔ 문서 연결(시작 작업)
            var docking = app.Get<DockWorkspace>();
            Assert.NotNull(FirstConnectEditor.For(app.Services)); // 시작 화면이 붙였다
            Assert.Empty(docking.Documents);

            var bus = app.Get<IEventBus>();
            bus.Publish(new SessionOpened(Guid.NewGuid(), BlockerTestData.Profile(), BlockerTestData.Server(BlockerTestData.NoPrivilege), SessionPurpose.Work));
            await RoleApp.SettleAsync(() => docking.Documents.Count == 1);
            Assert.Equal(1, FirstConnectEditor.For(app.Services)!.Opened);

            bus.Publish(new SessionOpened(Guid.NewGuid(), BlockerTestData.Profile(Guid.NewGuid()), BlockerTestData.Server(BlockerTestData.NoPrivilege), SessionPurpose.Work));
            await RoleApp.SettleAsync();
            Assert.Single(docking.Documents); // 문서가 이미 있으면 열지 않는다(복원 탭과 같다)
            Assert.Equal(1, FirstConnectEditor.For(app.Services)!.Opened);
        }
        finally
        {
            RoleApp.DeleteDir(dir);
        }
    });

    [Fact]
    public Task Only_work_sessions_with_no_open_documents_open_an_editor() => HeadlessHost.Run(async () =>
    {
        var bus = new FakeEventBus();
        var documents = 0;
        var opened = 0;
        using var editor = new FirstConnectEditor(bus, () => documents, () =>
        {
            opened++;
            return Task.CompletedTask;
        });

        void Open(SessionPurpose purpose)
        {
            bus.Publish(new SessionOpened(Guid.NewGuid(), BlockerTestData.Profile(), BlockerTestData.Server(BlockerTestData.NoPrivilege), purpose));
            Dispatcher.UIThread.RunJobs();
        }

        Open(SessionPurpose.Dedicated); // 전용 세션 탭은 자기 탭이 있다
        Open(SessionPurpose.Monitor); // 모니터 접속
        Assert.Equal(0, opened);

        documents = 2; // 복원된 탭이 있다
        Open(SessionPurpose.Work);
        Assert.Equal(0, opened);

        documents = 0;
        Open(SessionPurpose.Work);
        Assert.Equal(1, opened);
        Assert.Equal(1, editor.Opened);

        editor.Dispose(); // 정리한 뒤에는 듣지 않는다
        Open(SessionPurpose.Work);
        Assert.Equal(1, opened);
        await Task.CompletedTask;
    });

    // ── 화면 품질 ─────────────────────────────────────────────────

    [Theory]
    [InlineData("en", 1100)]
    [InlineData("ko", 520)]
    [InlineData("en", 520)]
    public Task The_card_renders_in_both_languages_and_wraps_in_a_narrow_window(string language, int width) => HeadlessHost.Run(async () =>
    {
        var dir = RoleApp.NewDir();
        try
        {
            using var app = new RoleApp(dir, null);
            await RoleApp.SettleAsync();
            var text = new ShellText(language: language);
            var page = new StartPage(app.Get<ICommandRegistry>(), app.Get<IContextKeys>(), text, _ => null, null, app.Services);
            var window = new Window { Content = page, Width = width, Height = 760 };
            window.Show();
            await RoleApp.SettleAsync(() => page.ShowsFirstRunCard);
            window.UpdateLayout();
            try
            {
                var card = page.GetVisualDescendants().OfType<Border>().Single(b => AutomationProperties.GetName(b) == text.Lookup(FeatureTexts.StartFirstRunTitle));
                var texts = card.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? string.Empty).ToList();
                Assert.Equal(language == "ko", texts.Any(t => t.Any(c => c is >= '가' and <= '힣')));
                Assert.True(card.Bounds.Width <= width, $"{card.Bounds.Width} <= {width}"); // 가로로 넘치지 않는다

                var buttons = ChoiceButtons(page);
                var rows = buttons.Select(b => Math.Round(b.TranslatePoint(default, card)!.Value.Y)).Distinct().Count();
                Assert.True(width < 700 ? rows >= 3 : rows == 2, $"rows {rows}, card {card.Bounds}, page {page.Bounds}"); // 넓으면 3 + 2, 좁으면 더 여러 줄로 감싼다
                Assert.Single(buttons.Select(b => Math.Round(b.Bounds.Height)).Distinct()); // 칸 높이가 같다
                Assert.All(buttons, b => Assert.True(b.Bounds.Right <= card.Bounds.Width + 0.5));

                MyBatisFlowRig.Render(window, $"start-first-run-{language}-{width}", width, 760);
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            RoleApp.DeleteDir(dir);
        }
    });
}
