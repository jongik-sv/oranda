using Oranda.App.Features.Ai.Settings;
using Oranda.Core.Ai.Profiles;
using Oranda.Core.Settings;

namespace Oranda.App.Tests.Features.Ai;

/// <summary>
/// 설정 「AI 모델」 쪽의 시스템 CLI 줄: 고르기, 프로필·Claude Code 와의 상호 배제, 쓸 수 없는 값의 문제 표시.
/// 이 빌드에서 두 CLI 는 켜져 있지 않으므로, 시험은 사용 가능한 줄을 페이지에 직접 넣는다(라우터의 <c>SystemClis</c> 와 같은 방식).
/// </summary>
public class AiSystemCliSettingsTests
{
    private static readonly IReadOnlyList<AiSystemCliInfo> Enabled =
    [
        new(AiProfilePresets.ClaudeCodeId, "Claude Code", true),
        new(AiProfilePresets.AntigravityCliId, "Antigravity CLI", false),
        new(AiProfilePresets.CodexCliId, "Codex CLI", true),
    ];

    private static AiSettings Base(string? cliId = null, Guid? profileId = null, params AiLlmProfile[] profiles) => new()
    {
        Model = "sonnet",
        Effort = "high",
        ExecutablePath = "/opt/example/claude",
        SystemCliId = cliId,
        ActiveProfileId = profileId,
        Profiles = profiles,
    };

    private static AiProfileChoice Row(AiModelSettingsPage page, AiChoiceKind kind, string? cliId = null) =>
        page.Choices.Single(c => c.Kind == kind && (cliId is null || c.CliId == cliId));

    [Fact]
    public void An_available_cli_row_is_its_own_kind_and_never_a_claude_code_row()
    {
        var page = new AiModelSettingsPage(Base(), systemClis: Enabled);

        var codex = Row(page, AiChoiceKind.SystemCli);
        Assert.Equal(("Codex CLI", AiProfilePresets.CodexCliId), (codex.Title, codex.CliId));
        Assert.True(codex.Selectable);
        Assert.Single(page.Choices, c => c.Kind == AiChoiceKind.ClaudeCode);
        Assert.False(Row(page, AiChoiceKind.DisabledCli).Selectable);
    }

    [Fact]
    public void Choosing_a_cli_row_saves_the_cli_id_and_keeps_the_claude_only_values()
    {
        var page = new AiModelSettingsPage(Base(), systemClis: Enabled);

        page.Choose(Row(page, AiChoiceKind.SystemCli));

        var saved = page.ToSettings();
        Assert.Equal(AiActiveKind.SystemCli, page.ActiveKind);
        Assert.Equal(AiProfilePresets.CodexCliId, saved.SystemCliId);
        Assert.Null(saved.ActiveProfileId);
        Assert.Equal(AiDriverKind.ClaudeCode, saved.Driver);
        Assert.Equal(("sonnet", "high", "/opt/example/claude"), (saved.Model, saved.Effort, saved.ExecutablePath));
        Assert.True(Row(page, AiChoiceKind.SystemCli).IsCurrent);
        Assert.False(Row(page, AiChoiceKind.ClaudeCode).IsCurrent);
        Assert.Equal("Codex CLI", page.SelectedTitle);
        Assert.False(page.ShowCliSettings); // Model·Effort 칸은 Claude Code 일 때만 보인다
        Assert.False(page.ShowProblem);

        page.Choose(Row(page, AiChoiceKind.ClaudeCode));
        Assert.True(page.ShowCliSettings);
        Assert.Equal(("sonnet", "high"), (page.Model, page.Effort)); // 저장값이 살아 있다
    }

    [Fact]
    public void Choosing_a_profile_or_claude_code_clears_the_cli_id()
    {
        var profile = ProfileSamples.Example();
        var page = new AiModelSettingsPage(Base("codex-cli", null, profile), systemClis: Enabled);
        Assert.Equal(AiActiveKind.SystemCli, page.ActiveKind);

        page.Choose(Row(page, AiChoiceKind.Profile));
        Assert.Null(page.ToSettings().SystemCliId);
        Assert.Equal(profile.Id, page.ToSettings().ActiveProfileId);

        page.Choose(Row(page, AiChoiceKind.SystemCli));
        Assert.Null(page.ToSettings().ActiveProfileId);
        page.Choose(Row(page, AiChoiceKind.ClaudeCode));
        var saved = page.ToSettings();
        Assert.Null(saved.SystemCliId);
        Assert.Null(saved.ActiveProfileId);
        Assert.Equal(AiActiveKind.ClaudeCode, page.ActiveKind);
    }

    [Fact]
    public void A_new_profile_chosen_after_creation_clears_the_cli_id()
    {
        var profile = ProfileSamples.Example();
        var page = new AiModelSettingsPage(Base("codex-cli"), systemClis: Enabled);

        page.ApplyStoreChange(Base("codex-cli", null, profile), selectNew: profile.Id);

        var saved = page.ToSettings();
        Assert.Null(saved.SystemCliId);
        Assert.Equal(profile.Id, saved.ActiveProfileId);
    }

    [Fact]
    public void Deleting_the_active_profile_clears_both_fields_in_the_page_and_says_claude_code()
    {
        var profile = ProfileSamples.Example();
        // 손으로 고친 파일: 두 칸이 함께 있다(프로필이 우선).
        var page = new AiModelSettingsPage(Base("codex-cli", profile.Id, profile), systemClis: Enabled);
        Assert.Equal(AiActiveKind.Profile, page.ActiveKind);

        page.ApplyStoreChange(Base(null, null), deletedId: profile.Id);

        var saved = page.ToSettings();
        Assert.Null(saved.SystemCliId);
        Assert.Null(saved.ActiveProfileId);
        Assert.Equal(AiActiveKind.ClaudeCode, page.ActiveKind);
        Assert.Contains("Claude Code", page.Notice, StringComparison.Ordinal);
    }

    [Fact]
    public void The_windows_list_has_both_clis_as_coming_soon_and_unselectable()
    {
        var page = new AiModelSettingsPage(Base(), systemClis: AiProfilePresets.SystemClisFor(AiHostPlatform.Windows));

        var disabled = page.Choices.Where(c => c.Kind == AiChoiceKind.DisabledCli).ToList();
        Assert.Equal(["Antigravity CLI", "Codex CLI"], disabled.Select(c => c.Title));
        Assert.All(disabled, c => Assert.False(c.Selectable));
        Assert.DoesNotContain(page.Choices, c => c.Kind == AiChoiceKind.SystemCli);

        page.Choose(disabled[0]);
        Assert.Null(page.ToSettings().SystemCliId);
        Assert.Equal(AiActiveKind.ClaudeCode, page.ActiveKind);
    }

    [Fact]
    public void The_macos_list_also_has_both_clis_as_coming_soon_and_unselectable()
    {
        var page = new AiModelSettingsPage(Base(), systemClis: AiProfilePresets.SystemClisFor(AiHostPlatform.MacOS));

        var disabled = page.Choices.Where(c => c.Kind == AiChoiceKind.DisabledCli).ToList();
        Assert.Equal(["Antigravity CLI", "Codex CLI"], disabled.Select(c => c.Title));
        Assert.All(disabled, c => Assert.False(c.Selectable));
        Assert.DoesNotContain(page.Choices, c => c.Kind == AiChoiceKind.SystemCli);

        page.Choose(disabled[0]);
        Assert.Null(page.ToSettings().SystemCliId);
    }

    /// <summary>설정 파일에 두 id 가 저장되어 있어도 어떤 플랫폼에서든 그 줄은 「준비 중」 이고 선택되지 않는다(not enabled on any platform).</summary>
    [Theory]
    [InlineData(AiHostPlatform.Windows, AiProfilePresets.CodexCliId)]
    [InlineData(AiHostPlatform.MacOS, AiProfilePresets.CodexCliId)]
    [InlineData(AiHostPlatform.Linux, AiProfilePresets.CodexCliId)]
    [InlineData(AiHostPlatform.Windows, AiProfilePresets.AntigravityCliId)]
    [InlineData(AiHostPlatform.MacOS, AiProfilePresets.AntigravityCliId)]
    [InlineData(AiHostPlatform.Linux, AiProfilePresets.AntigravityCliId)]
    public void A_stored_codex_or_antigravity_id_shows_as_its_coming_soon_row_on_every_platform(AiHostPlatform platform, string id)
    {
        var page = new AiModelSettingsPage(Base(id), systemClis: AiProfilePresets.SystemClisFor(platform));

        Assert.Equal(AiActiveKind.SystemCliUnavailable, page.ActiveKind);
        var current = Assert.Single(page.Choices, c => c.IsCurrent);
        Assert.Equal((AiChoiceKind.DisabledCli, id, false), (current.Kind, current.CliId, current.Selectable));
        Assert.DoesNotContain(page.Choices, c => c.Kind == AiChoiceKind.SystemCli);
        Assert.False(Row(page, AiChoiceKind.ClaudeCode).IsCurrent);

        page.Choose(current);
        Assert.Equal(id, page.ToSettings().SystemCliId); // 눌러도 고르지 않는다. 조용히 바뀌지도 않는다.
        Assert.Equal(AiActiveKind.SystemCliUnavailable, page.ActiveKind);
    }

    [Theory]
    [InlineData("codex-cli")] // 준비 중인 CLI(기본 목록)
    [InlineData("no-such-cli")]
    [InlineData(AiSettings.UnreadableSystemCliId)]
    public void An_unusable_cli_value_shows_as_a_problem_not_as_claude_code(string id)
    {
        var page = new AiModelSettingsPage(Base(id)); // 기본 목록: 두 CLI 모두 준비 중

        Assert.Equal(AiActiveKind.SystemCliUnavailable, page.ActiveKind);
        Assert.True(page.ShowProblem);
        Assert.NotEmpty(page.ProblemText);
        Assert.False(page.ShowCliSettings);
        Assert.False(page.ShowProfileInfo);
        Assert.NotEqual("Claude Code", page.SelectedTitle);
        Assert.False(Row(page, AiChoiceKind.ClaudeCode).IsCurrent);

        // 목록에 있는 준비 중 CLI 는 그 줄이 현재 줄이고(줄이 둘로 보이지 않는다), 목록에 없는 값만 별도 줄을 쓴다.
        var current = Assert.Single(page.Choices, c => c.IsCurrent);
        Assert.False(current.Selectable);
        if (id == AiProfilePresets.CodexCliId)
        {
            Assert.Equal((AiChoiceKind.DisabledCli, "Codex CLI"), (current.Kind, current.Title));
            Assert.Equal("Codex CLI", page.SelectedTitle);
            Assert.DoesNotContain(page.Choices, c => c.Kind == AiChoiceKind.MissingCli);
        }
        else
        {
            Assert.Equal(AiChoiceKind.MissingCli, current.Kind);
            Assert.Equal(id, current.CliId);
        }
    }

    [Fact]
    public void Confirming_without_choosing_keeps_the_unusable_value_and_choosing_another_row_resolves_it()
    {
        var page = new AiModelSettingsPage(Base(AiSettings.UnreadableSystemCliId), systemClis: Enabled);

        Assert.Equal(AiSettings.UnreadableSystemCliId, page.ToSettings().SystemCliId); // 조용히 Claude Code 가 되지 않는다
        Assert.Equal(AiSettings.UnreadableSystemCliId, page.ToSettings(Base(AiSettings.UnreadableSystemCliId)).SystemCliId);

        page.Choose(Row(page, AiChoiceKind.ClaudeCode));
        Assert.False(page.ShowProblem);
        Assert.Null(page.ToSettings().SystemCliId);
        Assert.DoesNotContain(page.Choices, c => c.Kind == AiChoiceKind.MissingCli);
    }

    [Fact]
    public void A_stale_cli_value_is_replaced_by_the_page_choice_on_save()
    {
        var page = new AiModelSettingsPage(Base(), systemClis: Enabled);

        // 저장소의 지금 값에 낡은 시스템 CLI 가 남아 있어도 이 쪽이 고른 값이 이긴다.
        Assert.Null(page.ToSettings(Base("codex-cli")).SystemCliId);
    }

    [Fact]
    public void The_summary_names_the_cli_or_the_failure()
    {
        var ok = new AiModelSettingsPage(Base("codex-cli"), systemClis: Enabled);
        var bad = new AiModelSettingsPage(Base("codex-cli"));

        Assert.Equal("AI: Codex CLI", ok.Summary);
        Assert.Contains("사용 불가", bad.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_manager_delete_path_clears_a_hand_edited_cli_id_with_the_active_profile()
    {
        var profile = ProfileSamples.Example();
        var harness = await ProfileHarness.CreateAsync(Base("codex-cli", profile.Id, profile));
        var page = harness.Page();
        var vm = harness.Manager(applied: page.ApplyStoreChange, select: profile.Id);
        await vm.Ready;

        Assert.True(await vm.DeleteProfileAsync());

        Assert.Null(harness.Ai.ActiveProfileId);
        Assert.Null(harness.Ai.SystemCliId);
        Assert.Equal(AiActiveKind.ClaudeCode, page.ActiveKind);
        Assert.Contains("Claude Code", page.Notice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deleting_another_profile_keeps_the_stored_cli_id()
    {
        var profile = ProfileSamples.Example();
        var harness = await ProfileHarness.CreateAsync(Base("codex-cli", null, profile));
        var vm = harness.Manager(applied: harness.Page().ApplyStoreChange, select: profile.Id);
        await vm.Ready;

        Assert.True(await vm.DeleteProfileAsync());

        Assert.Equal("codex-cli", harness.Ai.SystemCliId);
    }
}
