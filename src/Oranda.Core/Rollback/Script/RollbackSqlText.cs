namespace Oranda.Core.Rollback.Script;

/// <summary>
/// 엔진이 만든 원복 문장 글(한 줄, 리터럴만 씀)을 나누는 도우미. 작은따옴표·큰따옴표 안과 괄호 안은 나누지 않는다.
/// 일반 SQL 해석기가 아니다: <c>Capture/RollbackCaptureEngine</c> 이 쓰는 모양(UPDATE … SET … WHERE …, INSERT INTO … (…) VALUES (…), DELETE FROM … WHERE …)만 읽는다.
/// </summary>
internal static class RollbackSqlText
{
    /// <summary>바깥 단계(따옴표·괄호 밖)에서 <paramref name="separator"/> 로 나눈다.</summary>
    public static List<string> SplitTop(string text, string separator)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch is '\'' or '"')
            {
                i = SkipQuoted(text, i, ch);
                continue;
            }

            if (ch == '(')
            {
                depth++;
            }
            else if (ch == ')')
            {
                depth--;
            }
            else if (depth == 0 && string.CompareOrdinal(text, i, separator, 0, separator.Length) == 0)
            {
                parts.Add(text[start..i]);
                i += separator.Length - 1;
                start = i + 1;
            }
        }

        parts.Add(text[start..]);
        return parts;
    }

    /// <summary>바깥 단계에서 처음 나오는 <paramref name="token"/> 의 위치. 없으면 -1.</summary>
    public static int IndexOfTop(string text, string token, int from = 0)
    {
        var depth = 0;
        for (var i = from; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch is '\'' or '"')
            {
                i = SkipQuoted(text, i, ch);
                continue;
            }

            if (ch == '(')
            {
                depth++;
            }
            else if (ch == ')')
            {
                depth--;
            }
            else if (depth == 0 && string.CompareOrdinal(text, i, token, 0, token.Length) == 0)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// 조건 한 조각 → (열, 리터럴). <c>c = 리터럴</c> 이면 그 리터럴, <c>c IS NULL</c> 이면 <c>"NULL"</c>. 모양이 다르면 null.
    /// </summary>
    public static (string Column, string Literal)? ParsePredicate(string predicate)
    {
        var p = predicate.Trim();
        var eq = IndexOfTop(p, " = ");
        if (eq > 0)
        {
            return (p[..eq], p[(eq + 3)..]);
        }

        return p.EndsWith(" IS NULL", StringComparison.Ordinal) ? (p[..^8], "NULL") : null;
    }

    private static int SkipQuoted(string text, int start, char quote)
    {
        for (var i = start + 1; i < text.Length; i++)
        {
            if (text[i] == quote)
            {
                if (i + 1 < text.Length && text[i + 1] == quote)
                {
                    i++;
                    continue;
                }

                return i;
            }
        }

        return text.Length - 1;
    }
}
