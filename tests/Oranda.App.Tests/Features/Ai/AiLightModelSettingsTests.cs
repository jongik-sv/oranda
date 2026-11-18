using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.Ai.Settings;
using Oranda.App.Shell.Settings;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Ai.Profiles;
using Oranda.Core.Settings;

namespace Oranda.App.Tests.Features.Ai;

/// <summary>설정 「AI 모델」 쪽의 「가벼운 일 모델」 줄(TSK-15-02): 고르기, 저장·다시 열기, 지운 프로필, 기타 CLI, 화면 줄.</summary>
public class AiLightModelSettingsTests
{
    private static readonly IReadOnlyList<AiSystemCliInfo> Enabled =
    [
        new(AiProfilePresets.ClaudeCodeId, "Claude Code", true),
        new(AiProfilePresets.CodexCliId, "Codex CLI", true),
    ];

    private static AiLlmProfile Local(Guid? id = null) => ProfileSamples.Ollama(id) with { LocalAiConfirmedUrl = "http://localhost:11434/v1" };

    private static AiModelSettingsPage Page(AiSettings? ai = null) => new(ai ?? new AiSettings(), systemClis: Enabled);

    private static AiProfileChoice Row(AiModelSettingsPage page, Func<AiProfileChoice, bool> match) => page.LightChoices.Single(match);

    [Fact]
    public void Default_is_same_as_main_and_saves_no_light_value()
    {
        var page = Page();

        Assert.Equal("주 모델과 같음", page.LightSelectedTitle);
        Assert.True(page.LightChoices[0].IsCurrent);
        Assert.Equal(AiChoiceKind.SameAsMain, page.LightChoices[0].Kind);
        Assert.Null(page.ToSettings().Light);
        Assert.False(page.ShowLightProblem);
    }

    [Fact]
    public void Choosing_haiku_saves_the_model_without_effort_and_survives_reopen()
    {
        var page = Page();
        page.ChooseLight(Row(page, c => c.Model == "haiku"));

        var saved = page.ToSettings();
        Assert.Equal(new AiLightSettings { Model = "haiku" }, saved.Light);
        Assert.Null(saved.Light!.Effort); // 노력 칸 없음: 라우터가 low 로 고정
        Assert.Equal("Claude Code · haiku", page.LightSelectedTitle);

        var reopened = Page(saved);
        Assert.Equal("Claude Code · haiku", reopened.LightSelectedTitle);
        Assert.True(Row(reopened, c => c.Model == "haiku").IsCurrent);
        Assert.False(reopened.LightChoices[0].IsCurrent);
    }

    [Fact]
    public void Choosing_same_as_main_clears_a_stored_light_value()
    {
        var page = Page(new AiSettings { Light = new AiLightSettings { Model = "sonnet" } });
        Assert.True(Row(page, c => c.Model == "sonnet").IsCurrent);

        page.ChooseLight(page.LightChoices[0]);

        Assert.Null(page.ToSettings().Light);
        Assert.Equal("주 모델과 같음", page.LightSelectedTitle);
    }

    [Fact]
    public void Haiku_is_recommended_only_when_there_is_no_local_profile()
    {
        var paidOnly = Page(new AiSettings { Profiles = [ProfileSamples.Example()] });
        var withLocal = Page(new AiSettings { Profiles = [Local()] });

        Assert.Equal("추천", Row(paidOnly, c => c.Model == "haiku").DetailText);
        Assert.False(Row(withLocal, c => c.Model == "haiku").HasDetail);
        Assert.False(Row(paidOnly, c => c.Model == "sonnet").HasDetail);
    }

    [Fact]
    public void A_saved_profile_is_listed_under_its_header_with_a_local_mark_and_can_be_chosen()
    {
        var local = Local();
        var cloud = ProfileSamples.Example();
        var page = Page(new AiSettings { Profiles = [cloud, local] });

        var headers = page.LightChoices.Where(c => c.IsHeader).Select(c => c.Title).ToList();
        Assert.Contains("저장된 프로필", headers);
        Assert.Equal("로컬 Ollama (로컬)", Row(page, c => c.ProfileId == local.Id).Title);
        Assert.Equal("예제 LLM", Row(page, c => c.ProfileId == cloud.Id).Title);

        page.ChooseLight(Row(page, c => c.ProfileId == local.Id));

        Assert.Equal(new AiLightSettings { ProfileId = local.Id }, page.ToSettings().Light);
        Assert.Equal("로컬 Ollama (로컬)", page.LightSelectedTitle);
        Assert.Equal(AiActiveKind.ClaudeCode, page.ActiveKind); // 주 모델은 그대로
    }

    [Fact]
    public void A_deleted_profile_shows_no_profile_and_is_not_silently_replaced_by_the_main_model()
    {
        var gone = Guid.NewGuid();
        var page = Page(new AiSettings { Light = new AiLightSettings { ProfileId = gone } });

        Assert.Equal("프로필 없음", page.LightSelectedTitle);
        Assert.True(page.ShowLightProblem);
        var missing = Row(page, c => c.Kind == AiChoiceKind.MissingProfile);
        Assert.True(missing.IsCurrent);
        Assert.False(missing.Selectable);
        Assert.Equal(gone, page.ToSettings().Light!.ProfileId); // 저장해도 그대로
        Assert.False(page.LightChoices[0].IsCurrent);

        page.ChooseLight(missing); // 고를 수 없는 줄은 무시
        Assert.Equal(gone, page.ToSettings().Light!.ProfileId);
    }

    [Fact]
    public void Deleting_the_light_profile_in_the_manager_leaves_the_light_pointing_at_it()
    {
        var local = Local();
        var page = Page(new AiSettings { Profiles = [local], Light = new AiLightSettings { ProfileId = local.Id } });
        Assert.Equal("로컬 Ollama (로컬)", page.LightSelectedTitle);

        page.ApplyStoreChange(new AiSettings { Profiles = [], Light = new AiLightSettings { ProfileId = local.Id } }, deletedId: local.Id);

        Assert.Equal("프로필 없음", page.LightSelectedTitle);
        Assert.True(page.ShowLightProblem);
        Assert.Equal(local.Id, page.ToSettings().Light!.ProfileId);
    }

    [Fact]
    public void A_system_cli_and_a_custom_cli_can_be_chosen()
    {
        var page = Page(new AiSettings
        {
            CustomClis = [new AiCustomCli { Id = "mine", Name = "내 CLI", Command = "mycli {prompt}" }],
        });

        page.ChooseLight(Row(page, c => c.Kind == AiChoiceKind.SystemCli));
        Assert.Equal(AiProfilePresets.CodexCliId, page.ToSettings().Light!.SystemCliId);
        Assert.Equal("Codex CLI", page.LightSelectedTitle);

        page.ChooseLight(Row(page, c => c.Kind == AiChoiceKind.CustomCli));
        Assert.Equal(AiCustomCliTarget.SelectionOf("mine"), page.ToSettings().Light!.SystemCliId);
        Assert.Equal("내 CLI", page.LightSelectedTitle);
        Assert.True(Row(page, c => c.Kind == AiChoiceKind.CustomCli).IsCurrent);
    }

    [Fact]
    public void A_custom_cli_without_a_command_cannot_be_chosen()
    {
        var page = Page(new AiSettings { CustomClis = [new AiCustomCli { Id = "empty", Name = "빈 CLI", Command = string.Empty }] });

        var row = Row(page, c => c.Kind == AiChoiceKind.CustomCli);
        Assert.False(row.Selectable);
        page.ChooseLight(row);

        Assert.Null(page.ToSettings().Light);
    }

    [Fact]
    public void An_unlisted_claude_model_is_kept_as_the_current_row_and_unchanged_on_save()
    {
        var stored = new AiLightSettings { Model = "claude-example-model", Effort = "medium" };
        var page = Page(new AiSettings { Light = stored });

        var row = Row(page, c => c.Kind == AiChoiceKind.ClaudeModel && c.Model == "claude-example-model");
        Assert.True(row.IsCurrent);
        Assert.Equal(stored, page.ToSettings().Light); // 고르기 전에는 읽은 값 그대로
    }

    [Fact]
    public void Changing_the_light_choice_does_not_touch_the_main_choice()
    {
        var page = Page(new AiSettings { Model = "opus", Effort = "high" });
        page.ChooseLight(Row(page, c => c.Model == "haiku"));

        var saved = page.ToSettings();
        Assert.Equal(("opus", "high"), (saved.Model, saved.Effort));
        Assert.Null(saved.ActiveProfileId);
    }

    [Fact]
    public Task The_view_shows_the_light_row_with_its_hint_and_picks_a_row() => HeadlessHost.Run(() =>
    {
        var page = Page();
        var window = new Window { Width = 640, Height = 700, Content = new AiModelSettingsView { DataContext = page } };
        window.Show();
        window.UpdateLayout();
        try
        {
            T Find<T>(string name)
                where T : Control => window.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

            Assert.True(Find<StackPanel>("LightPanel").IsEffectivelyVisible);
            Assert.Equal("주 모델과 같음", Find<TextBlock>("LightTitle").Text);
            Assert.Equal("간단한 일(SQL 설명·오류 고치기·요약)에 씁니다. 입력이 크면 주 모델로 보냅니다.", Find<TextBlock>("LightHint").Text);
            Assert.False(Find<StackPanel>("LightProblemPanel").IsEffectivelyVisible);

            Find<Button>("LightBox").Command!.Execute(null);
            Assert.True(page.IsLightDropdownOpen);
            Dispatcher.UIThread.RunJobs();

            page.ChooseLight(Row(page, c => c.Model == "haiku"));
            Dispatcher.UIThread.RunJobs();
            Assert.False(page.IsLightDropdownOpen);
            Assert.Equal("Claude Code · haiku", Find<TextBlock>("LightTitle").Text);

            page.IsNone = true; // 「선택 안 함」 이면 줄을 숨긴다
            Dispatcher.UIThread.RunJobs();
            Assert.False(Find<StackPanel>("LightPanel").IsEffectivelyVisible);
        }
        finally
        {
            window.Close();
        }
    });
}
