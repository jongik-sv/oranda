using System.Text;
using Oranda.Language.Formatting;
using Oranda.Language.Lexing;

namespace Oranda.Language.MyBatis.Formatting;

/// <summary>
/// 본문 → 자리표시 SQL → 본문 복원. 일반 포매터(FormatEngine)가 읽을 수 있게 본문 구간을 바꾼다:
/// <c>#{…}</c>(길이 L) → <c>:b</c> + <c>_</c>×(L-2), <c>${…}</c> → <c>Qx</c> + <c>_</c>×(L-2)(앞뒤 낱말과 한 토큰이 되고 글자 수가 같다),
/// XML 구간 → <c>/*@n*/</c>(공백을 더하지 않고 그 자리에 넣는다), 엔티티 → 푼 글자.
/// 복원은 포맷 결과와 자리표시 SQL 의 공백 아닌 글자 흐름을 맞대어(대소문자 무시) 자리표시 범위를 원문으로 되돌린다.
/// </summary>
internal sealed class MyBatisPlaceholder
{
    readonly string body;
    readonly IReadOnlyList<BodySegment> segments;
    readonly int[] ownerOfChar;
    readonly int[] sqlStartOfSegment;
    readonly int[] markerOfSegment;
    readonly List<int> markerSegments = [];

    public MyBatisPlaceholder(string body, IReadOnlyList<BodySegment> segments)
    {
        this.body = body;
        this.segments = segments;
        var sb = new StringBuilder(body.Length);
        var owner = new List<int>(body.Length);
        var starts = new int[segments.Count];
        markerOfSegment = new int[segments.Count];
        for (int k = 0; k < segments.Count; k++)
        {
            var s = segments[k];
            starts[k] = sb.Length;
            switch (s.Kind)
            {
                case BodySegmentKind.Text:
                    sb.Append(body, s.Start, s.Length);
                    break;
                case BodySegmentKind.Param:
                    sb.Append(s.Decoded == '#' ? ":b" : "Qx").Append('_', s.Length - 2);
                    break;
                case BodySegmentKind.Entity:
                    sb.Append(s.Decoded);
                    break;
                default:
                    markerSegments.Add(k);
                    markerOfSegment[k] = markerSegments.Count;
                    sb.Append(MarkerText(markerSegments.Count));
                    break;
            }

            while (owner.Count < sb.Length) owner.Add(k);
        }

        // 자리표시 SQL 의 앞뒤 공백은 잘라서 넘긴다.
        int lead = 0;
        while (lead < sb.Length && char.IsWhiteSpace(sb[lead])) lead++;
        int end = sb.Length;
        while (end > lead && char.IsWhiteSpace(sb[end - 1])) end--;
        Sql = sb.ToString(lead, end - lead);
        ownerOfChar = owner.GetRange(lead, end - lead).ToArray();
        sqlStartOfSegment = starts.Select(x => x - lead).ToArray();
    }

    /// <summary>엔진에 넘길 SQL(앞뒤 공백 없음).</summary>
    public string Sql { get; }

    /// <summary>XML 구간 수(= 표식 수).</summary>
    public int MarkerCount => markerSegments.Count;

    /// <summary>자리표시 SQL 에 우리가 넣지 않은 <c>/*@</c> 가 있는가(엔티티로 만든 가짜 표식 등).</summary>
    public bool HasForeignMarker()
    {
        int count = 0;
        for (int i = Sql.IndexOf("/*@", StringComparison.Ordinal); i >= 0; i = Sql.IndexOf("/*@", i + 3, StringComparison.Ordinal)) count++;
        return count != MarkerCount;
    }

    public static string MarkerText(int n) => $"/*@{n}*/";

    /// <summary>표식 번호(1부터)의 구간.</summary>
    public BodySegment SegmentOfMarker(int n) => segments[markerSegments[n - 1]];

    /// <summary>구간 번호의 표식 번호(1부터). 표식이 없는 구간은 0.</summary>
    public int MarkerOfSegment(int segment) => markerOfSegment[segment];

    /// <summary>원문에서 구간 바로 앞 글자가 공백이 아니다(붙어 있다).</summary>
    public static bool GluedBefore(string text, BodySegment s) => s.Start > 0 && !char.IsWhiteSpace(text[s.Start - 1]);

    /// <summary>원문에서 구간 바로 뒤 글자가 공백이 아니다(붙어 있다).</summary>
    public static bool GluedAfter(string text, BodySegment s) => s.End < text.Length && !char.IsWhiteSpace(text[s.End]);

    /// <summary>
    /// 각 표식이 자리표시 SQL 에서 그 글 그대로 독립 주석 토큰으로 읽혔는지 본다(문자열·다른 주석 안에 들어가면 안 된다).
    /// 예외로 SQL 줄 주석(<c>--</c>) 안의 XML 주석(<c>--&lt;!-- … --&gt;</c>, 주석 처리 관례)은 받는다: 엔진이 줄 주석을 글자 그대로 두므로
    /// 원문으로 되돌릴 수 있다. 블록 주석 안은 표식의 <c>*/</c> 가 주석을 먼저 닫으므로 받지 않는다.
    /// 어긋난 표식 번호, 모두 맞으면 0.
    /// </summary>
    public int FirstBrokenMarker(FormatTokenizer.Result read)
    {
        var byStart = new Dictionary<int, FormatItem>();
        var lineComments = new List<FormatItem>();
        foreach (var it in read.Items)
        {
            if (it.IsRaw) continue;
            byStart[it.Start] = it;
            if (it.Kind == TokenKind.Comment && it.Text.StartsWith("--", StringComparison.Ordinal)) lineComments.Add(it);
        }

        for (int n = 1; n <= MarkerCount; n++)
        {
            int k = markerSegments[n - 1];
            int at = sqlStartOfSegment[k];
            if (byStart.TryGetValue(at, out var item) && item.Kind == TokenKind.Comment && item.Text == MarkerText(n)) continue;
            if (segments[k].Kind == BodySegmentKind.XmlComment && InLineComment(lineComments, at, MarkerText(n))) continue;
            return n;
        }

        return 0;
    }

    /// <summary><paramref name="at"/> 의 표식 글이 통째로 어느 줄 주석 안에 있는가.</summary>
    static bool InLineComment(List<FormatItem> lineComments, int at, string marker)
    {
        foreach (var c in lineComments)
        {
            if (c.Start < at && at + marker.Length <= c.Start + c.Text.Length)
            {
                return string.CompareOrdinal(c.Text, at - c.Start, marker, 0, marker.Length) == 0;
            }
        }

        return false;
    }

    /// <summary>
    /// 포맷 결과(<paramref name="layout"/>)를 본문으로 되돌린다. 자리표시 범위는 원문(<c>#{…}</c>·XML 구간·엔티티 철자)으로,
    /// 그 밖의 글자는 포맷 결과 글자(바뀐 대소문자)를 쓴다. 글자 흐름이 맞지 않으면 null.
    /// </summary>
    public string? Restore(string layout)
    {
        var sb = new StringBuilder(layout.Length + 64);
        int p = 0;
        foreach (char c in layout)
        {
            if (char.IsWhiteSpace(c))
            {
                sb.Append(c);
                continue;
            }

            while (p < Sql.Length && char.IsWhiteSpace(Sql[p])) p++;
            if (p >= Sql.Length || !SameChar(c, Sql[p])) return null;
            int k = ownerOfChar[p];
            var s = segments[k];
            if (s.Kind == BodySegmentKind.Text) sb.Append(c);
            else if (p == sqlStartOfSegment[k]) AppendOriginal(sb, s);
            p++;
        }

        while (p < Sql.Length && char.IsWhiteSpace(Sql[p])) p++;
        return p == Sql.Length ? sb.ToString() : null;
    }

    /// <summary>
    /// 구간 원문을 붙인다. 여러 줄 구간(selectKey·여러 줄 태그·XML 주석)은 안쪽 글을 한 글자도 바꾸지 않는다
    /// (안쪽 줄 들여쓰기를 옮기면 XML 주석·태그 원문이 달라지고, selectKey 안 여러 줄 문자열 내용도 바뀐다).
    /// 그래서 구간이 줄 처음에 있었고 지금도 줄 처음이면 그 줄 들여쓰기를 원문 칸으로 되돌려 안쪽 줄과 칸을 맞춘다.
    /// </summary>
    void AppendOriginal(StringBuilder sb, BodySegment s)
    {
        string text = body.Substring(s.Start, s.Length);
        string? orig = LinePrefix(body, s.Start);
        string? now = CurrentLinePrefix(sb);
        if (orig is not null && now is not null && orig != now && text.Contains('\n', StringComparison.Ordinal))
        {
            sb.Length -= now.Length;
            sb.Append(orig);
        }

        sb.Append(text);
    }

    /// <summary><paramref name="at"/> 가 줄의 첫 글자(앞에 공백뿐)이면 그 앞 공백, 아니면 null.</summary>
    static string? LinePrefix(string text, int at)
    {
        int ls = at;
        while (ls > 0 && text[ls - 1] is ' ' or '\t') ls--;
        return ls == 0 || text[ls - 1] == '\n' ? text[ls..at] : null;
    }

    static string? CurrentLinePrefix(StringBuilder sb)
    {
        int ls = sb.Length;
        while (ls > 0 && sb[ls - 1] is ' ' or '\t') ls--;
        return ls == 0 || sb[ls - 1] == '\n' ? sb.ToString(ls, sb.Length - ls) : null;
    }

    /// <summary>
    /// 원문에서 앞 토큰에 붙어 있던 쉼표 뒤에 XML 구간이 바로 이어지면, 앞 쉼표 배치(Leading)가 쉼표를 다음 줄로 옮겼더라도
    /// 쉼표를 앞 토큰 바로 뒤로 되돌린다. 쉼표만 있던 줄은 지운다.
    /// </summary>
    public string AttachCommas(string layout)
    {
        var picks = new List<int>();
        int p = 0;
        for (int i = 0; i < layout.Length; i++)
        {
            char c = layout[i];
            if (char.IsWhiteSpace(c)) continue;
            while (p < Sql.Length && char.IsWhiteSpace(Sql[p])) p++;
            if (p >= Sql.Length || !SameChar(c, Sql[p])) return layout;
            if (c == ',' && p > 0 && !char.IsWhiteSpace(Sql[p - 1]))
            {
                int q = p + 1;
                while (q < Sql.Length && char.IsWhiteSpace(Sql[q])) q++;
                if (string.CompareOrdinal(Sql, q, "/*@", 0, 3) == 0) picks.Add(i);
            }

            p++;
        }

        if (picks.Count == 0) return layout;
        var sb = new StringBuilder(layout);
        for (int k = picks.Count - 1; k >= 0; k--)
        {
            int at = picks[k];
            int prev = at - 1;
            while (prev >= 0 && char.IsWhiteSpace(sb[prev])) prev--;
            if (prev < 0 || prev == at - 1) continue;

            int ls = at;
            while (ls > 0 && sb[ls - 1] is ' ' or '\t') ls--;
            bool first = ls == 0 || sb[ls - 1] == '\n';
            sb[at] = ' ';
            if (first) RemoveIfBlankLine(sb, at);
            sb.Insert(prev + 1, ',');
        }

        return sb.ToString();
    }

    /// <summary><paramref name="at"/> 가 든 줄이 공백뿐이면 그 줄을 줄바꿈과 함께 지운다.</summary>
    static void RemoveIfBlankLine(StringBuilder sb, int at)
    {
        int ls = at;
        while (ls > 0 && sb[ls - 1] != '\n') ls--;
        int le = at;
        while (le < sb.Length && sb[le] != '\n') le++;
        for (int i = ls; i < le; i++)
        {
            if (!char.IsWhiteSpace(sb[i])) return;
        }

        if (le < sb.Length) sb.Remove(ls, le + 1 - ls);
        else if (ls > 0)
        {
            int from = ls > 1 && sb[ls - 2] == '\r' ? ls - 2 : ls - 1;
            sb.Remove(from, le - from);
        }
        else sb.Clear();
    }

    /// <summary>공백 아닌 글자 흐름이 같은가(대소문자 무시).</summary>
    public static bool SameFlow(string a, string b)
    {
        int x = 0, y = 0;
        while (true)
        {
            while (x < a.Length && char.IsWhiteSpace(a[x])) x++;
            while (y < b.Length && char.IsWhiteSpace(b[y])) y++;
            if (x >= a.Length || y >= b.Length) return x >= a.Length && y >= b.Length;
            if (!SameChar(a[x], b[y])) return false;
            x++;
            y++;
        }
    }

    static bool SameChar(char a, char b) =>
        a == b || char.ToUpperInvariant(a) == char.ToUpperInvariant(b) || char.ToLowerInvariant(a) == char.ToLowerInvariant(b);
}
