using System.Text;

namespace Oranda.Language.Snippets;

/// <summary>펼친 글자 안의 자리 하나. <see cref="Index"/> 0 = 마지막 커서 자리.</summary>
public sealed record SnippetStop(int Index, int Start, int Length);

/// <summary>펼친 결과: 삽입할 글자와 탭으로 옮겨 다닐 자리 목록(탭 순서: 1, 2, … 마지막 0).</summary>
public sealed record SnippetExpansion(string Text, IReadOnlyList<SnippetStop> Stops)
{
    /// <summary>같은 번호의 자리 묶음을 탭 순서로.</summary>
    public IReadOnlyList<IReadOnlyList<SnippetStop>> Groups =>
        Stops.GroupBy(s => s.Index)
            .OrderBy(g => g.Key == 0 ? int.MaxValue : g.Key)
            .Select(g => (IReadOnlyList<SnippetStop>)g.OrderBy(s => s.Start).ToList())
            .ToList();
}

/// <summary>
/// 조각 본문의 자리 표시자를 푼다. <c>$n</c>, <c>${n}</c>, <c>${n:기본값}</c>, <c>$0</c>(끝), <c>\$</c>(글자 $).
/// 같은 번호가 여럿이면 첫 기본값을 모두에 쓴다(함께 선택). <c>$0</c> 이 없으면 글자 끝이 마지막 자리다.
/// </summary>
public static class SnippetExpander
{
    public static SnippetExpansion Expand(string body, string? indent = null, string newLine = "\n")
    {
        ArgumentNullException.ThrowIfNull(body);
        var defaults = new Dictionary<int, string>();
        foreach (var (index, def, _) in Scan(body))
        {
            if (index > 0 && def is { Length: > 0 } && !defaults.ContainsKey(index))
            {
                defaults[index] = def;
            }
        }

        var sb = new StringBuilder();
        var stops = new List<SnippetStop>();
        var lineIndent = indent ?? string.Empty;
        foreach (var (index, def, literal) in Scan(body))
        {
            if (literal is not null)
            {
                foreach (var ch in literal)
                {
                    if (ch == '\n')
                    {
                        sb.Append(newLine).Append(lineIndent);
                    }
                    else if (ch != '\r')
                    {
                        sb.Append(ch);
                    }
                }

                continue;
            }

            var text = index > 0 && defaults.TryGetValue(index, out var d) ? d : (def ?? string.Empty);
            stops.Add(new SnippetStop(index, sb.Length, text.Length));
            sb.Append(text);
        }

        if (!stops.Any(s => s.Index == 0))
        {
            stops.Add(new SnippetStop(0, sb.Length, 0));
        }

        return new SnippetExpansion(sb.ToString(), stops);
    }

    // (index, default, literal): literal != null 이면 글자 조각, 아니면 자리.
    private static IEnumerable<(int Index, string? Default, string? Literal)> Scan(string s)
    {
        var lit = new StringBuilder();
        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (c == '\\' && i + 1 < s.Length && (s[i + 1] == '$' || s[i + 1] == '\\' || s[i + 1] == '}'))
            {
                lit.Append(s[i + 1]);
                i += 2;
                continue;
            }

            if (c == '$' && i + 1 < s.Length)
            {
                if (char.IsAsciiDigit(s[i + 1]))
                {
                    var j = i + 1;
                    while (j < s.Length && char.IsAsciiDigit(s[j]))
                    {
                        j++;
                    }

                    Flush(lit, out var l1);
                    if (l1 is not null)
                    {
                        yield return (0, null, l1);
                    }

                    yield return (int.Parse(s.AsSpan(i + 1, j - i - 1), System.Globalization.CultureInfo.InvariantCulture), null, null);
                    i = j;
                    continue;
                }

                if (s[i + 1] == '{')
                {
                    var j = i + 2;
                    while (j < s.Length && char.IsAsciiDigit(s[j]))
                    {
                        j++;
                    }

                    if (j > i + 2 && j < s.Length && (s[j] == '}' || s[j] == ':'))
                    {
                        var index = int.Parse(s.AsSpan(i + 2, j - i - 2), System.Globalization.CultureInfo.InvariantCulture);
                        string? def = null;
                        var end = j;
                        if (s[j] == ':')
                        {
                            var d = new StringBuilder();
                            end = j + 1;
                            while (end < s.Length && s[end] != '}')
                            {
                                if (s[end] == '\\' && end + 1 < s.Length)
                                {
                                    end++;
                                }

                                d.Append(s[end]);
                                end++;
                            }

                            def = d.ToString();
                        }

                        if (end < s.Length)
                        {
                            Flush(lit, out var l2);
                            if (l2 is not null)
                            {
                                yield return (0, null, l2);
                            }

                            yield return (index, def ?? string.Empty, null);
                            i = end + 1;
                            continue;
                        }
                    }
                }
            }

            lit.Append(c);
            i++;
        }

        Flush(lit, out var tail);
        if (tail is not null)
        {
            yield return (0, null, tail);
        }
    }

    private static void Flush(StringBuilder lit, out string? text)
    {
        text = lit.Length > 0 ? lit.ToString() : null;
        lit.Clear();
    }
}
