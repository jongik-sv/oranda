using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Shell;
using Oranda.App.Shell.Composition;
using Oranda.App.Shell.Features;
using Oranda.App.Shell.Settings;
using Oranda.App.Tests.Features.Update;
using Oranda.App.Tests.Shell;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Features;
using Oranda.Core.Formatting;
using Oranda.Core.Settings;
using Oranda.Core.Shell;
using Oranda.Testing;

namespace Oranda.App.Tests.Features.Roles;

/// <summary>
/// TSK-07-04 설정 창 「기능」 탭: ViewModel(Role 조합 → 확인란, 직접 켬/끔 최소 기록, 되돌리기, 조회 전용 흐림, Requires, 알 수 없는 id 보존)·
/// 저장 흐름(SettingsService.UpdateAsync)·재시작 띠·oranda.tools.roles 등록·Headless 렌더(ko/en·라이트/다크).
/// <c>ORANDA_RENDER_DIR</c> 가 있으면 상태별 PNG 를 그 폴더에 쓴다.
/// </summary>
[Collection("UiLanguage")]
public sealed class FeatureSettingsViewTests
{
    private static readonly ShellText Ko = new ShellText(language: "ko");

    private static async Task<(SettingsService Settings, MemorySettingsStore Store, SettingsViewModel Vm)> Create(
        FeaturesSettings? features = null, IFeatureGate? gate = null, IShellText? text = null)
    {
        var store = new MemorySettingsStore();
        var settings = new SettingsService(store);
        if (features is not null)
        {
            await settings.UpdateAsync(s => s with { Features = features });
        }

        return (settings, store, new SettingsViewModel(settings, text ?? Ko, featureGate: gate ?? new FakeFeatureGate()));
    }

    private static FeatureItem Item(FeatureSettingsPage page, string id) => page.Items.Single(i => i.Id == id);

    private static FeatureRoleItem Role(FeatureSettingsPage page, string id) => page.Roles.Single(r => r.Id == id);

    private static IEnumerable<string> Checked(FeatureSettingsPage page) => page.Items.Where(i => i.IsChecked).Select(i => i.Id);

    private static void AssertSet(IEnumerable<string> expected, IEnumerable<string>? actual) =>
        Assert.True(new HashSet<string>(expected).SetEquals(actual ?? []), $"expected [{string.Join(",", expected)}] actual [{string.Join(",", actual ?? [])}]");

    // ---- 처음 상태 ----

    [Fact]
    public async Task Without_a_features_section_the_tab_shows_every_feature_on_and_no_role_chosen()
    {
        var (_, _, vm) = await Create();
        var page = vm.Features;

        Assert.False(page.UseRoles);
        Assert.True(page.UseAllFeatures);
        Assert.False(page.RolesEditable);
        Assert.Equal(6, page.Roles.Count);
        Assert.All(page.Roles, r => Assert.False(r.IsChecked));
        Assert.All(page.Roles, r => Assert.False(r.CanEdit)); // 모든 기능 쓰기에서는 Role 확인란을 못 고친다
        Assert.Equal(25, page.Items.Count);
        Assert.All(page.Items, i => Assert.True(i.IsChecked, i.Id));
        Assert.All(page.Items, i => Assert.Equal(FeatureState.RoleDefault, i.State));
        Assert.False(page.HasOverrides);
        Assert.False(page.IsViewerOnly);
        Assert.False(page.HasRestartBanner);
        Assert.False(page.IsDirty);
    }

    [Fact]
    public async Task Groups_and_items_come_from_the_catalog_in_group_order_with_localized_names()
    {
        var (_, _, vm) = await Create();
        var page = vm.Features;

        Assert.Equal(Enum.GetValues<FeatureGroup>().Length, page.Groups.Count);
        Assert.Equal(["개발 보조", "PL/SQL 개발", "MyBatis 개발", "튜닝", "운영"], page.Groups.Select(g => g.Name));
        Assert.Equal(FeatureCatalog.Features.Count(f => f.Group == FeatureGroup.Tuning), page.Groups[3].Items.Count);
        Assert.Equal(FeatureIds.All.Order(), page.Items.Select(i => i.Id).Order());
        Assert.All(page.Items, i => Assert.DoesNotContain("Feature.", i.Name)); // 문구 키가 그대로 보이지 않는다
        Assert.All(page.Roles, r => Assert.DoesNotContain("Role.", r.Name));
        Assert.All(page.Roles, r => Assert.False(string.IsNullOrWhiteSpace(r.Description)));
        Assert.Equal("그리드 편집", Item(page, FeatureIds.GridEdit).Name);
        Assert.Equal("DB 튜너", Role(page, RoleIds.Tuner).Name);
    }

    [Fact]
    public async Task English_text_is_used_when_the_language_is_english()
    {
        var (_, _, vm) = await Create(text: new ShellText(language: "en"));
        var page = vm.Features;

        Assert.NotEqual("기능", page.TabTitle);
        Assert.DoesNotContain(page.Items, i => i.Name.Any(c => c is >= '가' and <= '힣'));
        Assert.DoesNotContain(page.Roles, r => (r.Name + r.Description).Any(c => c is >= '가' and <= '힣'));
        Assert.DoesNotContain(page.Groups, g => g.Name.Any(c => c is >= '가' and <= '힣'));
        Assert.DoesNotContain(page.ResetText, c => c is >= '가' and <= '힣');
    }

    [Fact]
    public async Task Initial_state_follows_a_saved_features_section()
    {
        var (_, _, vm) = await Create(new FeaturesSettings { Roles = [RoleIds.SqlDev], Enabled = [FeatureIds.Trace], Disabled = [FeatureIds.GridEdit] });
        var page = vm.Features;

        Assert.True(page.UseRoles);
        Assert.True(Role(page, RoleIds.SqlDev).IsChecked);
        Assert.False(Role(page, RoleIds.Dba).IsChecked);
        Assert.True(Item(page, FeatureIds.Trace).IsChecked);
        Assert.Equal(FeatureState.Enabled, Item(page, FeatureIds.Trace).State);
        Assert.Equal("직접 켬", Item(page, FeatureIds.Trace).StateText);
        Assert.False(Item(page, FeatureIds.GridEdit).IsChecked);
        Assert.Equal(FeatureState.Disabled, Item(page, FeatureIds.GridEdit).State);
        Assert.Equal("직접 끔", Item(page, FeatureIds.GridEdit).StateText);
        Assert.True(page.HasOverrides);
        AssertSet(FeatureCatalog.Resolve(vm.Features.ToSettings()), Checked(page));
    }

    [Fact]
    public async Task A_feature_in_both_lists_is_off_because_off_wins()
    {
        var (_, _, vm) = await Create(new FeaturesSettings { Roles = [RoleIds.Viewer], Enabled = [FeatureIds.Trace], Disabled = [FeatureIds.Trace] });

        Assert.False(Item(vm.Features, FeatureIds.Trace).IsChecked);
    }

    // ---- Role 조합 → 확인란 ----

    [Fact]
    public async Task Choosing_roles_turns_on_the_union_of_their_defaults()
    {
        var (_, _, vm) = await Create();
        var page = vm.Features;

        page.UseRoles = true;
        Assert.Empty(Checked(page)); // Role 을 하나도 안 고르면 공통만
        Assert.True(page.RolesEditable);
        Assert.All(page.Roles, r => Assert.True(r.CanEdit));

        Role(page, RoleIds.SqlDev).IsChecked = true;
        AssertSet(FeatureCatalog.RoleDefaults([RoleIds.SqlDev]), Checked(page));

        Role(page, RoleIds.MyBatisDev).IsChecked = true;
        AssertSet(FeatureCatalog.RoleDefaults([RoleIds.SqlDev, RoleIds.MyBatisDev]), Checked(page));
        Assert.True(Item(page, FeatureIds.MyBatis).IsChecked);
        Assert.False(Item(page, FeatureIds.Trace).IsChecked);

        page.UseAllFeatures = true; // 다시 모든 기능
        Assert.All(page.Items, i => Assert.True(i.IsChecked, i.Id));
    }

    [Fact]
    public async Task Adding_the_tuner_role_and_saving_writes_it_to_features_roles()
    {
        var (_, store, vm) = await Create(new FeaturesSettings { Roles = [RoleIds.SqlDev] });
        var closed = false;
        vm.CloseRequested += (_, _) => closed = true;

        Role(vm.Features, RoleIds.Tuner).IsChecked = true;
        await vm.SaveAsync();

        Assert.Equal([RoleIds.SqlDev, RoleIds.Tuner], store.Saved.Features.Roles); // 카탈로그 순서
        Assert.Empty(store.Saved.Features.Enabled);
        Assert.Empty(store.Saved.Features.Disabled);
        Assert.True(closed);
    }

    [Fact]
    public async Task Choosing_all_features_saves_roles_as_null()
    {
        var (_, store, vm) = await Create(new FeaturesSettings { Roles = [RoleIds.Dba] });

        vm.Features.UseAllFeatures = true;
        await vm.SaveAsync();

        Assert.Null(store.Saved.Features.Roles);
    }

    [Fact]
    public async Task Choosing_roles_but_none_saves_an_empty_roles_list()
    {
        var (_, store, vm) = await Create();

        vm.Features.UseRoles = true;
        await vm.SaveAsync();

        Assert.NotNull(store.Saved.Features.Roles);
        Assert.Empty(store.Saved.Features.Roles!);
    }

    [Fact]
    public async Task The_tuner_role_shows_the_license_pack_notice_only_while_it_is_chosen_in_role_mode()
    {
        var (_, _, vm) = await Create();
        var page = vm.Features;

        Assert.False(page.ShowPackNotice);
        page.UseRoles = true;
        Assert.False(page.ShowPackNotice);
        Role(page, RoleIds.Tuner).IsChecked = true;
        Assert.True(page.ShowPackNotice);
        Assert.Contains("Diagnostics", page.PackNoticeText);
        page.UseAllFeatures = true;
        Assert.False(page.ShowPackNotice);
    }

    // ---- 직접 켬/끔 최소 기록 ----

    [Fact]
    public async Task Only_features_that_differ_from_the_role_defaults_are_recorded()
    {
        var (_, store, vm) = await Create(new FeaturesSettings { Roles = [RoleIds.SqlDev] });
        var page = vm.Features;

        Item(page, FeatureIds.Trace).IsChecked = true;     // SQL 개발자 기본 꺼짐 → 직접 켬
        Item(page, FeatureIds.GridEdit).IsChecked = false; // 기본 켜짐 → 직접 끔
        Item(page, FeatureIds.LobRowView).IsChecked = false;
        Item(page, FeatureIds.LobRowView).IsChecked = true; // 도로 켜면 기록에서 빠진다

        Assert.Equal(FeatureState.Enabled, Item(page, FeatureIds.Trace).State);
        Assert.Equal(FeatureState.Disabled, Item(page, FeatureIds.GridEdit).State);
        Assert.Equal(FeatureState.RoleDefault, Item(page, FeatureIds.LobRowView).State);
        Assert.True(page.HasOverrides);

        await vm.SaveAsync();

        AssertSet([FeatureIds.Trace], store.Saved.Features.Enabled);
        AssertSet([FeatureIds.GridEdit], store.Saved.Features.Disabled);
        AssertSet([RoleIds.SqlDev], store.Saved.Features.Roles);
    }

    [Fact]
    public async Task An_override_that_becomes_the_role_default_after_changing_roles_is_dropped_on_save()
    {
        var (_, store, vm) = await Create(new FeaturesSettings { Roles = [RoleIds.SqlDev], Enabled = [FeatureIds.Trace], Disabled = [FeatureIds.GridEdit] });
        var page = vm.Features;

        Role(page, RoleIds.Tuner).IsChecked = true; // 튜너는 trace 가 기본 켜짐
        Assert.Equal(FeatureState.RoleDefault, Item(page, FeatureIds.Trace).State);
        await vm.SaveAsync();

        Assert.DoesNotContain(FeatureIds.Trace, store.Saved.Features.Enabled);
        AssertSet([FeatureIds.GridEdit], store.Saved.Features.Disabled);
    }

    [Fact]
    public async Task In_all_features_mode_turning_a_feature_off_is_recorded_as_disabled_only()
    {
        var (_, store, vm) = await Create();

        Item(vm.Features, FeatureIds.Trace).IsChecked = false;
        await vm.SaveAsync();

        Assert.Null(store.Saved.Features.Roles);
        AssertSet([FeatureIds.Trace], store.Saved.Features.Disabled);
        Assert.Empty(store.Saved.Features.Enabled);
    }

    [Fact]
    public async Task Saved_overrides_resolve_to_exactly_the_checked_boxes()
    {
        var (_, store, vm) = await Create(new FeaturesSettings { Roles = [RoleIds.PlsqlDev, RoleIds.Tuner] });
        var page = vm.Features;
        Item(page, FeatureIds.Flashback).IsChecked = false;
        Item(page, FeatureIds.TableSpec).IsChecked = true;
        Item(page, FeatureIds.DbTools).IsChecked = false;
        var shown = Checked(page).ToList();

        await vm.SaveAsync();

        AssertSet(shown, FeatureCatalog.Resolve(store.Saved.Features));
    }

    // ---- 되돌리기 ----

    [Fact]
    public async Task Reset_clears_the_overrides_and_keeps_the_chosen_roles()
    {
        var (_, store, vm) = await Create(new FeaturesSettings { Roles = [RoleIds.SqlDev], Enabled = [FeatureIds.Trace], Disabled = [FeatureIds.GridEdit] });
        var page = vm.Features;
        Assert.True(page.ResetCommand.CanExecute(null));

        page.ResetCommand.Execute(null);

        Assert.False(page.HasOverrides);
        Assert.False(page.ResetCommand.CanExecute(null));
        Assert.All(page.Items, i => Assert.Equal(FeatureState.RoleDefault, i.State));
        AssertSet(FeatureCatalog.RoleDefaults([RoleIds.SqlDev]), Checked(page));
        Assert.True(Role(page, RoleIds.SqlDev).IsChecked);

        await vm.SaveAsync();
        Assert.Empty(store.Saved.Features.Enabled);
        Assert.Empty(store.Saved.Features.Disabled);
        AssertSet([RoleIds.SqlDev], store.Saved.Features.Roles);
    }

    [Fact]
    public async Task Reset_is_disabled_when_nothing_differs()
    {
        var (_, _, vm) = await Create(new FeaturesSettings { Roles = [RoleIds.Dba] });

        Assert.False(vm.Features.ResetCommand.CanExecute(null));
        Item(vm.Features, FeatureIds.GridEdit).IsChecked = false;
        Assert.True(vm.Features.ResetCommand.CanExecute(null));
    }

    // ---- 조회 전용 ----

    [Fact]
    public async Task Viewer_only_dims_the_write_only_features_with_a_reason_and_they_cannot_be_turned_on()
    {
        var (_, store, vm) = await Create(new FeaturesSettings { Roles = [RoleIds.Viewer], Enabled = [FeatureIds.GridEdit] });
        var page = vm.Features;

        Assert.True(page.IsViewerOnly);
        Assert.Contains("DML", page.ViewerNoticeText);
        foreach (var def in FeatureCatalog.Features.Where(f => f.WritesOnly))
        {
            var item = Item(page, def.Id);
            Assert.False(item.IsChecked, def.Id);
            Assert.True(item.IsBlocked, def.Id);
            Assert.False(item.CanEdit, def.Id);
            Assert.Contains("조회 전용만 고른 상태", item.BlockedReason);
            Assert.Equal(string.Empty, item.StateText); // 못 켜는 기능에는 「직접 켬」을 붙이지 않는다
        }

        Assert.True(Item(page, FeatureIds.DataExport).IsChecked);
        Assert.False(Item(page, FeatureIds.DataExport).IsBlocked);

        // 설정 쪽 해석과 같다: Enabled 에 gridEdit 가 있어도 조회 전용만이면 꺼진다.
        AssertSet(FeatureCatalog.Resolve(page.ToSettings()), Checked(page));

        // 다른 Role 을 함께 고르면 풀린다.
        Role(page, RoleIds.SqlDev).IsChecked = true;
        Assert.False(page.IsViewerOnly);
        // plsqlWorkspace 는 PL/SQL 편집기가 꺼져 있어(SQL 개발자 기본에 없음) Requires 로 아직 못 켠다.
        Assert.All(FeatureCatalog.Features.Where(f => f.WritesOnly && f.Id != FeatureIds.PlsqlWorkspace), f => Assert.False(Item(page, f.Id).IsBlocked, f.Id));
        Assert.True(Item(page, FeatureIds.GridEdit).IsChecked);
        Assert.Contains("PL/SQL 편집기", Item(page, FeatureIds.PlsqlWorkspace).BlockedReason);
        await vm.SaveAsync();
        AssertSet([RoleIds.Viewer, RoleIds.SqlDev], store.Saved.Features.Roles);
    }

    [Fact]
    public async Task Viewer_notice_is_not_shown_in_all_features_mode_or_with_other_roles()
    {
        var (_, _, vm) = await Create(new FeaturesSettings { Roles = [RoleIds.Viewer, RoleIds.Tuner] });
        Assert.False(vm.Features.IsViewerOnly);
        vm.Features.UseAllFeatures = true;
        Assert.False(vm.Features.IsViewerOnly);
        Assert.All(vm.Features.Items, i => Assert.False(i.IsBlocked, i.Id));
    }

    // ---- Requires ----

    [Fact]
    public async Task A_feature_whose_required_feature_is_off_is_dimmed_with_the_reason_and_comes_back_when_it_is_on()
    {
        var (_, _, vm) = await Create(new FeaturesSettings { Roles = [RoleIds.PlsqlDev] });
        var page = vm.Features;
        var workspace = Item(page, FeatureIds.PlsqlWorkspace);
        Assert.True(workspace.IsChecked);
        Assert.False(workspace.IsBlocked);

        Item(page, FeatureIds.PlsqlEditor).IsChecked = false;

        Assert.False(workspace.IsChecked);
        Assert.True(workspace.IsBlocked);
        Assert.False(workspace.CanEdit);
        Assert.Equal("PL/SQL 편집기·컴파일·컴파일 오류 창 이(가) 켜져 있어야 합니다", workspace.BlockedReason);
        Assert.Contains(workspace.BlockedReason, workspace.Description);

        Item(page, FeatureIds.PlsqlEditor).IsChecked = true;

        Assert.True(workspace.IsChecked); // 직접 끈 적이 없으니 기본대로 돌아온다
        Assert.False(workspace.IsBlocked);
        Assert.Equal(string.Empty, workspace.BlockedReason);
    }

    [Fact]
    public async Task Requires_applies_in_all_features_mode_too()
    {
        var (_, store, vm) = await Create();

        Item(vm.Features, FeatureIds.PlsqlEditor).IsChecked = false;
        Assert.True(Item(vm.Features, FeatureIds.PlsqlWorkspace).IsBlocked);
        await vm.SaveAsync();

        AssertSet(FeatureCatalog.Resolve(store.Saved.Features), Checked(vm.Features));
        Assert.DoesNotContain(FeatureIds.PlsqlWorkspace, FeatureCatalog.Resolve(store.Saved.Features));
    }

    // ---- 저장 흐름 ----

    [Fact]
    public async Task Saving_without_touching_the_tab_leaves_the_features_section_as_it_was()
    {
        // 최소 형태가 아닌 값(기본과 같은 Enabled)·이 버전이 모르는 id 도 건드리지 않는다.
        var original = new FeaturesSettings { Roles = [RoleIds.Dba], Enabled = [FeatureIds.GridEdit, "futureFeature"], Disabled = ["otherFuture"] };
        var (settings, store, vm) = await Create(original);
        vm.AutoCommit = !vm.AutoCommit;

        await vm.SaveAsync();

        Assert.Equal(original, store.Saved.Features);
        Assert.Equal(original, settings.Current.Features);
    }

    [Fact]
    public async Task Unknown_role_and_feature_ids_are_kept_when_the_tab_is_edited()
    {
        var (_, store, vm) = await Create(new FeaturesSettings { Roles = [RoleIds.SqlDev, "futureRole"], Enabled = ["futureOn"], Disabled = ["futureOff"] });

        Item(vm.Features, FeatureIds.Trace).IsChecked = true;
        vm.Features.ResetCommand.Execute(null);
        Item(vm.Features, FeatureIds.GridEdit).IsChecked = false;
        await vm.SaveAsync();

        Assert.Contains("futureRole", store.Saved.Features.Roles!);
        Assert.Contains("futureOn", store.Saved.Features.Enabled);
        Assert.Contains("futureOff", store.Saved.Features.Disabled);
        AssertSet([FeatureIds.GridEdit, "futureOff"], store.Saved.Features.Disabled);
    }

    [Fact]
    public async Task Other_settings_in_the_window_are_saved_together_with_the_features()
    {
        var (_, store, vm) = await Create();
        vm.TabSize = 7;
        Role(vm.Features, RoleIds.Dba).IsChecked = true;
        vm.Features.UseRoles = true;

        await vm.SaveAsync();

        Assert.Equal(7, store.Saved.Editor.TabSize);
        AssertSet([RoleIds.Dba], store.Saved.Features.Roles);
    }

    [Fact]
    public void The_features_tab_comes_right_after_general()
    {
        Assert.Equal(1, SettingsViewModel.FeaturesTabIndex);
        Assert.Equal(SettingsViewModel.FeaturesTabIndex + 2, SettingsViewModel.FormatTabIndex);
        Assert.Equal(8, SettingsViewModel.UpdateTabIndex);
    }

    // ---- 재시작 띠 ----

    [Fact]
    public async Task The_restart_banner_lists_the_pending_features_by_name()
    {
        var gate = new FakeFeatureGate { PendingRestart = [FeatureIds.MyBatis, FeatureIds.BlockerAlert] };
        var (_, _, vm) = await Create(gate: gate);

        Assert.True(vm.Features.HasRestartBanner);
        Assert.Equal("재시작하면 적용됩니다: MyBatis 작업 공간, 블로커 알림(상태 칸)", vm.Features.RestartBannerText);
    }

    [Fact]
    public Task The_restart_banner_follows_the_gate_changes_and_disappears_when_nothing_is_pending() => HeadlessHost.Run(async () =>
    {
        var gate = new FakeFeatureGate();
        var (_, _, vm) = await Create(gate: gate);
        Assert.False(vm.Features.HasRestartBanner);

        gate.PendingRestart = [FeatureIds.ChangeJournal];
        gate.RaiseChanged();
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.Features.HasRestartBanner);
        Assert.Contains("변경 일지", vm.Features.RestartBannerText);

        gate.PendingRestart = [];
        gate.RaiseChanged();
        Dispatcher.UIThread.RunJobs();
        Assert.False(vm.Features.HasRestartBanner);

        vm.Features.Dispose();
        gate.PendingRestart = [FeatureIds.MyBatis];
        gate.RaiseChanged();
        Dispatcher.UIThread.RunJobs();
        Assert.False(vm.Features.HasRestartBanner); // 정리한 뒤에는 따라가지 않는다
    });

    [Fact]
    public async Task Saving_a_change_that_needs_a_restart_keeps_the_window_open_on_the_features_tab_with_the_banner()
    {
        var gate = new FakeFeatureGate();
        var (_, _, vm) = await Create(new FeaturesSettings { Roles = [RoleIds.SqlDev] }, gate);
        var closed = 0;
        vm.CloseRequested += (_, _) => closed++;
        vm.SelectedTab = 4;

        Item(vm.Features, FeatureIds.MyBatis).IsChecked = true;
        gate.PendingRestart = [FeatureIds.MyBatis]; // 저장하면 게이트가 다시 해석해 이렇게 알린다
        await vm.SaveAsync();

        Assert.Equal(0, closed);
        Assert.Equal(SettingsViewModel.FeaturesTabIndex, vm.SelectedTab);
        Assert.True(vm.Features.HasRestartBanner);
        Assert.False(vm.Features.IsDirty);

        await vm.SaveAsync(); // 두 번째 저장은 바뀐 것이 없어 닫는다
        Assert.Equal(1, closed);
    }

    [Fact]
    public async Task Saving_a_change_that_applies_immediately_closes_the_window()
    {
        var (_, _, vm) = await Create();
        var closed = 0;
        vm.CloseRequested += (_, _) => closed++;

        Item(vm.Features, FeatureIds.Trace).IsChecked = false;
        await vm.SaveAsync();

        Assert.Equal(1, closed);
    }

    // ---- 명령 ----

    [Fact]
    public async Task Roles_command_is_registered_in_the_tools_menu_and_opens_the_settings_window()
    {
        var services = AppComposition.Create(new AppCompositionOptions { DevBuild = true });
        try
        {
            var registry = services.GetRequiredService<ICommandRegistry>();
            var def = registry.Commands.Single(c => c.Id == FeatureCommandIds.Roles);
            Assert.Equal(CommandCategory.Tools, def.Category);
            Assert.Contains("menu:tools/roles@11", def.Placements);
            Assert.Equal("Role·기능 고르기…", Ko.Lookup("Cmd." + FeatureCommandIds.Roles));
            Assert.Equal([FeatureCommandIds.Roles], FeatureSettingsCommands.Ids);

            var ui = new RecordingUi();
            await new FeatureRolesCommandHandler(ui).ExecuteAsync(new CommandContext(FeatureCommandIds.Roles, services.GetRequiredService<IContextKeys>()), CancellationToken.None);
            Assert.Equal(1, ui.SettingsShown);
        }
        finally
        {
            services.Dispose();
        }
    }

    // ---- Headless 렌더 ----

    private static async Task<SettingsViewModel> OpenVm(FeaturesSettings? features, IFeatureGate? gate, string language)
    {
        var type = typeof(Oranda.Language.LanguageAssembly).Assembly.GetType("Oranda.Language.Formatting.SqlFormatter")!;
        var formatter = (ISqlFormatter)Activator.CreateInstance(type, nonPublic: true)!;
        var settings = new SettingsService(new MemorySettingsStore());
        if (features is not null)
        {
            await settings.UpdateAsync(s => s with { Features = features });
        }

        var text = new ShellText(language: language);
        var page = new SqlFormatPage(new SqlFormatSettingsTests.ProfileStore(), formatter, settings.Current.FormatterProfileId, text);
        await page.Loaded;
        return new SettingsViewModel(settings, text, sqlFormat: page, featureGate: gate ?? new FakeFeatureGate());
    }

    private static void Pump()
    {
        for (var i = 0; i < 4; i++)
        {
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static (SettingsWindow Window, FeatureSettingsView View) Open(SettingsViewModel vm)
    {
        vm.SelectedTab = SettingsViewModel.FeaturesTabIndex;
        var window = new SettingsWindow { DataContext = vm };
        window.Show();
        Pump();
        window.UpdateLayout();
        var view = window.GetVisualDescendants().OfType<FeatureSettingsView>().Single();
        return (window, view);
    }

    private static void Render(Window window, string name)
    {
        var dir = Environment.GetEnvironmentVariable("ORANDA_RENDER_DIR");
        if (string.IsNullOrEmpty(dir))
        {
            return;
        }

        Directory.CreateDirectory(dir);
        var previous = Application.Current!.RequestedThemeVariant;
        try
        {
            foreach (var (variant, tone) in new[] { (ThemeVariant.Light, "light"), (ThemeVariant.Dark, "dark") })
            {
                Application.Current.RequestedThemeVariant = variant;
                Pump();
                window.UpdateLayout();
                var size = new PixelSize((int)Math.Ceiling(window.Bounds.Width), (int)Math.Ceiling(window.Bounds.Height));
                using var bmp = new RenderTargetBitmap(size);
                bmp.Render(window);
#pragma warning disable CS0618 // 렌더 확인용: 새 오버로드가 없는 헤드리스 비트맵
                bmp.Save(Path.Combine(dir, $"features-{name}-{tone}.png"));
#pragma warning restore CS0618
            }
        }
        finally
        {
            Application.Current.RequestedThemeVariant = previous;
            Pump();
        }
    }

    [Theory]
    [InlineData("ko")]
    [InlineData("en")]
    public Task The_tab_renders_in_both_languages_with_every_role_and_feature_and_named_controls(string language) => HeadlessHost.Run(async () =>
    {
        var vm = await OpenVm(new FeaturesSettings { Roles = [RoleIds.SqlDev, RoleIds.Tuner], Enabled = [FeatureIds.MyBatis], Disabled = [FeatureIds.GridEdit] }, null, language);
        var (window, view) = Open(vm);
        try
        {
            var tabs = window.GetVisualDescendants().OfType<TabControl>().First();
            Assert.Equal(SettingsViewModel.FeaturesTabIndex, tabs.SelectedIndex);
            Assert.Equal(vm.Features.TabTitle, ((TabItem)tabs.Items[1]!).Header);

            var checks = view.GetVisualDescendants().OfType<CheckBox>().ToList();
            Assert.Equal(6 + 25, checks.Count);
            Assert.All(checks, c => Assert.False(string.IsNullOrWhiteSpace(Avalonia.Automation.AutomationProperties.GetName(c))));
            Assert.All(checks, c => Assert.True(c.Focusable));
            Assert.Equal(2, view.GetVisualDescendants().OfType<RadioButton>().Count());
            var reset = view.FindControl<Button>("ResetButton")!;
            Assert.Equal(vm.Features.ResetText, Avalonia.Automation.AutomationProperties.GetName(reset));
            Assert.True(reset.IsEffectivelyEnabled);

            // 직접 끔/켬 표시 두 개
            var badges = view.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("feat-badge") && b.IsVisible).ToList();
            Assert.Equal(2, badges.Count);

            // 모든 글자가 해당 언어
            var hangul = view.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? string.Empty)
                .Any(t => t.Any(c => c is >= '가' and <= '힣'));
            Assert.Equal(language == "ko", hangul);

            Render(window, $"tuner-{language}");
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task The_tab_does_not_resize_the_window_beyond_the_cap_and_scrolls_its_body() => HeadlessHost.Run(async () =>
    {
        var vm = await OpenVm(null, null, "ko");
        var (window, view) = Open(vm);
        try
        {
            var size = window.Bounds.Size;
            Assert.True(window.Bounds.Width <= SettingsWindow.MaxBodyWidth + 24 + 0.5, $"{window.Bounds.Width}");

            var body = view.FindControl<ScrollViewer>("Body")!;
            Assert.True(body.Extent.Height > body.Viewport.Height, "25개 기능은 한 화면보다 길어 스크롤된다");
            Assert.True(body.Viewport.Width + 0.5 >= body.Extent.Width, "가로로 넘치지 않는다");

            var tabs = window.GetVisualDescendants().OfType<TabControl>().First();
            for (var i = 0; i < tabs.ItemCount; i++)
            {
                tabs.SelectedIndex = i;
                Pump();
                window.UpdateLayout();
                Assert.Equal(size, window.Bounds.Size); // 탭을 바꿔도 창 크기는 그대로
            }
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Banners_and_dimmed_rows_render_for_restart_viewer_only_and_requires() => HeadlessHost.Run(async () =>
    {
        var gate = new FakeFeatureGate { PendingRestart = [FeatureIds.MyBatis, FeatureIds.PlsqlWorkspace] };
        var vm = await OpenVm(new FeaturesSettings { Roles = [RoleIds.Viewer], Enabled = [FeatureIds.Trace] }, gate, "ko");
        var (window, view) = Open(vm);
        try
        {
            Assert.True(view.FindControl<Border>("RestartBanner")!.IsEffectivelyVisible);
            Assert.True(view.FindControl<Border>("ViewerBanner")!.IsEffectivelyVisible);
            Assert.Equal(vm.Features.RestartBannerText, view.FindControl<TextBlock>("RestartText")!.Text);
            Assert.False(view.FindControl<TextBlock>("PackNotice")!.IsEffectivelyVisible);

            var dimmed = view.GetVisualDescendants().OfType<CheckBox>().Where(c => !c.IsEnabled).ToList();
            // 쓰기 전용 기능이 못 고치게 흐리다. Role 확인란은 Role 로 고르기라 켜져 있다.
            Assert.Equal(FeatureCatalog.Features.Count(f => f.WritesOnly), dimmed.Count);

            Render(window, "viewer-restart");

            vm.Features.UseAllFeatures = true;
            Item(vm.Features, FeatureIds.PlsqlEditor).IsChecked = false;
            Pump();
            window.UpdateLayout();
            Assert.False(view.FindControl<Border>("ViewerBanner")!.IsEffectivelyVisible);
            var groups = view.FindControl<ItemsControl>("GroupList")!;
            Assert.Single(groups.GetVisualDescendants().OfType<CheckBox>().Where(c => !c.IsEnabled)); // plsqlWorkspace 만
            Render(window, "all-requires");

            vm.Features.UseRoles = true;
            vm.Features.ResetCommand.Execute(null);
            Pump();
            Render(window, "viewer-reset");
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Opening_the_window_with_the_features_tab_selected_keeps_that_tab() => HeadlessHost.Run(async () =>
    {
        var vm = await OpenVm(null, null, "ko");
        var (window, _) = Open(vm);
        try
        {
            Assert.Equal(SettingsViewModel.FeaturesTabIndex, vm.SelectedTab); // 창이 열린 뒤에도 모든 범주를 재 본 다음 처음 탭으로 돌아온다
            var tabs = window.GetVisualDescendants().OfType<TabControl>().First();
            Assert.Equal(SettingsViewModel.FeaturesTabIndex, tabs.SelectedIndex);
        }
        finally
        {
            window.Close();
        }
    });

    private sealed class RecordingUi : IShellUi
    {
        public int SettingsShown { get; private set; }

        public Oranda.App.Shell.Docking.DockWorkspace Docking => throw new NotSupportedException();

        public SettingsService Settings => throw new NotSupportedException();

        public Oranda.Core.Connection.ConnectionEnvironment? ActiveEnvironment { get; set; }

        public void ShowCommandPalette() => throw new NotSupportedException();

        public void ShowSettings() => SettingsShown++;

        public void ShowAbout() => throw new NotSupportedException();

        public void CloseMainWindow() => throw new NotSupportedException();

        public void FocusDocument() => throw new NotSupportedException();
    }
}
