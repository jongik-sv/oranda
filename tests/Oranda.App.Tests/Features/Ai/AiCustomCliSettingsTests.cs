using Oranda.App.Features.Ai.Settings;
using Oranda.Core.Ai.Profiles;
using Oranda.Core.Settings;

namespace Oranda.App.Tests.Features.Ai;

/// <summary>
/// 설정 「AI 모델」 의 기타 CLI 등록: 선택 값이 Unavailable 로 빠지지 않음, 편집 중 목록 기준의 활성 대상, 저장·다시 읽기,
/// 지운 줄 안내, 항상 보이는 경고, 설치 확인(실행 안 함 · 오래된 결과 무시).
/// </summary>
public class AiCustomCliSettingsTests
{
    private static AiCustomCli Cli(string id, string name = "Example CLI", string command = "examplecli --print") => new() { Id = id, Name = name, Command = command };

    private static AiSettings Base(string? selection = null, params AiCustomCli[] clis) => new()
    {
        Driver = AiDriverKind.ClaudeCode,
        Model = "sonnet",
        SystemCliId = selection,
        CustomClis = clis,
    };

    [Fact]
    public void A_registered_custom_cli_selection_is_active_and_not_unavailable()
    {
        var page = new AiModelSettingsPage(Base(AiCustomCliTarget.SelectionOf("c1"), Cli("c1")));

        Assert.Equal(AiActiveKind.CustomCli, page.ActiveKind);
        Assert.False(page.ShowProblem);
        Assert.Equal("Example CLI", page.SelectedTitle);
        Assert.Equal("AI: Example CLI", page.Summary);
        Assert.True(page.ShowCustomCliInfo);
        Assert.False(page.ShowCliSettings); // 모델·effort 칸은 Claude Code 일 때만 보인다
    }

    [Fact]
    public void A_row_added_before_ok_is_already_active_when_chosen()
    {
        var page = new AiModelSettingsPage(Base());

        page.AddCustomCliCommand.Execute(null);
        var row = Assert.Single(page.CustomClis);
        row.Name = "Added";
        row.Command = "addedcli";
        page.UseCustomCliRowCommand.Execute(row);

        Assert.Equal(AiActiveKind.CustomCli, page.ActiveKind); // 저장 전 목록으로 만든 활성 대상
        Assert.True(row.IsSelected);
        var saved = page.ToSettings();
        Assert.Equal(AiCustomCliTarget.SelectionOf(row.Id), saved.SystemCliId);
        Assert.Equal([new AiCustomCli { Id = row.Id, Name = "Added", Command = "addedcli" }], saved.CustomClis);
    }

    [Fact]
    public void Untouched_settings_round_trip_equal_even_with_hand_edited_rows()
    {
        var source = Base(AiCustomCliTarget.SelectionOf("c2"), Cli("c1", "  Spaced  ", "  mycli  --x "), Cli("c2", "", ""), Cli("c1", "Duplicate id", "other"));
        var page = new AiModelSettingsPage(source);

        Assert.Equal(source, page.ToSettings()); // 다듬거나 빼거나 정렬하면 설정 창이 항상 바뀐 것으로 보인다
    }

    [Fact]
    public void A_draft_row_left_blank_is_not_saved()
    {
        var page = new AiModelSettingsPage(Base(null, Cli("c1")));

        page.AddCustomCliCommand.Execute(null);

        Assert.Equal(2, page.CustomClis.Count);
        Assert.Equal([Cli("c1")], page.ToSettings().CustomClis);
    }

    [Fact]
    public void Deleting_the_selected_row_keeps_the_selection_and_shows_the_unavailable_notice()
    {
        var page = new AiModelSettingsPage(Base(AiCustomCliTarget.SelectionOf("c1"), Cli("c1"), Cli("c2", "Other")));

        page.RemoveCustomCli(page.CustomClis[0]);

        Assert.Equal(AiActiveKind.CustomCliUnavailable, page.ActiveKind);
        Assert.True(page.ShowProblem);
        Assert.Contains("기타 CLI", page.ProblemText);
        Assert.Equal(AiCustomCliTarget.SelectionOf("c1"), page.ToSettings().SystemCliId); // 말없이 다른 대상으로 바꾸지 않는다
        Assert.Equal([Cli("c2", "Other")], page.ToSettings().CustomClis);
        var current = Assert.Single(page.Choices, c => c.IsCurrent);
        Assert.Equal(AiChoiceKind.MissingCustomCli, current.Kind);
        Assert.False(current.Selectable);
        Assert.Equal("AI: 기타 CLI · 사용 불가", page.Summary); // 「프로필 없음」 으로 빠지지 않는다
        Assert.Equal("사용할 수 없는 기타 CLI", page.SelectedTitle);
    }

    [Fact]
    public void Choosing_a_custom_row_in_the_dropdown_saves_the_prefixed_id_and_clears_the_profile()
    {
        var profile = ProfileSamples.Example();
        var source = Base(null, Cli("c1")) with { ActiveProfileId = profile.Id, Profiles = [profile] };
        var page = new AiModelSettingsPage(source);
        Assert.Equal(AiActiveKind.Profile, page.ActiveKind);

        page.Choose(page.Choices.Single(c => c.Kind == AiChoiceKind.CustomCli));

        var saved = page.ToSettings();
        Assert.Equal(AiCustomCliTarget.SelectionOf("c1"), saved.SystemCliId);
        Assert.Null(saved.ActiveProfileId);
        Assert.True(page.Choices.Single(c => c.Kind == AiChoiceKind.CustomCli).IsCurrent);
    }

    [Fact]
    public void The_custom_group_is_hidden_until_something_is_registered_or_selected()
    {
        Assert.DoesNotContain(new AiModelSettingsPage(Base()).Choices, c => c.Kind is AiChoiceKind.CustomCli or AiChoiceKind.MissingCustomCli);
        Assert.Contains(new AiModelSettingsPage(Base(null, Cli("c1"))).Choices, c => c.Kind == AiChoiceKind.CustomCli);
        Assert.Contains(new AiModelSettingsPage(Base(AiCustomCliTarget.SelectionOf("gone"))).Choices, c => c.Kind == AiChoiceKind.MissingCustomCli);
    }

    [Fact]
    public void A_custom_row_without_a_command_cannot_be_chosen()
    {
        var page = new AiModelSettingsPage(Base(null, Cli("c1", "Empty", "")));
        var row = page.Choices.Single(c => c.Kind == AiChoiceKind.CustomCli);

        Assert.False(row.Selectable);
        page.UseCustomCliRowCommand.Execute(page.CustomClis[0]);
        Assert.Null(page.ToSettings().SystemCliId);
    }

    [Fact]
    public void Global_value_masking_with_a_custom_cli_shows_the_block_warning()
    {
        var page = new AiModelSettingsPage(Base(AiCustomCliTarget.SelectionOf("c1"), Cli("c1")));
        Assert.False(page.ShowCustomCliMaskBlock);

        page.MaskLiterals = true;

        Assert.True(page.ShowCustomCliMaskBlock);
        page.Choose(page.Choices.Single(c => c.Kind == AiChoiceKind.ClaudeCode));
        Assert.False(page.ShowCustomCliMaskBlock);
    }

    [Fact]
    public void The_fixed_warnings_are_always_present_and_name_the_empty_folder_limit()
    {
        var page = new AiModelSettingsPage(Base());

        Assert.Contains("샌드박스가 아닙니다", page.CustomCliSandboxText);
        Assert.Contains("평문", page.CustomCliPlainText);
        Assert.Contains("운영", page.CustomCliMaskText);
    }

    [Fact]
    public void The_command_analysis_feeds_the_auto_approve_secret_and_prompt_warnings()
    {
        var page = new AiModelSettingsPage(Base(null, Cli("c1", "Risky", "EXAMPLE_API_KEY=abc examplecli --yolo -p {prompt}")));

        var warnings = page.CustomClis[0].Warnings;

        Assert.Equal(3, warnings.Count);
        Assert.Contains(warnings, w => w.Contains("--yolo"));
        Assert.Contains(warnings, w => w.Contains("EXAMPLE_API_KEY") && !w.Contains("abc"));
        Assert.Contains(warnings, w => w.Contains("{prompt}"));
    }

    [Fact]
    public void A_loader_or_runtime_environment_variable_and_an_ambiguous_space_are_warned_by_the_parser_rules()
    {
        var page = new AiModelSettingsPage(Base(null, Cli("c1", "Env", "NODE_OPTIONS=--inspect examplecli -p {prompt}"), Cli("c2", "Wide", "examplecli　-p {prompt}")));

        Assert.Contains(page.CustomClis[0].Warnings, w => w.Contains("NODE_OPTIONS") && !w.Contains("--inspect"));
        Assert.Equal(AiCustomCliCommand.MessageOf(AiCustomCliCommandError.AmbiguousWhitespace), Assert.Single(page.CustomClis[1].Warnings));
    }

    [Fact]
    public void An_unparsable_command_shows_the_fixed_parser_message_and_no_install_check()
    {
        var probed = new List<string>();
        var page = new AiModelSettingsPage(Base(null, Cli("c1", "Bad", "examplecli | tee")), Probe(probed));

        var row = page.CustomClis[0];
        Assert.Single(row.Warnings);
        Assert.Equal(AiCustomCliCommand.MessageOf(AiCustomCliCommandError.ShellSyntax), row.Warnings[0]);
        Assert.Equal(AiCustomCliInstallState.Unknown, row.InstallState);
        Assert.Empty(probed);
    }

    [Fact]
    public async Task The_install_check_finds_the_executable_name_only_and_never_the_arguments()
    {
        var probed = new List<string>();
        var page = new AiModelSettingsPage(Base(null, Cli("c1", "Found", "examplecli --secret-looking-arg")), Probe(probed, name => name == "examplecli"), installCheckDelay: TimeSpan.Zero);
        var row = page.CustomClis[0];

        await row.PendingCheck;

        Assert.Equal(AiCustomCliInstallState.Found, row.InstallState);
        Assert.Equal(["examplecli"], probed);

        row.Command = "missingcli";
        await row.PendingCheck;
        Assert.Equal(AiCustomCliInstallState.NotFound, row.InstallState);
    }

    [Fact]
    public async Task A_slow_check_for_an_old_command_cannot_overwrite_the_newer_result()
    {
        var release = new ManualResetEventSlim(false);
        var page = new AiModelSettingsPage(Base(null, Cli("c1", "Slow", "oldcli")),
            Probe(new List<string>(), name =>
            {
                if (name == "oldcli")
                {
                    release.Wait(TimeSpan.FromSeconds(10)); // 오래된 명령의 확인이 느리다
                    return true;
                }

                return false;
            }),
            installCheckDelay: TimeSpan.Zero);
        var row = page.CustomClis[0];
        var slow = row.PendingCheck;

        row.Command = "newcli";
        await row.PendingCheck;
        Assert.Equal(AiCustomCliInstallState.NotFound, row.InstallState);

        release.Set();
        await slow;
        Assert.Equal(AiCustomCliInstallState.NotFound, row.InstallState); // 늦게 끝난 old 결과(Found)가 덮지 않는다
    }

    [Fact]
    public async Task A_removed_row_ignores_a_late_check()
    {
        var release = new ManualResetEventSlim(false);
        var page = new AiModelSettingsPage(Base(null, Cli("c1", "Gone", "slowcli")),
            Probe(new List<string>(), _ =>
            {
                release.Wait(TimeSpan.FromSeconds(10));
                return true;
            }),
            installCheckDelay: TimeSpan.Zero);
        var row = page.CustomClis[0];
        var pending = row.PendingCheck;

        page.RemoveCustomCli(row);
        release.Set();
        await pending;

        Assert.Equal(AiCustomCliInstallState.Checking, row.InstallState); // 지운 줄은 더 갱신되지 않는다
    }

    [Fact]
    public void Without_a_probe_the_install_state_stays_unknown()
    {
        var page = new AiModelSettingsPage(Base(null, Cli("c1")));

        Assert.Equal(AiCustomCliInstallState.Unknown, page.CustomClis[0].InstallState);
    }

    [Fact]
    public void Editing_a_name_refreshes_the_dropdown_title_without_replacing_the_row_list()
    {
        var page = new AiModelSettingsPage(Base(AiCustomCliTarget.SelectionOf("c1"), Cli("c1")));
        var list = page.CustomClis;
        var changed = new List<string?>();
        page.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        page.CustomClis[0].Name = "Renamed";

        Assert.Same(list, page.CustomClis);
        Assert.Equal("Renamed", page.SelectedTitle);
        Assert.Contains(nameof(AiModelSettingsPage.Choices), changed);
        Assert.DoesNotContain(string.Empty, changed); // 전체 알림은 입력 중 다른 칸까지 다시 그린다
    }

    [Fact]
    public void Turning_ai_off_hides_the_custom_cli_info_and_the_masking_block()
    {
        var page = new AiModelSettingsPage(Base(AiCustomCliTarget.SelectionOf("c1"), Cli("c1")) with { MaskLiterals = true });
        Assert.True(page.ShowCustomCliInfo);
        var changed = new List<string?>();
        page.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        page.IsNone = true;

        Assert.False(page.ShowCustomCliInfo);
        Assert.False(page.ShowCustomCliMaskBlock);
        Assert.Contains(nameof(AiModelSettingsPage.ShowCustomCliInfo), changed); // 화면이 다시 읽는다
        Assert.Contains(nameof(AiModelSettingsPage.ShowCustomCliMaskBlock), changed);
    }

    [Fact]
    public void The_template_note_follows_the_executable_after_a_reload_and_goes_away_with_an_explicit_mode()
    {
        var zcode = AiCustomCliExamples.All.Single(e => e.Id == "zcode");
        var reloaded = new AiModelSettingsPage(Base(null, Cli("c1", "Z", zcode.Command)));
        Assert.Equal(zcode.NoteKo, reloaded.CustomClis[0].TemplateNote); // 저장하지 않은 안내가 다시 연 설정에서도 보인다

        reloaded.CustomClis[0].Command = "zcode --mode edit -p \"{prompt}\"";
        Assert.Null(reloaded.CustomClis[0].TemplateNote); // 사용자가 모드를 정했다

        reloaded.CustomClis[0].Command = "unrelatedcli";
        Assert.Null(reloaded.CustomClis[0].TemplateNote);
    }

    [Fact]
    public void Picking_a_template_adds_a_filled_saved_row_that_can_still_be_edited()
    {
        var page = new AiModelSettingsPage(Base());
        var codex = AiCustomCliExamples.All.Single(e => e.Id == "codex");

        var row = page.AddFromExample(codex);

        Assert.Equal((codex.Name, codex.Command), (row.Name, row.Command));
        Assert.Equal([new AiCustomCli { Id = row.Id, Name = codex.Name, Command = codex.Command }], page.ToSettings().CustomClis);
        row.Command += " --extra";
        Assert.EndsWith("--extra", page.ToSettings().CustomClis[0].Command);
    }

    [Fact]
    public void The_delete_confirmation_names_the_row_in_both_languages()
    {
        var page = new AiModelSettingsPage(Base(null, Cli("c1", "My CLI")));

        Assert.Contains("「My CLI」", page.DeleteCustomCliConfirmText(page.CustomClis[0]));
    }

    private static AiProfileServices Probe(List<string> probed, Func<string, bool>? found = null) =>
        new(null!, CustomCliInstalled: name =>
        {
            lock (probed)
            {
                probed.Add(name);
            }

            return found?.Invoke(name) ?? true;
        });
}
