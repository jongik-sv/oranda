using System.Globalization;

namespace Oranda.Core.Plans;

/// <summary>DBMS_XPLAN 출력 글에서 값을 읽는 도우미(PLAN-04).</summary>
public static class ExecutionPlanText
{
    private const string HashLabel = "Plan hash value:";

    /// <summary>
    /// <c>Plan hash value: 1234567</c> 줄의 값. DISPLAY·DISPLAY_CURSOR 모두 계획 머리에 이 줄을 낸다.
    /// EXPLAIN PLAN 의 <c>plan_table.plan_id</c> 는 실행마다 바뀌는 일련번호라 계획 해시로 쓸 수 없어 이 값을 쓴다. 없으면 null.
    /// </summary>
    public static long? ParsePlanHashValue(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var at = text.IndexOf(HashLabel, StringComparison.OrdinalIgnoreCase);
        if (at < 0)
        {
            return null;
        }

        var span = text.AsSpan(at + HashLabel.Length).TrimStart(" \t");
        var end = 0;
        while (end < span.Length && char.IsAsciiDigit(span[end]))
        {
            end++;
        }

        return end > 0 && long.TryParse(span[..end], NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : null;
    }
}
