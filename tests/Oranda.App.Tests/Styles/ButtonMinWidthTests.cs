using System.Xml.Linq;

namespace Oranda.App.Tests.Styles;

/// <summary>대화상자 단추가 MinWidth 를 따로 박으면 App.axaml 공통 최소 폭(84)과 어긋나므로 axaml 의 Button 요소에서 금지한다.</summary>
public class ButtonMinWidthTests
{
    // 공통 일반 단추 스타일이 적용되지 않는 클래스(툴바·도구 단추·팔레트 칸·시작 목록)는 자기 크기를 직접 정한다.
    private static readonly string[] Exempt = ["palette-box", "toolbar", "tool", "start-item"];

    [Fact]
    public void Axaml_buttons_do_not_hardcode_MinWidth_except_self_sized_classes()
    {
        var srcDir = Path.Combine(RepoRoot(), "src", "Oranda.App");
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(srcDir, "*.axaml", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            var doc = XDocument.Load(file);
            foreach (var button in doc.Descendants().Where(e => e.Name.LocalName == "Button"))
            {
                if (button.Attribute("MinWidth") is null)
                {
                    continue;
                }

                var classes = (button.Attribute("Classes")?.Value ?? string.Empty)
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (classes.Any(c => Exempt.Contains(c)))
                {
                    continue;
                }

                offenders.Add($"{Path.GetRelativePath(srcDir, file)}: {button.Attribute("Content")?.Value ?? button.Attribute("x:Name")?.Value ?? "(이름 없음)"}");
            }
        }

        Assert.True(offenders.Count == 0, "Button 에 MinWidth 하드코딩: " + string.Join(", ", offenders));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Oranda.sln")) && !File.Exists(Path.Combine(dir.FullName, "Oranda.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("저장소 루트를 찾지 못했다.");
    }
}
