namespace Oranda.App.Tests.Manual;

/// <summary>매뉴얼 사이트(<c>site/</c>) 본문을 시험에서 찾는 도우미. 저장소 루트는 <c>Oranda.sln</c> 이 있는 폴더다.</summary>
internal static class ManualSite
{
    private static readonly Lazy<string> Root = new(FindRoot);

    /// <summary>
    /// 아직 초안(<c>draft: true</c>)인 쪽을 F1 표·범위표가 가리켜도 되는지. 초안은 게시 빌드에서 빠지므로 가리키면 404 다.
    /// 본문을 다 쓰는 마지막 단계(M6)에서 false 로 바꾼다.
    /// </summary>
    internal const bool AllowDraftPages = true;

    /// <summary>저장소 루트.</summary>
    internal static string RepoRoot => Root.Value;

    /// <summary>본문 폴더(<c>site/src/content/docs</c>).</summary>
    internal static string DocsDir => Path.Combine(RepoRoot, "site", "src", "content", "docs");

    /// <summary>절 경로(예: <c>run/result-grid</c>)에 해당하는 본문 파일. 없으면 null.</summary>
    internal static string? FindPage(string path)
    {
        var relative = path.Trim('/').Replace('/', Path.DirectorySeparatorChar);
        foreach (var candidate in new[]
        {
            relative + ".md",
            relative + ".mdx",
            Path.Combine(relative, "index.md"),
            Path.Combine(relative, "index.mdx"),
        })
        {
            var full = Path.Combine(DocsDir, candidate);
            if (File.Exists(full))
            {
                return full;
            }
        }

        return null;
    }

    /// <summary>본문 머리말에 <c>draft: true</c> 가 있으면 true(아직 게시하지 않는 쪽).</summary>
    internal static bool IsDraft(string file)
    {
        var inFrontMatter = false;
        foreach (var line in File.ReadLines(file))
        {
            if (line.Trim() == "---")
            {
                if (inFrontMatter)
                {
                    return false;
                }

                inFrontMatter = true;
                continue;
            }

            if (inFrontMatter && System.Text.RegularExpressions.Regex.IsMatch(line.Trim(), @"^draft:\s*true\b"))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>본문 파일이 참조하는 이미지(<c>../../../assets/screens/x.png</c>)의 파일 이름들.</summary>
    internal static IEnumerable<string> ScreenImages(string file) =>
        System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(file), @"\]\((?:\.\./)+assets/screens/([\w.-]+\.png)\)")
            .Select(m => m.Groups[1].Value);

    private static string FindRoot()
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
}
