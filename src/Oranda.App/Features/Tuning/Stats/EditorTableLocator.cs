using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Oranda.App.Features.Tuning.Stats;

/// <summary>
/// 편집기 커서 아래의 표 이름을 찾는다(「편집기 표」). <c>owner.table</c>·<c>table</c>·별칭(<c>lh</c>·<c>lh.col</c> → FROM 절의 표)을 읽는다.
/// 예약어·문자열·숫자 위면 null.
/// </summary>
public static partial class EditorTableLocator
{
    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "FROM", "WHERE", "AND", "OR", "NOT", "JOIN", "INNER", "LEFT", "RIGHT", "FULL", "OUTER", "CROSS", "ON", "USING",
        "GROUP", "ORDER", "BY", "HAVING", "UNION", "ALL", "MINUS", "INTERSECT", "AS", "IN", "IS", "NULL", "LIKE", "BETWEEN",
        "EXISTS", "CASE", "WHEN", "THEN", "ELSE", "END", "DISTINCT", "INSERT", "INTO", "VALUES", "UPDATE", "SET", "DELETE",
        "MERGE", "WITH", "FETCH", "FIRST", "ROWS", "ONLY", "CONNECT", "START", "PRIOR", "DUAL", "SYSDATE", "ROWNUM",
    };

    /// <param name="text">편집기 글 전체.</param>
    /// <param name="caret">커서 위치.</param>
    /// <param name="scope">별칭을 찾을 범위(보통 커서가 있는 문장). null 이면 글 전체.</param>
    /// <param name="defaultOwner">소유자가 없을 때 쓸 이름(접속 계정).</param>
    public static TableStatsTarget? Find(string text, int caret, string? scope, string? defaultOwner)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (Token(text, caret) is not { } token)
        {
            return null;
        }

        var parts = TableStatsTarget.SplitDotted(token.Text);
        if (parts is null || parts.Count == 0 || parts.Count > 3)
        {
            return null;
        }

        // 커서가 놓인 칸 번호(점 기준).
        var index = PartIndex(token.Text, caret - token.Start);
        if (parts.Count == 1 && Keywords.Contains(parts[0]))
        {
            return null;
        }

        // 첫 칸이 별칭이면 그 별칭의 표(lh.evt_cd → MES.LOT_HIST).
        if (FindAlias(scope ?? text, parts[0]) is { } aliased)
        {
            return TableStatsTarget.Parse(aliased, defaultOwner);
        }

        return parts.Count switch
        {
            1 => TableStatsTarget.Parse(TableStatsTarget.Quote(parts[0]), defaultOwner),
            2 => new TableStatsTarget(parts[0], parts[1]),
            // owner.table.column: 열 위에 있어도 표는 앞 두 칸.
            _ => index <= 2 ? new TableStatsTarget(parts[0], parts[1]) : null,
        };
    }

    /// <summary>커서를 감싼 점 이름 하나(따옴표 칸 포함). 없으면 null.</summary>
    internal static (string Text, int Start)? Token(string text, int caret)
    {
        caret = Math.Clamp(caret, 0, text.Length);
        bool Part(int i) => i >= 0 && i < text.Length && (TableStatsTarget.IsIdentifierChar(text[i]) || text[i] is '.' or '"');
        var start = caret;
        while (Part(start - 1))
        {
            start--;
        }

        var end = caret;
        while (Part(end))
        {
            end++;
        }

        if (end <= start)
        {
            return null;
        }

        var token = text[start..end].Trim('.');
        var lead = text[start..end].Length - text[start..end].TrimStart('.').Length;
        if (token.Length == 0 || char.IsDigit(token[0]) || InsideQuote(text, start))
        {
            return null;
        }

        return (token, start + lead);
    }

    // 작은따옴표 문자열 안이면 참(시작 위치 앞의 ' 개수가 홀수).
    private static bool InsideQuote(string text, int position)
    {
        var count = 0;
        for (var i = 0; i < position; i++)
        {
            if (text[i] == '\'')
            {
                count++;
            }
        }

        return count % 2 == 1;
    }

    private static int PartIndex(string token, int offset)
    {
        var index = 1;
        var quoted = false;
        for (var i = 0; i < Math.Min(offset, token.Length); i++)
        {
            if (token[i] == '"')
            {
                quoted = !quoted;
            }
            else if (token[i] == '.' && !quoted)
            {
                index++;
            }
        }

        return index;
    }

    /// <summary>FROM·JOIN·쉼표 뒤의 <c>표 [AS] 별칭</c> 에서 별칭이 <paramref name="alias"/> 인 표 이름 글. 없으면 null.</summary>
    internal static string? FindAlias(string scope, string alias)
    {
        foreach (Match m in AliasPattern().Matches(scope))
        {
            var name = m.Groups["alias"].Value;
            var normalized = name.StartsWith('"') ? name.Trim('"') : name.ToUpperInvariant();
            if (string.Equals(normalized, alias, StringComparison.Ordinal) && !Keywords.Contains(name))
            {
                return m.Groups["table"].Value;
            }
        }

        return null;
    }

    [GeneratedRegex(@"(?<=\bFROM|\bJOIN|,)\s+(?<table>(?:""[^""]+""|[A-Za-z][\w$#]*)(?:\.(?:""[^""]+""|[A-Za-z][\w$#]*))?)\s+(?:AS\s+)?(?<alias>""[^""]+""|[A-Za-z][\w$#]*)", RegexOptions.IgnoreCase)]
    private static partial Regex AliasPattern();
}
