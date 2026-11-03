using Oranda.App.Features.Ai.Chat;
using Oranda.App.Features.Ai.Tuning;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Profiles;
using Oranda.Core.Connection;
using Oranda.Core.Settings;

namespace Oranda.App.Tests.Features.Ai;

/// <summary>
/// 시스템 CLI 가 활성일 때 화면 쪽 분기: 보낼 곳 표시, 시작 창의 모델 줄, 이미지 입구.
/// 이 항목에서는 어느 CLI 도 사용 가능이 아니므로, 손으로 고친 설정이 가리키는 「사용 불가」 줄로 확인한다(목록 줄의 이미지 지원 칸은 그대로 읽는다).
/// </summary>
public class AiSystemCliScreenTests
{
    private static readonly AiContext Connection = new("DEV-ERP", ConnectionEnvironment.Development, AiAccessLevel.Query);

    private static AiSettings Cli(string id) => new() { SystemCliId = id, Model = "sonnet", Effort = "high" };

    [Fact]
    public void Target_shows_the_cli_not_a_missing_profile_and_carries_its_id()
    {
        var target = AiConversationTarget.From(Cli(AiProfilePresets.CodexCliId));

        Assert.DoesNotContain("찾을 수 없는 프로필", target.Label, StringComparison.Ordinal);
        Assert.Null(target.ProfileId);
        Assert.Equal(AiProfilePresets.CodexCliId, target.SystemCliId);
        Assert.Equal(AiProfilePresets.CodexCliId, target.ToExpectation().SystemCliId);
        Assert.False(target.IsSameAs(AiConversationTarget.From(new AiSettings())));
        Assert.True(target.IsSameAs(AiConversationTarget.From(Cli(AiProfilePresets.CodexCliId))));
    }

    [Fact]
    public void Claude_code_target_has_no_cli_id()
    {
        var target = AiConversationTarget.From(new AiSettings());

        Assert.Null(target.SystemCliId);
        Assert.Equal("Claude Code", target.Label);
    }

    [Fact]
    public void Start_info_for_a_system_cli_has_no_claude_model_lines_and_no_effort()
    {
        var info = AiTuningLauncher.BuildStartInfo(null, Connection, Cli(AiProfilePresets.AntigravityCliId));

        var model = Assert.Single(info.Models);
        Assert.Equal(TuningStartInfo.DefaultModelId, model.Id);
        Assert.DoesNotContain(info.Models, m => m.Id is "opus" or "sonnet" or "haiku");
        Assert.False(info.ShowEffort);
    }

    [Theory]
    [InlineData(AiProfilePresets.CodexCliId)]
    [InlineData(AiProfilePresets.AntigravityCliId)]
    public void A_coming_soon_cli_in_the_settings_file_gets_the_system_cli_start_info_and_a_closing_notice(string id)
    {
        var info = AiTuningLauncher.BuildStartInfo(null, Connection, Cli(id));

        Assert.False(info.ShowEffort);
        Assert.Single(info.Models);
        Assert.DoesNotContain(info.Models, m => m.Id is "opus" or "sonnet" or "haiku");

        // 라우터가 닫으며 내는 안내(system_cli_unavailable)는 화면에서 드라이버 글 그대로 보인다.
        var notice = $"선택한 시스템 CLI({id})를 이 컴퓨터에서 쓸 수 없습니다. 설정의 「AI 모델」 에서 다른 모델을 고르세요.";
        Assert.Equal(notice, AiErrorText.Describe(new AiError(notice, AiActiveProfile.SystemCliUnavailableCode)));
    }

    [Fact]
    public void Start_info_for_claude_code_still_has_the_three_models()
    {
        var info = AiTuningLauncher.BuildStartInfo(null, Connection, new AiSettings { Model = "sonnet" });

        Assert.Equal(3, info.Models.Count);
        Assert.True(info.ShowEffort);
    }

    [Fact]
    public void A_cli_without_image_support_closes_the_image_entrance()
    {
        var vm = new AiChatViewModel();
        vm.StartConversation("대화");
        vm.SettingsProvider = () => Cli(AiProfilePresets.AntigravityCliId);

        Assert.False(vm.ImagesAllowed);
        Assert.Equal(AiChatViewModel.ImagesUnsupportedText, vm.ImagesBlockedReason);
        Assert.Equal("Antigravity CLI", vm.ModelLabel);
    }

    [Fact]
    public void A_cli_with_image_support_and_claude_code_keep_the_entrance_open()
    {
        var vm = new AiChatViewModel();
        vm.StartConversation("대화");

        vm.SettingsProvider = () => Cli(AiProfilePresets.CodexCliId);
        Assert.True(vm.ImagesAllowed);
        Assert.Equal("Codex CLI", vm.ModelLabel);

        vm.SettingsProvider = () => new AiSettings();
        Assert.True(vm.ImagesAllowed);
        Assert.Equal("Claude Code", vm.ModelLabel);
    }
}
