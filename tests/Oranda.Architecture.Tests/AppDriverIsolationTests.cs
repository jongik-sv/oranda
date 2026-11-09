using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Oranda.Architecture.Tests;

/// <summary>
/// D-046(T1): Oranda.App 은 DBMS 드라이버(ODP.NET, TDP.NET)를 직접 쓰지 않는다 — DBMS 별 일은 Core 인터페이스와 DBMS 키 구현(Oranda.Oracle·Oranda.Tibero) 뒤에 둔다.
/// App 프로젝트는 Oranda.Oracle 을 ProjectReference 로 가져오므로 드라이버 어셈블리가 간접으로 보이게 된다. 그래서 컴파일러가 막아 주지 않고,
/// 소스와 csproj 를 직접 검사한다(Avalonia 가 든 Oranda.App 어셈블리를 시험 프로젝트가 끌어오지 않기 위함).
/// </summary>
public partial class AppDriverIsolationTests
{
    private static readonly string[] DriverPackages = ["Oracle.ManagedDataAccess", "Oracle.DataAccess", "Tibero.DataAccess"];

    // using 선언이든 완전한 이름이든 드라이버 네임스페이스가 나오면 걸린다. Oranda.Oracle.* 는 걸리지 않는다(앞이 문자·점이 아닌 경우만).
    [GeneratedRegex(@"(?<![\w.])(Oracle\.(ManagedDataAccess|DataAccess)|Tibero\.DataAccess)\b", RegexOptions.CultureInvariant)]
    private static partial Regex DriverNamespace();

    [Fact]
    public void App_sources_do_not_use_a_database_driver_namespace()
    {
        var appDirectory = Path.Combine(FindRepositoryRoot(), "src", "Oranda.App");
        var violations = new List<string>();
        foreach (var path in SourceFiles(appDirectory))
        {
            var lineNumber = 0;
            foreach (var line in File.ReadLines(path))
            {
                lineNumber++;
                if (DriverNamespace().IsMatch(line))
                {
                    violations.Add($"{Path.GetRelativePath(appDirectory, path)}:{lineNumber}");
                }
            }
        }

        Assert.True(violations.Count == 0, "Oranda.App 이 DB 드라이버를 직접 쓴다: " + string.Join(", ", violations));
    }

    [Fact]
    public void App_project_does_not_reference_a_database_driver_package()
    {
        var project = Path.Combine(FindRepositoryRoot(), "src", "Oranda.App", "Oranda.App.csproj");
        var packages = XDocument.Load(project).Descendants("PackageReference")
            .Select(p => (string?)p.Attribute("Include") ?? string.Empty)
            .Where(include => DriverPackages.Any(d => include.StartsWith(d, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        Assert.True(packages.Count == 0, "Oranda.App.csproj 가 DB 드라이버 패키지를 직접 참조한다: " + string.Join(", ", packages));
    }

    [Fact]
    public void The_scan_sees_the_app_sources_and_catches_a_driver_namespace()
    {
        // 검사가 빈 폴더를 훑어 조용히 통과하지 않는지, 그리고 패턴이 실제로 걸리는지 확인한다.
        var appDirectory = Path.Combine(FindRepositoryRoot(), "src", "Oranda.App");
        Assert.True(SourceFiles(appDirectory).Count() > 100);

        Assert.Matches(DriverNamespace(), "using Oracle.ManagedDataAccess.Client;");
        Assert.Matches(DriverNamespace(), "var c = new Oracle.ManagedDataAccess.Client.OracleConnection();");
        Assert.Matches(DriverNamespace(), "using Tibero.DataAccess.Client;");
        Assert.DoesNotMatch(DriverNamespace(), "using Oranda.Oracle.Transfer;");
        Assert.DoesNotMatch(DriverNamespace(), "// Oranda.Oracle 는 드라이버를 감싼다");
    }

    private static IEnumerable<string> SourceFiles(string directory) =>
        Directory.EnumerateFiles(directory, "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

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
