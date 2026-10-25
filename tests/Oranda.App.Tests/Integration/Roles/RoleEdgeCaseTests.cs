using System.Text.Json;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Oranda.App.Features.Execution;
using Oranda.App.Features.GridEdit;
using Oranda.App.Shell;
using Oranda.App.Shell.Composition;
using Oranda.App.Shell.Menus;
using Oranda.App.Shell.Settings;
using Oranda.App.Tests.Shell;
using Oranda.App.Tests.Shell.Headless;
using Oranda.App.Views;
using Oranda.Core.Events;
using Oranda.Core.Features;
using Oranda.Core.Sessions;
using Oranda.Core.Settings;
using Oranda.Core.Shell;
using Oranda.Testing;
using Xunit.Abstractions;

namespace Oranda.App.Tests.Integration.Roles;

/// <summary>
/// TSK-07-08 통합 4: 경계·오류·되돌림과 앞 레인이 넘긴 확인 거리(followups.md). 결함으로 판정한 것은 「현재 동작」을 단정해 두고
/// (고치면 이 시험이 먼저 알려 준다) 결과서 docs/oranda1/itest/TSK-07-08.md 의 표에 번호로 올린다. D-1~D-4 는 TSK-07-09·10 에서 고쳐 단정을 뒤집었다.
/// </summary>
[Trait("Category", "Integration")]
[Collection("RoleItestUi")]
public sealed class RoleEdgeCaseTests(ITestOutputHelper output)
{
    private static string Features(string[]? roles, string[]? enabled = null, string[]? disabled = null) => PrdMatrix.FeaturesJson(roles, enabled, disabled);

    // ── 설정 저장 · 다시 읽기 ──────────────────────────────────────

    [Fact]
    public Task Settings_round_trip_saves_the_chosen_roles_and_overrides_and_a_fresh_load_reads_them_back() => HeadlessHost.Run(async () =>
    {
        var dir = RoleApp.NewDir();
        try
        {
            using (var app = new RoleApp(dir, null))
            {
                var settings = app.Get<SettingsService>();
                var vm = new SettingsViewModel(settings, app.Get<ShellController>().Text, featureGate: app.Gate);
                vm.Features.Primary = PrimaryWorkKind.Tuner; // 주 업무 「튜닝」 + 「PL/SQL 패키지·프로시저도 고친다」(TSK-09-08)
                vm.Features.PlsqlAddon = true;
                vm.Features.Items.Single(i => i.Id == FeatureIds.MyBatisLog).IsChecked = true; // R4·R2 기본 꺼짐 → 직접 켬
                vm.Features.Items.Single(i => i.Id == FeatureIds.DbTools).IsChecked = false; // R4·R2 기본 켬 → 직접 끔
                await vm.SaveAsync();

                Assert.Equal([RoleIds.Tuner, RoleIds.PlsqlDev], settings.Current.Features.Roles);
                Assert.Equal([FeatureIds.MyBatisLog], settings.Current.Features.Enabled);
                Assert.Equal([FeatureIds.DbTools], settings.Current.Features.Disabled);
            }

            // 파일을 날것으로 읽어 본다.
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "settings.json")));
            var f = doc.RootElement.GetProperty("features");
            Assert.Equal(["tuner", "plsqlDev"], f.GetProperty("roles").EnumerateArray().Select(e => e.GetString()).ToArray());
            Assert.Equal(["myBatisLog"], f.GetProperty("enabled").EnumerateArray().Select(e => e.GetString()).ToArray());
            Assert.Equal(["dbTools"], f.GetProperty("disabled").EnumerateArray().Select(e => e.GetString()).ToArray());

            // 재시작해서 다시 읽는다: 같은 값, 같은 켜진 집합.
            using var app2 = new RoleApp(dir, null);
            Assert.Equal([RoleIds.Tuner, RoleIds.PlsqlDev], app2.Get<SettingsService>().Current.Features.Roles);
            var expected = PrdMatrix.Enabled(["tuner", "plsqlDev"], ["myBatisLog"], ["dbTools"]);
            Assert.True(expected.SetEquals(app2.Gate.EnabledFeatures));
            var vm2 = new SettingsViewModel(app2.Get<SettingsService>(), app2.Get<ShellController>().Text, featureGate: app2.Gate);
            Assert.Equal(PrimaryWorkKind.Tuner, vm2.Features.Primary);
            Assert.True(vm2.Features.PlsqlAddon);
            Assert.False(vm2.Features.MyBatisAddon);
            Assert.True(vm2.Features.Items.Single(i => i.Id == FeatureIds.MyBatisLog).IsChecked);
            Assert.False(vm2.Features.Items.Single(i => i.Id == FeatureIds.DbTools).IsChecked);
            Assert.True(expected.SetEquals(vm2.Features.Items.Where(i => i.IsChecked).Select(i => i.Id)));
        }
        finally
        {
            RoleApp.DeleteDir(dir);
        }
    });

    [Fact]
    public Task Unknown_ids_from_a_newer_version_survive_a_save_and_do_not_break_the_gate() => HeadlessHost.Run(async () =>
    {
        var dir = RoleApp.NewDir();
        try
        {
            using var app = new RoleApp(dir, Features(["sqlDev", "futureRole"], ["futureFeature"], ["otherFutureFeature"]));
            var settings = app.Get<SettingsService>();

            // 모르는 id 는 켜진 기능 집합에 끼지 않고 앱은 정상 시작한다.
            Assert.True(PrdMatrix.Enabled(["sqlDev"]).SetEquals(app.Gate.EnabledFeatures));
            Assert.True(app.Gate.IsEnabled("futureFeature")); // 모르는 기능 id 는 막지 않는다(꼬리표가 없는 항목은 공통)

            // 기능 탭을 건드리지 않고 다른 설정을 저장해도 features 는 그대로.
            var vm = new SettingsViewModel(settings, app.Get<ShellController>().Text, featureGate: app.Gate);
            vm.AutoCommit = !vm.AutoCommit;
            await vm.SaveAsync();
            Assert.Equal(["sqlDev", "futureRole"], settings.Current.Features.Roles);
            Assert.Equal(["futureFeature"], settings.Current.Features.Enabled);
            Assert.Equal(["otherFutureFeature"], settings.Current.Features.Disabled);

            // 건드려서 저장해도 모르는 id 는 지우지 않는다.
            var vm2 = new SettingsViewModel(settings, app.Get<ShellController>().Text, featureGate: app.Gate);
            vm2.Features.Items.Single(i => i.Id == FeatureIds.Trace).IsChecked = true;
            await vm2.SaveAsync();
            Assert.Contains("futureRole", settings.Current.Features.Roles!);
            Assert.Contains("futureFeature", settings.Current.Features.Enabled);
            Assert.Contains("otherFutureFeature", settings.Current.Features.Disabled);
            Assert.Contains(FeatureIds.Trace, settings.Current.Features.Enabled);
        }
        finally
        {
            RoleApp.DeleteDir(dir);
        }
    });

    [Theory]
    [InlineData("{\"roles\":\"viewer\"}")] // 목록이 아니라 글자
    [InlineData("{\"roles\":[1,2]}")] // 글자가 아닌 목록
    [InlineData("{\"enabled\":5}")]
    [InlineData("[]")]
    [InlineData("\"viewer\"")]
    [InlineData("null")]
    public Task Malformed_features_section_never_stops_the_app_from_starting(string featuresJson) => HeadlessHost.Run(async () =>
    {
        var dir = RoleApp.NewDir();
        try
        {
            using var app = new RoleApp(dir, featuresJson);
            await RoleApp.SettleAsync();

            var s = app.Capture();
            Assert.NotEmpty(s.TopMenus); // 메인 창이 떴다
            Assert.Contains("PlSqlWarmUpTask", s.StartupTasks);
            output.WriteLine($"{featuresJson} => 켜진 기능 {s.GateEnabled.Count}개, 조회 전용 {s.GateReadOnlyRole}, 설정 알림 [{app.Get<SettingsService>().Notice}]");
            // 읽을 수 없는 값은 「Role 을 고르지 않음」(모든 기능)이거나 설정 파일을 백업한 기본값이다 — 어느 쪽이든 조회 전용으로 잠기지 않는다.
            Assert.False(s.GateReadOnlyRole && featuresJson is not "{\"roles\":\"viewer\"}");
        }
        finally
        {
            RoleApp.DeleteDir(dir);
        }
    });

    [Fact]
    public Task Hand_edited_settings_file_with_viewer_only_and_write_features_still_cannot_run_writes() => HeadlessHost.Run(async () =>
    {
        var dir = RoleApp.NewDir();
        try
        {
            using var app = new RoleApp(dir, Features(["viewer"], ["gridEdit", "dataImport", "plsqlEditor", "plsqlWorkspace", "plsqlRun", "columnReorder"]));
            await RoleApp.SettleAsync();
            var s = app.Capture();

            Assert.True(s.GateReadOnlyRole);
            Assert.Empty(s.GateEnabled.Intersect(PrdMatrix.WritesOnly)); // 손으로 켜도 꺼진 것으로 본다(RL-08)
            Assert.DoesNotContain("oranda.grid.edit.addRow", s.RegistryCommands);
            Assert.DoesNotContain("oranda.tools.import", s.RegistryCommands);
            Assert.DoesNotContain("oranda.file.newPlsqlEditor", s.RegistryCommands);
            Assert.Contains("SaveStatusViewModel", s.StatusTypes); // 저장 알림 칸은 공통(내보내기 완료 알림, TSK-07-09)

            // 설정 창도 같은 말을 한다: 쓰기 전용 기능은 막힘 표시 + 꺼짐.
            var vm = new SettingsViewModel(app.Get<SettingsService>(), app.Get<ShellController>().Text, featureGate: app.Gate);
            Assert.True(vm.Features.IsViewerOnly);
            Assert.All(vm.Features.Items.Where(i => PrdMatrix.WritesOnly.Contains(i.Id)), i => Assert.True(i.IsBlocked && !i.IsChecked, i.Id));
        }
        finally
        {
            RoleApp.DeleteDir(dir);
        }
    });

    // ── Role 0개 ───────────────────────────────────────────────────

    [Fact]
    public Task Choose_by_role_with_no_role_ticked_explains_that_only_the_common_features_stay_on() => HeadlessHost.Run(async () =>
    {
        // followups(07-04, TSK-07-10 D-4 수정): Role 0개(roles: []) = 공통 기능만. 켜지는 것·할 일을 한 줄로 알린다.
        // TSK-09-08 뒤로는 화면에서 새로 만들 수 없고(D-017), 그런 설정 파일을 열면 「지금 조합: Role 없음(그대로 두기)」 으로 보인다.
        var dir = RoleApp.NewDir();
        try
        {
            using var app = new RoleApp(dir, Features([]));
            var vm = new SettingsViewModel(app.Get<SettingsService>(), app.Get<ShellController>().Text, featureGate: app.Gate);
            Assert.Equal(PrimaryWorkKind.Custom, vm.Features.Primary);
            Assert.Equal("지금 조합: Role 없음(그대로 두기)", vm.Features.PrimaryOptions[^1].Name);

            Assert.All(vm.Features.Items, i => Assert.False(i.IsChecked, i.Id)); // 25개 모두 꺼짐
            Assert.True(vm.Features.ShowNoRoleNotice);
            Assert.Equal("Role 이 하나도 없어 공통 기능만 켜집니다. 위에서 주 업무를 고르세요.", vm.Features.NoRoleNoticeText);
            Assert.False(vm.Features.HasRestartBanner);
            Assert.False(vm.Features.IsViewerOnly);
            Assert.False(vm.Features.ShowPackNotice);

            // 주 업무를 고르면 안내가 사라지고, 「모든 기능」 에서도 안 보이며, 「지금 조합」 으로 돌아오면 다시 보인다(되돌림).
            vm.Features.Primary = PrimaryWorkKind.SqlDev;
            Assert.False(vm.Features.ShowNoRoleNotice);
            vm.Features.Primary = PrimaryWorkKind.All;
            Assert.False(vm.Features.ShowNoRoleNotice);
            vm.Features.Primary = PrimaryWorkKind.Custom;
            Assert.True(vm.Features.ShowNoRoleNotice);

            // 저장해도 roles = [] 그대로 → 공통 기능만 있는 앱이다(시작은 한다).
            await vm.SaveAsync();
            Assert.Empty(app.Get<SettingsService>().Current.Features.Roles!);
            await RoleApp.SettleAsync(() => !app.Gate.IsEnabled(FeatureIds.GridEdit));
            var s = app.Capture();
            Assert.Empty(s.GateEnabled);
            Assert.Contains("oranda.exec.runStatement", s.RegistryCommands);
            Assert.Contains("oranda.tools.roles", s.MenuCommands); // 되돌아갈 길(도구 > Role·기능 고르기…)은 남는다
        }
        finally
        {
            RoleApp.DeleteDir(dir);
        }
    });

    // ── 조회 전용 ──────────────────────────────────────────────────

    [Fact]
    public Task Viewer_only_marks_the_read_only_session_key_from_the_start_without_opening_the_execution_controller() => HeadlessHost.Run(async () =>
    {
        var dir = RoleApp.NewDir();
        try
        {
            using var app = new RoleApp(dir, Features(["viewer"]));
            await RoleApp.SettleAsync();

            // 상태 칸(시작 때 만든다)이 ExecutionCommands.Install 로 키를 Role 에 묶는다: 컨트롤러를 만들기 전에도 참.
            Assert.True(app.Get<ContextKeyService>().Get("readOnlySession"));
            Assert.True(app.Get<ContextKeyService>().Get("feature.tableSpec"));
            Assert.False(app.Get<ContextKeyService>().Get("feature.gridEdit"));
        }
        finally
        {
            RoleApp.DeleteDir(dir);
        }
    });

    [Fact]
    public Task Viewer_only_greys_the_auto_commit_toggle_but_the_reason_text_names_a_read_only_connection() => HeadlessHost.Run(async () =>
    {
        var dir = RoleApp.NewDir();
        try
        {
            using var app = new RoleApp(dir, Features(["viewer"]));
            await RoleApp.SettleAsync();
            var keys = app.Get<ContextKeyService>();
            keys.Set("hasSession", true);
            await RoleApp.SettleAsync();

            var item = FindMenuItem(app, "oranda.txn.toggleAutoCommit");
            Assert.NotNull(item);
            Assert.False(app.Get<ICommandRegistry>().CanExecute("oranda.txn.toggleAutoCommit", keys)); // Role 때문에 흐려진다
            var tip = Avalonia.Controls.ToolTip.GetTip(item!);
            output.WriteLine("R6 단독 흐린 명령 툴팁: " + tip);
            // 문구: 프로필 읽기 전용뿐 아니라 조회 전용 Role 도 말해야 한다(followups 07-05, 문구 파일을 고쳤다).
            Assert.Contains("조회 전용 Role", tip as string ?? string.Empty, StringComparison.Ordinal);
            Assert.Contains("읽기 전용 접속", tip as string ?? string.Empty, StringComparison.Ordinal);
        }
        finally
        {
            RoleApp.DeleteDir(dir);
        }
    });

    private static MenuItem? FindMenuItem(RoleApp app, string commandId)
    {
        MenuItem? found = null;
        void Walk(MenuItem item)
        {
            if (item.Command is RegistryCommand rc && rc.CommandId == commandId)
            {
                found ??= item;
            }

            foreach (var child in item.ItemsSource?.OfType<MenuItem>() ?? [])
            {
                Walk(child);
            }
        }

        foreach (var top in app.Window.Menus.ItemsSource!.Cast<MenuItem>())
        {
            Walk(top);
        }

        return found;
    }

    [Fact]
    public Task Status_bar_read_only_mark_shows_the_viewer_role_and_tells_the_profile_flag_apart() => HeadlessHost.Run(async () =>
    {
        // TSK-07-10(결함 D-2 수정): 상태 표시줄 「읽기 전용」 칸이 조회 전용 Role 도 반영한다. 프로필이면 「읽기 전용」, Role 이면 「조회 전용 Role」.
        var dir = RoleApp.NewDir();
        try
        {
            using var app = new RoleApp(dir, Features(["viewer"]));
            var bus = app.Get<IEventBus>();
            var chip = app.Window.ViewModel!.StatusItems.Select(e => e.Item).OfType<ProtectionStatusViewModel>().Single();
            Assert.Equal(string.Empty, chip.Text); // 접속 전에는 비어 있다

            var id = Guid.NewGuid();
            bus.Publish(new SessionOpened(id, TestData.Profile(readOnly: false), TestData.Server(), SessionPurpose.Work));
            await RoleApp.SettleAsync();
            Assert.True(app.Get<ContextKeyService>().Get("readOnlySession"));
            Assert.Equal("조회 전용 Role", chip.Text); // 쓰기 가능 프로필이어도 Role 때문에 막혔음을 말한다
            Assert.True(chip.IsVisible);
            Assert.Contains("설정 > 기능", chip.ToolTip, StringComparison.Ordinal); // 풀 방법

            var other = Guid.NewGuid();
            bus.Publish(new SessionOpened(other, TestData.Profile(readOnly: true), TestData.Server(), SessionPurpose.Work));
            await RoleApp.SettleAsync();
            Assert.Equal("읽기 전용", chip.Text); // 프로필 읽기 전용이 우선(정책 관문과 같은 순서)
            Assert.Equal("읽기 전용", chip.ToolTip);
        }
        finally
        {
            RoleApp.DeleteDir(dir);
        }

        // Role 을 더한 구성(R6 + R1)은 조회 전용이 아니므로 쓰기 가능 프로필에서 칸이 비어 있다.
        var dir2 = RoleApp.NewDir();
        try
        {
            using var app = new RoleApp(dir2, Features(["viewer", "sqlDev"]));
            var chip = app.Window.ViewModel!.StatusItems.Select(e => e.Item).OfType<ProtectionStatusViewModel>().Single();
            app.Get<IEventBus>().Publish(new SessionOpened(Guid.NewGuid(), TestData.Profile(readOnly: false), TestData.Server(), SessionPurpose.Work));
            await RoleApp.SettleAsync();
            Assert.Equal(string.Empty, chip.Text);
        }
        finally
        {
            RoleApp.DeleteDir(dir2);
        }
    });

    [Fact]
    public Task Save_feedback_notices_have_a_status_item_in_every_role_configuration() => HeadlessHost.Run(async () =>
    {
        // TSK-07-09(결함 D-1 수정): 「저장 알림」 칸은 gridEdit 와 무관한 공통이다. SaveFeedback 은 Excel 내보내기 결과·튜닝 보고서 저장·
        // 최근 파일 없음·MyBatis/PL/SQL 끔 대체 열기 안내까지 싣기 때문에 조회 전용(R6)·튜너(R4)에서도 보여야 한다.
        foreach (var (name, roles) in new[] { ("R6 조회 전용", "viewer"), ("R4 튜너", "tuner"), ("R1 SQL 개발자", "sqlDev") })
        {
            var dir = RoleApp.NewDir();
            try
            {
                using var app = new RoleApp(dir, Features([roles]));
                var chip = app.Window.ViewModel!.StatusItems.Select(e => e.Item).OfType<SaveStatusViewModel>().SingleOrDefault();
                Assert.NotNull(chip); // 칸이 있다
                Assert.False(chip.IsVisible); // 알림이 없으면 접혀 있다

                var file = Path.Combine(dir, "result.xlsx");
                SaveFeedback.ShowExported(120, file);
                try
                {
                    await RoleApp.SettleAsync(() => chip.IsVisible);
                    Assert.True(chip.IsVisible, name);
                    Assert.Equal(file, chip.FilePath); // 「열기」「폴더 열기」 링크가 쓸 경로
                    output.WriteLine($"{name}: 알림 칸 글자 [{chip.Text}]");
                }
                finally
                {
                    SaveFeedback.Clear();
                }

                await RoleApp.SettleAsync(() => !chip.IsVisible);
                Assert.False(chip.IsVisible); // 알림을 지우면 다시 접힌다
            }
            finally
            {
                RoleApp.DeleteDir(dir);
            }
        }
    });

    [Fact]
    public Task Viewer_only_palette_keeps_common_plan_commands_that_the_hidden_monitor_and_tuning_menus_used_to_hold() => HeadlessHost.Run(async () =>
    {
        // followups(07-02): R6 단독이면 모니터·튜닝 메뉴가 통째로 숨고, 거기에만 놓였던 공통 명령(힌트 재실행·통계 수준 등)은 팔레트에서만 남는다.
        var dir = RoleApp.NewDir();
        try
        {
            using var app = new RoleApp(dir, Features(["viewer"]));
            await RoleApp.SettleAsync();
            var s = app.Capture();
            var all = ShellTestHelpers.AppContributions.Commands;
            var paletteOnly = all
                .Where(x => s.PaletteCommands.Contains(x.Definition.Id) && !s.MenuCommands.Contains(x.Definition.Id) && !s.ToolbarCommands.Contains(x.Definition.Id) && !s.ContextCommands.Contains(x.Definition.Id))
                .Select(x => x.Definition.Id)
                .Where(id => all.Single(x => x.Definition.Id == id).Definition.Placements.Any(p => p.StartsWith("menu:tuning", StringComparison.Ordinal) || p.StartsWith("menu:monitor", StringComparison.Ordinal)))
                .Order()
                .ToList();
            output.WriteLine("R6 단독: 모니터·튜닝 메뉴에만 놓였다가 팔레트로만 남는 공통 명령 " + paletteOnly.Count + "개: " + string.Join(", ", paletteOnly));
            Assert.All(paletteOnly, id => Assert.Null(app.Gate.FeatureOf(id, all.Single(x => x.Definition.Id == id).HandlerType))); // 모두 공통(꼬리표 없음)이다
            Assert.Contains("oranda.plan.explain", s.MenuCommands); // 예상·실제 계획은 SQL 메뉴에도 있어 메뉴로 남는다
        }
        finally
        {
            RoleApp.DeleteDir(dir);
        }
    });

    // ── 설정 창 크기 ───────────────────────────────────────────────

    [Fact]
    public Task Settings_window_size_with_the_features_tab_is_recorded_for_the_eye_check() => HeadlessHost.Run(async () =>
    {
        // followups(07-04): 기능 탭 때문에 설정 창 전체 높이가 늘었는지는 사람이 눈으로 본다. 여기서는 숫자만 남긴다.
        var dir = RoleApp.NewDir();
        try
        {
            using var app = new RoleApp(dir, null);
            var vm = new SettingsViewModel(app.Get<SettingsService>(), app.Get<ShellController>().Text, featureGate: app.Gate);
            var window = new SettingsWindow { DataContext = vm };
            window.Show();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            await Task.Delay(50);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var sizes = new List<string>();
            foreach (var tab in new[] { 0, SettingsViewModel.FeaturesTabIndex, 2, 3 })
            {
                vm.SelectedTab = tab;
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                sizes.Add($"탭 {tab}: {window.Bounds.Width:0}x{window.Bounds.Height:0}");
            }

            output.WriteLine("설정 창(헤드리스 창 크기) " + string.Join(" · ", sizes) + $" · MinHeight {window.MinHeight} · 최소 {window.MinWidth}");
            Assert.True(window.Bounds.Height > 0);
            window.Close();
        }
        finally
        {
            RoleApp.DeleteDir(dir);
        }
    });

    // ── 되돌림: 같은 설정을 빠르게 바꿔도 마지막 값이 이긴다 ──────

    [Fact]
    public Task Rapid_role_changes_end_in_the_last_chosen_state() => HeadlessHost.Run(async () =>
    {
        var dir = RoleApp.NewDir();
        try
        {
            using var app = new RoleApp(dir, null);
            var settings = app.Get<SettingsService>();
            string[][] sequence = [["viewer"], ["viewer", "sqlDev"], ["tuner"], ["viewer"], ["dba", "myBatisDev"], ["sqlDev"]];
            foreach (var roles in sequence)
            {
                await settings.UpdateAsync(s => s with { Features = new FeaturesSettings { Roles = roles } });
            }

            await RoleApp.SettleAsync(() => app.Gate.IsEnabled(FeatureIds.Flashback) && !app.Gate.IsEnabled(FeatureIds.TuningReport));
            var s = app.Capture();
            var expected = PrdMatrix.Enabled(["sqlDev"]);
            Assert.True(expected.SetEquals(s.GateEnabled), "마지막 값(R1)이 이긴다");
            Assert.False(s.GateReadOnlyRole);
            Assert.DoesNotContain(s.TopMenus, t => t == app.Get<ShellController>().Text.Lookup("Menu.monitor"));
            Assert.Empty(s.RegistryCommands.Intersect(["oranda.monitor.locks", "oranda.tuning.report", "oranda.plsql.run"]));
            Assert.Contains("oranda.grid.edit.addRow", s.RegistryCommands);
        }
        finally
        {
            RoleApp.DeleteDir(dir);
        }
    });
}
