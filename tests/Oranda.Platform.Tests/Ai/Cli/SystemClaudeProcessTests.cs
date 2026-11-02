using Oranda.Platform.Ai.Cli;

namespace Oranda.Platform.Tests.Ai.Cli;

/// <summary>실제 자식 프로세스로 표준 입력 쓰기를 확인한다(Unix 의 /bin/cat·/bin/sh 를 쓰므로 Windows 에서는 건너뛴다).</summary>
public sealed class SystemClaudeProcessTests
{
    [Fact]
    public async Task Standard_input_is_written_whole_and_closed()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var launch = new ClaudeLaunch("/bin/cat", [], Path.GetTempPath(), """{"type":"user"}""" + "\n");
        await using var process = new SystemClaudeProcessLauncher().Start(launch);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var lines = new List<string>();
        await foreach (var line in process.ReadLinesAsync(timeout.Token))
        {
            lines.Add(line);
        }

        // cat 은 입력이 닫혀야 끝난다: 줄을 모두 받고 끝났다면 입력을 닫은 것이다.
        Assert.Equal(["""{"type":"user"}"""], lines);
        Assert.Equal(0, await process.WaitForExitAsync(timeout.Token));
    }

    [Fact]
    public async Task Process_that_exits_without_reading_a_huge_input_ends_cleanly_with_its_exit_code()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // 입력을 읽지 않고 끝나는 프로세스에 수십 MB 를 쓰다가 깨진 파이프가 난다. 예외가 새지 않고 종료 코드로 드러나야 한다.
        var input = new string('a', 40 * 1024 * 1024) + "\n";
        var launch = new ClaudeLaunch("/bin/sh", ["-c", "echo boom >&2; exit 3"], Path.GetTempPath(), input);
        await using var process = new SystemClaudeProcessLauncher().Start(launch);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        await foreach (var line in process.ReadLinesAsync(timeout.Token))
        {
            Assert.Fail("출력이 없어야 한다: " + line);
        }

        Assert.Equal(3, await process.WaitForExitAsync(timeout.Token));
        Assert.Contains("boom", process.StandardErrorTail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Secret_is_hidden_in_standard_error_before_the_tail_is_cut()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // 한 줄이 꼬리 한도(2000자)에 걸치도록 키 앞에 긴 글을 둔다. 줄 단위로 먼저 가리므로 키 일부가 남지 않는다.
        const string secret = "sk-secret-value-ABCDEFGHIJ";
        var line = new string('x', 1990) + secret + new string('y', 30);
        var launch = new ClaudeLaunch("/bin/sh", ["-c", $"echo {line} >&2; exit 1"], Path.GetTempPath(), string.Empty, null, secret);
        await using var process = new SystemClaudeProcessLauncher().Start(launch);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        await foreach (var unused in process.ReadLinesAsync(timeout.Token))
        {
        }

        await process.WaitForExitAsync(timeout.Token);
        Assert.DoesNotContain("sk-secret", process.StandardErrorTail, StringComparison.Ordinal);
        Assert.DoesNotContain("ABCDEFGHIJ", process.StandardErrorTail, StringComparison.Ordinal);
        Assert.Contains("***", process.StandardErrorTail, StringComparison.Ordinal);
    }
}
