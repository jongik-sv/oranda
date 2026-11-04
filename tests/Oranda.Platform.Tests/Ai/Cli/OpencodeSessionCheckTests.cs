using System.Diagnostics;
using Oranda.Platform.Ai.Cli;

namespace Oranda.Platform.Tests.Ai.Cli;

/// <summary>이어 말하기 전 대화 확인: 출력 해석(닫는 쪽 판단)과 실제 프로세스 처리(종료 코드·시간 초과·환경).</summary>
public sealed class OpencodeSessionCheckTests : IDisposable
{
    private const string Id = "ses_e5b124059ffeAppIM3ZV7gwvYH";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oranda-oc-check-" + Guid.NewGuid().ToString("N"));

    public OpencodeSessionCheckTests() => Directory.CreateDirectory(_dir);

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

    private static Task<Oranda.Core.Ai.Contracts.AiError?> Verify(string? exported)
        => OpencodeSessionCheck.VerifyAsync((_, _, _, _) => Task.FromResult(exported), "/fake/opencode", Id, CancellationToken.None);

    [Theory]
    [InlineData("""{"info":{"id":"ses_e5b124059ffeAppIM3ZV7gwvYH","agent":"oranda_locked","meta":{"inner":{"permissions":[{"action":"*"}]}}}}""")] // 안쪽에 든 규칙
    [InlineData("""{"info":{"id":"ses_e5b124059ffeAppIM3ZV7gwvYH","agent":"oranda_locked","list":[{"permission":"allow"}]}}""")]
    [InlineData("""{"info":{"id":"ses_e5b124059ffeAppIM3ZV7gwvYH","agent":"oranda_locked","parentID":"ses_other"}}""")] // 다른 대화에서 이어받음
    [InlineData("""{"info":{"id":"ses_e5b124059ffeAppIM3ZV7gwvYH","agent":"oranda_locked","forkSessionID":"ses_other"}}""")]
    public async Task Inherited_or_nested_rules_close_the_resume(string exported)
        => Assert.Equal("resume_permissions_unexpected", (await Verify(exported))!.Code);

    [Fact]
    public async Task A_conversation_made_by_the_locked_agent_without_rules_is_accepted()
        => Assert.Null(await Verify("""{"info":{"id":"ses_e5b124059ffeAppIM3ZV7gwvYH","agent":"oranda_locked","title":"t","tokens":{"input":1}},"messages":[{"type":"user","text":"permission"}]}"""));

    [Fact]
    public async Task An_exporter_that_throws_an_io_error_closes_the_resume()
    {
        var error = await OpencodeSessionCheck.VerifyAsync((_, _, _, _) => throw new IOException("x"), "/fake/opencode", Id, CancellationToken.None);

        Assert.Equal("resume_not_verified", error!.Code);
    }

    [Fact]
    public async Task The_real_export_passes_the_session_id_with_the_standalone_flags_and_an_empty_private_config_folder()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var fake = WriteScript("""
            #!/bin/sh
            printf '{"info":{"id":"%s","agent":"oranda_locked","args":"%s","config":"%s","token":"%s","empty":"%s"}}' "$5" "$*" "$OPENCODE_CONFIG_DIR" "$ORANDA_RUN_TOKEN" "$(ls -A "$OPENCODE_CONFIG_DIR" | wc -l | tr -d ' ')"
            """);

        var output = await OpencodeSessionCheck.ExportAsync(fake, Id, TimeSpan.FromSeconds(20), CancellationToken.None);

        Assert.NotNull(output);
        using var document = System.Text.Json.JsonDocument.Parse(output);
        var info = document.RootElement.GetProperty("info");
        Assert.Equal(Id, info.GetProperty("id").GetString());
        Assert.Equal($"session export --standalone --sanitize {Id}", info.GetProperty("args").GetString());
        Assert.Equal(string.Empty, info.GetProperty("token").GetString()); // 부모의 토큰 변수는 지운다
        Assert.Equal("0", info.GetProperty("empty").GetString());
        Assert.False(Directory.Exists(info.GetProperty("config").GetString())); // 끝난 뒤 지운다
    }

    [Fact]
    public async Task A_failing_exit_code_or_a_timeout_gives_null()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Null(await OpencodeSessionCheck.ExportAsync(WriteScript("#!/bin/sh\necho 'Session not found'\nexit 1\n", "fail"), Id, TimeSpan.FromSeconds(20), CancellationToken.None));

        var watch = Stopwatch.StartNew();
        Assert.Null(await OpencodeSessionCheck.ExportAsync(WriteScript("#!/bin/sh\nsleep 30\n", "slow"), Id, TimeSpan.FromMilliseconds(500), CancellationToken.None));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15));
    }

    private string WriteScript(string body, string name = "opencode")
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, body.Replace("\r\n", "\n", StringComparison.Ordinal).TrimStart());
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }
}
