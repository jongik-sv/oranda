using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Oranda.App.Features.PlsqlEditor;

/// <summary>소스 글자 다루기(머리 읽기·줄끝 정리·차이). 화면·서버 없이 시험할 수 있다. 소유: TSK-08-01.</summary>
public static class PlsqlSourceText
{
    /// <summary>편집기에 보이는 소스 앞에 붙이는 말. 서버 소스 첫 줄(<c>PACKAGE BODY x AS</c>)과 같은 줄에 붙여 줄 번호가 서버 오류와 맞는다.</summary>
    public const string CreatePrefix = "CREATE OR REPLACE ";

    private const string CreateWords = @"CREATE\s+(?:OR\s+REPLACE\s+)?(?:(?:NON)?EDITIONABLE\s+)?";

    private const string TypeAndName = @"(?<type>PACKAGE\s+BODY|PACKAGE|PROCEDURE|FUNCTION|TRIGGER|TYPE\s+BODY|TYPE)\s+(?<a>""[^""]+""|[\w$#]+)(?:\s*\.\s*(?<b>""[^""]+""|[\w$#]+))?";

    private static readonly Regex Header = new(@"^\s*(?:" + CreateWords + ")?" + TypeAndName, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>머리가 <c>CREATE</c> 로 시작해야 하는 형태(SQL 편집기에서 실행한 문장 판정용).</summary>
    private static readonly Regex CreateHeader = new(@"^\s*" + CreateWords + TypeAndName, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// <c>CREATE [OR REPLACE] [AND RESOLVE|COMPILE] [NOFORCE] JAVA SOURCE NAMED [소유자.]이름 … AS</c>(TSK-14-23). 뒤의 공백까지 먹어
    /// 일치 끝이 Java 코드의 첫 글자(서버 오류 1줄 1열)다.
    /// </summary>
    private static readonly Regex JavaCreateHeader = new(
        @"^\s*CREATE\s+(?:OR\s+REPLACE\s+)?(?:AND\s+(?:RESOLVE|COMPILE)\s+)?(?:NOFORCE\s+)?JAVA\s+SOURCE\s+NAMED\s+"
        + @"(?<a>""(?:[^""]|"""")+""|[\w$#]+)(?:\s*\.\s*(?<b>""(?:[^""]|"""")+""|[\w$#]+))?[\s\S]*?\bAS\b\s*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>소스를 가진 객체 유형(Oracle OBJECT_TYPE).</summary>
    public static IReadOnlySet<string> SourceTypes { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "PACKAGE", "PACKAGE BODY", "PROCEDURE", "FUNCTION", "TRIGGER", "TYPE", "TYPE BODY",
    };

    /// <summary>명세/본문 쌍이 있는 유형의 반대쪽. 없으면 null.</summary>
    public static string? SiblingType(string objectType) => objectType switch
    {
        "PACKAGE" => "PACKAGE BODY",
        "PACKAGE BODY" => "PACKAGE",
        "TYPE" => "TYPE BODY",
        "TYPE BODY" => "TYPE",
        _ => null,
    };

    private static readonly Regex StoredHeader = new(
        @"^(?<type>PACKAGE\s+BODY|PACKAGE|PROCEDURE|FUNCTION|TRIGGER|TYPE\s+BODY|TYPE)(?<ws>\s+)(?<name>""[^""]+""|(?>[\w$#]+))(?!\s*\.)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// 서버 소스에 <c>CREATE OR REPLACE</c> 를 붙인 편집기 글자. 줄끝은 그대로(LF).
    /// 서버는 소스를 저장할 때 소유자 이름(<c>SCOTT.</c>)을 공백으로 바꿔 두므로, 다른 스키마로 컴파일되지 않게 첫 줄에 소유자를 다시 적는다.
    /// <paramref name="line1Shift"/> = 그 때문에 첫 줄의 이름 뒤 글자가 오른쪽으로 밀린 칸 수(서버 오류의 1줄 열 보정용, 앞에 붙인 말 제외).
    /// </summary>
    public static string ToEditorText(string serverSource, string? owner, out int line1Shift)
    {
        var text = serverSource.TrimStart();
        line1Shift = 0;
        if (!string.IsNullOrEmpty(owner) && StoredHeader.Match(text) is { Success: true } m)
        {
            var ws = m.Groups["ws"];
            var qualifier = " \"" + owner.Replace("\"", "\"\"", StringComparison.Ordinal) + "\".";
            line1Shift = qualifier.Length - ws.Length;
            text = string.Concat(text.AsSpan(0, ws.Index), qualifier, text.AsSpan(ws.Index + ws.Length));
        }

        return CreatePrefix + text;
    }

    public static string ToEditorText(string serverSource) => ToEditorText(serverSource, null, out _);

    /// <summary>SQL*Plus 의 오류 보기 명령 줄(<c>SHOW ERRORS</c>·<c>SHOW ERR</c>·<c>SHO ERR</c>·<c>SHO ERRO</c>, 뒤에 객체 지정 <c>PACKAGE BODY pk</c> 등과 <c>;</c> 허용). 서버에 보낼 문장이 아니다.</summary>
    private static readonly Regex ShowErrorsLine = new(@"^SHO(?:W)?\s+ERR(?:ORS|OR|O)?(?:\s+[^;]+?)?\s*;?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// 편집기 글에서 컴파일할 첫 PL/SQL 단위를 가른다. 단위의 끝은 <c>/</c> 단독 줄(없으면 글 끝까지 한 단위, 본문 안 <c>;</c> 로는 자르지 않는다).
    /// 단위 뒤의 <c>/</c> 줄·<c>SHOW ERRORS</c> 류·빈 줄·주석은 떼고, 그 밖의 글(GRANT, 두 번째 CREATE …)이 남으면 <c>HasMore</c> 가 참이다.
    /// <c>Unit</c> 은 컴파일에 보낼 글자(줄끝 LF, 끝의 빈 줄·<c>/</c>·<c>SHOW ERRORS</c> 제외, 끝의 <c>;</c> 는 남긴다: PL/SQL 단위가 필요로 한다).
    /// </summary>
    public static (string Unit, bool HasMore) SplitCompileUnit(string editorText)
    {
        var lines = Normalize(editorText).Split('\n');
        var state = default(LexState);
        var starts = new LexState[lines.Length]; // 줄 머리에서의 글자 상태(주석·문자열 안의 단독 / 는 경계가 아니다)
        var slash = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            starts[i] = state;
            if (state.Mode == Lex.Code && lines[i].Trim() == "/" && i > 0 && lines.Take(i).Any(l => l.Trim().Length > 0))
            {
                slash = i;
                break;
            }

            state = Scan(lines[i], state);
        }

        var unit = (slash < 0 ? lines : lines[..slash]).ToList();
        while (unit.Count > 0 && (unit[^1].Trim() is "" or "/" || ShowErrorsLine.IsMatch(unit[^1].Trim())))
        {
            unit.RemoveAt(unit.Count - 1);
        }

        var hasMore = slash < 0 && HasOtherStatement(lines, starts);
        if (slash >= 0)
        {
            var inBlock = false;
            foreach (var line in lines[(slash + 1)..])
            {
                var t = line.Trim();
                if (inBlock)
                {
                    inBlock = Scan(line, BlockState).Mode == Lex.Block;
                    continue;
                }

                if (t.Length == 0 || t == "/" || t.StartsWith("--", StringComparison.Ordinal) || ShowErrorsLine.IsMatch(t))
                {
                    continue;
                }

                if (t.StartsWith("/*", StringComparison.Ordinal))
                {
                    inBlock = Scan(line, default).Mode == Lex.Block;
                    if (!inBlock && t.IndexOf("*/", StringComparison.Ordinal) is var end and >= 0 && t[(end + 2)..].Trim().Length > 0)
                    {
                        hasMore = true;
                    }

                    continue;
                }

                hasMore = true;
                break;
            }
        }

        return (string.Join('\n', unit).TrimEnd(), hasMore);
    }

    private enum Lex
    {
        Code,
        Block,
        Quote,
        QQuote,
    }

    /// <summary>줄 끝에서의 글자 상태. <c>Close</c> = <c>q'[…]'</c> 류 문자열의 닫는 구분자.</summary>
    private readonly record struct LexState(Lex Mode, char Close);

    private static readonly LexState BlockState = new(Lex.Block, '\0');

    /// <summary>
    /// 줄 하나를 지나며 글자 상태를 갱신한다: <c>/* … */</c>, <c>'…'</c>(<c>''</c> 는 이스케이프), <c>q'[…]'</c>(구분자 <c>[] {} () &lt;&gt;</c> 와 임의 문자) 는 여러 줄에 걸쳐 이어지고
    /// <c>--</c> 줄 주석은 줄 끝까지 건너뛴다. 단독 <c>/</c> 줄 판정과 줄 머리 낱말 판정에만 쓴다.
    /// </summary>
    private static LexState Scan(string line, LexState s)
    {
        var i = 0;
        while (i < line.Length)
        {
            switch (s.Mode)
            {
                case Lex.Block:
                {
                    var end = line.IndexOf("*/", i, StringComparison.Ordinal);
                    if (end < 0)
                    {
                        return s;
                    }

                    s = default;
                    i = end + 2;
                    break;
                }

                case Lex.Quote:
                {
                    var q = line.IndexOf('\'', i);
                    if (q < 0)
                    {
                        return s;
                    }

                    if (q + 1 < line.Length && line[q + 1] == '\'')
                    {
                        i = q + 2;
                    }
                    else
                    {
                        s = default;
                        i = q + 1;
                    }

                    break;
                }

                case Lex.QQuote:
                {
                    var end = line.IndexOf(s.Close + "'", i, StringComparison.Ordinal);
                    if (end < 0)
                    {
                        return s;
                    }

                    s = default;
                    i = end + 2;
                    break;
                }

                default:
                {
                    var c = line[i];
                    if (c == '-' && i + 1 < line.Length && line[i + 1] == '-')
                    {
                        return s;
                    }

                    if (c == '/' && i + 1 < line.Length && line[i + 1] == '*')
                    {
                        s = BlockState;
                        i += 2;
                    }
                    else if (c is 'q' or 'Q' && i + 2 < line.Length && line[i + 1] == '\'' && !line[i + 2].Equals(' ') && !IsIdentBefore(line, i))
                    {
                        var open = line[i + 2];
                        s = new LexState(Lex.QQuote, open switch { '[' => ']', '{' => '}', '(' => ')', '<' => '>', _ => open });
                        i += 3;
                    }
                    else if (c == '\'')
                    {
                        s = new LexState(Lex.Quote, '\0');
                        i++;
                    }
                    else
                    {
                        i++;
                    }

                    break;
                }
            }
        }

        return s;
    }

    /// <summary><c>q</c> 앞이 식별자 글자(<c>abq'</c>)면 참. 국가 문자 접두 <c>nq'…'</c> 는 접두로 본다.</summary>
    private static bool IsIdentBefore(string line, int i)
    {
        if (i > 0 && line[i - 1] is 'n' or 'N')
        {
            i--;
        }

        return i > 0 && (char.IsLetterOrDigit(line[i - 1]) || line[i - 1] is '_' or '$' or '#');
    }

    private static readonly Regex LineHead = new(@"^\s*(?:CREATE|GRANT|REVOKE)(?![\w$#])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// 슬래시 없는 글에서 첫 단위의 머리 줄 뒤에 다른 문장(<c>CREATE</c>·<c>GRANT</c>·<c>REVOKE</c> 로 시작하는 줄)이 있는가.
    /// 주석·문자열 안은 보지 않고, 낱말 다음이 <c>:=</c>·<c>(</c>·<c>.</c>·<c>;</c> 이면 변수·호출 이름이라 문장이 아니다.
    /// </summary>
    private static bool HasOtherStatement(string[] lines, LexState[] starts)
    {
        var first = -1;
        for (var i = 0; i < lines.Length && first < 0; i++)
        {
            var t = lines[i].TrimStart();
            if (starts[i].Mode == Lex.Code && t.Length > 0 && !t.StartsWith("--", StringComparison.Ordinal) && !t.StartsWith("/*", StringComparison.Ordinal))
            {
                first = i;
            }
        }

        if (first < 0)
        {
            return false;
        }

        for (var i = first + 1; i < lines.Length; i++)
        {
            if (starts[i].Mode != Lex.Code || LineHead.Match(lines[i]) is not { Success: true } m)
            {
                continue;
            }

            var rest = lines[i][m.Length..].TrimStart();
            for (var k = i + 1; rest.Length == 0 && k < lines.Length; k++)
            {
                rest = lines[k].TrimStart();
            }

            if (rest.Length > 0 && rest[0] is not ('(' or '.' or ';') && !rest.StartsWith(":=", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>비교용 정규화: 줄끝 LF, 줄 끝 공백·문서 끝 빈 줄 제거, <c>CREATE OR REPLACE</c> 머리 제거.</summary>
    public static string ForCompare(string text)
    {
        var lines = Normalize(text).Split('\n').Select(l => l.TrimEnd()).ToList();
        while (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        var joined = string.Join('\n', lines);
        var m = Header.Match(joined);
        if (m.Success && joined.TrimStart().StartsWith("CREATE", StringComparison.OrdinalIgnoreCase))
        {
            var typeStart = m.Groups["type"].Index;
            joined = joined[typeStart..];
        }

        // 서버는 소유자(SCOTT.)를 공백으로 바꿔 저장하고 편집기는 첫 줄에 소유자를 다시 적는다(ToEditorText). 머리는 「유형 이름」으로 맞춰 비교한다.
        var h = Header.Match(joined);
        if (h.Success)
        {
            var type = Regex.Replace(h.Groups["type"].Value, @"\s+", " ");
            var name = h.Groups["b"].Success ? h.Groups["b"].Value : h.Groups["a"].Value;
            joined = type + " " + name + joined[(h.Index + h.Length)..];
        }

        return joined;
    }

    public static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    /// <summary>소스 머리에서 유형·이름을 읽는다. 읽을 수 없으면 null. 따옴표 없는 이름은 대문자로.</summary>
    public static (string ObjectType, string? Owner, string Name)? ParseHeader(string text)
    {
        var m = Header.Match(StripLeadingComments(text));
        if (!m.Success)
        {
            return null;
        }

        var type = Regex.Replace(m.Groups["type"].Value.ToUpperInvariant(), @"\s+", " ");
        var a = Unquote(m.Groups["a"].Value);
        return m.Groups["b"].Success ? (type, a, Unquote(m.Groups["b"].Value)) : (type, null, a);
    }

    /// <summary>
    /// <c>CREATE [OR REPLACE] PROCEDURE|FUNCTION|PACKAGE [BODY]|TRIGGER|TYPE [BODY] 이름</c> 문장이면 유형·이름을, 아니면 null.
    /// <see cref="ParseHeader"/> 와 달리 <c>CREATE</c> 가 없는 글(<c>TYPE BODY t AS</c>)은 받지 않는다.
    /// </summary>
    public static (string ObjectType, string? Owner, string Name)? ParseCreateHeader(string text)
    {
        var m = CreateHeader.Match(StripLeadingComments(text));
        if (!m.Success)
        {
            return null;
        }

        var type = Regex.Replace(m.Groups["type"].Value.ToUpperInvariant(), @"\s+", " ");
        var a = Unquote(m.Groups["a"].Value);
        return m.Groups["b"].Success ? (type, a, Unquote(m.Groups["b"].Value)) : (type, null, a);
    }

    /// <summary>
    /// <c>CREATE … JAVA SOURCE NAMED [소유자.]이름 … AS</c> 문장이면 (<c>JAVA SOURCE</c>, 소유자, 이름)을, 아니면 null(TSK-14-23).
    /// 이름은 사전 이름 그대로(따옴표 없는 이름은 대문자). NAMED 가 없는 꼴(이름을 Java 코드에서 정한다)은 읽지 않는다.
    /// </summary>
    public static (string ObjectType, string? Owner, string Name)? ParseJavaCreateHeader(string text)
    {
        var m = JavaCreateHeader.Match(StripLeadingComments(text));
        if (!m.Success)
        {
            return null;
        }

        var a = Unquote(m.Groups["a"].Value);
        return m.Groups["b"].Success ? ("JAVA SOURCE", a, Unquote(m.Groups["b"].Value)) : ("JAVA SOURCE", null, a);
    }

    /// <summary>
    /// 서버가 소스로 저장하는 시작 위치(유형 낱말 <c>PACKAGE BODY</c> 등). 서버 오류의 줄·열은 여기서부터 센다.
    /// 반환 = (앞선 줄 수, 그 줄 안에서 앞선 글자 수). 읽을 수 없으면 (0, 0).
    /// </summary>
    public static (int LineOffset, int ColumnOffset) SourceStart(string text)
    {
        var normalized = Normalize(text);
        var stripped = StripLeadingComments(normalized);
        if (JavaCreateHeader.Match(stripped) is { Success: true } java)
        {
            // Java 소스: 서버는 AS 뒤 Java 코드부터 줄을 센다.
            var start = normalized.Length - stripped.Length + java.Length;
            var head = normalized[..start];
            return (head.Count(c => c == '\n'), start - (head.LastIndexOf('\n') + 1));
        }

        var m = Header.Match(stripped);
        if (!m.Success)
        {
            return (0, 0);
        }

        var offset = normalized.Length - stripped.Length + m.Groups["type"].Index;
        var before = normalized[..offset];
        var line = before.Count(c => c == '\n');
        var col = offset - (before.LastIndexOf('\n') + 1);
        return (line, col);
    }

    private static string Unquote(string id) => id.StartsWith('"') ? id.Trim('"') : id.ToUpperInvariant();

    private static string StripLeadingComments(string text)
    {
        var s = text.AsSpan().TrimStart();
        while (true)
        {
            if (s.StartsWith("--"))
            {
                var nl = s.IndexOf('\n');
                s = nl < 0 ? [] : s[(nl + 1)..].TrimStart();
            }
            else if (s.StartsWith("/*"))
            {
                var end = s.IndexOf("*/");
                s = end < 0 ? [] : s[(end + 2)..].TrimStart();
            }
            else
            {
                return s.ToString();
            }
        }
    }

    /// <summary>줄 단위 차이(LCS). 내 쪽에만 있는 줄 <c>- </c>, 서버에만 있는 줄 <c>+ </c>, 같은 줄 <c>  </c>. 같으면 빈 문자열.</summary>
    public static string BuildDiff(string local, string server)
    {
        var a = ForCompare(local).Split('\n');
        var b = ForCompare(server).Split('\n');
        if (a.SequenceEqual(b))
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        var pre = 0;
        while (pre < a.Length && pre < b.Length && a[pre] == b[pre])
        {
            pre++;
        }

        var suf = 0;
        while (suf < a.Length - pre && suf < b.Length - pre && a[a.Length - 1 - suf] == b[b.Length - 1 - suf])
        {
            suf++;
        }

        for (var k = 0; k < pre; k++)
        {
            sb.Append("  ").Append(a[k]).Append('\n');
        }

        var am = a[pre..(a.Length - suf)];
        var bm = b[pre..(b.Length - suf)];
        if ((long)am.Length * bm.Length > 4_000_000)
        {
            // 너무 크면 가운데를 통째로 바뀐 것으로 본다.
            foreach (var l in am)
            {
                sb.Append("- ").Append(l).Append('\n');
            }

            foreach (var l in bm)
            {
                sb.Append("+ ").Append(l).Append('\n');
            }
        }
        else
        {
            var lcs = new int[am.Length + 1, bm.Length + 1];
            for (var i = am.Length - 1; i >= 0; i--)
            {
                for (var j = bm.Length - 1; j >= 0; j--)
                {
                    lcs[i, j] = am[i] == bm[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
                }
            }

            int x = 0, y = 0;
            while (x < am.Length || y < bm.Length)
            {
                if (x < am.Length && y < bm.Length && am[x] == bm[y])
                {
                    sb.Append("  ").Append(am[x]).Append('\n');
                    x++;
                    y++;
                }
                else if (y < bm.Length && (x == am.Length || lcs[x, y + 1] >= lcs[x + 1, y]))
                {
                    sb.Append("+ ").Append(bm[y]).Append('\n');
                    y++;
                }
                else
                {
                    sb.Append("- ").Append(am[x]).Append('\n');
                    x++;
                }
            }
        }

        for (var k = a.Length - suf; k < a.Length; k++)
        {
            sb.Append("  ").Append(a[k]).Append('\n');
        }

        return sb.ToString();
    }
}
