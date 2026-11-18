using Oranda.Core.Schema.TableEdit.Partitions.Advisor;

namespace Oranda.Core.Tests.Schema.TableEdit.Partitions.Advisor;

// 소유: pa-core-l1 PL1. 프롬프트 본문·가림(설계 PA-3·PA-7).
public class PartitionAdvicePromptTests
{
    private static readonly string[] RealValues = ["OPEN_ZQ", "CLOSED_ZQ", "HOLD_ZQ", "SEOUL_ZQ", "BUSAN_ZQ", "2026-09", "2026-10", "2026-11"];

    [Fact]
    public void Masked_prompt_leaves_no_real_value_and_uses_one_placeholder_per_column_value()
    {
        var (prompt, vault) = PartitionAdvicePrompt.Build(AdvisorSamples.Orders(), new AdvicePromptOptions(AdviceGoal.QueryAndPurge, SendValues: false, Language: "ko"));

        foreach (var value in RealValues)
        {
            Assert.DoesNotContain(value, prompt, StringComparison.OrdinalIgnoreCase);
        }

        Assert.DoesNotContain("SECRET_DETAIL_ZQ", prompt, StringComparison.Ordinal); // 오류 원문은 보내지 않음
        Assert.Equal(5, vault.Count); // STATUS 3 + REGION 2. 월 이름은 상대 표기(M-2…)라 금고를 쓰지 않음
        Assert.Equal("OPEN_ZQ", vault.Unmask("STATUS", "V1"));
        Assert.Equal("SEOUL_ZQ", vault.Unmask("REGION", "V4"));
        Assert.Contains("V1", prompt, StringComparison.Ordinal);
        Assert.Contains("M-2", prompt, StringComparison.Ordinal);
        Assert.Contains("M0", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Sent_values_appear_as_they_are_and_the_vault_stays_empty()
    {
        var (prompt, vault) = PartitionAdvicePrompt.Build(AdvisorSamples.Orders(), new AdvicePromptOptions(AdviceGoal.Query, SendValues: true, Language: "en"));

        foreach (var value in RealValues)
        {
            Assert.Contains(value, prompt, StringComparison.Ordinal);
        }

        Assert.Equal(0, vault.Count);
    }

    [Fact]
    public void Prompt_cites_the_evidence_numbers_as_short_tables()
    {
        var (prompt, _) = PartitionAdvicePrompt.Build(AdvisorSamples.Orders(), new AdvicePromptOptions(AdviceGoal.QueryAndPurge, false, "en"));

        Assert.Contains("SAMPLE.ORDERS", prompt, StringComparison.Ordinal);
        Assert.Contains("50,000,000", prompt, StringComparison.Ordinal); // 행 수
        Assert.Contains("| ORDER_DT | 120,000 | 14 | 72.0% |", prompt, StringComparison.Ordinal); // 조회 조건
        Assert.Contains("| ORDER_YMD | VARCHAR2 |", prompt, StringComparison.Ordinal);
        Assert.Contains("YYYYMMDD 100.0%", prompt, StringComparison.Ordinal); // 날짜형
        Assert.Contains("| UX_ORDERS_ID_DT | Y | N | ORDER_ID, ORDER_DT |", prompt, StringComparison.Ordinal);
        Assert.Contains("PK_ORDERS", prompt, StringComparison.Ordinal);
        Assert.Contains("no privilege", prompt, StringComparison.OrdinalIgnoreCase); // 건너뛴 항목
    }

    [Fact]
    public void Output_rules_ask_for_schema_json_one_to_three_candidates_cited_numbers_and_no_partition_for_small_tables()
    {
        var (prompt, _) = PartitionAdvicePrompt.Build(AdvisorSamples.Orders(), new AdvicePromptOptions(AdviceGoal.QueryAndPurge, false, "en"));

        Assert.Contains("\"not_chosen\"", prompt, StringComparison.Ordinal); // 스키마 글
        Assert.Contains("1 to 3", prompt, StringComparison.Ordinal);
        Assert.Contains("no_partition", prompt, StringComparison.Ordinal);
        Assert.Contains("1,000,000", prompt, StringComparison.Ordinal);
        Assert.Contains("cite", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("V1", prompt, StringComparison.Ordinal); // 자리표시자 그대로 쓰기 규칙
    }

    [Fact]
    public void Korean_prompt_writes_rules_in_korean_and_require_candidates_forbids_an_empty_list()
    {
        var plain = PartitionAdvicePrompt.Build(AdvisorSamples.Orders(), new AdvicePromptOptions(AdviceGoal.Purge, false, "ko")).Prompt;
        var forced = PartitionAdvicePrompt.Build(AdvisorSamples.Orders(), new AdvicePromptOptions(AdviceGoal.Purge, false, "ko", RequireCandidates: true)).Prompt;

        Assert.Contains("한국어", plain, StringComparison.Ordinal);
        Assert.Contains("오래된 데이터 정리", plain, StringComparison.Ordinal);
        Assert.DoesNotContain("후보를 반드시", plain, StringComparison.Ordinal);
        Assert.Contains("후보를 반드시", forced, StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_evidence_still_builds_with_none_markers()
    {
        var empty = PartitionEvidence.Empty("SAMPLE", "ORDERS", Core.Connection.Dbms.Tibero, AdvisorSamples.CollectedAt);

        var (prompt, vault) = PartitionAdvicePrompt.Build(empty, new AdvicePromptOptions(AdviceGoal.QueryAndPurge, false, "en"));

        Assert.Contains("Tibero", prompt, StringComparison.Ordinal);
        Assert.Contains("(none)", prompt, StringComparison.Ordinal);
        Assert.Contains("unknown", prompt, StringComparison.Ordinal); // 행 수 모름
        Assert.Equal(0, vault.Count);
    }
}
