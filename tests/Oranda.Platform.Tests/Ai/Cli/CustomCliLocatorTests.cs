using Oranda.Platform.Ai.Cli;

namespace Oranda.Platform.Tests.Ai.Cli;

/// <summary>설치 표시용 탐색: 실행 때와 같은 규칙(PATH + 흔한 설치 위치, 절대 경로, 배치 파일 거부)이고 아무것도 실행하지 않는다.</summary>
public class CustomCliLocatorTests
{
    private static readonly string BrewDir = "/opt/homebrew/bin";

    private static string Exe(string dir, string name) => Path.Combine(dir, OperatingSystem.IsWindows() ? name + ".exe" : name);

    private static CustomCliLocator Create(string? path, params string[] existing)
    {
        var files = existing.ToHashSet(StringComparer.Ordinal);
        return new CustomCliLocator(() => path, files.Contains, "/home/x");
    }

    [Fact]
    public void A_name_is_found_on_the_path_and_in_the_common_install_folders_when_the_path_is_the_finder_one()
    {
        var onPath = Create("/usr/local/mytools", Exe("/usr/local/mytools", "zcode"));
        var finder = Create("/usr/bin:/bin", Exe(BrewDir, "zcode"));

        Assert.Equal(Exe("/usr/local/mytools", "zcode"), onPath.Locate("zcode"));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(Exe(BrewDir, "zcode"), finder.Locate("zcode")); // Finder 로 띄운 앱의 PATH 에 Homebrew 폴더가 없어도 찾는다
        }

        Assert.Null(Create("/usr/bin").Locate("zcode"));
    }

    [Fact]
    public void An_absolute_path_is_checked_as_a_file_and_a_relative_path_is_never_searched()
    {
        var absolute = OperatingSystem.IsWindows() ? @"C:\tools\x.exe" : "/tools/x";
        var locator = Create("/bin", absolute, Exe("/bin", "x"));

        Assert.Equal(absolute, locator.Locate(absolute));
        Assert.Null(Create("/bin").Locate(absolute));
        Assert.Null(locator.Locate("tools/x")); // 구분자가 든 상대 경로는 PATH 탐색으로 흘리지 않는다
        Assert.Null(locator.Locate("  "));
    }

    [Fact]
    public void A_batch_file_is_not_usable()
    {
        var batch = OperatingSystem.IsWindows() ? @"C:\tools\x.cmd" : "/tools/x.cmd";

        Assert.Null(Create("/bin", batch).Locate(batch));
    }

    [Fact]
    public void A_batch_file_found_through_the_path_by_its_name_is_refused_too()
    {
        var onPath = Path.Combine("/bin", "x.cmd");
        var locator = Create("/bin", onPath);

        var (path, error) = locator.Resolve("x.cmd", "기타 CLI");

        Assert.Null(path);
        Assert.Equal("cli_unsupported", error!.Code);
    }

    [Theory]
    [InlineData("x.cmd")]
    [InlineData("X.BAT")]
    [InlineData("x.bat.")]
    [InlineData("x.cmd ")]
    [InlineData(@"C:\t\a.bat.")]
    [InlineData(@"C:\t\a.bat. .")]
    public void A_batch_name_is_refused_before_the_search_even_with_trailing_dots_or_spaces(string name)
    {
        if (!OperatingSystem.IsWindows() && (name.EndsWith('.') || name.EndsWith(' ')))
        {
            return; // 끝의 점·공백을 같은 파일로 보는 것은 Windows 파일 시스템의 동작이다.
        }

        var (path, error) = Create("/bin", name, Path.Combine("/bin", name)).Resolve(name, "기타 CLI");

        Assert.Null(path);
        Assert.Equal("cli_unsupported", error!.Code);
    }

    [Fact]
    public void Relative_path_entries_are_skipped_so_the_result_is_always_a_full_path()
    {
        var separator = Path.PathSeparator;
        var relativeOnly = Create($".{separator}bin{separator}", Exe(".", "x"), Exe("bin", "x"));
        var mixed = Create($".{separator}/abs{separator}bin", Exe(".", "x"), Exe("/abs", "x"), Exe("bin", "x"));

        Assert.Null(relativeOnly.Locate("x"));
        Assert.Equal(Exe("/abs", "x"), mixed.Locate("x"));
    }

    [Fact]
    public void A_path_override_from_the_command_replaces_the_parent_path_for_the_search()
    {
        var locator = Create("/parent", Exe("/opt/t/bin", "mytool"));

        Assert.Null(locator.Resolve("mytool", "기타 CLI").Path);
        Assert.Equal(Exe("/opt/t/bin", "mytool"), locator.Resolve("mytool", "기타 CLI", "/opt/t/bin").Path);
    }
}
