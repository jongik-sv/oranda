using System.Text;

namespace Oranda.Tibero.Schema;

/// <summary>
/// Tibero 동의어가 가리키는 곳(소유자·이름·DB 링크). Tibero <c>*_synonyms</c> 에는 db_link 열이 없고, 원격 동의어는 대상 이름 열(<c>org_object_name</c>)에
/// <c>"이름"@링크</c> 처럼 이름과 링크를 함께 적는다(2026-11-21 실측, 따옴표 없는 <c>이름@링크</c> 도 받는다).
/// 이름 풀이 한 단계(<c>meta.name_step</c>)·동의어 DDL 이 이 규칙으로 읽는다. 표식 질의(<c>meta.marks_synonyms</c>)는 같은 규칙을 SQL 로 쓴다.
/// </summary>
internal readonly record struct TiberoSynonymTarget(string? Owner, string Name, string? DbLink)
{
    /// <summary>
    /// 사전 두 열 → 대상. 따옴표 밖의 <c>@</c> 가 있으면 그 뒤가 링크, 앞이 이름(따옴표를 벗긴다, 안의 <c>""</c> 는 <c>"</c>).
    /// 이름 글에 <c>소유자.이름</c> 이 들어 있으면 그 소유자가 앞선다. <c>@</c> 가 없으면 두 값 그대로(로컬 동의어는 지금 그대로 읽는다).
    /// </summary>
    public static TiberoSynonymTarget Parse(string? owner, string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var at = LinkSign(name);
        if (at < 0)
        {
            return new(owner, name, null);
        }

        var link = name[(at + 1)..].Trim();
        var parts = Split(name[..at].Trim());
        return parts.Count switch
        {
            0 => new(owner, name, null),
            1 => new(owner, parts[0], link.Length > 0 ? Unquote(link) : null),
            _ => new(parts[^2], parts[^1], link.Length > 0 ? Unquote(link) : null),
        };
    }

    /// <summary>따옴표 밖의 마지막 <c>@</c> 자리. 없으면 -1.</summary>
    private static int LinkSign(string text)
    {
        var quoted = false;
        var at = -1;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '"')
            {
                quoted = !quoted; // 안의 "" 는 두 번 뒤집혀 그대로다
            }
            else if (text[i] == '@' && !quoted)
            {
                at = i;
            }
        }

        return at;
    }

    /// <summary>따옴표 밖의 점으로 나누고 조각마다 따옴표를 벗긴다.</summary>
    private static List<string> Split(string text)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        foreach (var c in text)
        {
            if (c == '"')
            {
                quoted = !quoted;
            }

            if (c == '.' && !quoted)
            {
                parts.Add(Unquote(current.ToString().Trim()));
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0)
        {
            parts.Add(Unquote(current.ToString().Trim()));
        }

        return parts;
    }

    private static string Unquote(string part) =>
        part.Length >= 2 && part[0] == '"' && part[^1] == '"' ? part[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal) : part;
}
