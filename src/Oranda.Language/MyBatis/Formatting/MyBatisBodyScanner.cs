namespace Oranda.Language.MyBatis.Formatting;

/// <summary>본문 구간 종류. <see cref="OpenTag"/> 이상은 XML 구간(포맷 때 표식 하나로 바뀌는 구간)이다.</summary>
internal enum BodySegmentKind : byte
{
    /// <summary>SQL 글.</summary>
    Text,

    /// <summary><c>#{…}</c>·<c>${…}</c>.</summary>
    Param,

    /// <summary>엔티티(<c>&amp;lt;</c> 등). <see cref="BodySegment.Decoded"/> 가 푼 글자.</summary>
    Entity,

    OpenTag,
    CloseTag,

    /// <summary><c>&lt;include …/&gt;</c>·<c>&lt;bind …/&gt;</c> 처럼 스스로 닫힌 태그.</summary>
    EmptyTag,

    CdataStart,
    CdataEnd,
    XmlComment,

    /// <summary>원문 그대로 둘 통째 구간(<c>selectKey</c> 요소 전체, 한 줄에서 열고 닫는 동적 요소 전체).</summary>
    Whole,
}

/// <summary>본문 구간 1개. 위치는 본문 글 기준이다.</summary>
internal sealed class BodySegment(BodySegmentKind kind, int start, int length)
{
    public BodySegmentKind Kind { get; } = kind;

    public int Start { get; } = start;

    public int Length { get; } = length;

    public int End => Start + Length;

    /// <summary>태그 이름(여는·닫는·빈 태그).</summary>
    public string? Name { get; init; }

    /// <summary>엔티티를 푼 글자, <see cref="BodySegmentKind.Param"/> 이면 앞 글자(<c>#</c>·<c>$</c>).</summary>
    public char Decoded { get; init; }

    /// <summary>짝 구간 번호(여는↔닫는 태그, CDATA 시작↔끝). 없으면 -1.</summary>
    public int Pair { get; set; } = -1;

    public bool IsMarkup => Kind >= BodySegmentKind.OpenTag;
}

/// <summary>본문 스캔 결과. <see cref="Error"/> 가 있으면 포맷할 수 없는 모양이다.</summary>
internal sealed class BodyScan
{
    public required IReadOnlyList<BodySegment> Segments { get; init; }

    /// <summary>통째 구간으로 묶기 전의 잘게 나눈 구간. 여는·닫는 태그가 한 줄에 놓이는지에 따라 묶임이 달라지므로 구간 열 비교는 이쪽으로 한다.</summary>
    public IReadOnlyList<BodySegment> Fine { get; init; } = [];

    public string? Error { get; init; }

    public bool Ok => Error is null;
}

/// <summary>
/// MyBatis 문장 본문 스캐너. 본문을 XML 구간과 글 구간으로 나눈다(원문 위치 유지). MyBatis 는 XML 을 먼저 읽으므로
/// XML 인식은 SQL 문자열·주석보다 먼저 한다. CDATA 안에서는 태그·엔티티를 읽지 않고 <c>#{…}</c>·<c>${…}</c> 만 읽는다.
/// 짝이 안 맞는 태그·닫히지 않은 구간·낱말을 가르는 XML 경계 등 포맷할 수 없는 모양은 <see cref="BodyScan.Error"/> 로 알린다.
/// </summary>
internal static class MyBatisBodyScanner
{
    public static BodyScan Scan(string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (body.Contains("/*@", StringComparison.Ordinal)) return Fail("본문에 이미 /*@ 표식이 있습니다.");

        var walker = new Walker(body);
        string? error = walker.Run() ?? CheckBoundaries(body, walker.Segments);
        return error is not null ? Fail(error) : new BodyScan { Segments = Group(body, walker.Segments), Fine = walker.Segments };
    }

    static BodyScan Fail(string error) => new() { Segments = [], Error = error };

    /// <summary>
    /// <paramref name="from"/> 부터 여는·빈 태그 끝(「&gt;」 다음)을 찾는다. 속성 값 따옴표 안의 「&gt;」 는 건너뛴다.
    /// 못 찾으면 -1 이고, 따옴표가 닫히지 않아서이면 <paramref name="openQuote"/> 가 true.
    /// </summary>
    internal static int TagEnd(string text, int from, out bool openQuote)
    {
        char quote = '\0';
        for (int i = from; i < text.Length; i++)
        {
            char c = text[i];
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (c == '>')
            {
                openQuote = false;
                return i + 1;
            }
        }

        openQuote = quote != '\0';
        return -1;
    }

    internal static bool IsNameStart(char c) => char.IsLetter(c) || c == '_';

    internal static bool IsNameChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '-' or '.' or ':';

    /// <summary>낱말 글자(문자·숫자·<c>_</c>·<c>$</c>·<c>#</c>). 렉서가 한 식별자로 읽는 글자와 같다.</summary>
    internal static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '$' or '#';

    /// <summary>XML 구간 바로 앞 글자와 바로 뒤 글자가 모두 낱말 글자이면 낱말을 가르는 경계다.</summary>
    static string? CheckBoundaries(string body, List<BodySegment> segments)
    {
        foreach (var s in segments)
        {
            if (!s.IsMarkup) continue;
            if (s.Start > 0 && s.End < body.Length && IsWordChar(body[s.Start - 1]) && IsWordChar(body[s.End]))
            {
                return $"낱말을 가르는 XML 경계입니다: '{body[s.Start - 1]}' 와 '{body[s.End]}' 사이의 {Excerpt(body, s)}.";
            }
        }

        return null;
    }

    static string Excerpt(string body, BodySegment s)
    {
        string t = body.Substring(s.Start, Math.Min(s.Length, 40));
        return s.Length > 40 ? t + "…" : t;
    }

    /// <summary>selectKey 요소 전체, 여는 태그와 짝 닫는 태그가 같은 줄에 있는 요소 전체를 구간 하나로 묶는다.</summary>
    static List<BodySegment> Group(string body, List<BodySegment> fine)
    {
        var grouped = new List<BodySegment>(fine.Count);
        int i = 0;
        while (i < fine.Count)
        {
            var s = fine[i];
            if (s.Kind == BodySegmentKind.OpenTag)
            {
                var close = fine[s.Pair];
                bool oneLine = body.AsSpan(s.Start, close.End - s.Start).IndexOf('\n') < 0;
                if (oneLine || s.Name == "selectKey")
                {
                    grouped.Add(new BodySegment(BodySegmentKind.Whole, s.Start, close.End - s.Start) { Name = s.Name });
                    i = s.Pair + 1;
                    continue;
                }
            }

            grouped.Add(new BodySegment(s.Kind, s.Start, s.Length) { Name = s.Name, Decoded = s.Decoded });
            i++;
        }

        var stack = new Stack<int>();
        for (int k = 0; k < grouped.Count; k++)
        {
            var s = grouped[k];
            if (s.Kind is BodySegmentKind.OpenTag or BodySegmentKind.CdataStart)
            {
                stack.Push(k);
            }
            else if (s.Kind is BodySegmentKind.CloseTag or BodySegmentKind.CdataEnd)
            {
                int o = stack.Pop();
                s.Pair = o;
                grouped[o].Pair = k;
            }
        }

        return grouped;
    }

    /// <summary>왼쪽에서 오른쪽으로 한 번 읽으며 잘게 나눈 구간 열을 만든다.</summary>
    sealed class Walker(string body)
    {
        readonly Stack<int> open = new();
        int textStart;
        int cdata = -1;

        public List<BodySegment> Segments { get; } = [];

        /// <summary>끝까지 읽는다. 읽을 수 없는 모양이면 사유, 아니면 null.</summary>
        public string? Run()
        {
            int n = body.Length;
            int i = 0;
            while (i < n)
            {
                char c = body[i];
                string? error = null;
                if (cdata >= 0 && c == ']' && At(i, "]]>"))
                {
                    int end = Add(BodySegmentKind.CdataEnd, i, 3);
                    Segments[end].Pair = cdata;
                    Segments[cdata].Pair = end;
                    cdata = -1;
                    i += 3;
                }
                else if (cdata < 0 && c == '<')
                {
                    i = Markup(i, out error);
                }
                else if (cdata < 0 && c == '&')
                {
                    i = Entity(i, out error);
                }
                else if (c is '#' or '$' && i + 1 < n && body[i + 1] == '{')
                {
                    i = Param(i, out error);
                }
                else
                {
                    i++;
                }

                if (error is not null) return error;
            }

            if (cdata >= 0) return "닫히지 않은 CDATA 가 있습니다.";
            if (open.Count > 0) return $"닫히지 않은 태그 <{Segments[open.Peek()].Name}> 가 있습니다.";
            Flush(n);
            return null;
        }

        bool At(int i, string s) => string.CompareOrdinal(body, i, s, 0, s.Length) == 0;

        void Flush(int upTo)
        {
            if (upTo > textStart) Segments.Add(new BodySegment(BodySegmentKind.Text, textStart, upTo - textStart));
        }

        int Add(BodySegmentKind kind, int start, int length, string? name = null, char decoded = '\0')
        {
            Flush(start);
            Segments.Add(new BodySegment(kind, start, length) { Name = name, Decoded = decoded });
            textStart = start + length;
            return Segments.Count - 1;
        }

        /// <summary><c>&lt;</c> 에서 시작한 XML 구간을 읽고 다음 위치를 돌려준다.</summary>
        int Markup(int i, out string? error)
        {
            error = null;
            int n = body.Length;
            if (At(i, "<![CDATA["))
            {
                cdata = Add(BodySegmentKind.CdataStart, i, 9);
                return i + 9;
            }

            if (At(i, "<!--"))
            {
                int e = body.IndexOf("-->", i + 4, StringComparison.Ordinal);
                if (e < 0)
                {
                    error = "닫히지 않은 XML 주석이 있습니다.";
                    return n;
                }

                Add(BodySegmentKind.XmlComment, i, e + 3 - i);
                return e + 3;
            }

            if (At(i, "<!") || At(i, "<?"))
            {
                error = "지원하지 않는 XML 선언·처리 명령이 있습니다.";
                return n;
            }

            if (At(i, "</")) return CloseTag(i, out error);

            if (i + 1 < n && IsNameStart(body[i + 1])) return StartTag(i, out error);

            error = "XML 태그로 읽을 수 없는 '<' 가 있습니다(본문의 < 는 &lt; 나 CDATA 로 써야 합니다).";
            return n;
        }

        int NameEnd(int from)
        {
            int k = from;
            while (k < body.Length && IsNameChar(body[k])) k++;
            return k;
        }

        int CloseTag(int i, out string? error)
        {
            error = null;
            int n = body.Length;
            int j = i + 2;
            int k = NameEnd(j);
            int p = k;
            while (p < n && char.IsWhiteSpace(body[p])) p++;
            if (k == j || p >= n || body[p] != '>')
            {
                error = "닫는 태그 모양이 잘못됐습니다.";
                return n;
            }

            string name = body[j..k];
            if (open.Count == 0 || Segments[open.Peek()].Name != name)
            {
                error = $"짝이 맞지 않는 닫는 태그 </{name}> 가 있습니다.";
                return n;
            }

            int idx = Add(BodySegmentKind.CloseTag, i, p + 1 - i, name);
            int o = open.Pop();
            Segments[idx].Pair = o;
            Segments[o].Pair = idx;
            return p + 1;
        }

        int StartTag(int i, out string? error)
        {
            error = null;
            int k = NameEnd(i + 1);
            string name = body[(i + 1)..k];
            int end = TagEnd(body, k, out bool openQuote);
            if (end < 0)
            {
                error = openQuote ? $"<{name}> 태그의 속성 따옴표가 닫히지 않았습니다." : $"<{name}> 태그가 닫히지 않았습니다.";
                return body.Length;
            }

            if (end - 2 >= k && body[end - 2] == '/')
            {
                Add(BodySegmentKind.EmptyTag, i, end - i, name);
            }
            else
            {
                open.Push(Add(BodySegmentKind.OpenTag, i, end - i, name));
            }

            return end;
        }

        int Entity(int i, out string? error)
        {
            error = null;
            int n = body.Length;
            int semi = -1;
            for (int j = i + 1; j < n && j <= i + 10; j++)
            {
                if (body[j] == ';')
                {
                    semi = j;
                    break;
                }
            }

            string name = semi < 0 ? "" : body[(i + 1)..semi];
            char decoded = name switch
            {
                "lt" => '<',
                "gt" => '>',
                "amp" => '&',
                "quot" => '"',
                "apos" => '\'',
                _ => Numeric(name),
            };
            if (semi < 0 || decoded == '\0')
            {
                int stop = i + 1;
                while (stop < n && stop < i + 12 && !char.IsWhiteSpace(body[stop])) stop++;
                error = $"알 수 없는 엔티티가 있습니다: {body[i..stop]}";
                return n;
            }

            Add(BodySegmentKind.Entity, i, semi + 1 - i, decoded: decoded);
            return semi + 1;
        }

        /// <summary>숫자 엔티티 이름(<c>#60</c>·<c>#x3c</c>)을 푼다. 공백·제어 글자·BMP 밖은 읽지 않는다(\0).</summary>
        static char Numeric(string name)
        {
            if (name.Length < 2 || name[0] != '#') return '\0';
            bool hex = name[1] is 'x' or 'X';
            var digits = name.AsSpan(hex ? 2 : 1);
            var style = hex ? System.Globalization.NumberStyles.AllowHexSpecifier : System.Globalization.NumberStyles.None;
            if (!int.TryParse(digits, style, System.Globalization.CultureInfo.InvariantCulture, out int cp)) return '\0';
            if (cp is <= 0x20 or > 0xFFFF) return '\0';
            char ch = (char)cp;
            return char.IsSurrogate(ch) || char.IsWhiteSpace(ch) || char.IsControl(ch) ? '\0' : ch;
        }

        int Param(int i, out string? error)
        {
            error = null;
            int n = body.Length;
            int close = body.IndexOf('}', i + 2);
            if (close < 0 || body.AsSpan(i + 2, close - i - 2).IndexOfAny('\r', '\n') >= 0)
            {
                error = $"{body.Substring(i, 2)}…}} 가 닫히지 않았거나 여러 줄에 걸쳐 있습니다.";
                return n;
            }

            Add(BodySegmentKind.Param, i, close + 1 - i, decoded: body[i]);
            return close + 1;
        }
    }
}
