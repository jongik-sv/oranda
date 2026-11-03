using Oranda.Platform.Ai.Cli;

namespace Oranda.Platform.Tests.Ai.Cli;

/// <summary>실행 파일 이름으로 일반화한 찾기(<see cref="SystemCliExecutable"/>).</summary>
public sealed class SystemCliExecutableTests
{
    [Fact]
    public void Finds_the_named_executable_on_PATH()
    {
        var dir = OperatingSystem.IsWindows() ? @"C:\tools" : "/opt/tools";
        var file = Path.Combine(dir, OperatingSystem.IsWindows() ? "codex.exe" : "codex");

        var (found, error) = SystemCliExecutable.Locate("codex", "codex", dir, p => p == file, home: "/nohome");

        Assert.Equal(file, found);
        Assert.Null(error);
    }

    [Fact]
    public void Finds_the_named_executable_in_a_known_install_directory_when_PATH_lacks_it()
    {
        var home = OperatingSystem.IsWindows() ? @"C:\Users\x" : "/Users/x";
        var file = Path.Combine(home, ".local", "bin", OperatingSystem.IsWindows() ? "agy.exe" : "agy");

        var (found, _) = SystemCliExecutable.Locate("agy", "agy", null, p => p == file, home);

        Assert.Equal(file, found);
    }

    [Fact]
    public void Missing_executable_is_cli_not_found_and_never_falls_back_to_another_cli()
    {
        var home = OperatingSystem.IsWindows() ? @"C:\Users\x" : "/Users/x";
        var claude = Path.Combine(home, ".local", "bin", OperatingSystem.IsWindows() ? "claude.exe" : "claude");

        // claude 만 있고 codex 는 없다: codex 를 찾는 쪽이 claude 로 넘어가면 안 된다.
        var (found, error) = SystemCliExecutable.Locate("codex", "codex", Path.GetDirectoryName(claude), p => p == claude, home);

        Assert.Null(found);
        Assert.Equal("cli_not_found", error!.Code);
    }

    [Fact]
    public void Claude_executable_path_setting_is_not_an_input_of_the_generic_finder()
    {
        // 일반형 찾기에는 설정 경로를 받는 자리가 없다: Locate·Find 의 매개변수 어디에도 configuredPath 가 없다.
        var parameters = typeof(SystemCliExecutable).GetMethods()
            .Where(m => m.Name is nameof(SystemCliExecutable.Find) or nameof(SystemCliExecutable.Locate))
            .SelectMany(m => m.GetParameters())
            .Select(p => p.Name!);

        Assert.DoesNotContain(parameters, n => n.Contains("configured", StringComparison.OrdinalIgnoreCase) || n.Contains("executablePath", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Known_directories_of_the_generic_finder_exclude_the_claude_only_location()
    {
        var home = OperatingSystem.IsWindows() ? @"C:\Users\x" : "/Users/x";

        Assert.DoesNotContain(SystemCliExecutable.KnownDirectories(home), d => d.Contains(".claude", StringComparison.Ordinal));
        Assert.Contains(ClaudeExecutable.KnownDirectories(home), d => d.Contains(".claude", StringComparison.Ordinal) || OperatingSystem.IsWindows());
    }

    [Fact]
    public void Batch_files_are_still_refused()
    {
        Assert.True(ClaudeExecutable.IsBatchFile(@"C:\npm\codex.cmd"));
        Assert.True(ClaudeExecutable.IsBatchFile(@"C:\npm\codex.BAT"));
        Assert.False(ClaudeExecutable.IsBatchFile(@"C:\npm\codex.exe"));
    }
}
