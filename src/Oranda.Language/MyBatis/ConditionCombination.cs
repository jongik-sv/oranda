using Oranda.Core.Text;

namespace Oranda.Language.MyBatis;

/// <summary>조합을 만든 방식(MB-07).</summary>
public enum ConditionCombinationKind
{
    /// <summary>조건이 한도 이하라 모든 켬·끔 조합 중 하나.</summary>
    Exhaustive,

    /// <summary>모두 켬.</summary>
    AllOn,

    /// <summary>모두 끔.</summary>
    AllOff,

    /// <summary>하나만 끔(나머지 켬).</summary>
    OneOff,
}

/// <summary>
/// 조건 켬·끔 조합 하나(MB-07, TRD 4 Combinations). 계약(TSK-01-02) — 구현은 TSK-02-02.
/// <see cref="Overrides"/> 를 그대로 <see cref="IMyBatisEvaluator.Evaluate"/> 의 덮어쓰기로 넘기면 그 조합의 펼친 SQL 이 나온다.
/// 열쇠 = 조건 블록 범위(<see cref="MyBatisBlock.Range"/>, 문장 원문 기준).
/// </summary>
public sealed record ConditionCombination(ConditionCombinationKind Kind, IReadOnlyDictionary<TextRange, bool> Overrides);
