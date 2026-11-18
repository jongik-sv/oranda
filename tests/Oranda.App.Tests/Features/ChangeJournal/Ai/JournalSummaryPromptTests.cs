using Oranda.App.Features.Ai.Kit;
using Oranda.App.Features.ChangeJournal.Ai;
using Oranda.App.Tests.Features.Ai.Kit;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Connection;
using Oranda.Language.Ai.Literals;

namespace Oranda.App.Tests.Features.ChangeJournal.Ai;

/// <summary>변경 일지 「이 기간 요약」 의 보낼 글·값 규칙·답 읽기(설계 §4.2).</summary>
public class JournalSummaryPromptTests
{
    private static readonly SqlLiteralMasker Masker = new();

    private static readonly DateTimeOffset T0 = new(2026, 11, 18, 9, 0, 0, TimeSpan.Zero);

    private static JournalSummaryInput Input() => new(T0, T0.AddHours(8),
    [
        new JournalSummaryEntry(T0.AddMinutes(5), "UPDATE", "SAMPLE.ORDERS", 120) { StatementText = "UPDATE SAMPLE.ORDERS SET STATUS = 'CLOSED' WHERE REGION = 'SEOUL'", Outcome = "커밋" },
        new JournalSummaryEntry(T0.AddMinutes(9), "DELETE", "SAMPLE.ORDERS", 3) { StatementText = "DELETE FROM SAMPLE.ORDERS WHERE ORDER_ID = 777", Outcome = "커밋" },
        new JournalSummaryEntry(T0.AddHours(2), "DDL", "SAMPLE.ITEMS", null) { StatementText = "ALTER TABLE SAMPLE.ITEMS ADD (NOTE VARCHAR2(100))" },
    ]);

    private static AiOneShotRun Run(AiAccessLevel level, bool mask = false) =>
        KitFakes.Run(new FakeDriver(), KitFakes.Profile(mask ? ConnectionEnvironment.Production : ConnectionEnvironment.Development, level));

    private static AiStripRequest Ready(AiAccessLevel level, bool mask = false)
    {
        var prepared = JournalSummaryPrompt.Prepare(Input(), Run(level, mask), Masker);
        Assert.Null(prepared.RefusalText);
        return prepared.Request!;
    }

    [Theory]
    [InlineData(AiAccessLevel.Metadata, false)]
    [InlineData(AiAccessLevel.Query, false)]
    [InlineData(AiAccessLevel.QueryRows, true)]
    public void Below_query_rows_only_names_and_counts_are_sent_with_a_note(AiAccessLevel level, bool mask)
    {
        var request = Ready(level, mask);

        Assert.Contains("SAMPLE.ORDERS", request.Prompt, StringComparison.Ordinal);
        Assert.Contains("120", request.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("'SEOUL'", request.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("777", request.Prompt, StringComparison.Ordinal);
        Assert.Contains(JournalSummaryPrompt.NoValuesNote, JournalSummaryPrompt.Read("x", request).Notes);
    }

    [Fact]
    public void Query_rows_on_an_unmasked_connection_sends_the_statements()
    {
        var request = Ready(AiAccessLevel.QueryRows);

        Assert.Contains("'SEOUL'", request.Prompt, StringComparison.Ordinal);
        Assert.Empty(JournalSummaryPrompt.Read("x", request).Notes);
        Assert.Equal(AiTier.Light, JournalSummaryPrompt.Tier);
    }

    [Fact]
    public void Totals_group_by_object_and_kind()
    {
        var request = Ready(AiAccessLevel.Metadata);

        Assert.Contains("SAMPLE.ORDERS UPDATE: 1 statements, 120 rows", request.Prompt, StringComparison.Ordinal);
        Assert.Contains("SAMPLE.ITEMS DDL: 1 statements", request.Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Ai_off_or_an_empty_period_is_refused()
    {
        Assert.Equal(JournalSummaryPrompt.AiOffText, JournalSummaryPrompt.Prepare(Input(), Run(AiAccessLevel.Off), Masker).RefusalText);
        Assert.Equal(JournalSummaryPrompt.NothingText, JournalSummaryPrompt.Prepare(Input() with { Entries = [] }, Run(AiAccessLevel.Plan), Masker).RefusalText);
    }

    [Fact]
    public void A_framed_answer_has_the_summary_and_notable_points()
    {
        var request = Ready(AiAccessLevel.Metadata);

        var result = JournalSummaryPrompt.Read("## 요약\nORDERS 를 두 번 바꿨습니다.\n## 눈여겨볼 점\n- 없음", request);

        Assert.Equal(JournalSummaryPrompt.Title, result.Title);
        Assert.Equal("ORDERS 를 두 번 바꿨습니다.", result.Answer!["요약"]!.Body);
        Assert.Null(JournalSummaryPrompt.Read("요약입니다", request).Answer);
    }
}
