using Oranda.Core.Text;

namespace Oranda.Language.MyBatis;

/// <summary>
/// 조건 켬·끔 조합(MB-07, TRD 4 Combinations). 조건 = 문장 원문의 if·when·foreach 블록(<see cref="MyBatisBlock.IsCondition"/>).
/// 조건 수가 한도 이하면 2^n 전부(모두 켬부터), 넘으면 모두 켬 + 모두 끔 + 하나씩 끄기(n + 2).
/// 한도는 <see cref="MaxFullLimit"/> 를 넘지 않는다(UI 스레드에서 조합 사전을 수십만 개 만들지 않게). 더 큰 fullLimit 은 이 값으로 본다.
/// </summary>
internal static class ConditionCombinations
{
    public const int MaxFullLimit = 12;

    public static IReadOnlyList<ConditionCombination> Build(IReadOnlyList<MyBatisBlock> blocks, int fullLimit)
    {
        var conditions = blocks.Where(b => b.IsCondition).Select(b => b.Range).Distinct().ToList();
        int n = conditions.Count;
        var result = new List<ConditionCombination>();
        if (n <= Math.Max(0, Math.Min(fullLimit, MaxFullLimit)))
        {
            // 비트 1 = 끔. 0 부터 세면 첫 조합이 모두 켬, 마지막이 모두 끔이다.
            for (int mask = 0; mask < 1 << n; mask++)
            {
                var overrides = new Dictionary<TextRange, bool>(n);
                for (int i = 0; i < n; i++)
                {
                    overrides[conditions[i]] = (mask & (1 << i)) == 0;
                }

                result.Add(new ConditionCombination(ConditionCombinationKind.Exhaustive, overrides));
            }

            return result;
        }

        result.Add(new ConditionCombination(ConditionCombinationKind.AllOn, conditions.ToDictionary(r => r, _ => true)));
        result.Add(new ConditionCombination(ConditionCombinationKind.AllOff, conditions.ToDictionary(r => r, _ => false)));
        foreach (var off in conditions)
        {
            result.Add(new ConditionCombination(ConditionCombinationKind.OneOff, conditions.ToDictionary(r => r, r => r != off)));
        }

        return result;
    }
}
