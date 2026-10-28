using System.Xml.Linq;

namespace Oranda.Architecture.Tests;

/// <summary>
/// orandactl(Oranda.Agent.Cli)은 외부 AI 가 띄우는 가벼운 MCP 서버다. 앱·UI·Oracle 드라이버를 끌어오지 않고
/// Core 만 참조해야 한다. 컴파일러가 쓰지 않는 참조를 지우므로 csproj 를 직접 읽는다.
/// </summary>
public class AgentCliReferenceTests
{
    private const string ProjectPath = "src/Oranda.Agent.Cli/Oranda.Agent.Cli.csproj";

    private static readonly string[] ForbiddenPackages =
    [
        "Avalonia",
        "Oracle.ManagedDataAccess",
        "Microsoft.Data.Sqlite",
    ];

    [Fact]
    public void Agent_cli_references_only_core()
    {
        var document = XDocument.Load(Path.Combine(FindRepositoryRoot(), ProjectPath));

        var projects = document.Descendants("ProjectReference")
            .Select(r => Path.GetFileNameWithoutExtension(((string?)r.Attribute("Include") ?? string.Empty).Replace('\\', '/')))
            .ToList();

        Assert.Equal(["Oranda.Core"], projects);
    }

    [Fact]
    public void Agent_cli_does_not_reference_ui_or_database_packages()
    {
        var document = XDocument.Load(Path.Combine(FindRepositoryRoot(), ProjectPath));

        var violations = document.Descendants("PackageReference")
            .Select(p => (string?)p.Attribute("Include") ?? string.Empty)
            .Where(include => ForbiddenPackages.Any(f => include.StartsWith(f, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        Assert.True(violations.Count == 0, $"금지된 패키지: {string.Join(", ", violations)}");
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
