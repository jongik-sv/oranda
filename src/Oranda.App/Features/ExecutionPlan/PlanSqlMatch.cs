using System;
using System.Text;

namespace Oranda.App.Features.ExecutionPlan;

/// <summary>
/// 받은 DBMS_XPLAN 글(<c>SQL_ID …, child number n</c> 머리 다음에 서버가 가진 SQL 본문이 나온다)이 우리가 보낸 문장의 계획인지 대조한다.
/// 「직전 문장」 으로 커서를 얻는 길은 그 사이 같은 연결에서 돈 다른 조회의 계획을 줄 수 있어서다.
/// 실제 서버 글(26ai)에서 확인한 모양에 맞춘다:
/// <list type="bullet">
/// <item>줄바꿈·탭은 공백으로 바뀌고, 80자쯤에서 줄이 끊기며(낱말 중간도 가능), 1000자쯤에서 본문이 잘린다 → 공백을 모두 지우고 대소문자를 무시해 앞부분(접두)을 비교한다.</item>
/// <item>치환 변수(&amp;)는 서버에 치환한 글이 가므로 호출하는 쪽이 치환한 글(<c>ExecutionResult.Statement</c>)을 넘긴다.</item>
/// <item>cursor_sharing=FORCE 세션은 리터럴을 <c>:"SYS_B_n"</c> 으로 바꿔 보인다 → 그 자리는 보낸 글의 리터럴(문자열·숫자) 하나와 짝짓는다.</item>
/// </list>
/// 머리가 없는 글(본문을 확인할 수 없는 형식)은 어긋난 것으로 단정하지 않는다. 머리는 있는데 본문이 비었으면 확인할 수 없어 거부한다.
/// </summary>
internal static class PlanSqlMatch
{
    private const string Placeholder = ":\"SYS_B_";

    public static bool Matches(string? planText, string sent)
    {
        ArgumentNullException.ThrowIfNull(sent);
        if (ExtractSql(planText) is not { } body)
        {
            return true;
        }

        var shown = Normalize(body);
        if (shown.Length == 0)
        {
            return false;
        }

        var mine = Normalize(sent);
        var j = 0;
        for (var i = 0; i < shown.Length;)
        {
            if (string.CompareOrdinal(shown, i, Placeholder, 0, Placeholder.Length) == 0)
            {
                var close = shown.IndexOf('"', i + Placeholder.Length);
                if (close < 0 || !ConsumeLiteral(mine, ref j))
                {
                    return false;
                }

                i = close + 1;
                continue;
            }

            if (j >= mine.Length || shown[i] != mine[j])
            {
                return false; // 보인 본문(잘렸을 수 있다)이 보낸 글의 앞부분이 아니다
            }

            i++;
            j++;
        }

        return true;
    }

    /// <summary>머리 줄 다음, 구분선을 건너뛴 본문(빈 줄이나 「Plan hash value」 앞까지). 머리가 없으면 null.</summary>
    internal static string? ExtractSql(string? planText)
    {
        if (string.IsNullOrEmpty(planText))
        {
            return null;
        }

        var lines = planText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var i = 0;
        while (i < lines.Length && !lines[i].TrimStart().StartsWith("SQL_ID", StringComparison.OrdinalIgnoreCase))
        {
            i++;
        }

        if (i >= lines.Length)
        {
            return null;
        }

        i++;
        while (i < lines.Length && (lines[i].Trim().Length == 0 || lines[i].Trim().Trim('-').Length == 0))
        {
            i++;
        }

        var sql = new StringBuilder();
        for (; i < lines.Length; i++)
        {
            var t = lines[i].Trim();
            if (t.Length == 0 || t.StartsWith("Plan hash value", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            sql.Append(lines[i]).Append('\n');
        }

        return sql.ToString();
    }

    /// <summary>cursor_sharing=FORCE 가 바꾼 리터럴 하나(작은따옴표 문자열 또는 숫자)를 <paramref name="s"/>[j..] 에서 건너뛴다.</summary>
    private static bool ConsumeLiteral(string s, ref int j)
    {
        if (j >= s.Length)
        {
            return false;
        }

        if (s[j] == '\'')
        {
            for (var k = j + 1; k < s.Length; k++)
            {
                if (s[k] != '\'')
                {
                    continue;
                }

                if (k + 1 < s.Length && s[k + 1] == '\'')
                {
                    k++; // '' 는 따옴표 글자
                    continue;
                }

                j = k + 1;
                return true;
            }

            return false;
        }

        var start = j;
        while (j < s.Length && (char.IsAsciiDigit(s[j]) || s[j] == '.'))
        {
            j++;
        }

        if (j > start && j < s.Length && s[j] == 'E')
        {
            var k = j + 1;
            if (k < s.Length && s[k] is '+' or '-')
            {
                k++;
            }

            var digits = k;
            while (k < s.Length && char.IsAsciiDigit(s[k]))
            {
                k++;
            }

            if (k > digits)
            {
                j = k;
            }
        }

        return j > start;
    }

    /// <summary>모든 공백을 지우고 대문자로 바꾼다(서버가 줄을 끊거나 공백으로 바꿔도 같게).</summary>
    private static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (!char.IsWhiteSpace(c))
            {
                sb.Append(char.ToUpperInvariant(c));
            }
        }

        return sb.ToString();
    }
}
