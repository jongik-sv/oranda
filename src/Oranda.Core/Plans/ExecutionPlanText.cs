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

    /// <summary>
    /// DISPLAY_CURSOR 머리 줄 <c>SQL_ID  7h35uxf5uhmm1, child number 0</c> 의 커서. 없으면 null.
    /// </summary>
    public static CursorRef? ParseCursor(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("SQL_ID", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var rest = line.AsSpan(6).TrimStart(" \t");
            var end = 0;
            while (end < rest.Length && char.IsAsciiLetterOrDigit(rest[end]))
            {
                end++;
            }

            if (end != 13)
            {
                continue;
            }

            var sqlId = rest[..end].ToString();
            int? child = null;
            var at = line.IndexOf("child number", StringComparison.OrdinalIgnoreCase);
            if (at >= 0)
            {
                var num = line.AsSpan(at + "child number".Length).TrimStart(" \t");
                var n = 0;
                while (n < num.Length && char.IsAsciiDigit(num[n]))
                {
                    n++;
                }

                if (n > 0 && int.TryParse(num[..n], NumberStyles.None, CultureInfo.InvariantCulture, out var c))
                {
                    child = c;
                }
            }

            return new CursorRef(sqlId, child);
        }

        return null;
    }
}
