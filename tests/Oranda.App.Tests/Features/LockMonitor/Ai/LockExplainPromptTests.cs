using Oranda.App.Features.LockMonitor.Ai;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.App.Features.Ai.Kit;
using Oranda.App.Tests.Features.Ai.Kit;
using Oranda.Core.Connection;
using Oranda.Core.Monitoring;
using Oranda.Language.Ai.Literals;

namespace Oranda.App.Tests.Features.LockMonitor.Ai;

/// <summary>락·세션 상황 풀이의 보낼 글과 답 읽기(설계 §3.2, 결정 AE-3).</summary>
public class LockExplainPromptTests
{
    private static readonly SessionKey Holder = new(1, 1287, 40211);
    private static readonly SessionKey Waiter = new(1, 1422, 9001);

    private static readonly SqlLiteralMasker Masker = new();

    private static LockExplainInput Chain() => new(LockExplainKind.LockChain,
    [
        new LockExplainSession(Holder)
        {
            Username = "SCOTT", OsUser = "kim", Machine = "PC-ACCT-01", Program = "sqlplus.exe", Module = "ORDERS_APP", Status = "INACTIVE",
            LockedObject = "SAMPLE.ORDERS", HeldMode = "X (6)", PrevSqlId = "abc123", PrevSqlText = "UPDATE SAMPLE.ORDERS SET STATUS = 'HOLD' WHERE ORDER_ID = 42",
            TransactionStart = new DateTimeOffset(2026, 11, 18, 9, 0, 0, TimeSpan.Zero), UndoBlocks = 12,
        },
        new LockExplainSession(Waiter)
        {
            BlockerKey = Holder, Username = "HR", Machine = "PC-02", Program = "app.exe", Status = "ACTIVE",
            Event = "enq: TX - row lock contention", SecondsInWait = 287, LockedObject = "SAMPLE.ORDERS", RequestedMode = "X (6)",
            SqlId = "def456", SqlText = "UPDATE SAMPLE.ORDERS SET STATUS = 'DONE' WHERE ORDER_ID = 42",
        },
    ]);

    private static AiOneShotRun Run(AiAccessLevel level, bool mask = false) =>
        KitFakes.Run(new FakeDriver(), KitFakes.Profile(mask ? ConnectionEnvironment.Production : ConnectionEnvironment.Development, level));

    private static AiStripRequest Ready(LockExplainInput input, AiAccessLevel level, bool mask = false)
    {
        var prepared = LockExplainPrompt.Prepare(input, Run(level, mask), Masker);
        Assert.Null(prepared.RefusalText);
        return prepared.Request!;
    }

    [Fact]
    public void Plan_level_replaces_user_machine_program_and_module_with_placeholders()
    {
        var request = Ready(Chain(), AiAccessLevel.Plan);

        foreach (var raw in new[] { "SCOTT", "kim", "PC-ACCT-01", "sqlplus.exe", "ORDERS_APP", "PC-02", "app.exe" })
        {
            Assert.DoesNotContain(raw, request.Prompt, StringComparison.Ordinal);
            Assert.DoesNotContain(raw, request.Bundle.PreviewText, StringComparison.Ordinal);
        }

        foreach (var alias in new[] { "USER1", "USER2", "USER3", "HOST1", "HOST2", "PROGRAM1", "MODULE1" })
        {
            Assert.Contains(alias, request.Prompt, StringComparison.Ordinal);
        }

        Assert.Contains("enq: TX - row lock contention", request.Prompt, StringComparison.Ordinal);
        Assert.Contains("SAMPLE.ORDERS", request.Prompt, StringComparison.Ordinal);
        Assert.Contains("1·1287", request.Prompt, StringComparison.Ordinal);
        Assert.Contains("## 결론", request.Prompt, StringComparison.Ordinal);
        Assert.Equal(AiTier.Light, LockExplainPrompt.Tier);
    }

    [Theory]
    [InlineData(AiAccessLevel.Query)]
    [InlineData(AiAccessLevel.QueryRows)]
    public void Query_level_sends_names_as_they_are(AiAccessLevel level)
    {
        var request = Ready(Chain(), level);

        Assert.Contains("SCOTT", request.Prompt, StringComparison.Ordinal);
        Assert.Contains("PC-ACCT-01", request.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("USER1", request.Prompt, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(AiAccessLevel.Off)]
    [InlineData(AiAccessLevel.Metadata)]
    public void Below_plan_level_nothing_is_sent(AiAccessLevel level)
    {
        var prepared = LockExplainPrompt.Prepare(Chain(), Run(level), Masker);

        Assert.Null(prepared.Request);
        Assert.Equal(LockExplainPrompt.LevelTooLowText, prepared.RefusalText);
    }

    [Fact]
    public void Sql_literals_are_masked_with_the_existing_masker_when_the_connection_requires_it()
    {
        var request = Ready(Chain(), AiAccessLevel.Query, mask: true);

        Assert.True(request.Bundle.Masked);
        Assert.DoesNotContain("'HOLD'", request.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("ORDER_ID = 42", request.Prompt, StringComparison.Ordinal);
        Assert.Contains("UPDATE SAMPLE.ORDERS SET STATUS =", request.Prompt, StringComparison.Ordinal);
        Assert.NotNull(request.Bundle.Vault);
    }

    [Fact]
    public void Sql_that_cannot_be_masked_is_left_out_with_a_note()
    {
        var input = new LockExplainInput(LockExplainKind.SessionWait, [new LockExplainSession(Holder) { Event = "db file sequential read", SqlText = "SELECT 'open FROM DUAL" }]);

        var request = Ready(input, AiAccessLevel.Query, mask: true);

        Assert.DoesNotContain("'open", request.Prompt, StringComparison.Ordinal);
        Assert.Contains(LockExplainPrompt.SqlWithheldNote, LockExplainPrompt.Read("x", input, request).Notes);
    }

    [Fact]
    public void Unmasked_sql_is_sent_as_it_is()
    {
        var request = Ready(Chain(), AiAccessLevel.Plan);

        Assert.Contains("'HOLD'", request.Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void A_long_chain_is_cut_and_says_how_many_were_left_out()
    {
        var sessions = Enumerable.Range(1, LockExplainPrompt.MaxSessions + 5)
            .Select(i => new LockExplainSession(new SessionKey(1, i, i)) { BlockerKey = i == 1 ? null : new SessionKey(1, 1, 1), Event = "enq" })
            .ToList();

        var request = Ready(new LockExplainInput(LockExplainKind.LockChain, sessions), AiAccessLevel.Plan);

        Assert.Contains("5 more", request.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("1·" + (LockExplainPrompt.MaxSessions + 1).ToString("0000", System.Globalization.CultureInfo.InvariantCulture), request.Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_input_is_refused()
    {
        var prepared = LockExplainPrompt.Prepare(new LockExplainInput(LockExplainKind.LockChain, []), Run(AiAccessLevel.Plan), Masker);

        Assert.Equal(LockExplainPrompt.NothingText, prepared.RefusalText);
    }

    [Fact]
    public void The_answer_has_three_sections_and_a_conclusion_with_placeholders_put_back()
    {
        var input = Chain();
        var request = Ready(input, AiAccessLevel.Plan);

        var result = LockExplainPrompt.Read("""
            ## 상황
            USER1(HOST1) 이 SAMPLE.ORDERS 행을 잡고 쉬고 있습니다.
            ## 원인 추정
            커밋 빠짐.
            ## 권하는 행동
            USER1 에게 연락. 10 분 넘게 응답이 없으면 KILL 을 고려.
            ## 결론
            **USER1 에게 커밋을 요청하세요.**
            """, input, request);

        Assert.Equal(LockExplainPrompt.ChainTitle, result.Title);
        Assert.NotNull(result.Answer);
        Assert.Equal(["상황", "원인 추정", "권하는 행동", "결론"], result.Answer!.Parts.Select(p => p.Title));
        Assert.Equal("SCOTT(PC-ACCT-01) 이 SAMPLE.ORDERS 행을 잡고 쉬고 있습니다.", result.Answer["상황"]!.Body);
        Assert.Equal("SCOTT 에게 커밋을 요청하세요.", result.Conclusion);
        Assert.Contains(LockExplainPrompt.AliasNote, result.Notes);
        Assert.Contains("USER1", result.RawText, StringComparison.Ordinal);
    }

    [Fact]
    public void A_broken_answer_is_shown_as_it_is_without_a_conclusion()
    {
        var input = Chain() with { Kind = LockExplainKind.SessionWait };
        var request = Ready(input, AiAccessLevel.Plan);

        var result = LockExplainPrompt.Read("잘 모르겠습니다. USER1 을 보세요.", input, request);

        Assert.Null(result.Answer);
        Assert.Equal(LockExplainPrompt.SessionTitle, result.Title);
        Assert.Null(result.Conclusion);
        Assert.Equal("잘 모르겠습니다. USER1 을 보세요.", result.RawText);
    }

    [Fact]
    public void The_kill_dialog_line_is_the_conclusion_only_when_there_is_one()
    {
        var input = Chain();
        var request = Ready(input, AiAccessLevel.Plan);
        var framed = LockExplainPrompt.Read("## 상황\n가\n## 원인 추정\n나\n## 권하는 행동\n다\n## 결론\n- 기다리세요.", input, request);

        Assert.Equal("AI 풀이: 기다리세요.", LockExplainPrompt.KillDialogLine(framed));
        Assert.Null(LockExplainPrompt.KillDialogLine(null));
        Assert.Null(LockExplainPrompt.KillDialogLine(LockExplainPrompt.Read("틀 없음", input, request)));
    }
}
