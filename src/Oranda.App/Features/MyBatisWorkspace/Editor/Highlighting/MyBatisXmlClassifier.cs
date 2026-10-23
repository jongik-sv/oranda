using Oranda.Language.Lexing;

namespace Oranda.App.Features.MyBatisWorkspace.Editor.Highlighting;

/// <summary>MyBatis 문장 원문 강조 구간 종류(App 소유 enum, R21: Language 값 형식을 필드에 두지 않으려고 따로 둔다).</summary>
public enum MyBatisSpanKind : byte
{
    XmlTag,
    XmlAttrName,
    XmlAttrValue,
    XmlComment,
    XmlCData,
    XmlEntity,
    Param,
    Substitution,
    SqlKeyword,
    SqlString,
    SqlComment,
    SqlNumber,
    SqlBind,
    SqlHint,
    SqlFunction,
    SqlQuoted,
}

/// <summary>줄 안 구간 하나(줄 안 0 기반 위치). 그리는 순서 = 목록 순서(뒤가 앞을 덮는다).</summary>
public readonly record struct MyBatisSpan(int Start, int Length, MyBatisSpanKind Kind);

/// <summary>
/// 줄 시작 상태 = XML 모드와 SQL 렉서 상태의 짝. <see cref="Sql"/> 은 <see cref="SqlLexer.LexLine"/> 의 int 상태 그대로다
/// (Language 의 값 형식을 필드에 두지 않는다, design.md 9장 R21).
/// </summary>
public readonly record struct MyBatisLineState(MyBatisXmlMode Xml, char Quote, int Sql)
{
    public static MyBatisLineState Start => default;
}

public enum MyBatisXmlMode : byte
{
    /// <summary>요소 안 글(SQL 구간).</summary>
    Text,

    /// <summary>여는 태그 안(속성 사이).</summary>
    InTag,

    /// <summary>속성 값 안(<see cref="MyBatisLineState.Quote"/> 로 닫힘).</summary>
    InAttrValue,

    /// <summary>&lt;!-- … --&gt; 안.</summary>
    InComment,

    /// <summary>&lt;![CDATA[ … ]]&gt; 안(SQL 구간, 태그·엔티티 없음).</summary>
    InCData,

    /// <summary>&lt;? … ?&gt;·&lt;!DOCTYPE … &gt; 안.</summary>
    InDecl,
}

/// <summary>
/// MyBatis 문장 원문 한 줄을 강조 구간으로 나눈다(TSK-03-01 F4 시안, 스파이크).
/// 1) XML 구간(태그·속성·주석·CDATA 경계·엔티티·#{}·${})을 찾고, 2) 그 구간을 같은 길이 공백으로 가린 줄을 L1 렉서에 넣어 SQL 토큰을 얻고,
/// 3) SQL 토큰 → XML 구간 → #{}·${} 순으로 돌려준다(뒤가 앞을 덮으므로 SQL 문자열 안의 ${} 도 바인드 색이 이긴다).
/// 순수 함수라 UI 없이 시험한다.
/// </summary>
public static class MyBatisXmlClassifier
{
    /// <summary>글 전체를 줄마다 나눈다(시험·덤프용). 줄 끝 \r 은 글자로 남는다(편집기 줄도 같다).</summary>
    public static List<List<MyBatisSpan>> ClassifyAll(string text)
    {
        var result = new List<List<MyBatisSpan>>();
        var state = MyBatisLineState.Start;
        foreach (var line in text.Split('\n'))
        {
            var spans = new List<MyBatisSpan>();
            state = ClassifyLine(line, state, spans);
            result.Add(spans);
        }

        return result;
    }

    /// <summary>줄마다 시작 상태를 돌려준다(마지막 항목 = 글 끝 상태). 변환기의 증분 갱신 결과를 전체 다시 분류와 맞대는 기준이다.</summary>
    public static List<MyBatisLineState> StartStates(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var result = new List<MyBatisLineState>();
        var state = MyBatisLineState.Start;
        var scratch = new List<MyBatisSpan>();
        result.Add(state);
        foreach (var line in text.Split('\n'))
        {
            scratch.Clear();
            state = ClassifyLine(line, state, scratch);
            result.Add(state);
        }

        return result;
    }

    /// <summary>줄 하나를 나누고 줄 끝 상태를 돌려준다.</summary>
    public static MyBatisLineState ClassifyLine(ReadOnlySpan<char> line, MyBatisLineState state, List<MyBatisSpan> spans)
    {
        var xml = new List<MyBatisSpan>();
        var vars = new List<MyBatisSpan>();
        var masked = line.Length <= 512 ? stackalloc char[line.Length] : new char[line.Length];
        line.CopyTo(masked);

        var mode = state.Xml;
        var quote = state.Quote;
        int i = 0, n = line.Length;
        while (i < n)
        {
            switch (mode)
            {
                case MyBatisXmlMode.Text:
                case MyBatisXmlMode.InCData:
                    {
                        bool cdata = mode == MyBatisXmlMode.InCData;
                        int j = i;
                        while (j < n)
                        {
                            char c = line[j];
                            if (cdata && c == ']' && line[j..].StartsWith("]]>"))
                            {
                                break;
                            }

                            if (!cdata && (c == '<' || c == '&'))
                            {
                                break;
                            }

                            if ((c == '#' || c == '$') && j + 1 < n && line[j + 1] == '{')
                            {
                                int close = line[(j + 2)..].IndexOf('}');
                                int end = close < 0 ? n : j + 2 + close + 1;
                                vars.Add(new(j, end - j, c == '#' ? MyBatisSpanKind.Param : MyBatisSpanKind.Substitution));
                                Mask(masked, j, end);
                                j = end;
                                continue;
                            }

                            j++;
                        }

                        i = j;
                        if (i >= n)
                        {
                            break;
                        }

                        if (cdata)
                        {
                            xml.Add(new(i, 3, MyBatisSpanKind.XmlCData));
                            Mask(masked, i, i + 3);
                            i += 3;
                            mode = MyBatisXmlMode.Text;
                        }
                        else if (line[i] == '&')
                        {
                            int semi = line[i..].IndexOf(';');
                            int end = semi is > 1 and <= 10 ? i + semi + 1 : i + 1;
                            if (end > i + 1)
                            {
                                xml.Add(new(i, end - i, MyBatisSpanKind.XmlEntity));
                                Mask(masked, i, end);
                            }

                            i = end;
                        }
                        else
                        {
                            i = OpenMarkup(line, i, masked, xml, ref mode);
                        }

                        break;
                    }

                case MyBatisXmlMode.InTag:
                    {
                        char c = line[i];
                        if (c == '>' || (c == '/' && i + 1 < n && line[i + 1] == '>'))
                        {
                            int len = c == '>' ? 1 : 2;
                            xml.Add(new(i, len, MyBatisSpanKind.XmlTag));
                            Mask(masked, i, i + len);
                            i += len;
                            mode = MyBatisXmlMode.Text;
                        }
                        else if (c == '"' || c == '\'')
                        {
                            quote = c;
                            mode = MyBatisXmlMode.InAttrValue;
                            int end = CloseQuote(line, i + 1, c);
                            xml.Add(new(i, end - i, MyBatisSpanKind.XmlAttrValue));
                            Mask(masked, i, end);
                            if (end <= n && end > i + 1 && line[end - 1] == c)
                            {
                                mode = MyBatisXmlMode.InTag;
                            }

                            i = end;
                        }
                        else if (IsNameChar(c))
                        {
                            int j = i;
                            while (j < n && IsNameChar(line[j]))
                            {
                                j++;
                            }

                            xml.Add(new(i, j - i, MyBatisSpanKind.XmlAttrName));
                            Mask(masked, i, j);
                            i = j;
                        }
                        else
                        {
                            Mask(masked, i, i + 1); // 공백·= 은 색 없이 가리기만
                            i++;
                        }

                        break;
                    }

                case MyBatisXmlMode.InAttrValue:
                    {
                        int end = CloseQuote(line, i, quote);
                        xml.Add(new(i, end - i, MyBatisSpanKind.XmlAttrValue));
                        Mask(masked, i, end);
                        if (end > i && line[end - 1] == quote)
                        {
                            mode = MyBatisXmlMode.InTag;
                        }

                        i = end;
                        break;
                    }

                case MyBatisXmlMode.InComment:
                    {
                        int close = line[i..].IndexOf("-->");
                        int end = close < 0 ? n : i + close + 3;
                        xml.Add(new(i, end - i, MyBatisSpanKind.XmlComment));
                        Mask(masked, i, end);
                        if (close >= 0)
                        {
                            mode = MyBatisXmlMode.Text;
                        }

                        i = end;
                        break;
                    }

                case MyBatisXmlMode.InDecl:
                    {
                        int close = line[i..].IndexOf('>');
                        int end = close < 0 ? n : i + close + 1;
                        xml.Add(new(i, end - i, MyBatisSpanKind.XmlTag));
                        Mask(masked, i, end);
                        if (close >= 0)
                        {
                            mode = MyBatisXmlMode.Text;
                        }

                        i = end;
                        break;
                    }
            }
        }

        // 2) 가린 줄을 L1 렉서로. 줄 끝 \r 은 렉서에 넘기지 않는다(SqlSyntaxColorizer 와 같이 줄 글만).
        var tokens = new List<Token>();
        var sqlLine = masked;
        if (sqlLine.Length > 0 && sqlLine[^1] == '\r')
        {
            sqlLine = sqlLine[..^1];
        }

        int sqlState = SqlLexer.LexLine(sqlLine, state.Sql, tokens);
        foreach (var t in tokens)
        {
            MyBatisSpanKind? kind = SqlTokenColors.Of(t, sqlLine) switch
            {
                SqlColorKind.Keyword => MyBatisSpanKind.SqlKeyword,
                SqlColorKind.StringLiteral => MyBatisSpanKind.SqlString,
                SqlColorKind.Comment => MyBatisSpanKind.SqlComment,
                SqlColorKind.Number => MyBatisSpanKind.SqlNumber,
                SqlColorKind.Bind => MyBatisSpanKind.SqlBind,
                SqlColorKind.Hint => MyBatisSpanKind.SqlHint,
                SqlColorKind.Quoted => MyBatisSpanKind.SqlQuoted,
                SqlColorKind.Function => MyBatisSpanKind.SqlFunction,
                _ => null,
            };
            if (kind is { } k && !AllBlank(sqlLine.Slice(t.Start, t.Length)))
            {
                spans.Add(new(t.Start, t.Length, k));
            }
        }

        spans.AddRange(xml);
        spans.AddRange(vars);

        // XML 이 SQL 주석·문자열을 끊지 않는다: CDATA·글 구간의 SQL 상태만 다음 줄로 넘긴다. 태그 안에 있으면 SQL 상태는 그대로 둔다.
        return new MyBatisLineState(mode, quote, sqlState);
    }

    /// <summary>&lt; 에서 시작하는 표지를 읽는다(여는·닫는 태그, 주석, CDATA, 선언).</summary>
    private static int OpenMarkup(ReadOnlySpan<char> line, int i, Span<char> masked, List<MyBatisSpan> xml, ref MyBatisXmlMode mode)
    {
        int n = line.Length;
        var rest = line[i..];
        if (rest.StartsWith("<!--"))
        {
            int close = line[(i + 4)..].IndexOf("-->");
            int end = close < 0 ? n : i + 4 + close + 3;
            xml.Add(new(i, end - i, MyBatisSpanKind.XmlComment));
            Mask(masked, i, end);
            mode = close < 0 ? MyBatisXmlMode.InComment : MyBatisXmlMode.Text;
            return end;
        }

        if (rest.StartsWith("<![CDATA["))
        {
            xml.Add(new(i, 9, MyBatisSpanKind.XmlCData));
            Mask(masked, i, i + 9);
            mode = MyBatisXmlMode.InCData;
            return i + 9;
        }

        if (rest.StartsWith("<?") || rest.StartsWith("<!"))
        {
            int close = rest.IndexOf('>');
            int end = close < 0 ? n : i + close + 1;
            xml.Add(new(i, end - i, MyBatisSpanKind.XmlTag));
            Mask(masked, i, end);
            mode = close < 0 ? MyBatisXmlMode.InDecl : MyBatisXmlMode.Text;
            return end;
        }

        int j = i + 1;
        if (j < n && line[j] == '/')
        {
            j++;
        }

        if (j >= n || !IsNameStart(line[j]))
        {
            return i + 1; // 그냥 < 글자(깨진 xml): SQL 연산자로 남긴다
        }

        while (j < n && IsNameChar(line[j]))
        {
            j++;
        }

        xml.Add(new(i, j - i, MyBatisSpanKind.XmlTag));
        Mask(masked, i, j);
        mode = MyBatisXmlMode.InTag;
        return j;
    }

    /// <summary>따옴표 <paramref name="q"/> 를 닫는 위치 다음(없으면 줄 끝).</summary>
    private static int CloseQuote(ReadOnlySpan<char> line, int from, char q)
    {
        int k = line[from..].IndexOf(q);
        return k < 0 ? line.Length : from + k + 1;
    }

    private static bool IsNameStart(char c) => char.IsLetter(c) || c == '_' || c == ':';

    private static bool IsNameChar(char c) => char.IsLetterOrDigit(c) || c is '_' or ':' or '-' or '.';

    private static void Mask(Span<char> masked, int from, int to)
    {
        for (int k = from; k < to && k < masked.Length; k++)
        {
            if (masked[k] != '\r')
            {
                masked[k] = ' ';
            }
        }
    }

    private static bool AllBlank(ReadOnlySpan<char> s) => s.IsWhiteSpace();
}
