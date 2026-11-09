using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Oranda.Architecture.Tests;

/// <summary>
/// D-046-1: Tibero 드라이버(TDP.NET, <c>Tibero.DataAccess</c>)는 빌드에 넣지 않는다. 사용자가 위치를 지정하고 <c>src/Oranda.Tibero/Driver/</c> 의
/// 리플렉션 어댑터만 드라이버 형식을 안다(docs/design/tibero-driver-adapter.md 4장).
/// </summary>
public partial class TiberoDriverIsolationTests
{
    private const string DriverAssembly = "Tibero.DataAccess";

    /// <summary>
    /// 아직 어댑터로 옮기지 않은 파일(`#if TIBERO_DRIVER` 안의 옛 코드). 옮기는 항목에서 지운다: DV2 = TiberoDriverInfo, DV5 = 접속 시험.
    /// </summary>
    private static readonly string[] PendingAdapterMove =
    [
        "src/Oranda.Tibero/TiberoDriverInfo.cs",
        "src/Oranda.Tibero/Connection/TiberoConnectionProbe.cs",
    ];

    // 드라이버 이름공간(Tibero.DataAccess 아래 모두, tbcomm). "Tibero.DataAccess.dll" 같은 파일 이름과 Oranda.Tibero 는 걸리지 않는다.
    [GeneratedRegex(@"(?<![\w.])(Tibero\.DataAccess(\.(?!dll\b)\w+|\s*;)|tbcomm\.)", RegexOptions.CultureInvariant)]
    private static partial Regex DriverNamespace();

    [Fact]
    public void No_project_or_props_references_the_tibero_driver()
    {
        var root = FindRepositoryRoot();
        var files = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "tests"), "*.csproj", SearchOption.AllDirectories))
            .Concat(Directory.EnumerateFiles(root, "Directory.*.props", SearchOption.TopDirectoryOnly))
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "tests"), "Directory.*.props", SearchOption.TopDirectoryOnly))
            .Where(path => !IsBuildOutput(path))
            .ToList();
        Assert.True(files.Count > 10, "검사할 프로젝트 파일을 찾지 못했다.");

        var violations = files
            .SelectMany(path => XDocument.Load(path).Descendants()
                .Where(e => e.Name.LocalName is "PackageReference" or "PackageVersion" or "GlobalPackageReference" or "Reference")
                .Where(e => ((string?)e.Attribute("Include") ?? string.Empty).StartsWith(DriverAssembly, StringComparison.OrdinalIgnoreCase))
                .Select(_ => Path.GetRelativePath(root, path)))
            .ToList();

        Assert.True(violations.Count == 0, "Tibero 드라이버를 참조한다: " + string.Join(", ", violations));
    }

    [Fact]
    public void Oranda_tibero_assembly_does_not_reference_the_driver()
    {
        var references = typeof(Oranda.Tibero.TiberoAssembly).Assembly.GetReferencedAssemblies().Select(a => a.Name).ToList();

        Assert.Contains("Oranda.Core", references);
        Assert.DoesNotContain(DriverAssembly, references);
    }

    [Fact]
    public void Build_output_does_not_carry_the_driver()
    {
        // 이 시험 프로젝트는 Oranda.Tibero 를 참조하므로 Oranda.Tibero 의 전이 의존이 모두 이 폴더에 복사된다.
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "Oranda.Tibero.dll")));
        Assert.False(File.Exists(Path.Combine(AppContext.BaseDirectory, DriverAssembly + ".dll")));
    }

    [Fact]
    public void Build_output_carries_the_driver_dependency_missing_from_the_runtime()
    {
        // 드라이버가 참조하지만 .NET 런타임에 없는 어셈블리는 Oranda.Tibero 가 싣는다(D-046-1). 빠지면 드라이버를 읽은 뒤 첫 사용에서 FileNotFoundException.
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "System.Security.Permissions.dll")));
    }

    [Fact]
    public void Driver_namespaces_appear_only_in_the_adapter_folder()
    {
        var root = FindRepositoryRoot();
        var src = Path.Combine(root, "src");
        var adapter = Path.Combine(src, "Oranda.Tibero", "Driver") + Path.DirectorySeparatorChar;
        var pending = PendingAdapterMove.Select(p => Path.GetFullPath(Path.Combine(root, p))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var files = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories).Where(path => !IsBuildOutput(path)).ToList();
        Assert.True(files.Count > 100, "검사할 소스를 찾지 못했다.");

        var violations = new List<string>();
        foreach (var path in files)
        {
            if (path.StartsWith(adapter, StringComparison.OrdinalIgnoreCase) || pending.Contains(Path.GetFullPath(path)))
            {
                continue;
            }

            var lineNumber = 0;
            foreach (var line in File.ReadLines(path))
            {
                lineNumber++;
                if (DriverNamespace().IsMatch(CodePart(line)))
                {
                    violations.Add($"{Path.GetRelativePath(root, path)}:{lineNumber}");
                }
            }
        }

        Assert.True(violations.Count == 0, "드라이버 이름공간이 Driver/ 밖에 있다: " + string.Join(", ", violations));
    }

    [Fact]
    public void Source_projects_do_not_reference_the_fake_driver()
    {
        var root = FindRepositoryRoot();
        var violations = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories)
            .Where(path => XDocument.Load(path).Descendants("ProjectReference")
                .Any(p => ((string?)p.Attribute("Include") ?? string.Empty).Contains("Oranda.Tibero.FakeDriver", StringComparison.OrdinalIgnoreCase)))
            .Select(path => Path.GetRelativePath(root, path))
            .ToList();

        Assert.Empty(violations);
    }

    [Fact]
    public void The_namespace_scan_catches_driver_code_and_skips_comments_and_file_names()
    {
        Assert.Matches(DriverNamespace(), CodePart("using Tibero.DataAccess.Client;"));
        Assert.Matches(DriverNamespace(), CodePart("var t = asm.GetType(\"Tibero.DataAccess.Types.TiberoClob\");"));
        Assert.Matches(DriverNamespace(), CodePart("catch (global::tbcomm.err.TbOdpException) { }"));
        Assert.Matches(DriverNamespace(), CodePart("using Tibero.DataAccess;"));
        Assert.Matches(DriverNamespace(), CodePart("using T = Tibero.DataAccess.Common.Thing;"));
        Assert.DoesNotMatch(DriverNamespace(), CodePart("const string File = \"Tibero.DataAccess.dll\";"));
        Assert.DoesNotMatch(DriverNamespace(), CodePart("// Tibero.DataAccess.Client.TiberoConnection 을 어댑터가 만든다"));
        Assert.DoesNotMatch(DriverNamespace(), CodePart("/// <c>Tibero.DataAccess.Client.TiberoException</c>"));
        Assert.DoesNotMatch(DriverNamespace(), CodePart("namespace Oranda.Tibero.Connection;"));
    }

    /// <summary>줄의 코드 부분: <c>//</c>·<c>///</c> 주석과 블록 주석 안쪽 줄(<c>*</c> 로 시작)을 뺀다. 문자열 안의 <c>//</c> 는 드물어 따지지 않는다.</summary>
    private static string CodePart(string line)
    {
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith('*') || trimmed.StartsWith("/*", StringComparison.Ordinal))
        {
            return string.Empty;
        }

        var comment = line.IndexOf("//", StringComparison.Ordinal);
        return comment < 0 ? line : line[..comment];
    }

    private static bool IsBuildOutput(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        || path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

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
