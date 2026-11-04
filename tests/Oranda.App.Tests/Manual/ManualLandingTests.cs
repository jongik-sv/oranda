using System.Text.RegularExpressions;

namespace Oranda.App.Tests.Manual;

/// <summary>
/// 랜딩 페이지(<c>site/src/pages/index.astro</c>)와 설명서 첫 화면(<c>ManualHome.astro</c>)은 본문 쪽 주소를 손으로 적는다.
/// 쪽 이름이나 제목이 바뀌면 조용히 404 가 되므로, 적은 주소가 본문에 실제로 있는지 검사한다.
/// </summary>
public sealed partial class ManualLandingTests
{
    private static readonly string[] Sources =
    [
        Path.Combine("site", "src", "pages", "index.astro"),
        Path.Combine("site", "src", "components", "ManualHome.astro"),
    ];

    [Fact]
    public void Links_on_the_landing_page_and_manual_home_point_to_published_pages_and_headings()
    {
        var problems = new List<string>();
        foreach (var source in Sources)
        {
            var text = File.ReadAllText(Path.Combine(ManualSite.RepoRoot, source));
            var links = PageLinkPattern().Matches(text).Select(m => m.Groups[1].Value).ToList();
            Assert.True(links.Count >= 5, $"{source} 에서 page('…') 주소를 {links.Count}개만 찾았다. 표기가 바뀌었으면 이 검출을 고친다.");
            problems.AddRange(links.Select(link => Check(source, link)).OfType<string>());
        }

        Assert.True(problems.Count == 0, "손으로 적은 설명서 주소가 본문과 맞지 않는다." + Environment.NewLine + string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void Site_root_belongs_to_the_landing_page_and_the_manual_home_moved_under_manual()
    {
        Assert.True(
            ManualSite.FindPage(string.Empty) is null,
            "site/src/content/docs/index.mdx 가 있으면 site/src/pages/index.astro 와 같은 주소를 두고 충돌한다.");
        Assert.NotNull(ManualSite.FindPage("manual"));
    }

    private static string? Check(string source, string link)
    {
        var parts = link.Split('#', 2);
        var file = ManualSite.FindPage(parts[0]);
        if (file is null)
        {
            return $"{source}: {link} → 본문에 없는 쪽";
        }

        if (ManualSite.IsDraft(file))
        {
            return $"{source}: {link} → 초안이라 게시되지 않는 쪽";
        }

        if (parts.Length == 2 && !Headings(file).Contains(parts[1]))
        {
            return $"{source}: {link} → 그 쪽에 없는 제목";
        }

        return null;
    }

    /// <summary>본문 제목을 Starlight 앵커(공백은 하이픈, 문장 부호 삭제, 소문자)로 바꾼다.</summary>
    private static HashSet<string> Headings(string file) =>
        HeadingPattern().Matches(File.ReadAllText(file))
            .Select(m => PunctuationPattern().Replace(m.Groups[1].Value.Trim().ToLowerInvariant(), string.Empty).Replace(' ', '-'))
            .ToHashSet(StringComparer.Ordinal);

    [GeneratedRegex(@"\bpage\('([^']+)'\)")]
    private static partial Regex PageLinkPattern();

    [GeneratedRegex(@"^#{2,4}\s+(.+)$", RegexOptions.Multiline)]
    private static partial Regex HeadingPattern();

    [GeneratedRegex(@"[^\p{L}\p{N}\p{M}\s_-]")]
    private static partial Regex PunctuationPattern();
}
