using Oranda.App.Features.Ai;
using Oranda.App.Features.Ai.Tuning.Commands;
using Oranda.Core.Ai.Access;

namespace Oranda.App.Tests.Features.Ai;

/// <summary>SQL 설명 명령(cmd-explain-sql): 가림, 수준 상한(이어 말하기 뒤에도), 레포트·비교 창·새 탭 없음, 문장 없음 거부, 메뉴 식.</summary>
public class AiCommandExplainSqlTests
{
    private static readonly ExplainSqlCommand Command = ExplainSqlCommand.Instance;

    [Fact]
    public async Task Masked_prompt_has_no_literal_or_comment_body_and_the_first_line_says_so()
    {
        var rig = new AiCommandExplainRig { Mask = true };

        Assert.Null(await rig.RunAsync(Command));

        var prompt = Assert.Single(rig.Driver.Requests).Prompt;
        Assert.DoesNotContain("SECRET", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("메모본문", prompt, StringComparison.Ordinal);
        Assert.Contains(":v1", prompt, StringComparison.Ordinal);
        Assert.Contains("Explain this Oracle SQL statement", prompt, StringComparison.Ordinal);
        Assert.Contains("Call at most 5 tools", prompt, StringComparison.Ordinal);
        Assert.Contains("comment text is missing", prompt, StringComparison.Ordinal);
        Assert.Contains("Response style", prompt, StringComparison.Ordinal);
        var first = rig.AssistantTexts.First();
        Assert.Contains("가려서 보냈습니다", first, StringComparison.Ordinal);
        Assert.Contains(ExplainSqlCommand.MaskedSentNote, first, StringComparison.Ordinal);

        var plain = new AiCommandExplainRig();
        Assert.Null(await plain.RunAsync(Command));
        Assert.Contains("SECRET", Assert.Single(plain.Driver.Requests).Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("comment text is missing", plain.Driver.Requests[0].Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(ExplainSqlCommand.MaskedSentNote, plain.AssistantTexts.First(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_masking_failure_or_missing_masker_does_not_call_the_driver()
    {
        var missing = new AiCommandExplainRig { Mask = true, Masker = null };
        Assert.Null(await missing.RunAsync(Command));
        Assert.Empty(missing.Driver.Requests);

        var failing = new AiCommandExplainRig { Mask = true };
        failing.Masker!.FailMask = true;
        Assert.Null(await failing.RunAsync(Command));
        Assert.Empty(failing.Driver.Requests);
    }

    [Fact]
    public async Task The_level_is_capped_at_Metadata_and_stays_capped_after_a_follow_up()
    {
        var rig = new AiCommandExplainRig();
        Assert.Null(await rig.RunAsync(Command));

        var first = rig.Driver.Requests[0];
        Assert.Equal(AiAccessLevel.Metadata, first.Context.AccessLevel);
        Assert.Equal(AiAccessLevel.Metadata, first.Session!.Level); // 접속은 QueryRows 라도 벤치마크 도구가 닫힌다

        await rig.Flow.FollowUpAsync("조인 조건을 더 설명해 줘"); // refreshContext 가 접속의 지금 수준(QueryRows)으로 되돌려도
        var second = rig.Driver.Requests[^1];
        Assert.Equal(AiAccessLevel.Metadata, second.Context.AccessLevel);
        Assert.Equal(AiAccessLevel.Metadata, second.Session!.Level);
        Assert.Contains("Response style", second.Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task There_is_no_report_no_compare_window_and_no_new_tab()
    {
        var rig = new AiCommandExplainRig();
        Assert.Null(await rig.RunAsync(Command));

        Assert.False(rig.Chat.CanSaveReport);
        Assert.Empty(rig.Editor.Proposed);
        Assert.Empty(rig.Tabs);
        Assert.Null(rig.Driver.Requests[0].OutputSchema); // 자유 글: 구조 결과 없음
    }

    [Fact]
    public async Task No_statement_is_refused_without_starting()
    {
        var rig = new AiCommandExplainRig();

        Assert.Equal(ExplainSqlCommand.NoStatementText, await rig.RunAsync(Command, statement: null));
        Assert.Equal(ExplainSqlCommand.NoStatementText, await rig.RunAsync(Command, statement: "  "));
        Assert.Empty(rig.Driver.Requests);
    }

    [Fact]
    public void The_instruction_depends_only_on_the_mask_flag_and_the_menu_expression_is_the_tune_one()
    {
        var target = new AiCommandTarget(AiCommandExplainRig.Statement("SELECT 'LEAK' FROM dual"), "abc123", null, "STATE-LEAK");
        var plain = Command.BuildInstruction(new AiCommandPromptInput(target, "SELECT :v1 FROM dual", null, null, new("DEV-ERP", Oranda.Core.Connection.ConnectionEnvironment.Development, AiAccessLevel.Metadata)));

        Assert.DoesNotContain("LEAK", plain, StringComparison.Ordinal);
        Assert.DoesNotContain("abc123", plain, StringComparison.Ordinal);
        Assert.Equal(AiCommands.TuneEnablement, ExplainSqlCommand.Enablement);
        Assert.Equal(AiAccessLevel.Metadata, Command.AccessCeiling);
        Assert.False(Command.ProducesReport);
        Assert.Null(Command.OutputSchema);
    }
}
