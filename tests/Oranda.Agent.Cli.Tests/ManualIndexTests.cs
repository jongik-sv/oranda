using System.Text.RegularExpressions;
using Oranda.Core.Ai.Manual;

namespace Oranda.Agent.Cli.Tests;

/// <summary>
/// 매뉴얼 색인(<see cref="OrandaManual.All"/>)이 <c>site/src/content/docs</c> 와 맞는지(E-27·E-28). 본문 머리말·h2 를 바꾸면 이 시험이 깨진다 —
/// 리포 루트에서 <c>python3 tools/manual-index/gen.py</c> 를 다시 돌린다. 규칙은 생성기 머리 설명과 같다.
/// </summary>
public sealed partial class ManualIndexTests
{
    private const string Regenerate = "리포 루트에서 python3 tools/manual-index/gen.py 를 돌려 OrandaManualIndex.cs 를 다시 만든다";

    [Fact]
    public void Index_matches_the_site_pages()
    {
        var expected = SitePages().OrderBy(e => e.Url, StringComparer.Ordinal).Select(Line).ToList();
        var actual = OrandaManual.All.OrderBy(e => e.Url, StringComparer.Ordinal).Select(Line).ToList();

        var missing = expected.Except(actual).ToList();
        var extra = actual.Except(expected).ToList();
        Assert.True(missing.Count == 0 && extra.Count == 0,
            $"{Regenerate}.\n색인에 없음:\n{string.Join('\n', missing)}\n색인에만 있음:\n{string.Join('\n', extra)}");
    }

    [Fact]
    public void Urls_use_the_published_site_root_and_end_with_a_slash()
    {
        var root = SiteRoot();
        Assert.All(OrandaManual.All, e =>
        {
            Assert.StartsWith(root + "/", e.Url, StringComparison.Ordinal);
            Assert.EndsWith("/", e.Url, StringComparison.Ordinal);
        });
        Assert.Equal(OrandaManual.BaseUrl, root);
        Assert.Contains(OrandaManual.All, e => e.Url == root + "/manual/");
        Assert.Contains(OrandaManual.All, e => e.Url == root + "/ai/external-agents/");
        Assert.DoesNotContain(OrandaManual.All, e => e.Url.Contains("/index/", StringComparison.Ordinal) || e.Url.Contains(".md", StringComparison.Ordinal));
    }

    private static string Line(OrandaManual.Entry e) => $"{e.Url} | {e.Section} | {e.Title} | {e.Description} | {string.Join(" / ", e.Headings)}";

    private static IEnumerable<OrandaManual.Entry> SitePages()
    {
        var docs = Path.Combine(RepoRoot(), "site", "src", "content", "docs");
        foreach (var file in Directory.EnumerateFiles(docs, "*.*", SearchOption.AllDirectories)
                     .Where(f => f.EndsWith(".md", StringComparison.Ordinal) || f.EndsWith(".mdx", StringComparison.Ordinal)))
        {
            var lines = File.ReadAllText(file).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            var end = Array.IndexOf(lines, "---", 1);
            var meta = lines[1..end]
                .Select(l => TopKey().Match(l))
                .Where(m => m.Success)
                .ToDictionary(m => m.Groups[1].Value, m => Unquote(m.Groups[2].Value), StringComparer.Ordinal);
            if (meta.TryGetValue("draft", out var draft) && draft == "true")
            {
                continue;
            }

            var relative = Path.GetRelativePath(docs, file).Replace(Path.DirectorySeparatorChar, '/');
            var slug = IndexSuffix().Replace(Extension().Replace(relative, string.Empty), string.Empty);
            var folder = relative.Contains('/', StringComparison.Ordinal) ? relative[..relative.IndexOf('/', StringComparison.Ordinal)] : string.Empty;
            yield return new OrandaManual.Entry(
                meta["title"],
                meta.GetValueOrDefault("description", string.Empty),
                SiteRoot() + "/" + (slug.Length > 0 ? slug + "/" : string.Empty),
                folder)
            {
                Headings = Headings(lines[(end + 1)..]),
            };
        }
    }

    /// <summary>코드 블록 밖의 <c>## </c> 줄(CommonMark 울타리: 같은 글자 3개 이상, 같은 글자를 그 이상 늘어놓은 빈 줄로 닫는다).</summary>
    private static string[] Headings(IEnumerable<string> body)
    {
        var result = new List<string>();
        string? fence = null;
        foreach (var line in body)
        {
            var m = Fence().Match(line);
            if (m.Success)
            {
                var run = m.Groups[1].Value;
                if (fence is null)
                {
                    fence = run;
                }
                else if (run[0] == fence[0] && run.Length >= fence.Length && m.Groups[2].Value.Trim().Length == 0)
                {
                    fence = null;
                }

                continue;
            }

            if (fence is null && line.StartsWith("## ", StringComparison.Ordinal))
            {
                result.Add(line[3..].Trim());
            }
        }

        return [.. result];
    }

    private static string SiteRoot()
    {
        var config = Config();
        return SiteKey().Match(config).Groups[1].Value.TrimEnd('/') + "/" + BaseKey().Match(config).Groups[1].Value.Trim('/');
    }

    private static string Config() => File.ReadAllText(Path.Combine(RepoRoot(), "site", "astro.config.mjs"));

    private static string Unquote(string value)
    {
        value = value.Trim();
        return value.Length >= 2 && value[0] == value[^1] && value[0] is '\'' or '"'
            ? value[0] == '\'' ? value[1..^1].Replace("''", "'", StringComparison.Ordinal) : value[1..^1].Replace("\\\"", "\"", StringComparison.Ordinal)
            : value;
    }

    /// <summary>저장소 루트 = <c>Oranda.sln</c> 이 있는 폴더(빌드 출력 위치를 박지 않는다).</summary>
    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Oranda.sln")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("저장소 루트(Oranda.sln)를 찾지 못했다.");
    }

    [GeneratedRegex(@"^([A-Za-z_]+):\s*(.*)$")]
    private static partial Regex TopKey();

    [GeneratedRegex(@"\.mdx?$")]
    private static partial Regex Extension();

    [GeneratedRegex(@"(^|/)index$")]
    private static partial Regex IndexSuffix();

    [GeneratedRegex(@"^ {0,3}(`{3,}|~{3,})(.*)$")]
    private static partial Regex Fence();

    [GeneratedRegex(@"site:\s*'([^']+)'")]
    private static partial Regex SiteKey();

    [GeneratedRegex(@"base:\s*'([^']+)'")]
    private static partial Regex BaseKey();
}
