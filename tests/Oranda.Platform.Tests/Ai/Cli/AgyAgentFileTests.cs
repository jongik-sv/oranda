using Oranda.Platform.Ai.Cli;

namespace Oranda.Platform.Tests.Ai.Cli;

/// <summary>agy 에이전트 파일: 필수 잠금 필드, YAML 값 이스케이프, 파일 권한.</summary>
public sealed class AgyAgentFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oranda-agy-file-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // 임시 폴더 정리 실패는 무시한다.
        }
    }

    [Fact]
    public void Both_lock_switches_are_always_written_with_or_without_the_finish_tool()
    {
        foreach (var withFinish in new[] { true, false })
        {
            var text = AgyAgentFile.Render("/x/orandactl", "pipe", withFinish);

            Assert.Contains("excludeDefaultComponents: true\n", text, StringComparison.Ordinal);
            Assert.Contains("inheritCustomizations: false\n", text, StringComparison.Ordinal);
            Assert.Contains(withFinish ? "tools: [finish]\n" : "tools: []\n", text, StringComparison.Ordinal);
            Assert.StartsWith("---\nname: oranda\n", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_server_entry_renders_timeoutSeconds_21600_by_default_and_a_given_value_when_shortened()
    {
        Assert.Equal(21_600, AgyAgentFile.DefaultToolTimeoutSeconds);
        Assert.Contains("    timeoutSeconds: 21600\n", AgyAgentFile.Render("/x/orandactl", "pipe", withFinishTool: false), StringComparison.Ordinal);
        Assert.Contains("    timeoutSeconds: 5\n", AgyAgentFile.Render("/x/orandactl", "pipe", withFinishTool: false, toolTimeoutSeconds: 5), StringComparison.Ordinal);
    }

    [Fact]
    public void Paths_with_backslashes_quotes_and_non_ascii_characters_are_written_as_escaped_quoted_strings_that_read_back_unchanged()
    {
        const string Executable = @"C:\Users\한글 ""x""\orandactl.exe";
        const string Pipe = @"pipe\name";

        var text = AgyAgentFile.Render(Executable, Pipe, withFinishTool: false);

        var command = text.Split('\n').Single(l => l.StartsWith("    command: ", StringComparison.Ordinal))["    command: ".Length..];
        Assert.StartsWith("\"", command, StringComparison.Ordinal);
        Assert.Equal(Executable, System.Text.Json.JsonSerializer.Deserialize<string>(command));
        Assert.DoesNotContain("한글", text, StringComparison.Ordinal);
        var args = text.Split('\n').Single(l => l.StartsWith("    args: ", StringComparison.Ordinal));
        Assert.Contains(System.Text.Json.JsonSerializer.Serialize(Pipe), args, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_creates_the_file_under_dot_agents_agents_and_refuses_to_overwrite()
    {
        var run = Path.Combine(_dir, "run-1");

        var path = AgyAgentFile.Write(run, "/x/orandactl", "pipe", withFinishTool: false);

        Assert.Equal(Path.Combine(run, ".agents", "agents", "oranda.md"), path);
        Assert.True(File.Exists(path));
        Assert.ThrowsAny<IOException>(() => AgyAgentFile.Write(run, "/x/orandactl", "pipe", withFinishTool: false));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
    }

    [Fact]
    public void The_file_is_owner_only_inside_an_owner_only_run_folder_and_the_folder_removal_takes_it_with_it()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var run = Path.Combine(_dir, "runs", "run-1");

        var path = AgyAgentFile.Write(run, "/x/orandactl", "pipe", withFinishTool: false);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        var ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        Assert.Equal(ownerOnly, File.GetUnixFileMode(run));
        Assert.Equal(ownerOnly, File.GetUnixFileMode(Path.Combine(run, ".agents")));
        Assert.Equal(ownerOnly, File.GetUnixFileMode(Path.GetDirectoryName(path)!));
        SystemCliRun.DeleteQuietly(run);
        Assert.False(File.Exists(path));
    }
}
