using System.Text.RegularExpressions;

namespace Oranda.Core.Ai.Manual;

/// <summary>
/// Oranda 매뉴얼 색인(2단계 F3, 계약 §8.5). <c>orandactl docs</c>·스킬 본문이 기능 질문에 매뉴얼 링크로 답할 때 쓴다.
/// 앱은 실행 중 <c>site/</c> 를 갖지 않으므로 표를 코드에 둔다. 표(<c>OrandaManualIndex.cs</c>)는 <c>tools/manual-index/gen.py</c> 가
/// <c>site/src/content/docs/**</c> 머리말·h2 에서 만들고, Agent.Cli 시험이 site 와 맞는지 확인한다. CLI 는 Core 만 참조하므로 Core 에 둔다.
/// </summary>
public static partial class OrandaManual
{
    /// <summary>매뉴얼 사이트 주소(<c>site/astro.config.mjs</c> 의 site + base). 끝에 <c>/</c> 없음.</summary>
    public const string BaseUrl = "https://jongik-sv.github.io/oranda";

    /// <summary>색인 한 줄.</summary>
    /// <param name="Title">문서 제목(머리말 <c>title</c>).</param>
    /// <param name="Description">한 줄 설명(머리말 <c>description</c>).</param>
    /// <param name="Url">절대 주소(<see cref="BaseUrl"/> 로 시작, 끝 <c>/</c>).</param>
    /// <param name="Section">장 이름(<c>docs</c> 아래 첫 폴더, 예 <c>run</c>·<c>plan</c>·<c>ai</c>).</param>
    public sealed record Entry(string Title, string Description, string Url, string Section)
    {
        /// <summary>쪽의 h2 제목들(본문 순서). 검색에만 쓴다(E-28, 스킬 본문에는 넣지 않는다).</summary>
        public IReadOnlyList<string> Headings { get; init; } = [];

        /// <summary>h2 하나로 가는 주소(<see cref="Url"/> + <c>#</c> + <see cref="Anchor"/>).</summary>
        public string HeadingUrl(string heading) => Url + "#" + Anchor(heading);
    }

    /// <summary>찾은 쪽 하나와 검색어에 걸린 h2 제목들.</summary>
    public sealed record Hit(Entry Entry, IReadOnlyList<string> Headings);

    /// <summary>색인 전부(사이드바 순서). 생성된 표를 그대로 돌려준다(정적 초기화 순서에 기대지 않게 속성 본문으로 읽는다).</summary>
    public static IReadOnlyList<Entry> All => Generated;

    /// <summary><see cref="All"/> 에서 찾는다(<see cref="Search(string?, IReadOnlyList{Entry}, int)"/>).</summary>
    public static IReadOnlyList<Entry> Search(string? query, int limit = 10) => Search(query, All, limit);

    /// <summary>
    /// 검색어의 낱말이 모두 제목·설명·장 이름 어딘가에 있는 줄(대소문자 무시, 표 순서). 빈 검색어 = 전부. 최대 <paramref name="limit"/> 줄.
    /// </summary>
    public static IReadOnlyList<Entry> Search(string? query, IReadOnlyList<Entry> entries, int limit = 10)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var words = Words(query);
        return [.. entries.Where(e => words.All(w => InPage(e, w))).Take(limit)];
    }

    /// <summary>
    /// <c>orandactl docs</c> 검색: 낱말이 모두 그 쪽(제목·설명·장 이름·h2 제목) 어딘가에 있는 쪽. 제목·설명·장 이름만으로 다 맞는 쪽을 먼저,
    /// h2 까지 봐야 맞는 쪽을 뒤에 둔다(각각 표 순서). 걸린 h2 = 낱말 하나라도 들어 있는 h2. 빈 검색어 = 모든 쪽(h2 없이).
    /// </summary>
    public static IReadOnlyList<Hit> Find(string? query, int limit = 10) => Find(query, All, limit);

    /// <summary><see cref="Find(string?, int)"/> 를 주어진 표에서.</summary>
    public static IReadOnlyList<Hit> Find(string? query, IReadOnlyList<Entry> entries, int limit = 10)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var words = Words(query);
        if (words.Length == 0)
        {
            return [.. entries.Take(limit).Select(e => new Hit(e, []))];
        }

        var page = new List<Hit>();
        var deep = new List<Hit>();
        foreach (var entry in entries)
        {
            var headings = entry.Headings.Where(h => words.Any(w => Has(h, w))).ToList();
            if (words.All(w => InPage(entry, w)))
            {
                page.Add(new Hit(entry, headings));
            }
            else if (words.All(w => InPage(entry, w) || entry.Headings.Any(h => Has(h, w))))
            {
                deep.Add(new Hit(entry, headings));
            }
        }

        return [.. page.Concat(deep).Take(limit)];
    }

    /// <summary>
    /// h2 제목의 Starlight 앵커: 소문자, 글자·숫자·결합 부호·공백·<c>_</c>·<c>-</c> 밖의 문자 삭제, 공백 → <c>-</c>
    /// (App.Tests 의 매뉴얼 링크 시험 규칙과 같다. 예 「접속의 AI 허용 수준」 → <c>접속의-ai-허용-수준</c>).
    /// </summary>
    public static string Anchor(string heading)
    {
        ArgumentNullException.ThrowIfNull(heading);
        return Punctuation().Replace(heading.Trim().ToLowerInvariant(), string.Empty).Replace(' ', '-');
    }

    private static string[] Words(string? query) => (query ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    private static bool InPage(Entry entry, string word) => Has(entry.Title, word) || Has(entry.Description, word) || Has(entry.Section, word);

    private static bool Has(string text, string word) => text.Contains(word, StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"[^\p{L}\p{N}\p{M}\s_-]")]
    private static partial Regex Punctuation();
}
