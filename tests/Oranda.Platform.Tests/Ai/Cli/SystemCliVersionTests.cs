using Oranda.Platform.Ai.Cli;

namespace Oranda.Platform.Tests.Ai.Cli;

/// <summary>시스템 CLI 버전 읽기와 미검증 버전 안내(공용 부분).</summary>
public sealed class SystemCliVersionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oranda-version-" + Guid.NewGuid().ToString("N"));

    public SystemCliVersionTests() => Directory.CreateDirectory(_dir);

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

    [Theory]
    [InlineData("1.4.12", "1.4.12")]
    [InlineData("agy version 1.4.12\n", "1.4.12")]
    [InlineData("codex-cli 0.166.0", "0.166.0")]
    [InlineData("v2.0", "2.0")]
    [InlineData("1.4.12-beta", null)]
    [InlineData("무슨 글", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Parse_takes_the_first_dotted_number(string? output, string? expected)
        => Assert.Equal(expected, SystemCliVersion.Parse(output));

    [Fact]
    public async Task The_notice_text_names_the_cli_and_other_clis_get_the_same_sentence()
    {
        var notice = await SystemCliVersion.UnverifiedNoticeAsync((_, _, _) => Task.FromResult<string?>("9.9.9"), "x", ["1.0.0"], "Codex CLI", CancellationToken.None);

        Assert.Equal("[안내] 이 버전의 Codex CLI 는 Oranda 에서 잠금을 검증하지 않았습니다.\n\n", notice);
        Assert.Null(await SystemCliVersion.UnverifiedNoticeAsync((_, _, _) => Task.FromResult<string?>("1.0.0"), "x", ["1.0.0"], "Codex CLI", CancellationToken.None));
    }

    [Fact]
    public async Task Caller_cancellation_is_not_swallowed()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SystemCliVersion.UnverifiedNoticeAsync(
            (_, _, ct) => Task.FromCanceled<string?>(ct), "x", ["1.0.0"], "CLI", cts.Token));
    }

    [Fact]
    public async Task The_real_reader_returns_the_first_output_line_without_the_run_token_variable()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var script = WriteScript("echo \"1.2.3 token=${ORANDA_RUN_TOKEN:-none}\"");
        Environment.SetEnvironmentVariable("ORANDA_RUN_TOKEN", "inherited-fake-value");
        try
        {
            var output = await SystemCliVersion.ReadAsync(script, TimeSpan.FromSeconds(10), CancellationToken.None);

            Assert.Equal("1.2.3 token=none", output);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ORANDA_RUN_TOKEN", null);
        }
    }

    [Fact]
    public async Task The_real_reader_gives_null_on_time_limit_missing_executable_and_failure_exit()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var slow = WriteScript("sleep 30");
        var started = DateTime.UtcNow;
        Assert.Null(await SystemCliVersion.ReadAsync(slow, TimeSpan.FromMilliseconds(400), CancellationToken.None));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));

        Assert.Null(await SystemCliVersion.ReadAsync(Path.Combine(_dir, "does-not-exist"), TimeSpan.FromSeconds(5), CancellationToken.None));
    }

    private string WriteScript(string body)
    {
        var path = Path.Combine(_dir, "cli-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }
}
