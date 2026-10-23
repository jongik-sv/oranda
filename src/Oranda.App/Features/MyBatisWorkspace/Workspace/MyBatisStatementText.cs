using System.Text.RegularExpressions;

namespace Oranda.App.Features.MyBatisWorkspace.Workspace;

/// <summary>
/// 문장 원문(여는 태그 ~ 닫는 태그) 글자 도우미: id 속성 찾기·바꾸기, 새 문장 틀, 동적 태그 수, include 수, 탭 ↔ 파일 들여쓰기(Dedent·Reindent).
/// 탭에서 새로 쓴 줄은 CDATA·여러 줄 문자열 안에 있어도 저장 때 파일 들여쓰기가 붙는다(바이트 왕복 보장이 우선, 지시 mybatis-indent-1).
/// </summary>
public static partial class MyBatisStatementText
{
    private static readonly string[] DynamicTags = ["if", "choose", "when", "otherwise", "foreach", "where", "set", "trim", "bind"];

    /// <summary>MyBatis 문장 id 로 받는 모양(영문·숫자·밑줄, 첫 글자는 숫자가 아님). 점은 namespace 구분이라 받지 않는다.</summary>
    public static bool IsValidId(string? id) => id is not null && IdPattern().IsMatch(id);

    /// <summary>
    /// 첫 요소 여는 태그의 id 속성 값 범위(따옴표 안). 앞쪽 공백·주석·처리 지시(<c>&lt;?…?&gt;</c>)는 건너뛰고, 여는 태그는 따옴표를 고려해 끝까지 읽는다
    /// (속성 값 안의 <c>&gt;</c> 에서 끊기지 않게). 찾지 못하면 false.
    /// </summary>
    public static bool TryFindIdValue(string text, out int start, out int length)
    {
        ArgumentNullException.ThrowIfNull(text);
        start = 0;
        length = 0;
        if (FirstOpenTag(text) is not var (open, close) || close < 0)
        {
            return false;
        }

        var m = IdAttribute().Match(text, open, close - open);
        if (!m.Success)
        {
            return false;
        }

        var value = m.Groups["v"];
        start = value.Index;
        length = value.Length;
        return true;
    }

    /// <summary>첫 요소 이름으로 본 종류(select·insert·update·delete·sql). 그 밖이면 null.</summary>
    public static MyBatisStatementKind? KindOfText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (FirstOpenTag(text) is not var (open, _))
        {
            return null;
        }

        var name = OpenTag().Match(text, open) is { Success: true } m ? m.Groups["n"].Value : string.Empty;
        return name switch
        {
            "select" => MyBatisStatementKind.Select,
            "insert" => MyBatisStatementKind.Insert,
            "update" => MyBatisStatementKind.Update,
            "delete" => MyBatisStatementKind.Delete,
            "sql" => MyBatisStatementKind.Sql,
            _ => null,
        };
    }

    /// <summary>첫 요소 여는 태그의 <c>&lt;</c> 위치와 닫는 <c>&gt;</c> 위치(없으면 -1). 요소가 없으면 null.</summary>
    private static (int Open, int Close)? FirstOpenTag(string text)
    {
        int i = 0;
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                i++;
                continue;
            }

            if (text.AsSpan(i).StartsWith("<!--", StringComparison.Ordinal))
            {
                int end = text.IndexOf("-->", i + 4, StringComparison.Ordinal);
                i = end < 0 ? text.Length : end + 3;
                continue;
            }

            if (text.AsSpan(i).StartsWith("<?", StringComparison.Ordinal))
            {
                int end = text.IndexOf("?>", i + 2, StringComparison.Ordinal);
                i = end < 0 ? text.Length : end + 2;
                continue;
            }

            if (text[i] != '<' || i + 1 >= text.Length || !char.IsLetter(text[i + 1]))
            {
                return null; // 요소 앞에 다른 글이 있다
            }

            char quote = '\0';
            for (int k = i + 1; k < text.Length; k++)
            {
                char c = text[k];
                if (quote != '\0')
                {
                    quote = c == quote ? '\0' : quote;
                }
                else if (c is '"' or '\'')
                {
                    quote = c;
                }
                else if (c == '>')
                {
                    return (i, k);
                }
            }

            return (i, -1);
        }

        return null;
    }

    /// <summary>여는 태그의 id 속성 값만 <paramref name="newId"/> 로 바꾼 글. id 속성이 없으면 그대로.</summary>
    public static string RenameId(string text, string newId)
    {
        ArgumentNullException.ThrowIfNull(text);
        return TryFindIdValue(text, out var start, out var length)
            ? string.Concat(text.AsSpan(0, start), newId, text.AsSpan(start + length))
            : text;
    }

    /// <summary>
    /// 「+ 새 문장」 틀: 빈 줄 하나를 품은 요소. <paramref name="newLine"/> 은 매퍼의 줄 끝, <paramref name="indent"/> 는 매퍼 요소의 들여쓰기다.
    /// 02-01 Apply 는 넣는 요소의 첫 줄만 들여 쓰므로 둘째 줄부터 직접 들여 쓴다.
    /// </summary>
    public static string NewStatement(MyBatisStatementKind kind, string id, string newLine, string indent = "")
    {
        var name = MyBatisStatementEntry.KindName(kind);
        return $"<{name} id=\"{id}\">{newLine}{indent}  {newLine}{indent}</{name}>";
    }

    /// <summary>
    /// 매퍼 원문 <paramref name="text"/> 의 <paramref name="start"/>(요소 여는 태그의 <c>&lt;</c>) 앞 같은 줄 공백.
    /// 줄 시작 ~ <c>&lt;</c> 가 공백·탭뿐일 때 그 원문, 아니면 빈 글(02-01 Apply 의 들여쓰기 규칙과 같다).
    /// </summary>
    public static string IndentBefore(string text, int start)
    {
        ArgumentNullException.ThrowIfNull(text);
        start = Math.Clamp(start, 0, text.Length);
        int lineStart = start == 0 ? 0 : text.AsSpan(0, start).LastIndexOfAny('\r', '\n') + 1;
        for (int k = lineStart; k < start; k++)
        {
            if (text[k] is not (' ' or '\t'))
            {
                return string.Empty;
            }
        }

        return text[lineStart..start];
    }

    /// <summary>
    /// 파일 → 탭: 둘째 줄부터 <paramref name="indent"/> 로 시작하는 줄에서 그 접두를 뗀다. 첫 줄(여는 태그 줄)은 그대로.
    /// 줄 끝(CRLF·LF·CR, Apply 의 줄 나눔과 같다)과 탭 문자는 원문 그대로 둔다.
    /// </summary>
    public static string Dedent(string text, string indent)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(indent);
        LineEnd(text, 0, out int i);
        if (indent.Length == 0 || i >= text.Length)
        {
            return text;
        }

        var sb = new System.Text.StringBuilder(text.Length);
        sb.Append(text, 0, i);
        while (i < text.Length)
        {
            int content = LineEnd(text, i, out int next);
            int from = text.AsSpan(i, content - i).StartsWith(indent, StringComparison.Ordinal) ? i + indent.Length : i;
            sb.Append(text, from, next - from);
            i = next;
        }

        return sb.ToString();
    }

    /// <summary>
    /// 탭 → 파일: 둘째 줄부터 빈 줄(줄 끝을 뺀 길이 0)을 뺀 모든 줄 앞에 <paramref name="indent"/> 를 붙인다. 첫 줄은 Apply·원래 자리가 들여 쓴다.
    /// 탭에서 새로 쓴 줄도 이 규칙대로 접두가 붙는다(CDATA·여러 줄 문자열 안 줄 포함).
    /// </summary>
    public static string Reindent(string text, string indent)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(indent);
        LineEnd(text, 0, out int i);
        if (indent.Length == 0 || i >= text.Length)
        {
            return text;
        }

        var sb = new System.Text.StringBuilder(text.Length + 64);
        sb.Append(text, 0, i);
        while (i < text.Length)
        {
            int content = LineEnd(text, i, out int next);
            if (content > i)
            {
                sb.Append(indent);
            }

            sb.Append(text, i, next - i);
            i = next;
        }

        return sb.ToString();
    }

    /// <summary><paramref name="start"/> 줄의 내용 끝(줄 끝 앞). <paramref name="next"/> = 다음 줄 시작(줄 끝이 없으면 글 끝). CRLF·LF·CR 을 줄 끝으로 본다.</summary>
    private static int LineEnd(string text, int start, out int next)
    {
        int at = text.AsSpan(start).IndexOfAny('\r', '\n');
        if (at < 0)
        {
            next = text.Length;
            return text.Length;
        }

        at += start;
        next = at + (text[at] == '\r' && at + 1 < text.Length && text[at + 1] == '\n' ? 2 : 1);
        return at;
    }

    /// <summary>
    /// <paramref name="indent"/> 로 Dedent 한 글이 Reindent 로 원문 바이트 그대로 돌아오면 true 와 그 글.
    /// 접두가 없거나 돌아오지 않으면(덜 들여 쓴 줄·공백만 있는 줄 등) false 와 원문 그대로 — 그 요소는 원문 그대로 보인다.
    /// </summary>
    public static bool TryDedent(string text, string indent, out string dedented)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(indent);
        dedented = text;
        if (indent.Length == 0)
        {
            return false;
        }

        var cut = Dedent(text, indent);
        if (!string.Equals(Reindent(cut, indent), text, StringComparison.Ordinal))
        {
            return false;
        }

        dedented = cut;
        return true;
    }

    /// <summary>
    /// 동적 태그(if·choose·when·otherwise·foreach·where·set·trim·bind) 여는 태그 수. include 는 조각 끼우기라 세지 않는다
    /// (시안 selectEmpList = 5).
    /// </summary>
    public static int CountDynamicTags(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        int count = 0;
        foreach (Match m in OpenTag().Matches(text))
        {
            if (Array.IndexOf(DynamicTags, m.Groups["n"].Value) >= 0)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary><paramref name="text"/> 안에서 sql 조각 <paramref name="fragmentId"/> 를 끼우는 include 수(<c>refid="id"</c> 또는 <c>refid="ns.id"</c>).</summary>
    public static int CountIncludes(string text, string fragmentId, string? mapperNamespace)
    {
        ArgumentNullException.ThrowIfNull(text);
        int count = 0;
        foreach (Match m in IncludeRef().Matches(text))
        {
            var refid = m.Groups["r"].Value;
            if (string.Equals(refid, fragmentId, StringComparison.Ordinal)
                || (mapperNamespace is not null && refid.Length == mapperNamespace.Length + 1 + fragmentId.Length
                    && refid.StartsWith(mapperNamespace, StringComparison.Ordinal) && refid[mapperNamespace.Length] == '.'
                    && refid.EndsWith(fragmentId, StringComparison.Ordinal)))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary><paramref name="text"/> 안의 include 를 같은 매퍼 조각 id 별로 <paramref name="counts"/> 에 더한다(다른 namespace 를 가리키는 것은 뺀다).</summary>
    public static void AddIncludes(string text, string? mapperNamespace, Dictionary<string, int> counts)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(counts);
        foreach (Match m in IncludeRef().Matches(text))
        {
            var refid = m.Groups["r"].Value;
            if (mapperNamespace is not null && refid.Length > mapperNamespace.Length + 1
                && refid.StartsWith(mapperNamespace, StringComparison.Ordinal) && refid[mapperNamespace.Length] == '.')
            {
                refid = refid[(mapperNamespace.Length + 1)..];
            }

            if (!refid.Contains('.', StringComparison.Ordinal))
            {
                counts[refid] = counts.GetValueOrDefault(refid) + 1;
            }
        }
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*\\z")]
    private static partial Regex IdPattern();

    [GeneratedRegex("""\sid\s*=\s*(?<q>["'])(?<v>[^"']*)\k<q>""")]
    private static partial Regex IdAttribute();

    [GeneratedRegex("<(?<n>[A-Za-z]+)\\b")]
    private static partial Regex OpenTag();

    [GeneratedRegex("""<include\b[^>]*\brefid\s*=\s*(["'])(?<r>[^"']*)\1""")]
    private static partial Regex IncludeRef();
}
