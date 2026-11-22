namespace Oranda.Core.Ai.Manual;

/// <summary>
/// Oranda 매뉴얼 색인(2단계 F3, 계약 C0). <c>orandactl docs</c>·스킬 본문이 기능 질문에 매뉴얼 링크로 답할 때 쓴다.
/// 앱은 실행 중 <c>site/</c> 를 갖지 않으므로 표를 코드에 둔다. C0 는 빈 껍데기이고 표(<see cref="All"/>)는 m-skill-docs(S1)가
/// <c>site/src/content/docs/**</c> 머리말에서 만들어 채운다. CLI 는 Core 만 참조하므로 Core 에 둔다.
/// </summary>
public static class OrandaManual
{
    /// <summary>매뉴얼 사이트 주소(<c>site/astro.config.mjs</c> 의 site + base). 끝에 <c>/</c> 없음.</summary>
    public const string BaseUrl = "https://jongik-sv.github.io/oranda";

    /// <summary>색인 한 줄.</summary>
    /// <param name="Title">문서 제목(머리말 <c>title</c>).</param>
    /// <param name="Description">한 줄 설명(머리말 <c>description</c>).</param>
    /// <param name="Url">절대 주소(<see cref="BaseUrl"/> 로 시작).</param>
    /// <param name="Section">장 이름(<c>docs</c> 아래 첫 폴더, 예 <c>run</c>·<c>plan</c>·<c>ai</c>).</param>
    public sealed record Entry(string Title, string Description, string Url, string Section);

    /// <summary>색인 전부. C0 = 빈 목록.</summary>
    public static IReadOnlyList<Entry> All { get; } = [];

    /// <summary><see cref="All"/> 에서 찾는다(<see cref="Search(string?, IReadOnlyList{Entry}, int)"/>).</summary>
    public static IReadOnlyList<Entry> Search(string? query, int limit = 10) => Search(query, All, limit);

    /// <summary>
    /// 검색어의 낱말이 모두 제목·설명·장 이름 어딘가에 있는 줄(대소문자 무시, 표 순서). 빈 검색어 = 전부. 최대 <paramref name="limit"/> 줄.
    /// </summary>
    public static IReadOnlyList<Entry> Search(string? query, IReadOnlyList<Entry> entries, int limit = 10)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var words = (query ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var found = new List<Entry>();
        foreach (var entry in entries)
        {
            if (found.Count >= limit)
            {
                break;
            }

            if (words.All(w => Has(entry.Title, w) || Has(entry.Description, w) || Has(entry.Section, w)))
            {
                found.Add(entry);
            }
        }

        return found;
    }

    private static bool Has(string text, string word) => text.Contains(word, StringComparison.OrdinalIgnoreCase);
}
