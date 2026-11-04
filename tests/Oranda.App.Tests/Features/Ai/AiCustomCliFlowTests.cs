using Oranda.App.Features.Ai.Chat;
using Oranda.App.Features.Ai.Tuning;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Profiles;
using Oranda.Core.Settings;

namespace Oranda.App.Tests.Features.Ai;

/// <summary>
/// 기타 CLI(사용자가 등록한 명령, 글만 주고받음)를 고른 대화의 분기: 튜닝 안내 후 닫힘, 가림 대상에서 요청 없음, 요청의 가림 값, 이미지 입구 닫힘,
/// 일반 대화에 문장·도구가 없음. 진짜 라우터를 거치는 시험은 가짜 흐름이 닫는 경로(기대값 없음·가림 값 불일치)를 라우터가 한 번 더 닫는지 본다.
/// </summary>
public class AiCustomCliFlowTests
{
    private static async Task<AiCustomCliRig> GeneralAsync(AiCustomCliRig rig, string text = "NVL 과 COALESCE 차이가 뭐야?")
    {
        await rig.Flow.FollowUpAsync(text);
        return rig;
    }

    [Fact]
    public async Task Starting_a_tuning_on_a_custom_cli_explains_and_sends_nothing()
    {
        var rig = new AiCustomCliRig();
        var generation = rig.Flow.Generation;

        await rig.Flow.StartAsync(new TuningStartChoice(Benchmark: false, IndexDdl: false, TuningStartInfo.DefaultModelId), AiCustomCliRig.Dev);

        Assert.Empty(rig.Driver.Requests);
        Assert.Contains(AiTuningCustomCli.TuningText, rig.AssistantTexts);
        Assert.Equal(generation, rig.Flow.Generation); // 거부된 시작이 앞 대화의 상태를 바꾸지 않는다
        Assert.False(rig.Chat.IsTuning);
    }

    [Fact]
    public async Task Starting_a_tuning_on_a_custom_cli_whose_row_is_gone_also_explains_and_sends_nothing()
    {
        var rig = new AiCustomCliRig { Settings = new AiSettings { SystemCliId = "custom:gone", CustomClis = [AiCustomCliRig.Mine] } };

        await rig.Flow.StartAsync(new TuningStartChoice(Benchmark: false, IndexDdl: false, TuningStartInfo.DefaultModelId), AiCustomCliRig.Dev);

        Assert.Empty(rig.Driver.Requests);
        Assert.Contains(AiTuningCustomCli.TuningText, rig.AssistantTexts);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task A_tuning_request_says_whether_it_must_be_masked(bool mask, bool expected)
    {
        var rig = new AiCustomCliRig { Settings = AiCustomCliRig.ClaudeSettings(), Mask = mask };

        await rig.Flow.StartAsync(new TuningStartChoice(Benchmark: false, IndexDdl: false, TuningStartInfo.DefaultModelId), AiCustomCliRig.Dev);

        var request = Assert.Single(rig.Driver.Requests);
        Assert.Equal(expected, request.MaskingRequired);
        Assert.Null(request.Expected!.CustomCliCommandHash);
    }

    [Fact]
    public async Task A_tuning_flow_that_cannot_tell_whether_to_mask_asks_for_masking()
    {
        var rig = new AiCustomCliRig { Settings = AiCustomCliRig.ClaudeSettings() };
        var flow = new AiTuningFlow(new AiCustomCliRig.Editor(), rig.Driver, rig.Chat, (_, _, _) => { }, settings: () => rig.Settings); // maskRequired 판단 쪽이 없다

        await flow.StartAsync(new TuningStartChoice(Benchmark: false, IndexDdl: false, TuningStartInfo.DefaultModelId), AiCustomCliRig.Dev);

        Assert.True(Assert.Single(rig.Driver.Requests).MaskingRequired);
    }

    [Fact]
    public async Task A_general_chat_on_a_custom_cli_sends_only_the_text_with_no_statement_session_tools_or_images()
    {
        var rig = await GeneralAsync(new AiCustomCliRig());

        var request = Assert.Single(rig.Driver.Requests);
        Assert.False(request.MaskingRequired);
        Assert.Contains("NVL 과 COALESCE 차이가 뭐야?", request.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("<statement>", request.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET", request.Prompt, StringComparison.Ordinal);
        Assert.Contains("No database tools exist", request.Prompt, StringComparison.Ordinal);
        Assert.Null(request.Context.StatementText);
        Assert.Null(request.Images);
        Assert.Equal(AiAccessLevel.Off, request.Session!.Run.Context.AccessLevel); // 도구가 없는 범위
        Assert.Empty(rig.Sessions); // 도구 범위를 만들지 않았다
        Assert.Equal("custom:c1", request.Expected!.SystemCliId);
        Assert.Equal(AiCustomCliTarget.CommandHash(AiCustomCliRig.Mine.Command), request.Expected.CustomCliCommandHash);
        Assert.Contains(AiTuningCustomCli.GeneralText, rig.AssistantTexts);
    }

    [Fact]
    public async Task A_general_chat_on_a_masked_connection_sends_nothing_on_a_custom_cli_and_asks_for_no_confirmation()
    {
        var rig = await GeneralAsync(new AiCustomCliRig { Mask = true });

        Assert.Empty(rig.Driver.Requests);
        Assert.Empty(rig.TextConfirmed);
        Assert.Contains(AiTuningCustomCli.MaskingBlockedText, rig.AssistantTexts);
        Assert.Equal("NVL 과 COALESCE 차이가 뭐야?", rig.Chat.Draft); // 입력은 돌려 둔다
    }

    [Fact]
    public async Task A_general_chat_whose_connection_is_unknown_is_treated_as_masked_on_a_custom_cli()
    {
        var rig = new AiCustomCliRig();
        var flow = new AiTuningFlow(new AiCustomCliRig.Editor(), rig.Driver, rig.Chat, (_, _, _) => { }, settings: () => rig.Settings); // generalContext 없음

        await flow.FollowUpAsync("질문");

        Assert.Empty(rig.Driver.Requests);
        Assert.Contains(AiTuningCustomCli.MaskingBlockedText, rig.AssistantTexts);
    }

    [Fact]
    public async Task A_general_chat_on_a_custom_cli_whose_row_is_gone_sends_nothing()
    {
        var rig = await GeneralAsync(new AiCustomCliRig { Settings = new AiSettings { SystemCliId = "custom:gone" } });

        Assert.Empty(rig.Driver.Requests);
        Assert.Contains(AiTuningCustomCli.UnavailableText, rig.AssistantTexts);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task A_general_request_on_claude_code_says_whether_it_must_be_masked(bool mask, bool expected)
    {
        var rig = await GeneralAsync(new AiCustomCliRig { Settings = AiCustomCliRig.ClaudeSettings(), Mask = mask });

        var request = Assert.Single(rig.Driver.Requests);
        Assert.Equal(expected, request.MaskingRequired);
        Assert.Null(request.Expected!.CustomCliCommandHash);
        Assert.DoesNotContain("No database tools exist", request.Prompt, StringComparison.Ordinal); // 다른 종류의 안내는 그대로
        Assert.Contains("<statement>", request.Prompt, StringComparison.Ordinal); // 같은 편집기에서 Claude Code 는 문장을 범위로 도구를 연다
        Assert.Equal(AiAccessLevel.Query, request.Session!.Run.Context.AccessLevel);
        Assert.Single(rig.Sessions);
    }

    [Fact]
    public async Task The_image_entrance_is_closed_for_a_custom_cli_and_the_model_label_is_its_name()
    {
        var rig = new AiCustomCliRig();
        _ = rig.Flow;

        Assert.False(rig.Chat.ImagesAllowed);
        Assert.Equal("사내 CLI", rig.Chat.ModelLabel);
        await rig.Chat.AddImagesAsync([AiChatImageAttachTests.Png()]);
        Assert.Empty(rig.Chat.Attachments);

        rig.Settings = new AiSettings { SystemCliId = "custom:gone" }; // 쓸 수 없는 줄도 닫는다
        Assert.False(rig.Chat.ImagesAllowed);
    }

    [Fact]
    public async Task An_attachment_made_before_switching_to_a_custom_cli_is_not_sent()
    {
        var rig = new AiCustomCliRig { Settings = AiCustomCliRig.ClaudeSettings() };
        _ = rig.Flow;
        await rig.Chat.AddImagesAsync([AiChatImageAttachTests.Png()]);
        var images = rig.Chat.Attachments.ToList();
        Assert.Single(images);
        rig.Settings = AiCustomCliRig.CustomSettings(); // 붙인 뒤에 기타 CLI 로 바꿨다

        await rig.Flow.FollowUpAsync(new ChatFollowUp("이 화면 설명해 줘", images));

        Assert.Empty(rig.Driver.Requests);
        Assert.Contains(AiTuningCustomCli.ImagesText, rig.AssistantTexts);
    }

    [Fact]
    public async Task The_real_router_sends_an_unmasked_general_chat_to_the_custom_driver_with_the_confirmed_command()
    {
        var rig = await GeneralAsync(new AiCustomCliRig { UseRealRouter = true });

        var (request, row) = Assert.Single(rig.CustomDriver.Calls);
        Assert.Equal(AiCustomCliRig.Mine, row);
        Assert.False(request.MaskingRequired);
        Assert.Null(request.Session); // 라우터가 도구 세션을 비워 넘긴다
        Assert.Contains("기타 CLI 답 1", rig.AssistantTexts);
    }

    [Fact]
    public async Task The_real_router_closes_a_request_from_a_flow_that_does_not_know_the_target()
    {
        var rig = new AiCustomCliRig { UseRealRouter = true, NoSettings = true }; // 흐름은 보낼 곳을 모른다 → 기대값 null

        await rig.Flow.FollowUpAsync("질문");

        Assert.Empty(rig.CustomDriver.Calls);
        Assert.Contains(rig.AssistantTexts, text => text.Contains("모델 설정이 바뀌어 요청을 보내지 않았습니다", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_real_router_still_blocks_when_the_settings_turn_masking_on_after_the_flow_decided()
    {
        var rig = new AiCustomCliRig { UseRealRouter = true }; // 흐름은 가림이 필요 없다고 봤다(Mask=false)
        _ = rig.Flow;
        rig.Store.Current = new AppSettings { Ai = AiCustomCliRig.CustomSettings(mask: true) }; // 그 사이 파일 설정이 바뀌었다

        await rig.Flow.FollowUpAsync("질문");

        Assert.Empty(rig.CustomDriver.Calls);
        Assert.Contains(rig.AssistantTexts, text => text.Contains("가림 대상 접속", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Switching_from_claude_code_to_a_custom_cli_in_a_conversation_starts_a_new_one_without_the_old_resume_id()
    {
        var rig = new AiCustomCliRig { Settings = AiCustomCliRig.ClaudeSettings() };
        await rig.Flow.FollowUpAsync("첫 질문");
        Assert.NotNull(rig.Chat.ResumeId); // 이어 말할 대화가 있다
        rig.Use(AiCustomCliRig.CustomSettings());

        await rig.Flow.FollowUpAsync("둘째 질문");

        Assert.Equal(2, rig.Driver.Requests.Count);
        var second = rig.Driver.Requests[1];
        Assert.Null(second.ResumeId); // 옛 대화의 id 를 기타 CLI 로 넘기지 않는다
        Assert.Equal("custom:c1", second.Expected!.SystemCliId);
        Assert.Contains(AiTuningFlow.GeneralTargetChangedText, rig.AssistantTexts);
        Assert.DoesNotContain("<statement>", second.Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Changing_only_the_command_of_the_custom_cli_in_a_conversation_starts_a_new_one_with_the_new_hash()
    {
        var rig = new AiCustomCliRig();
        await rig.Flow.FollowUpAsync("첫 질문");
        Assert.NotNull(rig.Chat.ResumeId);
        var edited = AiCustomCliRig.Mine with { Command = "mycli --auto" };
        rig.Use(new AiSettings { SystemCliId = "custom:c1", CustomClis = [edited] });

        await rig.Flow.FollowUpAsync("둘째 질문");

        var second = rig.Driver.Requests[1];
        Assert.Null(second.ResumeId);
        Assert.Equal(AiCustomCliTarget.CommandHash(edited.Command), second.Expected!.CustomCliCommandHash);
        Assert.Contains(AiTuningFlow.GeneralTargetChangedText, rig.AssistantTexts);
    }

    [Fact]
    public async Task The_real_router_accepts_the_second_turn_after_the_command_was_edited_because_the_flow_re_read_the_target()
    {
        var rig = new AiCustomCliRig { UseRealRouter = true };
        await rig.Flow.FollowUpAsync("첫 질문");
        var edited = AiCustomCliRig.Mine with { Command = "mycli --auto" };
        rig.Use(new AiSettings { SystemCliId = "custom:c1", CustomClis = [edited] });

        await rig.Flow.FollowUpAsync("둘째 질문");

        Assert.Equal(2, rig.CustomDriver.Calls.Count);
        Assert.Equal(edited, rig.CustomDriver.Calls[1].Row);
    }

    [Fact]
    public async Task A_follow_up_after_switching_a_started_conversation_to_a_custom_cli_points_to_a_new_conversation()
    {
        var rig = new AiCustomCliRig { Settings = AiCustomCliRig.ClaudeSettings() };
        await rig.Flow.StartAsync(new TuningStartChoice(Benchmark: false, IndexDdl: false, TuningStartInfo.DefaultModelId), AiCustomCliRig.Dev);
        Assert.True(rig.Chat.IsTuning);
        rig.Use(AiCustomCliRig.CustomSettings());

        await rig.Flow.FollowUpAsync("이어서");

        Assert.Single(rig.Driver.Requests);
        Assert.Contains(AiTuningFlow.ProfileChangedToCustomCliText, rig.AssistantTexts);
        Assert.DoesNotContain(AiTuningFlow.ProfileChangedText, rig.AssistantTexts);
    }
}
