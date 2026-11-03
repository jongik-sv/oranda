using Oranda.Core.Ai.Contracts;
using Oranda.Platform.Ai.Cli;

namespace Oranda.Platform.Tests.Ai.Cli;

/// <summary>도구 사용 집계의 판정표(성공 · 실행 뒤 실패 · 게이트 거부).</summary>
public sealed class SystemCliToolUsageTests
{
    [Fact]
    public void One_or_more_successes_pass_whatever_else_happened()
    {
        var usage = Usage(Ok(), Fail(), Deny());

        Assert.Null(usage.AllFailedError("CLI"));
        Assert.Null(usage.Notice(structuredResult: true, firstTurn: true));
    }

    [Fact]
    public void A_masked_result_counts_as_a_success()
    {
        var usage = Usage([new AiToolCall("1", "t", "{}"), new AiToolResult("1", "t", 5, AiToolJudgement.Masked)]);

        Assert.Null(usage.AllFailedError("CLI"));
        Assert.Equal(1, usage.Succeeded);
    }

    [Fact]
    public void No_success_and_at_least_one_failure_closes_even_with_denials_mixed_in()
    {
        var failedOnly = Usage(Fail(), Fail());
        var mixed = Usage(Fail(), Deny());

        Assert.Equal("all_tools_failed", failedOnly.AllFailedError("CLI")!.Code);
        Assert.Contains("모두 실패", failedOnly.AllFailedError("CLI")!.Message, StringComparison.Ordinal);
        var mixedError = mixed.AllFailedError("CLI")!;
        Assert.Equal("all_tools_failed", mixedError.Code);
        Assert.Contains("모두 실패하거나 거부", mixedError.Message, StringComparison.Ordinal);
        Assert.Contains("실패 1건, 거부 1건", mixedError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_call_without_any_result_counts_as_a_failure()
    {
        var usage = Usage([new AiToolCall("1", "t", "{}")]);

        Assert.Equal(1, usage.Failed);
        Assert.Equal("all_tools_failed", usage.AllFailedError("CLI")!.Code);
    }

    [Fact]
    public void Only_denials_do_not_close_and_get_the_denied_notice_on_the_first_turn_regardless_of_the_schema()
    {
        var usage = Usage(Deny(), Deny());

        Assert.Null(usage.AllFailedError("CLI"));
        Assert.Equal("[안내] 모든 도구 호출이 거부되어 실측 근거가 없습니다.\n\n", usage.Notice(structuredResult: false, firstTurn: true));
        Assert.Equal("[안내] 모든 도구 호출이 거부되어 실측 근거가 없습니다.\n\n", usage.Notice(structuredResult: true, firstTurn: true));
        Assert.Null(usage.Notice(structuredResult: true, firstTurn: false)); // 이어 말하기: 레포트로 모이므로 안내 없이 통과
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    public void No_calls_give_the_no_evidence_line_only_for_a_structured_first_turn(bool structured, bool firstTurn, bool expected)
    {
        var usage = new SystemCliToolUsage();

        Assert.Null(usage.AllFailedError("CLI"));
        Assert.Equal(expected ? "[안내] AI 가 도구를 쓰지 않고 답했으므로 실측 근거가 없습니다.\n\n" : null, usage.Notice(structured, firstTurn));
    }

    [Fact]
    public void Other_events_are_ignored()
    {
        var usage = Usage([new AiTextDelta("x"), new AiStarted("c")]);

        Assert.Equal(0, usage.Calls);
    }

    private static int _id;

    private static SystemCliToolUsage Usage(params AiEvent[][] groups)
    {
        var usage = new SystemCliToolUsage();
        foreach (var e in groups.SelectMany(g => g))
        {
            usage.Observe(e);
        }

        return usage;
    }

    private static AiEvent[] Call(AiToolJudgement judgement)
    {
        var id = Interlocked.Increment(ref _id).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return [new AiToolCall(id, "t", "{}"), new AiToolResult(id, "t", 1, judgement)];
    }

    private static AiEvent[] Ok() => Call(AiToolJudgement.Allowed);

    private static AiEvent[] Fail() => Call(AiToolJudgement.Failed);

    private static AiEvent[] Deny() => Call(AiToolJudgement.Denied);
}
