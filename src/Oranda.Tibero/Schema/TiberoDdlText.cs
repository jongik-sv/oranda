using System.Text;

namespace Oranda.Tibero.Schema;

/// <summary>
/// 저장 PSM 객체의 <c>*_SOURCE</c> 줄 → 생성 DDL 글 조립(세션 없는 정적 부품). Tibero 의 <c>DBMS_METADATA.GET_DDL</c> 은 TABLE 만 받으므로
/// (카탈로그 <c>meta.ddl</c> 머리말) 트리거(<see cref="TiberoTriggerDdl"/>)·프로시저·함수·패키지(명세·본문)·타입(명세·본문)은 소스 글로 만든다.
/// <list type="bullet">
/// <item>원문 첫머리는 <c>PROCEDURE 이름 …</c>·<c>PACKAGE BODY 이름 …</c> 꼴이다(Oracle 과 같다). 앞에 <c>CREATE [OR REPLACE] [EDITIONABLE|NONEDITIONABLE]</c> 가 붙어 와도 받는다.</item>
/// <item>결과는 <c>CREATE OR REPLACE 키워드 "OWNER"."NAME" …</c> 생성 문장 하나다. 이름 자리는 늘 <c>owner</c>·<c>name</c> 인자로 바꿔 쓴다(다른 스키마로 다시 만들 때 소유자를 바꿀 수 있게).</item>
/// <item>끝 공백·SQL*Plus 끝 줄(<c>/</c>)은 지운다. 끝 표시는 부르는 쪽이 <see cref="WithTerminator"/> 로 붙인다.</item>
/// </list>
/// </summary>
internal static class TiberoDdlText
{
    /// <summary>
    /// 소스 줄 → 생성 DDL. <paramref name="keyword"/> 는 원문이 시작해야 하는 낱말(<c>TRIGGER</c>, <c>PACKAGE BODY</c> 처럼 공백으로 이은 여러 낱말 가능).
    /// 줄이 없거나 원문이 <paramref name="keyword"/> 와 이름으로 시작하지 않으면 null.
    /// </summary>
    internal static string? FromSource(string keyword, string owner, string name, IReadOnlyList<string?> lines)
    {
        ArgumentException.ThrowIfNullOrEmpty(keyword);
        ArgumentException.ThrowIfNullOrEmpty(owner);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(lines);
        var text = Join(lines);
        var i = SkipSpace(text, 0);
        foreach (var word in (ReadOnlySpan<string>)["CREATE", "OR", "REPLACE", "EDITIONABLE", "NONEDITIONABLE"])
        {
            if (TryWord(text, i, word, out var next))
            {
                i = SkipSpace(text, next);
            }
        }

        foreach (var word in keyword.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!TryWord(text, i, word, out var next))
            {
                return null;
            }

            i = SkipSpace(text, next);
        }

        // 명세 키워드(PACKAGE·TYPE)에 본문 소스(PACKAGE BODY …)가 오면 BODY 를 이름으로 먹지 않게 막는다.
        if (!keyword.EndsWith("BODY", StringComparison.OrdinalIgnoreCase) && TryWord(text, i, "BODY", out _))
        {
            return null;
        }

        var rest = ReplaceLeadingName(text, i, keyword, owner, name);
        return rest is null ? null : Finish(rest);
    }

    /// <summary>
    /// 끝 표시. <paramref name="terminator"/> 면 PL/SQL 객체 관례대로 새 줄에 <c>/</c> 를 붙인다(Oracle 의 Java 소스 DDL 과 같은 모양:
    /// 소스 안의 <c>;</c> 는 문장 끝이 아니라서 SQL*Plus 가 <c>/</c> 로 끝을 안다). 아니면 그대로.
    /// </summary>
    internal static string WithTerminator(string ddl, bool terminator)
    {
        ArgumentNullException.ThrowIfNull(ddl);
        return terminator ? ddl + "\n/" : ddl;
    }

    /// <summary>따옴표로 감싼 <c>"OWNER"."NAME"</c>(안의 따옴표는 두 번).</summary>
    internal static string Qualified(string owner, string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(owner);
        ArgumentException.ThrowIfNullOrEmpty(name);
        return Quote(owner) + "." + Quote(name);
    }

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    /// <summary>줄을 잇는다. 줄 끝 줄바꿈이 없는 줄에는 붙인다(사전 뷰마다 다르다).</summary>
    private static string Join(IReadOnlyList<string?> lines)
    {
        var text = new StringBuilder();
        foreach (var line in lines)
        {
            if (line is null)
            {
                continue;
            }

            text.Append(line);
            if (!line.EndsWith('\n'))
            {
                text.Append('\n');
            }
        }

        return text.ToString();
    }

    /// <summary><paramref name="start"/> 의 (스키마로 한정됐을 수 있는) 이름을 <c>"OWNER"."NAME"</c> 로 바꾼 <c>CREATE OR REPLACE 키워드 …</c> 글.</summary>
    private static string? ReplaceLeadingName(string text, int start, string keyword, string owner, string name)
    {
        var end = SkipIdentifierPath(text, start);
        return end == start ? null : "CREATE OR REPLACE " + keyword + " " + Qualified(owner, name) + text[end..];
    }

    /// <summary><c>a</c>, <c>"a"</c>, <c>a.b</c>, <c>"a"."b"</c> 모양의 이름 끝 위치(이름이 없으면 <paramref name="start"/>).</summary>
    private static int SkipIdentifierPath(string text, int start)
    {
        var i = start;
        while (true)
        {
            var partEnd = SkipIdentifier(text, i);
            if (partEnd == i)
            {
                return i == start ? start : i - 1; // 점 뒤에 이름이 없으면 점 앞까지
            }

            i = partEnd;
            if (i < text.Length && text[i] == '.')
            {
                i++;
                continue;
            }

            return i;
        }
    }

    private static int SkipIdentifier(string text, int i)
    {
        if (i < text.Length && text[i] == '"')
        {
            var j = i + 1;
            while (j < text.Length)
            {
                if (text[j] == '"')
                {
                    if (j + 1 < text.Length && text[j + 1] == '"')
                    {
                        j += 2;
                        continue;
                    }

                    return j + 1;
                }

                j++;
            }

            return i; // 닫히지 않은 따옴표
        }

        var k = i;
        while (k < text.Length && (char.IsLetterOrDigit(text[k]) || text[k] is '_' or '$' or '#'))
        {
            k++;
        }

        return k;
    }

    private static int SkipSpace(string text, int i)
    {
        while (i < text.Length && char.IsWhiteSpace(text[i]))
        {
            i++;
        }

        return i;
    }

    /// <summary><paramref name="i"/> 에 낱말 <paramref name="word"/>(대소문자 무시, 뒤가 이름 글자가 아님)가 있으면 그 끝.</summary>
    private static bool TryWord(string text, int i, string word, out int end)
    {
        end = i + word.Length;
        return end <= text.Length
            && string.Compare(text, i, word, 0, word.Length, StringComparison.OrdinalIgnoreCase) == 0
            && (end == text.Length || !(char.IsLetterOrDigit(text[end]) || text[end] is '_' or '$' or '#' or '"'));
    }

    /// <summary>끝 공백·SQL*Plus 끝 줄(<c>/</c>)을 지운다.</summary>
    private static string Finish(string sql)
    {
        var trimmed = sql.TrimEnd();
        if (trimmed.EndsWith("\n/", StringComparison.Ordinal))
        {
            trimmed = trimmed[..^2].TrimEnd();
        }

        return trimmed;
    }
}
