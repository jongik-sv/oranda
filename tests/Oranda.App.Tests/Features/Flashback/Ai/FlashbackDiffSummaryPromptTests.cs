using Oranda.App.Features.Ai.Kit;
using Oranda.App.Features.Flashback.Ai;
using Oranda.App.Tests.Features.Ai.Kit;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Connection;

namespace Oranda.App.Tests.Features.Flashback.Ai;

/// <summary>플래시백 「차이 요약」 의 보낼 글·값 규칙·답 읽기(설계 §4.2).</summary>
public class FlashbackDiffSummaryPromptTests
{
    private static FlashbackDiffInput Input() => new("SAMPLE.CUSTOMERS", ["CUST_ID"],
    [
        new FlashbackVersion("I", "2026-11-18 09:00:00", "1000", []) { KeyText = "CUST_ID=501" },
        new FlashbackVersion("U", "2026-11-18 10:00:00", "1100", [new FlashbackVersionChange("GRADE", "SILVER", "GOLD"), new FlashbackVersionChange("PHONE_NO", "010-1111-2222", "010-3333-4444")]) { KeyText = "CUST_ID=501" },
        new FlashbackVersion("D", "2026-11-18 11:00:00", "1200", []) { KeyText = "CUST_ID=502" },
    ]);

    private static AiStripRequest Ready(AiAccessLevel level, bool mask = false)
    {
        var run = KitFakes.Run(new FakeDriver(), KitFakes.Profile(mask ? ConnectionEnvironment.Production : ConnectionEnvironment.Development, level));
        var prepared = FlashbackDiffSummaryPrompt.Prepare(Input(), run);
        Assert.Null(prepared.RefusalText);
        return prepared.Request!;
    }

    [Theory]
    [InlineData(AiAccessLevel.Metadata, false)]
    [InlineData(AiAccessLevel.Plan, false)]
    [InlineData(AiAccessLevel.QueryRows, true)]
    public void Without_row_values_only_columns_operations_and_counts_are_sent(AiAccessLevel level, bool mask)
    {
        var request = Ready(level, mask);

        Assert.Contains("GRADE", request.Prompt, StringComparison.Ordinal);
        Assert.Contains("operation U", request.Prompt, StringComparison.Ordinal);
        Assert.Contains("row 1", request.Prompt, StringComparison.Ordinal);
        Assert.Contains("row 2", request.Prompt, StringComparison.Ordinal);
        foreach (var value in new[] { "SILVER", "GOLD", "501", "502", "010-" })
        {
            Assert.DoesNotContain(value, request.Prompt, StringComparison.Ordinal);
        }

        Assert.Contains(FlashbackDiffSummaryPrompt.NoValuesNote, FlashbackDiffSummaryPrompt.Read("x", request).Notes);
    }

    [Fact]
    public void Row_values_are_sent_at_query_rows_but_sensitive_columns_stay_hidden()
    {
        var request = Ready(AiAccessLevel.QueryRows);

        Assert.Contains("GRADE: SILVER -> GOLD", request.Prompt, StringComparison.Ordinal);
        Assert.Contains("CUST_ID=501", request.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("010-", request.Prompt, StringComparison.Ordinal);
        Assert.Contains("PHONE_NO: [가림]", request.Prompt, StringComparison.Ordinal);
        Assert.Empty(FlashbackDiffSummaryPrompt.Read("x", request).Notes);
        Assert.Equal(AiTier.Light, FlashbackDiffSummaryPrompt.Tier);
    }

    [Fact]
    public void Ai_off_or_no_versions_is_refused()
    {
        var off = KitFakes.Run(new FakeDriver(), KitFakes.Profile(access: AiAccessLevel.Off));
        Assert.Equal(FlashbackDiffSummaryPrompt.AiOffText, FlashbackDiffSummaryPrompt.Prepare(Input(), off).RefusalText);
        var plan = KitFakes.Run(new FakeDriver(), KitFakes.Profile(access: AiAccessLevel.Plan));
        Assert.Equal(FlashbackDiffSummaryPrompt.NothingText, FlashbackDiffSummaryPrompt.Prepare(Input() with { Versions = [] }, plan).RefusalText);
    }

    [Fact]
    public void A_framed_answer_is_read()
    {
        var request = Ready(AiAccessLevel.Metadata);

        var result = FlashbackDiffSummaryPrompt.Read("## 요약\n등급이 올랐고 한 행이 지워졌습니다.\n## 눈여겨볼 점\n- 없음", request);

        Assert.Equal("등급이 올랐고 한 행이 지워졌습니다.", result.Answer!["요약"]!.Body);
        Assert.Equal(FlashbackDiffSummaryPrompt.Title, result.Title);
    }
}
