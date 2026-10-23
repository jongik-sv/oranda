using System.Text.RegularExpressions;

namespace Oranda.App.Features.MyBatisWorkspace.Workspace;

/// <summary>문장 원문(여는 태그 ~ 닫는 태그) 글자 도우미: id 속성 찾기·바꾸기, 새 문장 틀, 동적 태그 수, include 수.</summary>
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

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex IdPattern();

    [GeneratedRegex("""\sid\s*=\s*(?<q>["'])(?<v>[^"']*)\k<q>""")]
    private static partial Regex IdAttribute();

    [GeneratedRegex("<(?<n>[A-Za-z]+)\\b")]
    private static partial Regex OpenTag();

    [GeneratedRegex("""<include\b[^>]*\brefid\s*=\s*(["'])(?<r>[^"']*)\1""")]
    private static partial Regex IncludeRef();
}
