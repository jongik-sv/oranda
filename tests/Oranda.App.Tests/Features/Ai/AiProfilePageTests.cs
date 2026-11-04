using Oranda.App.Features.Ai.Settings;
using Oranda.App.Shell;
using Oranda.App.Shell.Settings;
using Oranda.App.Tests.Features.Update;
using Oranda.Core.Ai.Profiles;
using Oranda.Core.Settings;

namespace Oranda.App.Tests.Features.Ai;

/// <summary>설정 「AI 모델」 쪽(set1): 카드 · 드롭다운 묶음 · 아래 칸 전환 · 요약 글 · 저장 규칙(설계 §6.3).</summary>
public class AiProfilePageTests
{
    [Fact]
    public void Default_settings_select_claude_code_and_keep_the_card_names_the_chat_lane_reads()
    {
        var page = new AiModelSettingsPage(new AiSettings());

        Assert.True(page.IsClaudeCode);
        Assert.False(page.IsNone);
        Assert.Equal(AiActiveKind.ClaudeCode, page.ActiveKind);
        Assert.Null(page.ActiveProfileId);
        Assert.True(page.ShowCliSettings);
        Assert.False(page.ShowProfileInfo);
        Assert.Equal("AI: Claude Code · opus", page.Summary);
        Assert.Equal(["opus", "sonnet", "haiku"], AiModelSettingsPage.KnownModels);
    }

    [Fact]
    public void Dropdown_groups_system_clis_and_my_profiles_without_unknown_entries()
    {
        var exampleProfile = ProfileSamples.Example();
        var page = new AiModelSettingsPage(ProfileSamples.With(exampleProfile), systemClis: AiProfilePresets.SystemClisFor(AiHostPlatform.Windows));

        var rows = page.Choices;
        Assert.Equal(["시스템 CLI", "Claude Code", "Antigravity CLI", "Codex CLI", "내 프로필", "예제 LLM", "새 프로필 만들기…"], rows.Select(r => r.Title));
        Assert.Equal([AiChoiceKind.Header, AiChoiceKind.ClaudeCode, AiChoiceKind.SystemCli, AiChoiceKind.SystemCli, AiChoiceKind.Header, AiChoiceKind.Profile, AiChoiceKind.NewProfile],
            rows.Select(r => r.Kind));
        Assert.All(rows.Where(r => r.Kind == AiChoiceKind.SystemCli), r => Assert.True(r.Selectable));
        Assert.True(rows.Single(r => r.Kind == AiChoiceKind.ClaudeCode).IsCurrent);
        Assert.Equal("example-model", rows.Single(r => r.Kind == AiChoiceKind.Profile).Detail);
        Assert.DoesNotContain(rows, r => r.Title.Contains("Unknown", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Choosing_a_profile_switches_the_lower_area_and_the_summary_and_choosing_claude_code_returns()
    {
        var exampleProfile = ProfileSamples.Example();
        var page = new AiModelSettingsPage(ProfileSamples.With(exampleProfile));

        page.Choose(page.Choices.Single(r => r.Kind == AiChoiceKind.Profile));

        Assert.Equal(AiActiveKind.Profile, page.ActiveKind);
        Assert.Equal(exampleProfile.Id, page.ActiveProfileId);
        Assert.True(page.ShowProfileInfo);
        Assert.False(page.ShowCliSettings);
        Assert.Equal("AI: 예제 LLM · example-model", page.Summary);
        Assert.Equal(("example-model", "http://llm.example:8080"), (page.ProfileModel, page.ProfileUrl));
        Assert.Equal("Anthropic (claude 실행 파일 사용)", page.ProfileFormatText);
        Assert.Equal("지원", page.ProfileImagesText);
        Assert.True(page.ShowPlainTextWarning); // 사내 HTTP 서버: SQL 과 이미지가 암호화 없이 나간다
        var saved = page.ToSettings();
        Assert.Equal((AiDriverKind.ClaudeCode, exampleProfile.Id), (saved.Driver, saved.ActiveProfileId));

        page.Choose(page.Choices.Single(r => r.Kind == AiChoiceKind.ClaudeCode));
        Assert.Equal(AiActiveKind.ClaudeCode, page.ActiveKind);
        Assert.Null(page.ToSettings().ActiveProfileId);
        Assert.False(page.ShowPlainTextWarning);
    }

    [Theory]
    [InlineData("http://localhost:11434/v1", false)]
    [InlineData("http://127.0.0.1:1234/v1", false)]
    [InlineData("https://llm.example/v1", false)]
    [InlineData("http://llm.example:8080", true)]
    public void Plain_http_warning_appears_only_for_remote_hosts(string url, bool warns)
    {
        var profile = ProfileSamples.Example() with { BaseUrl = url };
        var page = new AiModelSettingsPage(new AiSettings { Profiles = [profile], ActiveProfileId = profile.Id });

        Assert.Equal(warns, page.ShowPlainTextWarning);
    }

    [Fact]
    public void New_profile_row_opens_the_manager_without_changing_the_selection()
    {
        var page = new AiModelSettingsPage(ProfileSamples.With(ProfileSamples.Example()));
        ProfileManagerRequestedEventArgs? request = null;
        page.ProfileManagerRequested += (_, e) => request = e;

        page.Choose(page.Choices.Single(r => r.Kind == AiChoiceKind.NewProfile));

        Assert.NotNull(request);
        Assert.True(request.StartNew);
        Assert.Equal(AiActiveKind.ClaudeCode, page.ActiveKind); // 「새 프로필 만들기…」 가 선택값이 되지 않는다
        Assert.Null(page.ToSettings().ActiveProfileId);
    }

    [Fact]
    public void Disabled_cli_rows_cannot_be_chosen()
    {
        var page = new AiModelSettingsPage(new AiSettings(), systemClis:
        [
            new(AiProfilePresets.ClaudeCodeId, "Claude Code", true),
            new(AiProfilePresets.CodexCliId, "Codex CLI", false),
        ]);
        var before = page.ToSettings();
        var row = page.Choices.First(r => r.Kind == AiChoiceKind.DisabledCli);

        page.Choose(row);

        Assert.False(row.Selectable);
        Assert.Equal("준비 중", row.Detail);
        Assert.Equal(before, page.ToSettings());
    }

    [Fact]
    public void A_deleted_profile_stays_selected_with_its_reason_instead_of_falling_back_silently()
    {
        var gone = Guid.NewGuid();
        var page = new AiModelSettingsPage(new AiSettings { ActiveProfileId = gone, Profiles = [ProfileSamples.Example()] });

        Assert.Equal(AiActiveKind.ProfileMissing, page.ActiveKind);
        Assert.True(page.ShowProblem);
        Assert.Contains("찾을 수 없습니다", page.ProblemText);
        Assert.False(page.ShowCliSettings);
        Assert.Equal("삭제된 프로필", page.SelectedTitle);
        var row = page.Choices.Single(r => r.Kind == AiChoiceKind.MissingProfile);
        Assert.True(row.IsCurrent);
        Assert.False(row.Selectable);
        Assert.Equal(gone, page.ToSettings().ActiveProfileId); // 확인을 눌러도 말없이 Claude Code 로 바뀌지 않는다
    }

    [Fact]
    public void An_unreadable_active_profile_id_is_kept_as_missing()
    {
        var page = new AiModelSettingsPage(new AiSettings { ActiveProfileId = Guid.Empty });

        Assert.Equal(AiActiveKind.ProfileMissing, page.ActiveKind);
        Assert.Equal(Guid.Empty, page.ToSettings().ActiveProfileId);
    }

    [Fact]
    public void A_profile_this_build_does_not_know_shows_the_reason_and_cannot_be_picked()
    {
        var unknown = ProfileSamples.Example() with { Preset = AiProfilePreset.Unknown };
        var page = new AiModelSettingsPage(new AiSettings { Profiles = [unknown], ActiveProfileId = unknown.Id });

        Assert.Equal(AiActiveKind.ProfileInvalid, page.ActiveKind);
        Assert.True(page.ShowProblem);
        Assert.False(page.ShowProfileInfo);
        var row = page.Choices.Single(r => r.Kind == AiChoiceKind.Profile);
        Assert.False(row.Selectable);
        Assert.Equal("이 버전에서 쓸 수 없음", row.Reason);

        var other = new AiModelSettingsPage(new AiSettings { Profiles = [unknown] });
        other.Choose(other.Choices.Single(r => r.Kind == AiChoiceKind.Profile));
        Assert.Equal(AiActiveKind.ClaudeCode, other.ActiveKind);
    }

    [Fact]
    public void None_keeps_the_chosen_profile_so_switching_back_restores_it()
    {
        var exampleProfile = ProfileSamples.Example();
        var page = new AiModelSettingsPage(new AiSettings { Profiles = [exampleProfile], ActiveProfileId = exampleProfile.Id, Model = "sonnet" });

        page.IsNone = true;
        var off = page.ToSettings();
        Assert.Equal((AiDriverKind.None, null, exampleProfile.Id), (off.Driver, off.Model, off.ActiveProfileId));
        Assert.Equal("AI: 선택 안 함", page.Summary);
        Assert.False(page.ShowSelection);

        page.IsClaudeCode = true;
        Assert.Equal(AiActiveKind.Profile, page.ActiveKind);
    }

    [Fact]
    public async Task Confirming_the_settings_window_does_not_overwrite_profiles_the_manager_saved_meanwhile()
    {
        var harness = await ProfileHarness.CreateAsync();
        var page = harness.Page(); // 설정 창이 열린 시점의 사본: 프로필 없음
        var manager = harness.Manager(applied: (latest, deleted, created) => page.ApplyStoreChange(latest, deleted, created), startNew: true);
        manager.Name = "새 서버";
        manager.BaseUrl = "https://llm.example/v1";
        manager.ModelName = "model-example";
        Assert.True(await manager.SaveProfileAsync(null));

        page.Model = "sonnet"; // 확인 전의 다른 변경
        var written = page.ToSettings(harness.Ai);

        Assert.Single(written.Profiles);
        Assert.Equal("새 서버", written.Profiles[0].Name);
        Assert.Equal("sonnet", written.Model);
    }

    [Fact]
    public async Task Settings_window_save_keeps_the_profiles_the_manager_saved()
    {
        var settings = new SettingsService(new MemorySettingsStore());
        await settings.LoadAsync();
        var vm = new SettingsViewModel(settings, new ShellText(language: "ko"));
        var manager = vm.AiModel.CreateManager(new ProfileManagerRequestedEventArgs(null, true), _ => Task.FromResult(true))!;
        manager.Name = "프로필";
        manager.BaseUrl = "https://llm.example/v1";
        manager.ModelName = "m1";
        Assert.True(await manager.SaveProfileAsync(null));
        Assert.Single(vm.AiModel.Profiles);

        vm.AiModel.Model = "haiku";
        await vm.SaveAsync();

        Assert.Single(settings.Current.Ai.Profiles);
        Assert.Equal("haiku", settings.Current.Ai.Model);
    }

    [Fact]
    public void A_stale_page_copy_only_contributes_its_own_choices_to_the_current_settings()
    {
        var kept = ProfileSamples.Example();
        var page = new AiModelSettingsPage(new AiSettings()); // 프로필 없는 옛 사본
        var current = new AiSettings { Profiles = [kept], ExecutablePath = "claude-example" };

        var written = page.ToSettings(current);

        Assert.Equal([kept], written.Profiles);
        Assert.Equal("claude-example", written.ExecutablePath);
    }

    [Fact]
    public async Task Manage_button_needs_services_and_passes_the_selected_profile_to_the_manager()
    {
        Assert.False(new AiModelSettingsPage(new AiSettings()).ManageProfilesCommand.CanExecute(null));

        var exampleProfile = ProfileSamples.Example();
        var harness = await ProfileHarness.CreateAsync(new AiSettings { Profiles = [exampleProfile], ActiveProfileId = exampleProfile.Id });
        var page = harness.Page();
        ProfileManagerRequestedEventArgs? request = null;
        page.ProfileManagerRequested += (_, e) => request = e;

        Assert.True(page.ManageProfilesCommand.CanExecute(null));
        page.ManageProfilesCommand.Execute(null);

        Assert.Equal((exampleProfile.Id, false), (request!.ProfileId, request.StartNew));
    }
}
