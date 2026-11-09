namespace Oranda.Core.Execution;

/// <summary>
/// <c>SELECT … FOR UPDATE</c>(<c>FOR UPDATE OF</c>·<c>NOWAIT</c>·<c>WAIT n</c>·<c>SKIP LOCKED</c> 포함) 잠금 절 판정.
/// 문자열 리터럴(<c>'…'</c>, <c>q'[…]'</c>)·따옴표 이름(<c>"…"</c>)·주석(<c>-- …</c>, <c>/* … */</c>) 안의 글자는 보지 않고,
/// 주석이나 줄바꿈으로 갈라 쓴 <c>FOR /* … */ UPDATE</c> 는 잡는다. Core 정책 관문이 쓴다(언어 계층을 참조하지 않는다).
/// </summary>
public static class SelectLocking
{
    /// <summary>문자열·따옴표 이름·주석 밖에 <c>FOR</c> 다음 <c>UPDATE</c> 낱말이 이어지면 true.</summary>
    public static bool HasForUpdate(string? sql)
    {
        if (string.IsNullOrEmpty(sql))
        {
            return false;
        }

        var code = Blank(sql);
        for (var i = 0; i + 3 <= code.Length; i++)
        {
            if (!IsWordAt(code, i, "FOR"))
            {
                continue;
            }

            var j = i + 3;
            while (j < code.Length && char.IsWhiteSpace(code[j]))
            {
                j++;
            }

            if (j > i + 3 && IsWordAt(code, j, "UPDATE"))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>문자열·따옴표 이름·주석 안을 공백으로 바꾼 같은 길이의 글(줄바꿈은 그대로).</summary>
    private static string Blank(string sql)
    {
        var chars = sql.ToCharArray();
        var n = chars.Length;
        var i = 0;
        while (i < n)
        {
            var c = chars[i];
            if (c == '-' && i + 1 < n && chars[i + 1] == '-')
            {
                while (i < n && chars[i] != '\n' && chars[i] != '\r')
                {
                    chars[i++] = ' ';
                }

                continue;
            }

            if (c == '/' && i + 1 < n && chars[i + 1] == '*')
            {
                var end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                var stop = end < 0 ? n : end + 2;
                for (; i < stop; i++)
                {
                    BlankChar(chars, i);
                }

                continue;
            }

            if ((c == 'q' || c == 'Q') && i + 2 < n && chars[i + 1] == '\'' && (i == 0 || !IsWordChar(chars[i - 1])) && QuoteEnd(sql, i + 2) is { } qEnd)
            {
                for (; i < qEnd; i++)
                {
                    BlankChar(chars, i);
                }

                continue;
            }

            if (c == '\'' || c == '"')
            {
                var stop = QuotedEnd(sql, i, c);
                for (; i < stop; i++)
                {
                    BlankChar(chars, i);
                }

                continue;
            }

            i++;
        }

        return new string(chars);
    }

    private static void BlankChar(char[] chars, int i)
    {
        if (chars[i] != '\n' && chars[i] != '\r')
        {
            chars[i] = ' ';
        }
    }

    /// <summary>따옴표(<paramref name="quote"/>) 하나로 열린 글자 덩어리의 끝 다음 위치. 겹친 따옴표는 안쪽 글자로 본다. 닫히지 않으면 글 끝.</summary>
    private static int QuotedEnd(string sql, int start, char quote)
    {
        var i = start + 1;
        while (i < sql.Length)
        {
            if (sql[i] == quote)
            {
                if (i + 1 < sql.Length && sql[i + 1] == quote)
                {
                    i += 2;
                    continue;
                }

                return i + 1;
            }

            i++;
        }

        return sql.Length;
    }

    /// <summary><c>q'</c> 다음(<paramref name="delimAt"/> = 구분자 위치)부터 대안 따옴표 문자열의 끝 다음 위치. 구분자가 공백이면 대안 따옴표가 아니라 null.</summary>
    private static int? QuoteEnd(string sql, int delimAt)
    {
        var open = sql[delimAt];
        if (char.IsWhiteSpace(open))
        {
            return null;
        }

        var close = open switch
        {
            '[' => ']',
            '(' => ')',
            '{' => '}',
            '<' => '>',
            _ => open,
        };
        var end = sql.IndexOf(close + "'", delimAt + 1, StringComparison.Ordinal);
        return end < 0 ? sql.Length : end + 2;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '$' or '#';

    private static bool IsWordAt(string code, int index, string word)
    {
        if (index + word.Length > code.Length
            || string.Compare(code, index, word, 0, word.Length, StringComparison.OrdinalIgnoreCase) != 0)
        {
            return false;
        }

        return (index == 0 || !IsWordChar(code[index - 1]))
            && (index + word.Length == code.Length || !IsWordChar(code[index + word.Length]));
    }
}
