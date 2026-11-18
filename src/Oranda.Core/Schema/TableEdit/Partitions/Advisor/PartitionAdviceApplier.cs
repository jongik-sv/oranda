namespace Oranda.Core.Schema.TableEdit.Partitions.Advisor;

/// <summary>
/// 채우기 결과. <see cref="Draft"/> 를 파티션 편집기에 넣는다.
/// 하위 파티션 템플릿은 <see cref="PartitionScheme"/> 에 바꾸기용 모델이 생기기 전까지 <see cref="Subpartition"/> 에 따로 담는다(pa-script 머지 뒤 모델로 옮김).
/// </summary>
/// <param name="LocalIndexes">LOCAL 로 만들 인덱스(PA-12)</param>
/// <param name="Problems">채우면서 생긴 확인(✖ = <see cref="AdviceCheckCodes.ValueUnknown"/> 등, 있으면 채우지 않음)</param>
public sealed record AdviceDraft(
    PartitionTableDraft Draft,
    SubpartitionAdvice? Subpartition,
    IReadOnlyList<string> LocalIndexes,
    IReadOnlyList<AdviceCheck> Problems);

/// <summary>후보 → 파티션 편집 모델(설계 4.2, PC5 에서 줄 만들기를 채움).</summary>
public static class PartitionAdviceApplier
{
    /// <param name="vault">프롬프트를 만들 때의 금고(LIST 값 되돌리기)</param>
    /// <param name="today">「오늘」(앞으로 N 개월·첫 경계 기준)</param>
    public static AdviceDraft Apply(PartitionCandidate candidate, PartitionEvidence evidence, EvidenceValueVault vault, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(vault);
        _ = today;

        if (AdviceMethod.ToMethod(candidate.Method) is not { } method)
        {
            return new AdviceDraft(PartitionTableDraft.None, null, [], [new AdviceCheck(AdviceCheckLevel.Stop, AdviceCheckCodes.MethodUnknown, candidate.Method)]);
        }

        // PC1 껍데기: 방식·키 열만, 줄은 PC5.
        var scheme = new PartitionScheme(method, [.. candidate.KeyColumns]);
        return new AdviceDraft(new PartitionTableDraft(scheme, []), candidate.Subpartition, [.. candidate.LocalIndexes], []);
    }
}
