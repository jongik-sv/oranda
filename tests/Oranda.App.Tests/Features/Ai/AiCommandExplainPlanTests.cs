using Oranda.App.Features.Ai;
using Oranda.App.Features.Ai.Tuning.Commands;
using Oranda.App.Features.ExecutionPlan;
using Oranda.Core.Ai.Access;

namespace Oranda.App.Tests.Features.Ai;

/// <summary>실행 계획 해설 명령(cmd-explain-plan): 화면 계획 글 미포함, sql_id 대조, 읽을 길 없을 때 거부, 가림, 수준 상한, 레포트·비교 창·새 탭 없음, 메뉴 식.</summary>
public class AiCommandExplainPlanTests
{
    private static readonly Guid Tab = Guid.Parse("11111111-1111-1111-1111-111111111111");

    // 화면 계획 글: 머리(SQL_ID) 다음에 서버가 가진 SQL 본문이 나오고, 술어에 리터럴이 있다.
    private static readonly string ScreenText =
        "SQL_ID  abc123xyz, child number 0\n-------------------------------------\n"
        + "SELECT * FROM tb_order WHERE stat_cd = 'SECRET' /* 메모본문 */\n\n"
        + "Plan hash value: 111\n\n"
        + "Predicate Information:\n   1 - filter(\"STAT_CD\"='SECRET-PREDICATE')\n";

    private static ExplainPlanCommand With(string? sqlId, string? text) =>
        new(_ => new ExplainPlanCommand.ScreenPlan(sqlId, text));

    private static readonly ExplainPlanCommand NoScreen = new(_ => null);

    private static Oranda.Core.Connection.ConnectionProfile ExplainOff() => AiCommandExplainRig.Dev with { AllowExplainPlan = false };

    [Fact]
    public async Task The_screen_plan_text_and_predicates_never_reach_the_prompt_and_the_matching_cursor_gives_the_sql_id()
    {
        var rig = new AiCommandExplainRig();
        var command = With("abc123xyz", ScreenText);

        Assert.Null(await rig.RunAsync(command, tabId: Tab));

        var request = Assert.Single(rig.Driver.Requests);
        Assert.DoesNotContain("SECRET-PREDICATE", request.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Predicate Information", request.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Plan hash value", request.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("abc123xyz", request.Prompt, StringComparison.Ordinal);
        Assert.Equal("abc123xyz", request.Context.SqlId);
        Assert.Contains("get_cursor_plan", request.Prompt, StringComparison.Ordinal);
        Assert.Contains("Call at most 5 tools", request.Prompt, StringComparison.Ordinal);
        Assert.Contains(ExplainPlanCommand.SentNoteText, rig.AssistantTexts.First(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_plan_of_another_statement_gives_no_sql_id_and_reads_the_expected_plan()
    {
        var rig = new AiCommandExplainRig();
        var other = ScreenText.Replace("tb_order", "tb_customer", StringComparison.Ordinal);

        Assert.Null(await rig.RunAsync(With("abc123xyz", other), tabId: Tab));

        var request = Assert.Single(rig.Driver.Requests);
        Assert.Null(request.Context.SqlId);
        Assert.Contains("explain_plan", request.Prompt, StringComparison.Ordinal);
        Assert.Contains("estimate", request.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("tb_customer", request.Prompt, StringComparison.Ordinal);
    }

    // 「실제 계획(A-Rows)」 은 서버에 힌트 사본을 보낸다: 서버 글의 본문은 PlanStatisticsHint 가 만든 사본이다.
    private static string HintedScreenText(string statement) =>
        "SQL_ID  abc123xyz, child number 0\n-------------------------------------\n"
        + PlanStatisticsHint.AddTo(statement) + "\n\n"
        + "Plan hash value: 111\n\n"
        + "Predicate Information:\n   1 - filter(\"STAT_CD\"='SECRET-PREDICATE')\n";

    [Fact]
    public async Task A_plan_of_the_hinted_copy_of_this_statement_gives_the_sql_id_and_a_plan_of_another_hinted_statement_does_not()
    {
        var same = new AiCommandExplainRig();
        Assert.Null(await same.RunAsync(With("abc123xyz", HintedScreenText(AiCommandExplainRig.Cursor)), tabId: Tab));
        var request = Assert.Single(same.Driver.Requests);
        Assert.Equal("abc123xyz", request.Context.SqlId);
        Assert.Contains("get_cursor_plan", request.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("GATHER_PLAN_STATISTICS", request.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET-PREDICATE", request.Prompt, StringComparison.Ordinal);

        // 서버가 줄을 끊고 공백을 바꿔 보여도 같다.
        var wrapped = HintedScreenText(AiCommandExplainRig.Cursor).Replace(" FROM ", "\n FROM\t", StringComparison.Ordinal);
        var rewrapped = new AiCommandExplainRig();
        Assert.Null(await rewrapped.RunAsync(With("abc123xyz", wrapped), tabId: Tab));
        Assert.Equal("abc123xyz", Assert.Single(rewrapped.Driver.Requests).Context.SqlId);

        // 편집기 글에 이미 같은 힌트가 있어도 같다.
        var already = PlanStatisticsHint.AddTo(AiCommandExplainRig.Cursor)!;
        var withHint = new AiCommandExplainRig();
        Assert.Null(await withHint.RunAsync(With("abc123xyz", HintedScreenText(already)), statement: already, tabId: Tab));
        Assert.Equal("abc123xyz", Assert.Single(withHint.Driver.Requests).Context.SqlId);

        // 다른 문장의 힌트 사본, 힌트 외 차이가 있는 사본은 거른다.
        foreach (var other in new[]
        {
            AiCommandExplainRig.Cursor.Replace("tb_order", "tb_customer", StringComparison.Ordinal),
            AiCommandExplainRig.Cursor.Replace("stat_cd", "stat_cd2", StringComparison.Ordinal),
        })
        {
            var rig = new AiCommandExplainRig();
            Assert.Null(await rig.RunAsync(With("abc123xyz", HintedScreenText(other)), tabId: Tab));
            var sent = Assert.Single(rig.Driver.Requests);
            Assert.Null(sent.Context.SqlId);
            Assert.Contains("explain_plan", sent.Prompt, StringComparison.Ordinal);
        }

        var denied = new AiCommandExplainRig();
        var otherText = HintedScreenText(AiCommandExplainRig.Cursor.Replace("tb_order", "tb_customer", StringComparison.Ordinal));
        Assert.Equal(ExplainPlanCommand.NoReadablePlanText, await denied.RunAsync(With("abc123xyz", otherText), profile: ExplainOff(), tabId: Tab));
    }

    [Fact]
    public async Task Without_a_cursor_the_expected_plan_is_read_when_explain_plan_is_allowed_and_nothing_is_sent_when_it_is_not()
    {
        var allowed = new AiCommandExplainRig();
        Assert.Null(await allowed.RunAsync(NoScreen, tabId: Tab));
        Assert.Null(Assert.Single(allowed.Driver.Requests).Context.SqlId);

        var noCursorId = new AiCommandExplainRig();
        Assert.Null(await noCursorId.RunAsync(With(null, ScreenText), tabId: Tab)); // 계획은 있으나 커서가 없다 = 커서 없음
        Assert.Null(Assert.Single(noCursorId.Driver.Requests).Context.SqlId);

        var denied = new AiCommandExplainRig();
        Assert.Equal(ExplainPlanCommand.NoReadablePlanText, await denied.RunAsync(NoScreen, profile: ExplainOff(), tabId: Tab));
        Assert.Empty(denied.Driver.Requests);

        var mismatch = new AiCommandExplainRig();
        var other = ScreenText.Replace("tb_order", "tb_customer", StringComparison.Ordinal);
        Assert.Equal(ExplainPlanCommand.NoReadablePlanText, await mismatch.RunAsync(With("abc123xyz", other), profile: ExplainOff(), tabId: Tab));
        Assert.Empty(mismatch.Driver.Requests);

        // 맞는 커서가 있으면 explain_plan 이 꺼져 있어도 보낸다(get_cursor_plan 으로 읽는다).
        var cursorOnly = new AiCommandExplainRig();
        Assert.Null(await cursorOnly.RunAsync(With("abc123xyz", ScreenText), profile: ExplainOff(), tabId: Tab));
        Assert.Equal("abc123xyz", Assert.Single(cursorOnly.Driver.Requests).Context.SqlId);
    }

    [Fact]
    public async Task No_statement_is_refused_without_starting()
    {
        var rig = new AiCommandExplainRig();

        Assert.Equal(ExplainPlanCommand.NoStatementText, await rig.RunAsync(NoScreen, statement: null));
        Assert.Empty(rig.Driver.Requests);
    }

    [Fact]
    public async Task Masked_prompt_has_no_literal_or_comment_body()
    {
        var rig = new AiCommandExplainRig { Mask = true };

        Assert.Null(await rig.RunAsync(With("abc123xyz", ScreenText), tabId: Tab));

        var prompt = Assert.Single(rig.Driver.Requests).Prompt;
        Assert.DoesNotContain("SECRET", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("메모본문", prompt, StringComparison.Ordinal);
        Assert.Contains(":v1", prompt, StringComparison.Ordinal);
        Assert.Contains("가려서 보냈습니다", rig.AssistantTexts.First(), StringComparison.Ordinal);

        var missing = new AiCommandExplainRig { Mask = true, Masker = null };
        Assert.Null(await missing.RunAsync(NoScreen, tabId: Tab));
        Assert.Empty(missing.Driver.Requests);

        var failing = new AiCommandExplainRig { Mask = true };
        failing.Masker!.FailMask = true;
        Assert.Null(await failing.RunAsync(NoScreen, tabId: Tab));
        Assert.Empty(failing.Driver.Requests);
    }

    [Fact]
    public async Task A_headerless_plan_text_does_not_vouch_for_its_sql_id()
    {
        // BASIC 형식처럼 SQL_ID 머리가 없는 글은 어느 문장의 계획인지 알 수 없다: 다른 문장의 sql_id 를 싣지 않는다.
        foreach (var text in new string?[] { null, "Plan hash value: 111\n| Id | Operation |" })
        {
            var rig = new AiCommandExplainRig();
            Assert.Null(await rig.RunAsync(With("zzz999", text), tabId: Tab));
            Assert.Null(Assert.Single(rig.Driver.Requests).Context.SqlId);
        }
    }

    [Fact]
    public async Task The_level_is_capped_at_Plan_and_stays_capped_after_a_follow_up()
    {
        var rig = new AiCommandExplainRig();
        Assert.Null(await rig.RunAsync(NoScreen, tabId: Tab));

        var first = rig.Driver.Requests[0];
        Assert.Equal(AiAccessLevel.Plan, first.Context.AccessLevel);
        Assert.Equal(AiAccessLevel.Plan, first.Session!.Level);

        await rig.Flow.FollowUpAsync("인덱스를 안 타는 이유가 뭐야?");
        var second = rig.Driver.Requests[^1];
        Assert.Equal(AiAccessLevel.Plan, second.Context.AccessLevel);
        Assert.Equal(AiAccessLevel.Plan, second.Session!.Level);
    }

    [Fact]
    public async Task There_is_no_report_no_compare_window_and_no_new_tab()
    {
        var rig = new AiCommandExplainRig();
        Assert.Null(await rig.RunAsync(NoScreen, tabId: Tab));

        Assert.False(rig.Chat.CanSaveReport);
        Assert.Empty(rig.Editor.Proposed);
        Assert.Empty(rig.Tabs);
        Assert.Null(rig.Driver.Requests[0].OutputSchema);
    }

    [Fact]
    public void The_menu_expression_needs_the_plan_access_key_and_the_instruction_ignores_the_target_text_and_state()
    {
        Assert.Equal(AiCommands.TuneEnablement + " && aiAccessPlan", ExplainPlanCommand.Enablement);
        Assert.Equal(AiAccessLevel.Plan, ExplainPlanCommand.Instance.AccessCeiling);
        Assert.False(ExplainPlanCommand.Instance.ProducesReport);

        var target = new AiCommandTarget(AiCommandExplainRig.Statement("SELECT 'LEAK' FROM dual"), null, null, "STATE-LEAK");
        var text = NoScreen.BuildInstruction(new AiCommandPromptInput(target, "SELECT :v1 FROM dual", null, null, new("DEV-ERP", Oranda.Core.Connection.ConnectionEnvironment.Development, AiAccessLevel.Plan)));
        Assert.DoesNotContain("LEAK", text, StringComparison.Ordinal);
    }
}
