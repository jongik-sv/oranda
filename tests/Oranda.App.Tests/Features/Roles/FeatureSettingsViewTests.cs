using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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
/// 설정 창 「기능」 탭(TSK-07-04 → TSK-09-08, D-017): 주 업무 라디오 5개·덧붙임 2개·지금 조합 보존·덧붙임 흐림과 이유, 접힌 「고급」 묶음(요약 숫자),
/// 직접 켬/끔 최소 기록·되돌리기·조회 전용 흐림·Requires·알 수 없는 id 보존, 팩 선언 바로 가기, 저장 흐름(SettingsService.UpdateAsync)·재시작 띠·
/// oranda.tools.roles 등록, Headless 렌더(ko/en·라이트/다크, 처음 보이는 입력 칸 10개 이하).
/// <c>ORANDA_RENDER_DIR</c> 가 있으면 상태별 PNG 를 그 폴더에 쓴다.
/// </summary>
[Collection("UiLanguage")]
public sealed class FeatureSettingsViewTests
{
    private static readonly ShellText Ko = new ShellText(language: "ko");

    /// <summary>「모든 기능」 으로 저장된 설정(D-013: 설정 없음은 기본 Role SQL 개발자다).</summary>
    private static readonly FeaturesSettings All = new() { AllFeatures = true };

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

    private static IEnumerable<string> Checked(FeatureSettingsPage page) => page.Items.Where(i => i.IsChecked).Select(i => i.Id);

    private static PrimaryWorkKind[] CheckedOptions(FeatureSettingsPage page) => [.. page.PrimaryOptions.Where(o => o.IsChecked).Select(o => o.Kind)];

    private static void AssertSet(IEnumerable<string> expected, IEnumerable<string>? actual) =>
        Assert.True(new HashSet<string>(expected).SetEquals(actual ?? []), $"expected [{string.Join(",", expected)}] actual [{string.Join(",", actual ?? [])}]");

    // ---- 처음 상태 ----

    [Fact]
    public async Task Without_a_features_section_the_tab_shows_sql_development_chosen_with_the_default_notice()
    {
        // D-013: 설정 없음 = 기본 Role(SQL 개발자). 주 업무 「SQL 개발」 이 골라지고 덧붙임은 꺼진 채 열린다.
        var (_, _, vm) = await Create();
        var page = vm.Features;

        Assert.Equal(PrimaryWorkKind.SqlDev, page.Primary);
        Assert.Equal([PrimaryWorkKind.SqlDev], CheckedOptions(page));
        Assert.Equal(5, page.PrimaryOptions.Count); // 지금 조합 줄은 맞지 않는 조합일 때만
        Assert.True(page.AddonsEnabled);
        Assert.False(page.PlsqlAddon);
        Assert.False(page.MyBatisAddon);
        Assert.False(page.HasAddonReason);
        Assert.True(page.IsDefaultRole);
        Assert.True(page.ShowDefaultRoleNotice);
        Assert.Equal("아직 주 업무를 고르지 않아 SQL 개발자 메뉴로 쓰는 중입니다. 아래에서 다른 주 업무를 고를 수 있습니다.", page.DefaultRoleNoticeText);
        Assert.Equal(25, page.Items.Count);
        AssertSet(FeatureCatalog.RoleDefaults([RoleIds.SqlDev]), Checked(page));
        Assert.All(page.Items, i => Assert.Equal(FeatureState.RoleDefault, i.State));
        Assert.False(page.HasOverrides);
        Assert.False(page.IsViewerOnly);
        Assert.False(page.ShowNoRoleNotice);
        Assert.False(page.ShowPackNotice);
        Assert.False(page.HasRestartBanner);
        Assert.False(page.IsDirty);
        Assert.False(page.IsAdvancedExpanded); // 고급은 접혀 있다(UX-10)
        Assert.Equal("기능 하나씩 켜고 끄기(고급) — 켜진 기능 10/25 · 직접 바꾼 것 0개", page.AdvancedHeader);
    }

    [Fact]
    public async Task In_the_default_state_saving_without_touching_keeps_nothing_chosen_and_any_change_is_a_choice()
    {
        // 손대지 않고 저장 = 설정 그대로(여전히 고른 적 없음 → 시작 화면 카드가 남는다).
        var (settings, store, vm) = await Create();
        vm.TabSize = 3;
        vm.Features.Primary = PrimaryWorkKind.SqlDev; // 이미 골라진 라디오를 다시 눌러도 바뀐 것이 아니다
        await vm.SaveAsync();
        Assert.Equal(new FeaturesSettings(), store.Saved.Features);
        Assert.True(FeatureCatalog.IsUnchosen(settings.Current.Features));

        // 기능 하나를 켜면 R1 을 고른 것으로 저장된다.
        (settings, store, vm) = await Create();
        Item(vm.Features, FeatureIds.Trace).IsChecked = true;
        Assert.False(vm.Features.ShowDefaultRoleNotice); // 바꾸면 안내 띠가 사라진다
        await vm.SaveAsync();
        Assert.Equal([RoleIds.SqlDev], store.Saved.Features.Roles);
        Assert.Equal([FeatureIds.Trace], store.Saved.Features.Enabled);
        Assert.False(store.Saved.Features.AllFeatures);
        Assert.False(FeatureCatalog.IsUnchosen(settings.Current.Features));

        // 덧붙임을 켜면 [주 업무, 덧붙임].
        (_, store, vm) = await Create();
        vm.Features.PlsqlAddon = true;
        Assert.False(vm.Features.ShowDefaultRoleNotice);
        await vm.SaveAsync();
        Assert.Equal([RoleIds.SqlDev, RoleIds.PlsqlDev], store.Saved.Features.Roles);

        // 「모든 기능」 은 allFeatures 로 따로 저장된다.
        (_, store, vm) = await Create();
        vm.Features.Primary = PrimaryWorkKind.All;
        Assert.All(vm.Features.Items, i => Assert.True(i.IsChecked, i.Id));
        await vm.SaveAsync();
        Assert.True(store.Saved.Features.AllFeatures);
        Assert.Null(store.Saved.Features.Roles);
        Assert.Equal(FeatureIds.All.Count, FeatureCatalog.Resolve(store.Saved.Features).Count);
    }

    [Fact]
    public async Task A_saved_use_all_features_opens_on_all_with_the_addons_dimmed_as_included()
    {
        var (_, _, vm) = await Create(All);
        var page = vm.Features;

        Assert.Equal(PrimaryWorkKind.All, page.Primary);
        Assert.Equal([PrimaryWorkKind.All], CheckedOptions(page));
        Assert.False(page.IsDefaultRole);
        Assert.False(page.ShowDefaultRoleNotice);
        Assert.False(page.AddonsEnabled);
        Assert.True(page.PlsqlAddon); // 이미 들어 있어 켜져 보인다
        Assert.True(page.MyBatisAddon);
        Assert.Equal("모든 기능에 이미 들어 있습니다", page.AddonReason);
        Assert.All(page.Items, i => Assert.True(i.IsChecked, i.Id));
        Assert.All(page.Items, i => Assert.Equal(FeatureState.RoleDefault, i.State));
        Assert.False(page.HasOverrides);
        Assert.False(page.IsDirty);
        Assert.Equal("기능 하나씩 켜고 끄기(고급) — 켜진 기능 25/25 · 직접 바꾼 것 0개", page.AdvancedHeader);

        // 흐린 덧붙임을 눌러도 바뀌지 않는다.
        page.PlsqlAddon = false;
        Assert.True(page.PlsqlAddon);
        Assert.False(page.IsDirty);
    }

    [Fact]
    public async Task A_legacy_all_features_file_with_a_disabled_feature_opens_on_all_and_saves_the_flag()
    {
        // D-013 전 「모든 기능 쓰기」 + 직접 끔(roles 없음).
        var (_, store, vm) = await Create(new FeaturesSettings { Disabled = [FeatureIds.Trace] });
        var page = vm.Features;

        Assert.Equal(PrimaryWorkKind.All, page.Primary);
        Assert.False(page.ShowDefaultRoleNotice);
        Assert.False(Item(page, FeatureIds.Trace).IsChecked);
        Assert.Equal(FeatureState.Disabled, Item(page, FeatureIds.Trace).State);
        Assert.Equal(1, page.OverrideCount);

        Item(page, FeatureIds.DbTools).IsChecked = false;
        await vm.SaveAsync();
        Assert.True(store.Saved.Features.AllFeatures);
        AssertSet([FeatureIds.Trace, FeatureIds.DbTools], store.Saved.Features.Disabled);
    }

    [Fact]
    public async Task Choices_groups_and_items_come_from_the_catalog_with_localized_names()
    {
        var (_, _, vm) = await Create();
        var page = vm.Features;

        Assert.Equal(["모든 기능", "SQL 개발", "튜닝", "운영 DBA", "조회만"], page.PrimaryOptions.Select(o => o.Name));
        Assert.Equal(PrimaryWork.Choices, page.PrimaryOptions.Select(o => o.Kind));
        Assert.All(page.PrimaryOptions, o => Assert.True(o.HasDescription, o.Name));
        Assert.Equal("느린 SQL 찾기·계획·통계·트레이스·보고서", page.PrimaryOptions[2].Description); // 튜닝 = DB 튜너 Role 설명
        Assert.Equal("튜닝 - 느린 SQL 찾기·계획·통계·트레이스·보고서", page.PrimaryOptions[2].AccessibleName);
        Assert.Equal("주 업무", page.PrimaryTitle);
        Assert.Equal("함께 하는 일", page.AddonTitle);
        Assert.Equal("PL/SQL 패키지·프로시저도 고친다", page.PlsqlAddonText);
        Assert.Equal("MyBatis 매퍼를 쓴다", page.MyBatisAddonText);
        Assert.Equal("패키지·프로시저 편집·컴파일·실행", page.PlsqlAddonTip);

        Assert.Equal(Enum.GetValues<FeatureGroup>().Length, page.Groups.Count);
        Assert.Equal(["개발 보조", "PL/SQL 개발", "MyBatis 개발", "튜닝", "운영"], page.Groups.Select(g => g.Name));
        Assert.Equal(FeatureCatalog.Features.Count(f => f.Group == FeatureGroup.Tuning), page.Groups[3].Items.Count);
        Assert.Equal(FeatureIds.All.Order(), page.Items.Select(i => i.Id).Order());
        Assert.All(page.Items, i => Assert.DoesNotContain("Feature.", i.Name)); // 문구 키가 그대로 보이지 않는다
        Assert.All(page.PrimaryOptions, o => Assert.DoesNotContain("Features.", o.Name + o.Description));
        Assert.Equal("그리드 편집", Item(page, FeatureIds.GridEdit).Name);
        Assert.Equal("기본값으로 되돌리기", page.ResetText);
    }

    [Fact]
    public async Task English_text_is_used_when_the_language_is_english()
    {
        var (_, _, vm) = await Create(new FeaturesSettings { Roles = [RoleIds.Tuner, RoleIds.Dba] }, text: new ShellText(language: "en"));
        var page = vm.Features;

        static bool Hangul(string s) => s.Any(c => c is >= '가' and <= '힣');
        Assert.NotEqual("기능", page.TabTitle);
        Assert.DoesNotContain(page.Items, i => Hangul(i.Name));
        Assert.DoesNotContain(page.PrimaryOptions, o => Hangul(o.Name + o.Description));
        Assert.DoesNotContain(page.Groups, g => Hangul(g.Name));
        Assert.False(Hangul(page.ResetText + page.AdvancedHeader + page.PrimaryTitle + page.AddonTitle + page.PlsqlAddonText + page.MyBatisAddonText
            + page.AddonReason + page.PackNoticeText + page.PackLinkText + page.DefaultRoleNoticeText + page.NoRoleNoticeText));
        Assert.Equal("Current combination: DB Tuner, Operations DBA (keep as is)", page.PrimaryOptions[^1].Name);
    }

    [Fact]
    public async Task Initial_state_follows_a_saved_features_section()
    {
        var (_, _, vm) = await Create(new FeaturesSettings { Roles = [RoleIds.SqlDev, RoleIds.MyBatisDev], Enabled = [FeatureIds.Trace], Disabled = [FeatureIds.GridEdit] });
        var page = vm.Features;

        Assert.Equal(PrimaryWorkKind.SqlDev, page.Primary);
        Assert.False(page.PlsqlAddon);
        Assert.True(page.MyBatisAddon);
        Assert.False(page.ShowDefaultRoleNotice);
        Assert.True(Item(page, FeatureIds.Trace).IsChecked);
        Assert.Equal(FeatureState.Enabled, Item(page, FeatureIds.Trace).State);
        Assert.Equal("직접 켬", Item(page, FeatureIds.Trace).StateText);
        Assert.False(Item(page, FeatureIds.GridEdit).IsChecked);
        Assert.Equal(FeatureState.Disabled, Item(page, FeatureIds.GridEdit).State);
        Assert.Equal("직접 끔", Item(page, FeatureIds.GridEdit).StateText);
        Assert.True(page.HasOverrides);
        AssertSet(FeatureCatalog.Resolve(vm.Features.ToSettings()), Checked(page));

        // 접힌 머리의 숫자 = 켜진 기능 수 / 25 · 배지 수.
        var on = FeatureCatalog.Resolve(vm.Features.ToSettings()).Count;
        Assert.Equal(on, page.EnabledCount);
        Assert.Equal(2, page.OverrideCount);
        Assert.Equal($"기능 하나씩 켜고 끄기(고급) — 켜진 기능 {on}/25 · 직접 바꾼 것 2개", page.AdvancedHeader);
    }

    [Fact]
    public async Task A_feature_in_both_lists_is_off_because_off_wins()
    {
        var (_, _, vm) = await Create(new FeaturesSettings { Roles = [RoleIds.Viewer], Enabled = [FeatureIds.Trace], Disabled = [FeatureIds.Trace] });

        Assert.False(Item(vm.Features, FeatureIds.Trace).IsChecked);
    }

    // ---- 주 업무·덧붙임 → 확인란 ----

    [Fact]
    public async Task Choosing_a_primary_work_and_addons_turns_on_the_union_of_their_defaults_and_the_header_follows()
    {
        var (_, _, vm) = await Create(All);
        var page = vm.Features;

        page.Primary = PrimaryWorkKind.SqlDev;
        Assert.True(page.AddonsEnabled);
        Assert.False(page.PlsqlAddon); // 모든 기능에서 보이던 「포함」 표시는 고른 값이 아니다
        AssertSet(FeatureCatalog.RoleDefaults([RoleIds.SqlDev]), Checked(page));
        Assert.Equal("기능 하나씩 켜고 끄기(고급) — 켜진 기능 10/25 · 직접 바꾼 것 0개", page.AdvancedHeader);

        page.MyBatisAddon = true;
        AssertSet(FeatureCatalog.RoleDefaults([RoleIds.SqlDev, RoleIds.MyBatisDev]), Checked(page));
        Assert.True(Item(page, FeatureIds.MyBatis).IsChecked);
        Assert.False(Item(page, FeatureIds.Trace).IsChecked);
        Assert.Equal(FeatureCatalog.RoleDefaults([RoleIds.SqlDev, RoleIds.MyBatisDev]).Count, page.EnabledCount);

        page.Primary = PrimaryWorkKind.Tuner; // 덧붙임은 주 업무를 바꿔도 남는다
        AssertSet(FeatureCatalog.RoleDefaults([RoleIds.Tuner, RoleIds.MyBatisDev]), Checked(page));

        page.Primary = PrimaryWorkKind.All; // 다시 모든 기능
        Assert.All(page.Items, i => Assert.True(i.IsChecked, i.Id));
    }

    [Fact]
    public async Task Sql_development_with_plsql_saves_exactly_those_two_roles_and_all_features_saves_the_flag()
    {
        // 인수조건: 「SQL 개발」 + PL/SQL 저장 → features.roles = ["sqlDev","plsqlDev"], 「모든 기능」 → allFeatures: true.
        var (_, store, vm) = await Create(new FeaturesSettings { Roles = [RoleIds.Dba] });
        var closed = false;
        vm.CloseRequested += (_, _) => closed = true;

        vm.Features.Primary = PrimaryWorkKind.SqlDev;
        vm.Features.PlsqlAddon = true;
        await vm.SaveAsync();

        Assert.Equal([RoleIds.SqlDev, RoleIds.PlsqlDev], store.Saved.Features.Roles);
        Assert.False(store.Saved.Features.AllFeatures);
        Assert.Empty(store.Saved.Features.Enabled);
        Assert.Empty(store.Saved.Features.Disabled);
        Assert.True(closed);

        (_, store, vm) = await Create(new FeaturesSettings { Roles = [RoleIds.Dba] });
        vm.Features.Primary = PrimaryWorkKind.All;
        await vm.SaveAsync();
        Assert.Null(store.Saved.Features.Roles);
        Assert.True(store.Saved.Features.AllFeatures); // D-013: 설정 없음(기본 Role)과 구별해 저장
    }

    [Fact]
    public async Task Addon_choices_are_remembered_while_they_are_dimmed()
    {
        var (_, store, vm) = await Create(new FeaturesSettings { Roles = [RoleIds.Dba, RoleIds.PlsqlDev] });
        var page = vm.Features;
        Assert.True(page.PlsqlAddon);

        page.Primary = PrimaryWorkKind.Viewer;
        Assert.False(page.PlsqlAddon); // 조회만에서는 꺼져 보인다
        page.Primary = PrimaryWorkKind.All;
        Assert.True(page.PlsqlAddon); // 모든 기능에서는 포함으로 보인다
        page.Primary = PrimaryWorkKind.Dba;
        Assert.True(page.PlsqlAddon); // 고른 값이 돌아온다

        await vm.SaveAsync();
        Assert.Equal([RoleIds.Dba, RoleIds.PlsqlDev], store.Saved.Features.Roles);
    }

    [Fact]
    public async Task Viewer_dims_the_addons_with_the_reason_and_never_saves_them()
    {
        // 인수조건: 조회만이면 덧붙임을 켤 수 없다(덧붙이면 조회 전용 차단이 풀린다, D-006).
        var (_, store, vm) = await Create(new FeaturesSettings { Roles = [RoleIds.SqlDev, RoleIds.MyBatisDev] });
        var page = vm.Features;

        page.Primary = PrimaryWorkKind.Viewer;
        Assert.False(page.AddonsEnabled);
        Assert.Equal("덧붙이면 조회 전용 차단이 풀립니다", page.AddonReason);
        page.PlsqlAddon = true;
        page.MyBatisAddon = true;
        Assert.False(page.PlsqlAddon);
        Assert.False(page.MyBatisAddon);
        Assert.True(page.IsViewerOnly);

        await vm.SaveAsync();
        Assert.Equal([RoleIds.Viewer], store.Saved.Features.Roles);
        Assert.True(FeatureCatalog.IsReadOnly(store.Saved.Features));
    }

    // ---- 지금 조합(맞지 않는 기존 조합, D-017) ----

    [Fact]
    public async Task An_existing_combination_shows_as_the_current_combination_row_and_saving_keeps_the_roles()
    {
        // 인수조건: ["tuner","dba"] 설정을 열어 저장만 하면 roles 그대로.
        var original = new FeaturesSettings { Roles = [RoleIds.Tuner, RoleIds.Dba] };
        var (_, store, vm) = await Create(original);
        var page = vm.Features;

        Assert.Equal(PrimaryWorkKind.Custom, page.Primary);
        Assert.Equal(6, page.PrimaryOptions.Count);
        var custom = page.PrimaryOptions[^1];
        Assert.True(custom.IsCustom);
        Assert.True(custom.IsChecked);
        Assert.Equal("지금 조합: DB 튜너, 운영 DBA(그대로 두기)", custom.Name);
        Assert.False(custom.HasDescription);
        Assert.False(page.AddonsEnabled);
        Assert.Equal("위에서 주 업무를 고르면 덧붙일 수 있습니다", page.AddonReason);
        Assert.True(page.ShowPackNotice); // 튜너가 들어 있다
        AssertSet(FeatureCatalog.RoleDefaults([RoleIds.Tuner, RoleIds.Dba]), Checked(page));

        vm.TabSize = 5;
        await vm.SaveAsync();
        Assert.Equal(original, store.Saved.Features);

        // 고급에서 기능 하나를 바꿔 저장해도 roles 는 그대로다.
        (_, store, vm) = await Create(original);
        Item(vm.Features, FeatureIds.MyBatisLog).IsChecked = true;
        await vm.SaveAsync();
        Assert.Equal([RoleIds.Tuner, RoleIds.Dba], store.Saved.Features.Roles);
        Assert.Equal([FeatureIds.MyBatisLog], store.Saved.Features.Enabled);

        // 다른 주 업무를 골랐다가 「지금 조합」 으로 돌아오면 그 조합이 다시 저장된다(되돌림).
        (_, store, vm) = await Create(original);
        vm.Features.Primary = PrimaryWorkKind.Dba;
        Assert.False(vm.Features.ShowPackNotice);
        vm.Features.Primary = PrimaryWorkKind.Custom;
        Assert.True(vm.Features.ShowPackNotice);
        await vm.SaveAsync();
        Assert.Equal([RoleIds.Tuner, RoleIds.Dba], store.Saved.Features.Roles);

        // 주 업무를 고르면 그 값이 된다.
        (_, store, vm) = await Create(original);
        vm.Features.Primary = PrimaryWorkKind.Tuner;
        vm.Features.PlsqlAddon = true;
        await vm.SaveAsync();
        Assert.Equal([RoleIds.Tuner, RoleIds.PlsqlDev], store.Saved.Features.Roles);
    }

    [Fact]
    public async Task The_current_combination_cannot_be_chosen_when_the_settings_fit_a_primary_work()
    {
        var (_, _, vm) = await Create(new FeaturesSettings { Roles = [RoleIds.Tuner] });

        vm.Features.Primary = PrimaryWorkKind.Custom;
        Assert.Equal(PrimaryWorkKind.Tuner, vm.Features.Primary);
        Assert.False(vm.Features.IsDirty);
    }

    [Fact]
    public async Task Viewer_mixed_with_addons_is_a_combination_and_shows_the_addons_it_holds()
    {
        var (_, _, vm) = await Create(new FeaturesSettings { Roles = [RoleIds.Viewer, RoleIds.PlsqlDev] });

        Assert.Equal(PrimaryWorkKind.Custom, vm.Features.Primary);
        Assert.True(vm.Features.PlsqlAddon);
        Assert.False(vm.Features.MyBatisAddon);
        Assert.False(vm.Features.AddonsEnabled);
        Assert.False(vm.Features.IsViewerOnly); // 덧붙임이 섞여 조회 전용 차단이 풀린 조합
    }

    [Fact]
    public async Task An_empty_role_list_shows_the_no_role_notice_only_while_the_combination_is_kept()
    {
        var (_, store, vm) = await Create(new FeaturesSettings { Roles = [] });
        var page = vm.Features;

        Assert.Equal("지금 조합: Role 없음(그대로 두기)", page.PrimaryOptions[^1].Name);
        Assert.True(page.ShowNoRoleNotice);
        Assert.Empty(Checked(page));
        Assert.Equal("기능 하나씩 켜고 끄기(고급) — 켜진 기능 0/25 · 직접 바꾼 것 0개", page.AdvancedHeader);

        page.Primary = PrimaryWorkKind.SqlDev;
        Assert.False(page.ShowNoRoleNotice);
        page.Primary = PrimaryWorkKind.Custom;
        Assert.True(page.ShowNoRoleNotice);
        await vm.SaveAsync();
        Assert.NotNull(store.Saved.Features.Roles);
        Assert.Empty(store.Saved.Features.Roles!);
    }

    // ---- 팩 선언 바로 가기 ----

    [Fact]
    public async Task The_tuner_shows_the_license_pack_notice_with_a_link_that_opens_the_connection_manager()
    {
        var (_, _, vm) = await Create(All);
        var page = vm.Features;
        var opened = 0;
        page.OpenConnectionManager = () =>
        {
            opened++;
            return Task.CompletedTask;
        };

        Assert.False(page.ShowPackNotice);
        page.Primary = PrimaryWorkKind.Tuner;
        Assert.True(page.ShowPackNotice);
        Assert.Contains("Diagnostics", page.PackNoticeText);
        Assert.Equal("접속 관리자 열기", page.PackLinkText);
        Assert.Equal(page.PackNoticeText + " — 접속 관리자 열기", page.PackLinkName);

        var dirty = page.IsDirty;
        await page.OpenConnectionManagerLinkCommand.ExecuteAsync(null);
        Assert.Equal(1, opened);
        Assert.Equal(dirty, page.IsDirty); // 링크는 설정을 바꾸지 않는다

        page.Primary = PrimaryWorkKind.All;
        Assert.False(page.ShowPackNotice);
        page.Primary = PrimaryWorkKind.SqlDev;
        Assert.False(page.ShowPackNotice);
    }

    [Fact]
    public async Task Without_a_delegate_the_link_runs_the_connection_manager_command_through_the_shell_runner()
    {
        Assert.Equal("oranda.conn.manage", FeatureSettingsPage.ConnectionManagerCommandId);
        var (_, _, vm) = await Create(new FeaturesSettings { Roles = [RoleIds.Tuner] });
        var old = ShellCommandRunner.Runner;
        var runs = new List<string>();
        try
        {
            ShellCommandRunner.Runner = id =>
            {
                runs.Add(id);
                return Task.CompletedTask;
            };
            await vm.Features.OpenConnectionManagerLinkCommand.ExecuteAsync(null);
        }
        finally
        {
            ShellCommandRunner.Runner = old;
        }

        Assert.Equal(["oranda.conn.manage"], runs);
        Assert.False(vm.Features.IsDirty);
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
        Assert.Equal(2, page.OverrideCount);

        await vm.SaveAsync();

        AssertSet([FeatureIds.Trace], store.Saved.Features.Enabled);
        AssertSet([FeatureIds.GridEdit], store.Saved.Features.Disabled);
        AssertSet([RoleIds.SqlDev], store.Saved.Features.Roles);
    }

    [Fact]
    public async Task An_override_that_becomes_the_default_after_changing_the_primary_work_is_dropped_on_save()
    {
        var (_, store, vm) = await Create(new FeaturesSettings { Roles = [RoleIds.SqlDev], Enabled = [FeatureIds.Trace], Disabled = [FeatureIds.GridEdit] });
        var page = vm.Features;

        page.Primary = PrimaryWorkKind.Tuner; // 튜너는 trace 가 기본 켜짐, gridEdit 가 기본 꺼짐
        Assert.Equal(FeatureState.RoleDefault, Item(page, FeatureIds.Trace).State);
        Assert.Equal(FeatureState.RoleDefault, Item(page, FeatureIds.GridEdit).State);
        Assert.Equal(0, page.OverrideCount);
        await vm.SaveAsync();

        Assert.Equal([RoleIds.Tuner], store.Saved.Features.Roles);
        Assert.Empty(store.Saved.Features.Enabled);
        Assert.Empty(store.Saved.Features.Disabled);
    }

    [Fact]
    public async Task In_all_features_turning_a_feature_off_is_recorded_as_disabled_only()
    {
        var (_, store, vm) = await Create(All);

        Item(vm.Features, FeatureIds.Trace).IsChecked = false;
        Assert.Equal("기능 하나씩 켜고 끄기(고급) — 켜진 기능 24/25 · 직접 바꾼 것 1개", vm.Features.AdvancedHeader);
        await vm.SaveAsync();

        Assert.Null(store.Saved.Features.Roles);
        Assert.True(store.Saved.Features.AllFeatures);
        AssertSet([FeatureIds.Trace], store.Saved.Features.Disabled);
        Assert.Empty(store.Saved.Features.Enabled);
    }

    [Fact]
    public async Task Saved_overrides_resolve_to_exactly_the_checked_boxes()
    {
        var (_, store, vm) = await Create(new FeaturesSettings { Roles = [RoleIds.Tuner, RoleIds.PlsqlDev] });
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
    public async Task Reset_clears_the_overrides_and_keeps_the_chosen_primary_work()
    {
        var (_, store, vm) = await Create(new FeaturesSettings { Roles = [RoleIds.SqlDev], Enabled = [FeatureIds.Trace], Disabled = [FeatureIds.GridEdit] });
        var page = vm.Features;
        Assert.True(page.ResetCommand.CanExecute(null));

        page.ResetCommand.Execute(null);

        Assert.False(page.HasOverrides);
        Assert.False(page.ResetCommand.CanExecute(null));
        Assert.All(page.Items, i => Assert.Equal(FeatureState.RoleDefault, i.State));
        AssertSet(FeatureCatalog.RoleDefaults([RoleIds.SqlDev]), Checked(page));
        Assert.Equal(PrimaryWorkKind.SqlDev, page.Primary);
        Assert.Equal(0, page.OverrideCount);

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

        Assert.Equal(PrimaryWorkKind.Viewer, page.Primary);
        Assert.True(page.IsViewerOnly);
        Assert.Equal("조회만이면 DML·DDL·PL/SQL 실행을 막습니다", page.ViewerNoticeText);
        foreach (var def in FeatureCatalog.Features.Where(f => f.WritesOnly))
        {
            var item = Item(page, def.Id);
            Assert.False(item.IsChecked, def.Id);
            Assert.True(item.IsBlocked, def.Id);
            Assert.False(item.CanEdit, def.Id);
            Assert.Equal("조회만에서는 켤 수 없습니다. 다른 주 업무를 고르세요.", item.BlockedReason);
            Assert.Equal(string.Empty, item.StateText); // 못 켜는 기능에는 「직접 켬」을 붙이지 않는다
        }

        Assert.True(Item(page, FeatureIds.DataExport).IsChecked);
        Assert.False(Item(page, FeatureIds.DataExport).IsBlocked);
        Assert.Equal(0, page.OverrideCount); // 막힌 직접 켬은 배지로 세지 않는다

        // 설정 쪽 해석과 같다: Enabled 에 gridEdit 가 있어도 조회 전용만이면 꺼진다.
        AssertSet(FeatureCatalog.Resolve(page.ToSettings()), Checked(page));

        // 다른 주 업무를 고르면 풀린다.
        page.Primary = PrimaryWorkKind.SqlDev;
        Assert.False(page.IsViewerOnly);
        // plsqlWorkspace 는 PL/SQL 편집기가 꺼져 있어(SQL 개발자 기본에 없음) Requires 로 아직 못 켠다.
        Assert.All(FeatureCatalog.Features.Where(f => f.WritesOnly && f.Id != FeatureIds.PlsqlWorkspace), f => Assert.False(Item(page, f.Id).IsBlocked, f.Id));
        Assert.True(Item(page, FeatureIds.GridEdit).IsChecked);
        Assert.Contains("PL/SQL 편집기", Item(page, FeatureIds.PlsqlWorkspace).BlockedReason);
        await vm.SaveAsync();
        AssertSet([RoleIds.SqlDev], store.Saved.Features.Roles);
    }

    [Fact]
    public async Task Viewer_notice_is_not_shown_in_all_features_or_in_a_combination_with_other_roles()
    {
        var (_, _, vm) = await Create(new FeaturesSettings { Roles = [RoleIds.Viewer, RoleIds.Tuner] });
        Assert.Equal(PrimaryWorkKind.Custom, vm.Features.Primary);
        Assert.False(vm.Features.IsViewerOnly);
        vm.Features.Primary = PrimaryWorkKind.All;
        Assert.False(vm.Features.IsViewerOnly);
        Assert.All(vm.Features.Items, i => Assert.False(i.IsBlocked, i.Id));
    }

    // ---- Requires ----

    [Fact]
    public async Task A_feature_whose_required_feature_is_off_is_dimmed_with_the_reason_and_comes_back_when_it_is_on()
    {
        var (_, _, vm) = await Create(new FeaturesSettings { Roles = [RoleIds.SqlDev, RoleIds.PlsqlDev] });
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
    public async Task Requires_applies_in_all_features_too()
    {
        var (_, store, vm) = await Create(All);

        Item(vm.Features, FeatureIds.PlsqlEditor).IsChecked = false;
        Assert.True(Item(vm.Features, FeatureIds.PlsqlWorkspace).IsBlocked);
        await vm.SaveAsync();

        AssertSet(FeatureCatalog.Resolve(store.Saved.Features), Checked(vm.Features));
        Assert.DoesNotContain(FeatureIds.PlsqlWorkspace, FeatureCatalog.Resolve(store.Saved.Features));
    }

    // ---- 저장 흐름 ----

    public static TheoryData<FeaturesSettings> ExistingShapes() => new()
    {
        new FeaturesSettings(),
        new FeaturesSettings { AllFeatures = true },
        new FeaturesSettings { Disabled = [FeatureIds.Trace] },
        new FeaturesSettings { Roles = [] },
        new FeaturesSettings { Roles = [RoleIds.Viewer] },
        new FeaturesSettings { Roles = [RoleIds.SqlDev, RoleIds.PlsqlDev, RoleIds.MyBatisDev] },
        new FeaturesSettings { Roles = [RoleIds.Tuner, RoleIds.Dba] },
        new FeaturesSettings { Roles = [RoleIds.PlsqlDev] },
        new FeaturesSettings { Roles = [RoleIds.Viewer, RoleIds.MyBatisDev] },
        new FeaturesSettings { Roles = [.. RoleIds.All] },
        new FeaturesSettings { Roles = [RoleIds.Dba], Enabled = [FeatureIds.GridEdit, "futureFeature"], Disabled = ["otherFuture"] },
        new FeaturesSettings { Roles = ["futureRole"] },
    };

    [Theory]
    [MemberData(nameof(ExistingShapes))]
    public async Task Saving_without_touching_the_tab_leaves_any_existing_features_section_as_it_was(FeaturesSettings original)
    {
        // 최소 형태가 아닌 값(기본과 같은 Enabled)·이 버전이 모르는 id·지금 조합도 건드리지 않는다(TRD 11.12 왕복).
        var (settings, store, vm) = await Create(original);
        vm.AutoCommit = !vm.AutoCommit;
        _ = vm.Features.PrimaryOptions.Single(o => o.IsChecked); // 어느 모양이든 라디오 하나가 골라져 있다

        await vm.SaveAsync();

        Assert.Equal(original, store.Saved.Features);
        Assert.Equal(original, settings.Current.Features);
    }

    [Fact]
    public async Task Unknown_role_and_feature_ids_are_kept_when_the_tab_is_edited()
    {
        var (_, store, vm) = await Create(new FeaturesSettings { Roles = [RoleIds.SqlDev, "futureRole"], Enabled = ["futureOn"], Disabled = ["futureOff"] });
        Assert.Equal(PrimaryWorkKind.Custom, vm.Features.Primary); // 모르는 Role id 는 지금 조합으로 보존된다

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
    public async Task Choosing_a_primary_work_replaces_unknown_role_ids_but_keeps_unknown_feature_ids()
    {
        var (_, store, vm) = await Create(new FeaturesSettings { Roles = [RoleIds.SqlDev, "futureRole"], Enabled = ["futureOn"], Disabled = ["futureOff"] });

        vm.Features.Primary = PrimaryWorkKind.SqlDev;
        await vm.SaveAsync();

        Assert.Equal([RoleIds.SqlDev], store.Saved.Features.Roles);
        Assert.Equal(["futureOn"], store.Saved.Features.Enabled);
        Assert.Equal(["futureOff"], store.Saved.Features.Disabled);
    }

    [Fact]
    public async Task Other_settings_in_the_window_are_saved_together_with_the_features()
    {
        var (_, store, vm) = await Create(All);
        vm.TabSize = 7;
        vm.Features.Primary = PrimaryWorkKind.Dba;

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
    public async Task Saving_an_addon_that_needs_a_restart_keeps_the_window_open_on_the_features_tab_with_the_banner()
    {
        var gate = new FakeFeatureGate();
        var (_, _, vm) = await Create(new FeaturesSettings { Roles = [RoleIds.SqlDev] }, gate);
        var closed = 0;
        vm.CloseRequested += (_, _) => closed++;
        vm.SelectedTab = 4;

        vm.Features.MyBatisAddon = true;
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
        var (_, _, vm) = await Create(All);
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
            Assert.Contains(registry.Commands, c => c.Id == FeatureSettingsPage.ConnectionManagerCommandId); // 팩 링크가 부르는 명령이 있다

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

    private static void Relayout(Window window)
    {
        Pump();
        window.UpdateLayout();
        Pump();
    }

    /// <summary>보이는 입력 칸(라디오·확인란·단추·고급 머리 토글). 인수조건 「처음 열면 10개 이하」 를 센다.</summary>
    private static List<Control> VisibleInputs(Control view) =>
        [.. view.GetVisualDescendants().OfType<Control>()
            .Where(c => c is RadioButton or CheckBox or Button or ToggleButton && c.IsEffectivelyVisible && !c.GetVisualAncestors().OfType<ScrollBar>().Any())
            .Distinct()];

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
    public Task The_tab_opens_with_ten_or_fewer_inputs_and_the_advanced_group_lists_every_feature(string language) => HeadlessHost.Run(async () =>
    {
        var vm = await OpenVm(new FeaturesSettings { Roles = [RoleIds.Tuner, RoleIds.MyBatisDev], Enabled = [FeatureIds.Flashback], Disabled = [FeatureIds.Trace] }, null, language);
        var (window, view) = Open(vm);
        try
        {
            var tabs = window.GetVisualDescendants().OfType<TabControl>().First();
            Assert.Equal(SettingsViewModel.FeaturesTabIndex, tabs.SelectedIndex);
            Assert.Equal(vm.Features.TabTitle, ((TabItem)tabs.Items[1]!).Header);

            // 처음: 라디오 5 + 덧붙임 2 + 고급 머리 1 + 팩 링크 1(튜닝) = 9 ≤ 10(조사 33).
            var inputs = VisibleInputs(view);
            Assert.True(inputs.Count <= 10, $"보이는 입력 칸 {inputs.Count}개");
            Assert.Equal(9, inputs.Count);
            var radios = view.GetVisualDescendants().OfType<RadioButton>().Where(r => r.IsEffectivelyVisible).ToList();
            Assert.Equal(5, radios.Count);
            Assert.Single(radios, r => r.IsChecked == true);
            Assert.All(inputs, c => Assert.True(c.Focusable, c.GetType().Name)); // 키보드만으로 닿는다
            // 화면 읽기 이름: 라디오·확인란·팩 링크(고급 머리 토글은 Expander 자신의 이름을 읽는다, 아래).
            Assert.All(inputs.Where(c => c is RadioButton or CheckBox || c.Name == "PackLink"),
                c => Assert.False(string.IsNullOrWhiteSpace(Avalonia.Automation.AutomationProperties.GetName(c)), c.GetType().Name));
            Assert.True(view.FindControl<CheckBox>("MyBatisAddonBox")!.IsChecked);
            Assert.False(view.FindControl<CheckBox>("PlsqlAddonBox")!.IsChecked);
            Assert.True(view.FindControl<Button>("PackLink")!.IsEffectivelyVisible);
            var expander = view.FindControl<Expander>("AdvancedExpander")!;
            Assert.False(expander.IsExpanded);
            Assert.Equal(vm.Features.AdvancedHeader, view.FindControl<TextBlock>("AdvancedHeaderText")!.Text);
            Assert.Equal(vm.Features.AdvancedHeader, Avalonia.Automation.AutomationProperties.GetName(expander));
            Assert.DoesNotContain(inputs, c => c.Name == "ResetButton"); // 되돌리기는 고급 안에 있다

            // 모든 글자가 해당 언어
            bool HangulShown() => view.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? string.Empty)
                .Any(t => t.Any(c => c is >= '가' and <= '힣'));
            Assert.Equal(language == "ko", HangulShown());
            Render(window, $"tuner-{language}");

            // 고급 펼침: 25개 확인란과 배지 2개(직접 켬·끔), 되돌리기.
            vm.Features.IsAdvancedExpanded = true;
            Relayout(window);
            Assert.True(expander.IsExpanded);
            var featureChecks = view.FindControl<ItemsControl>("GroupList")!.GetVisualDescendants().OfType<CheckBox>().ToList();
            Assert.Equal(25, featureChecks.Count);
            Assert.All(featureChecks, c => Assert.False(string.IsNullOrWhiteSpace(Avalonia.Automation.AutomationProperties.GetName(c))));
            var badges = view.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("feat-badge") && b.IsVisible).ToList();
            Assert.Equal(2, badges.Count);
            var reset = view.FindControl<Button>("ResetButton")!;
            Assert.Equal(vm.Features.ResetText, Avalonia.Automation.AutomationProperties.GetName(reset));
            Assert.True(reset.IsEffectivelyEnabled);
            Assert.Equal(language == "ko", HangulShown());
            Render(window, $"tuner-advanced-{language}");
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task The_tab_does_not_resize_the_window_beyond_the_cap_and_scrolls_its_body_when_expanded() => HeadlessHost.Run(async () =>
    {
        var vm = await OpenVm(null, null, "ko");
        var (window, view) = Open(vm);
        try
        {
            var size = window.Bounds.Size;
            Assert.True(window.Bounds.Width <= SettingsWindow.MaxBodyWidth + 24 + 0.5, $"{window.Bounds.Width}");

            var body = view.FindControl<ScrollViewer>("Body")!;
            Assert.True(body.Viewport.Width + 0.5 >= body.Extent.Width, "가로로 넘치지 않는다");

            vm.Features.IsAdvancedExpanded = true;
            Relayout(window);
            Assert.True(body.Extent.Height > body.Viewport.Height, "고급을 펼치면 25개 기능이 한 화면보다 길어 스크롤된다");
            var expander = view.FindControl<Expander>("AdvancedExpander")!;
            Assert.True(body.Offset.Y > 0, "펼치면 고급 머리가 본문 맨 위로 올라온다");
            Assert.Equal(0, expander.TranslatePoint(default, body)!.Value.Y, 1);
            Assert.True(body.Viewport.Width + 0.5 >= body.Extent.Width, "펼쳐도 가로로 넘치지 않는다");
            Assert.Equal(size, window.Bounds.Size); // 펼쳐도 창 크기는 그대로

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
            Assert.False(view.FindControl<WrapPanel>("PackRow")!.IsEffectivelyVisible);

            // 접힌 상태: 흐린 것은 덧붙임 2개뿐, 이유 줄이 보인다.
            Assert.False(view.FindControl<CheckBox>("PlsqlAddonBox")!.IsEnabled);
            Assert.False(view.FindControl<CheckBox>("MyBatisAddonBox")!.IsEnabled);
            var reason = view.FindControl<TextBlock>("AddonReason")!;
            Assert.True(reason.IsEffectivelyVisible);
            Assert.Equal("덧붙이면 조회 전용 차단이 풀립니다", reason.Text);
            Render(window, "viewer-restart");

            // 펼치면 쓰기 전용 기능이 못 고치게 흐리다.
            vm.Features.IsAdvancedExpanded = true;
            Relayout(window);
            var groups = view.FindControl<ItemsControl>("GroupList")!;
            Assert.Equal(FeatureCatalog.Features.Count(f => f.WritesOnly), groups.GetVisualDescendants().OfType<CheckBox>().Count(c => !c.IsEnabled));
            Render(window, "viewer-restart-advanced");

            vm.Features.Primary = PrimaryWorkKind.All;
            Item(vm.Features, FeatureIds.PlsqlEditor).IsChecked = false;
            Relayout(window);
            Assert.False(view.FindControl<Border>("ViewerBanner")!.IsEffectivelyVisible);
            Assert.Single(groups.GetVisualDescendants().OfType<CheckBox>().Where(c => !c.IsEnabled)); // plsqlWorkspace 만
            Assert.Equal("모든 기능에 이미 들어 있습니다", reason.Text);
            Render(window, "all-requires");
        }
        finally
        {
            window.Close();
        }
    });

    [Theory]
    [InlineData("ko")]
    [InlineData("en")]
    public Task The_default_state_renders_sql_development_chosen_with_the_notice_banner(string language) => HeadlessHost.Run(async () =>
    {
        var vm = await OpenVm(null, null, language);
        var (window, view) = Open(vm);
        try
        {
            var banner = view.FindControl<Border>("DefaultRoleBanner")!;
            Assert.True(banner.IsEffectivelyVisible);
            Assert.Equal(vm.Features.DefaultRoleNoticeText, Avalonia.Automation.AutomationProperties.GetName(banner));
            Assert.Equal(vm.Features.DefaultRoleNoticeText, view.FindControl<TextBlock>("DefaultRoleText")!.Text);
            var radios = view.FindControl<ItemsControl>("PrimaryList")!.GetVisualDescendants().OfType<RadioButton>().ToList();
            Assert.Equal(5, radios.Count);
            Assert.Equal(1, radios.FindIndex(r => r.IsChecked == true)); // SQL 개발
            Assert.All(radios, r => Assert.True(r.IsEnabled));
            Assert.True(view.FindControl<CheckBox>("PlsqlAddonBox")!.IsEnabled);
            Assert.False(view.FindControl<TextBlock>("AddonReason")!.IsEffectivelyVisible);
            Assert.False(view.FindControl<Border>("ViewerBanner")!.IsEffectivelyVisible);
            Assert.False(view.FindControl<Border>("RestartBanner")!.IsEffectivelyVisible);
            Assert.False(view.FindControl<TextBlock>("NoRoleNotice")!.IsEffectivelyVisible);
            Assert.Equal(8, VisibleInputs(view).Count); // 라디오 5 + 덧붙임 2 + 고급 1
            if (language == "en")
            {
                Assert.DoesNotContain(vm.Features.DefaultRoleNoticeText, c => c is >= '가' and <= '힣');
                Assert.Contains("SQL Developer", vm.Features.DefaultRoleNoticeText);
            }

            Render(window, $"default-{language}");

            // 화면 단추로 주 업무를 바꾸면(라디오 클릭과 같은 길) 안내 띠가 사라지고 덧붙임이 흐려진다.
            radios[4].IsChecked = true; // 조회만
            Relayout(window);
            Assert.Equal(PrimaryWorkKind.Viewer, vm.Features.Primary);
            Assert.Single(radios, r => r.IsChecked == true);
            Assert.False(banner.IsEffectivelyVisible);
            Assert.True(view.FindControl<Border>("ViewerBanner")!.IsEffectivelyVisible);
            Assert.False(view.FindControl<CheckBox>("PlsqlAddonBox")!.IsEnabled);
            Assert.True(view.FindControl<TextBlock>("AddonReason")!.IsEffectivelyVisible);
            Render(window, $"primary-viewer-{language}");

            // 확인란 클릭으로 덧붙임: 튜닝 + PL/SQL.
            radios[2].IsChecked = true;
            view.FindControl<CheckBox>("PlsqlAddonBox")!.IsChecked = true;
            Relayout(window);
            Assert.Equal(new PrimaryWorkChoice(PrimaryWorkKind.Tuner, Plsql: true), vm.Features.Choice);
            Assert.True(view.FindControl<WrapPanel>("PackRow")!.IsEffectivelyVisible);
            Assert.Equal(9, VisibleInputs(view).Count); // + 팩 링크
            Render(window, $"primary-tuner-{language}");
        }
        finally
        {
            window.Close();
        }
    });

    [Theory]
    [InlineData("ko")]
    [InlineData("en")]
    public Task An_existing_combination_renders_the_current_combination_row(string language) => HeadlessHost.Run(async () =>
    {
        var vm = await OpenVm(new FeaturesSettings { Roles = [RoleIds.Tuner, RoleIds.Dba] }, null, language);
        var (window, view) = Open(vm);
        try
        {
            var radios = view.FindControl<ItemsControl>("PrimaryList")!.GetVisualDescendants().OfType<RadioButton>().ToList();
            Assert.Equal(6, radios.Count);
            Assert.True(radios[^1].IsChecked);
            Assert.Equal(vm.Features.PrimaryOptions[^1].Name, Avalonia.Automation.AutomationProperties.GetName(radios[^1]));
            Assert.True(VisibleInputs(view).Count <= 10, $"{VisibleInputs(view).Count}"); // 라디오 6 + 덧붙임 2 + 고급 1 + 팩 링크 1
            Assert.False(view.FindControl<CheckBox>("PlsqlAddonBox")!.IsEnabled);
            Render(window, $"custom-{language}");
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
