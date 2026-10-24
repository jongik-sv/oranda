using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Oranda.App.Features.Tuning.Stats;

/// <summary>통계를 볼 표(소유자·이름은 데이터 사전 표기 — 따옴표 없는 이름은 대문자).</summary>
public sealed partial record TableStatsTarget(string Owner, string Table)
{
    /// <summary>화면 표기 <c>MES.LOT_HIST</c>. 대문자 단순 이름이 아니면 큰따옴표로 감싼다.</summary>
    public string Display => Quote(Owner) + "." + Quote(Table);

    public override string ToString() => Display;

    /// <summary>
    /// <c>owner.table</c> 또는 <c>table</c>(소유자는 <paramref name="defaultOwner"/>)을 읽는다. 따옴표 없는 이름은 대문자로, <c>"Mixed"</c> 는 그대로.
    /// 형식이 틀리면 null.
    /// </summary>
    public static TableStatsTarget? Parse(string? text, string? defaultOwner)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var parts = SplitDotted(text.Trim());
        if (parts is null)
        {
            return null;
        }

        return parts.Count switch
        {
            1 when !string.IsNullOrWhiteSpace(defaultOwner) => new TableStatsTarget(defaultOwner.Trim().ToUpperInvariant(), parts[0]),
            2 => new TableStatsTarget(parts[0], parts[1]),
            _ => null,
        };
    }

    /// <summary>대문자 단순 식별자면 그대로, 아니면 큰따옴표로 감싼다.</summary>
    public static string Quote(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return SimpleUpper().IsMatch(name) ? name : "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    /// <summary>점으로 나뉜 이름(따옴표 칸 포함)을 사전 표기로 나눈다. 빈 칸·닫히지 않은 따옴표·식별자가 아닌 글자면 null.</summary>
    internal static List<string>? SplitDotted(string text)
    {
        var parts = new List<string>();
        var i = 0;
        while (i <= text.Length)
        {
            if (i < text.Length && text[i] == '"')
            {
                var end = text.IndexOf('"', i + 1);
                if (end < 0 || end == i + 1)
                {
                    return null;
                }

                parts.Add(text[(i + 1)..end]);
                i = end + 1;
            }
            else
            {
                var start = i;
                while (i < text.Length && IsIdentifierChar(text[i]))
                {
                    i++;
                }

                if (i == start || !char.IsLetter(text[start]))
                {
                    return null;
                }

                parts.Add(text[start..i].ToUpperInvariant());
            }

            if (i == text.Length)
            {
                return parts;
            }

            if (text[i] != '.')
            {
                return null;
            }

            i++;
        }

        return null;
    }

    internal static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '$' or '#';

    [GeneratedRegex("^[A-Z][A-Z0-9_$#]*$")]
    private static partial Regex SimpleUpper();
}
