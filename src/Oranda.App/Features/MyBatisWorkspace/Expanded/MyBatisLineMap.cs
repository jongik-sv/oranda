using System.Text.RegularExpressions;

namespace Oranda.App.Features.MyBatisWorkspace.Expanded;

/// <summary>
/// MyBatis 쿼리 탭 원문 ↔ 펼친 SQL 스크롤 맞춤용 「원문 줄 ↔ 펼친 SQL 줄」 대응표.
/// 두 글을 줄로 잘라 비교용 키로 바꾼다(XML 태그 지움, <c>#{…}</c>·<c>${…}</c>·<c>:이름</c> → <c>?</c>, 엔터티 해석, 공백 정리). 빈 키 줄은 뺀 뒤 나머지 줄을 LCS 로 짝짓는다.
/// 짝지은 줄 (i, j) 는 기준점 (i, j)·(i+1, j+1) 로 1:1 대응시키고, 처음 (0, 0)·끝 (줄 수, 줄 수) 을 더한다. 기준점은 두 축 모두 줄지 않고
/// 앞 점과 한 축 이상 다르다(한 축만 같으면 그 구간은 상대 쪽 한 점으로 모인다: 예 = 원문 첫 줄 태그 → 펼친 0줄).
/// 기준점 사이는 선형 보간이라 두 축이 모두 늘어나는 구간에서는 <see cref="ToSource"/>(<see cref="ToExpanded"/>(x)) = x 가 성립한다.
/// 계산 칸 수가 <see cref="MaxLcsCells"/> 를 넘으면 LCS 를 건너뛰고 시작·끝 기준점만 둔 비율 방식으로 간다.
/// </summary>
public sealed partial class MyBatisLineMap
{
    /// <summary>LCS 표 칸 수 상한(<c>MapperLineDiff</c> 와 같은 값).</summary>
    public const int MaxLcsCells = 4_000_000;

    private readonly double[] _source;
    private readonly double[] _expanded;

    private MyBatisLineMap(int sourceLineCount, int expandedLineCount, List<(double Source, double Expanded)> anchors)
    {
        SourceLineCount = sourceLineCount;
        ExpandedLineCount = expandedLineCount;
        Anchors = anchors.AsReadOnly();
        _source = [.. anchors.Select(a => a.Source)];
        _expanded = [.. anchors.Select(a => a.Expanded)];
    }

    /// <summary>0줄 ↔ 0줄 대응표.</summary>
    public static MyBatisLineMap Empty { get; } = new(0, 0, [(0.0, 0.0)]);

    /// <summary>원문 줄 수(마지막 줄바꿈 뒤 빈 줄도 한 줄. "a\n" = 2줄).</summary>
    public int SourceLineCount { get; }

    /// <summary>펼친 SQL 줄 수.</summary>
    public int ExpandedLineCount { get; }

    /// <summary>
    /// 기준점(원문 줄 위치, 펼친 줄 위치). 둘 다 줄지 않고 앞 점과 한 축 이상 다르다. 시험·계측용.
    /// 한쪽 줄 수가 0 이면 (0, 0) 한 점뿐이다.
    /// </summary>
    public IReadOnlyList<(double Source, double Expanded)> Anchors { get; }

    /// <summary>원문 위치(0 기준 소수 줄 위치, 줄 i 는 [i, i+1) 구간) → 펼친 SQL 위치. 범위 밖은 잘린다. 한쪽 줄 수가 0 이면 0.</summary>
    public double ToExpanded(double sourceLine) => Map(sourceLine, _source, _expanded);

    /// <summary>펼친 SQL 위치(0 기준 소수 줄 위치) → 원문 위치. 범위 밖은 잘린다. 한쪽 줄 수가 0 이면 0.</summary>
    public double ToSource(double expandedLine) => Map(expandedLine, _expanded, _source);

    /// <summary>원문과 평가해 펼친 SQL 로 대응표를 만든다.</summary>
    public static MyBatisLineMap Build(string source, string expanded)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(expanded);
        var a = SplitLines(source);
        var b = SplitLines(expanded);
        if (a.Count == 0 || b.Count == 0)
        {
            return new MyBatisLineMap(a.Count, b.Count, [(0.0, 0.0)]);
        }

        int sc = a.Count;
        int ec = b.Count;
        var points = new List<(double Source, double Expanded)> { (0.0, 0.0) };
        foreach (var (i, j) in MatchLines(Keys(a), Keys(b)))
        {
            TryAdd(points, i, j, sc, ec);
            TryAdd(points, i + 1, j + 1, sc, ec);
        }

        if (points[^1] != (sc, ec))
        {
            points.Add((sc, ec));
        }

        return new MyBatisLineMap(sc, ec, points);
    }

    // 앞 점보다 두 축 모두 작지 않고 한 축 이상 커야 하며 끝 점(sc, ec) 안에 있어야 기준점으로 쓴다. 아니면 버린다.
    // 첫 줄끼리·끝 줄끼리 짝도 남는다(원문 0줄 태그 뒤 1줄 SELECT ↔ 펼친 0줄 SELECT 면 (0,0)·(1,0)·(2,1)).
    private static void TryAdd(List<(double Source, double Expanded)> points, int s, int e, int sc, int ec)
    {
        var last = points[^1];
        if (s >= last.Source && e >= last.Expanded && (s > last.Source || e > last.Expanded) && s <= sc && e <= ec)
        {
            points.Add((s, e));
        }
    }

    // src 기준점 구간을 이진 탐색으로 찾아 dst 쪽으로 선형 보간한다. src 폭이 0 인 구간(한 축만 늘어난 구간)은 그 끝 점을 돌려준다.
    private static double Map(double value, double[] src, double[] dst)
    {
        if (src.Length < 2 || double.IsNaN(value))
        {
            return 0;
        }

        value = Math.Clamp(value, src[0], src[^1]);
        int lo = 0;
        int hi = src.Length - 2;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (src[mid] <= value)
            {
                lo = mid;
            }
            else
            {
                hi = mid - 1;
            }
        }

        double span = src[lo + 1] - src[lo];
        return span > 0 ? dst[lo] + ((value - src[lo]) / span * (dst[lo + 1] - dst[lo])) : dst[lo + 1];
    }

    // 줄 끝은 「\r\n」·「\n」·「\r」. 빈 글은 0줄, 그 밖에는 마지막 줄바꿈 뒤 빈 줄까지 센다.
    private static List<string> SplitLines(string text)
    {
        var lines = new List<string>();
        if (text.Length == 0)
        {
            return lines;
        }

        int start = 0;
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '\n' || c == '\r')
            {
                lines.Add(text[start..i]);
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                start = i + 1;
            }

            i++;
        }

        lines.Add(text[start..]);
        return lines;
    }

    // 정규화한 키가 빈 줄은 기준점 후보에서 뺀다(원래 줄 번호를 같이 들고 간다).
    private static List<(int Line, string Key)> Keys(List<string> lines)
    {
        var keys = new List<(int Line, string Key)>(lines.Count);
        for (int i = 0; i < lines.Count; i++)
        {
            var key = Normalize(lines[i]);
            if (key.Length > 0)
            {
                keys.Add((i, key));
            }
        }

        return keys;
    }

    // 비교용 키: 태그 지움 → 매개변수 표기·바인드 이름 → ? → 엔터티 해석 → 공백 정리.
    private static string Normalize(string line)
    {
        var s = TagRegex().Replace(line, string.Empty);
        s = ParamRegex().Replace(s, "?");
        s = s.Replace("&lt;", "<", StringComparison.Ordinal)
            .Replace("&gt;", ">", StringComparison.Ordinal)
            .Replace("&quot;", "\"", StringComparison.Ordinal)
            .Replace("&apos;", "'", StringComparison.Ordinal)
            .Replace("&amp;", "&", StringComparison.Ordinal);
        s = BindRegex().Replace(s, "?");
        return WhitespaceRegex().Replace(s, " ").Trim();
    }

    // 키가 같은 줄끼리 LCS 로 짝짓는다(원래 줄 번호 쌍, 증가 순). 칸 수가 상한을 넘으면 짝 없음.
    private static List<(int Source, int Expanded)> MatchLines(List<(int Line, string Key)> a, List<(int Line, string Key)> b)
    {
        var pairs = new List<(int Source, int Expanded)>();
        int n = a.Count;
        int m = b.Count;
        if (n == 0 || m == 0 || (long)n * m > MaxLcsCells)
        {
            return pairs;
        }

        // 뒤에서부터 길이 표를 채우고 앞에서부터 따라간다(MapperLineDiff 와 같은 방식).
        var lcs = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
        {
            for (int j = m - 1; j >= 0; j--)
            {
                lcs[i, j] = string.Equals(a[i].Key, b[j].Key, StringComparison.Ordinal)
                    ? lcs[i + 1, j + 1] + 1
                    : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }

        int x = 0;
        int y = 0;
        while (x < n && y < m)
        {
            if (string.Equals(a[x].Key, b[y].Key, StringComparison.Ordinal))
            {
                pairs.Add((a[x].Line, b[y].Line));
                x++;
                y++;
            }
            else if (lcs[x + 1, y] >= lcs[x, y + 1])
            {
                x++;
            }
            else
            {
                y++;
            }
        }

        return pairs;
    }

    // XML 태그·주석 한 줄 분량. 속성 값 따옴표 안의 「>」(예: test="a.size() > 0")는 태그 끝으로 보지 않는다.
    [GeneratedRegex("""<!--.*?-->|</?[A-Za-z_][^\s/>"']*(?:"[^"]*"|'[^']*'|[^>"'])*>""")]
    private static partial Regex TagRegex();

    // MyBatis 매개변수 표기 #{…}·${…}.
    [GeneratedRegex(@"[#$]\{[^}]*\}")]
    private static partial Regex ParamRegex();

    // 펼친 SQL 의 바인드 :이름. 「:=」·「::형변환」·단어 뒤의 「:」는 바인드가 아니다.
    [GeneratedRegex(@"(?<![:\w]):[A-Za-z_][A-Za-z0-9_]*")]
    private static partial Regex BindRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
