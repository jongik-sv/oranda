using System.Xml.Linq;

namespace Oranda.Architecture.Tests;

/// <summary>
/// 컴파일러는 쓰지 않는 참조를 어셈블리에서 지우므로, csproj 의 PackageReference·ProjectReference 도
/// ProjectReference 를 따라 재귀로 검사한다. 참조만 넣고 아직 쓰지 않은 경우도 잡는다.
/// </summary>
public class ProjectReferenceTests
{
    private static readonly string[] ForbiddenPackages =
    [
        "Avalonia",
        "Oracle.ManagedDataAccess",
    ];

    private static readonly string[] ForbiddenProjects =
    [
        "Oranda.Oracle",
        "Oranda.App",
        "Oranda.Platform",
    ];

    [Theory]
    [InlineData("src/Oranda.Core/Oranda.Core.csproj")]
    [InlineData("src/Oranda.Language/Oranda.Language.csproj")]
    public void Project_does_not_reference_ui_or_oracle_driver(string relativePath)
    {
        var root = FindRepositoryRoot();
        var violations = new List<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        pending.Push(Path.GetFullPath(Path.Combine(root, relativePath)));

        while (pending.Count > 0)
        {
            var project = pending.Pop();
            if (!visited.Add(project))
            {
                continue;
            }

            var name = Path.GetFileNameWithoutExtension(project);
            var document = XDocument.Load(project);

            foreach (var package in document.Descendants("PackageReference"))
            {
                var include = (string?)package.Attribute("Include") ?? string.Empty;
                if (ForbiddenPackages.Any(p => include.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                {
                    violations.Add($"{name} -> package {include}");
                }
            }

            foreach (var reference in document.Descendants("ProjectReference"))
            {
                var include = ((string?)reference.Attribute("Include") ?? string.Empty).Replace('\\', Path.DirectorySeparatorChar);
                var target = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(project)!, include));
                var targetName = Path.GetFileNameWithoutExtension(target);
                if (ForbiddenProjects.Contains(targetName, StringComparer.OrdinalIgnoreCase))
                {
                    violations.Add($"{name} -> project {targetName}");
                }

                pending.Push(target);
            }
        }

        Assert.True(violations.Count == 0, $"금지된 참조: {string.Join(", ", violations)}");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Oranda.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Oranda.sln 을 찾지 못했다.");
    }
}
