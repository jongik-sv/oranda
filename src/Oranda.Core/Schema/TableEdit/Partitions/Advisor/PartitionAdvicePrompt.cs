using System.Globalization;
using System.Text;

namespace Oranda.Core.Schema.TableEdit.Partitions.Advisor;

/// <summary>추천 목적(확인 창 라디오).</summary>
public enum AdviceGoal
{
    /// <summary>조회 성능 + 오래된 데이터 정리(기본).</summary>
    QueryAndPurge,

    /// <summary>조회 성능.</summary>
    Query,

    /// <summary>오래된 데이터 정리.</summary>
    Purge,
}

/// <summary>프롬프트 선택.</summary>
/// <param name="SendValues">실제 값 보내기(PA-3). 거짓이면 값은 <see cref="EvidenceValueVault"/> 자리표시자</param>
/// <param name="Language"><c>ko</c> | <c>en</c>(화면 언어, 이유·요약 글 언어)</param>
/// <param name="RequireCandidates">no_partition 뒤 「후보 3개 받기」 — 후보를 꼭 내라고 덧붙임</param>
public sealed record AdvicePromptOptions(AdviceGoal Goal, bool SendValues, string Language, bool RequireCandidates = false);

/// <summary>근거 → AI 프롬프트(PC3 에서 본문을 채움).</summary>
public static class PartitionAdvicePrompt
{
    /// <summary>프롬프트 글과 그 글에 쓴 가림 금고. 금고는 응답을 채울 때 되돌리기에 쓴다.</summary>
    public static (string Prompt, EvidenceValueVault Vault) Build(PartitionEvidence evidence, AdvicePromptOptions options)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(options);

        // PC1 껍데기: 표 이름과 목적만.
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"Recommend a partitioning for table {evidence.Owner}.{evidence.Table} ({evidence.Dbms}). Goal: {options.Goal}.");
        return (sb.ToString(), new EvidenceValueVault());
    }
}
