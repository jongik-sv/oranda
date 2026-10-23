using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Oranda.Language.Formatting;

namespace Oranda.Language.MyBatis.Formatting;

/// <summary>
/// 엔진 출력(표식이 든 자리표시 SQL 배치)의 후처리. (1) 원문에서 앞뒤 글자와 붙어 있던 XML 구간을 다시 붙인다.
/// (2) 태그 줄(줄이 XML 구간으로만 이루어진 줄)의 들여쓰기를 고친다: 여는 태그 = 감싼 첫 SQL 줄과 같은 칸, 닫는 태그 = 짝 여는 태그 줄과 같은 칸,
/// 빈 태그·XML 주석·통째 구간 = 다음 SQL 줄 칸(없으면 앞 줄 칸). SQL 배치 자체는 건드리지 않는다.
/// </summary>
internal static partial class MyBatisTagLayout
{
    [GeneratedRegex(@"/\*@([0-9]+)\*/", RegexOptions.CultureInvariant)]
    private static partial Regex Marker();

    [GeneratedRegex(@"^(?:/\*@[0-9]+\*/[ \t]*)+$", RegexOptions.CultureInvariant)]
    private static partial Regex TagLine();

    enum LineKind : byte
    {
        Blank,
        Sql,
        Tag,

        /// <summary>여러 줄 토큰의 이어지는 줄·원문 줄. 건드리지 않고 기준으로도 쓰지 않는다.</summary>
        Fixed,
    }

    /// <summary>
    /// 원문에서 XML 구간과 바로 앞·뒤 글자 사이에 공백이 없었으면, 출력에서 그 사이 공백(줄바꿈 포함)을 지운다.
    /// 반대로 공백이 있었는데 출력에서 붙어 버렸으면(<c>(</c> 뒤 등) 공백 하나를 넣는다.
    /// </summary>
    public static string Glue(string layout, MyBatisPlaceholder ph, string body)
    {
        var sb = new StringBuilder(layout.Length);
        int pos = 0;
        foreach (Match m in Marker().Matches(layout))
        {
            var seg = ph.SegmentOfMarker(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
            sb.Append(layout, pos, m.Index - pos);
            if (MyBatisPlaceholder.GluedBefore(body, seg))
            {
                while (sb.Length > 0 && char.IsWhiteSpace(sb[^1])) sb.Length--;
            }
            else if (sb.Length > 0 && !char.IsWhiteSpace(sb[^1]))
            {
                sb.Append(' ');
            }

            sb.Append(m.Value);
            pos = m.Index + m.Length;
            if (MyBatisPlaceholder.GluedAfter(body, seg))
            {
                while (pos < layout.Length && char.IsWhiteSpace(layout[pos])) pos++;
            }
            else if (pos < layout.Length && !char.IsWhiteSpace(layout[pos]))
            {
                sb.Append(' ');
            }
        }

        sb.Append(layout, pos, layout.Length - pos);
        return sb.ToString();
    }

    /// <summary>태그 줄의 들여쓰기를 고친다. <paramref name="baseIndent"/> 는 본문 기준 들여쓰기.</summary>
    public static string IndentTagLines(string layout, MyBatisPlaceholder ph, string baseIndent)
    {
        var lines = layout.Split('\n');
        int count = lines.Length;
        var kind = new LineKind[count];
        var firstMarker = new int[count];
        var markerLine = new int[ph.MarkerCount + 1];
        var fixedLine = FixedLines(layout, count);
        var indent = new string[count];
        for (int l = 0; l < count; l++)
        {
            string t = lines[l].Trim();
            int ws = 0;
            while (ws < lines[l].Length && lines[l][ws] is ' ' or '\t') ws++;
            indent[l] = lines[l][..ws];
            kind[l] = fixedLine[l] ? LineKind.Fixed : t.Length == 0 ? LineKind.Blank : TagLine().IsMatch(t) ? LineKind.Tag : LineKind.Sql;
            foreach (Match m in Marker().Matches(lines[l]))
            {
                int n = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                markerLine[n] = l;
                if (firstMarker[l] == 0) firstMarker[l] = n;
            }
        }

        // 다음 SQL 줄·다음 비어 있지 않은 줄·앞 비어 있지 않은 줄을 미리 색인한다.
        var nextSql = new int[count + 1];
        var nextAny = new int[count + 1];
        var prevAny = new int[count];
        nextSql[count] = nextAny[count] = count;
        for (int l = count - 1; l >= 0; l--)
        {
            nextSql[l] = kind[l] == LineKind.Sql ? l : nextSql[l + 1];
            nextAny[l] = kind[l] != LineKind.Blank ? l : nextAny[l + 1];
        }

        for (int l = 0; l < count; l++) prevAny[l] = kind[l] != LineKind.Blank ? l : l > 0 ? prevAny[l - 1] : -1;

        string? At(int index, int limit) => index < limit ? indent[index] : null;

        var changed = false;
        for (int l = 0; l < count; l++)
        {
            if (kind[l] != LineKind.Tag) continue;
            var seg = ph.SegmentOfMarker(firstMarker[l]);
            string target = seg.Kind switch
            {
                BodySegmentKind.OpenTag or BodySegmentKind.CdataStart =>
                    At(nextSql[l + 1], Math.Min(markerLine[ph.MarkerOfSegment(seg.Pair)] + 1, count)) ?? At(nextAny[l + 1], count) ?? baseIndent,
                BodySegmentKind.CloseTag or BodySegmentKind.CdataEnd =>
                    markerLine[ph.MarkerOfSegment(seg.Pair)] is var pl && pl < l ? indent[pl] : indent[l],
                _ => At(nextSql[l + 1], count) ?? (l > 0 && prevAny[l - 1] >= 0 ? indent[prevAny[l - 1]] : null) ?? baseIndent,
            };
            if (target == indent[l]) continue;
            indent[l] = target;
            changed = true;
        }

        if (!changed) return layout;
        for (int l = 0; l < count; l++)
        {
            if (kind[l] != LineKind.Tag) continue;
            lines[l] = indent[l] + lines[l].Trim() + (lines[l].EndsWith('\r') ? "\r" : "");
        }

        return string.Join('\n', lines);
    }

    /// <summary>여러 줄 토큰(문자열·주석)의 이어지는 줄과 원문 줄.</summary>
    static bool[] FixedLines(string layout, int count)
    {
        var fixedLine = new bool[count];
        foreach (var it in FormatTokenizer.Read(layout).Items)
        {
            if (it.IsRaw) fixedLine[it.Line] = true;
            for (int l = it.Line + 1; l <= it.EndLine; l++) fixedLine[l] = true;
        }

        return fixedLine;
    }
}
