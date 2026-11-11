using System.Diagnostics;
using Oranda.Core.Transfer.Copy.ExternalLoad;
using Oranda.Platform.ExternalLoad;

namespace Oranda.Platform.Tests.ExternalLoad;

// 소유: XL1. 외부 도구 실행기(표준 입력 쓰고 닫기·환경 덮어쓰기·끝 64KB·시간 제한·취소 때 프로세스 트리 종료). Windows 의 cmd 로 시험한다(그 밖 OS 는 건너뜀).
public class ExternalProcessRunnerTests
{
    private static readonly Dictionary<string, string?> NoEnvironment = [];

    private static string Cmd => Path.Combine(Environment.SystemDirectory, "cmd.exe");

    private static ExternalProcessStart CmdStart(string command, string? input = null, IReadOnlyDictionary<string, string?>? environment = null, TimeSpan? timeout = null) =>
        new(Cmd, ["/d", "/c", command], Path.GetTempPath(), environment ?? NoEnvironment, input, timeout);

    [Fact]
    public async Task Captures_output_and_exit_code()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await new ExternalProcessRunner().RunAsync(CmdStart("echo hello& echo oops 1>&2& exit 3"), CancellationToken.None);

        Assert.Equal(3, result.ExitCode);
        Assert.Contains("hello", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("oops", result.StandardError, StringComparison.Ordinal);
        Assert.False(result.TimedOut);
    }

    [Fact]
    public async Task Writes_standard_input_then_closes_it()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // findstr 는 표준 입력이 닫혀야(EOF) 끝나고 ASCII 줄을 그대로 내보낸다(sort 는 콘솔 코드 페이지에 따라 출력이 깨진다).
        var result = await new ExternalProcessRunner().RunAsync(CmdStart("findstr .", input: "b\r\na\r\n"), CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["b", "a"], result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    [Fact]
    public async Task No_input_closes_standard_input_immediately()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await new ExternalProcessRunner().RunAsync(CmdStart("sort", timeout: TimeSpan.FromSeconds(20)), CancellationToken.None);

        Assert.False(result.TimedOut);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Environment_overrides_set_and_remove_variables()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var environment = new Dictionary<string, string?> { ["ORANDA_XL_TEST"] = "v-1", ["USERNAME"] = null };
        var result = await new ExternalProcessRunner().RunAsync(CmdStart("echo [%ORANDA_XL_TEST%][%USERNAME%]", environment: environment), CancellationToken.None);

        Assert.Contains("[v-1][%USERNAME%]", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Working_directory_is_used()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var name = "oranda-xl-" + Guid.NewGuid().ToString("N");
        var dir = Path.Combine(Path.GetTempPath(), name);
        Directory.CreateDirectory(dir);
        try
        {
            var start = new ExternalProcessStart(Cmd, ["/d", "/c", "cd"], dir, NoEnvironment, null);
            var result = await new ExternalProcessRunner().RunAsync(start, CancellationToken.None);

            Assert.EndsWith(name, result.StandardOutput.Trim(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(dir);
        }
    }

    [Fact]
    public async Task Keeps_only_the_last_64KB_of_output()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await new ExternalProcessRunner().RunAsync(CmdStart("for /L %i in (1,1,12000) do @echo line-%i-xxxxxxxxxxxxxxxxxxxx"), CancellationToken.None);

        Assert.True(result.StandardOutput.Length <= ExternalProcessRunner.TailChars);
        Assert.True(result.StandardOutput.Length > ExternalProcessRunner.TailChars / 2);
        Assert.EndsWith("line-12000-xxxxxxxxxxxxxxxxxxxx", result.StandardOutput.TrimEnd(), StringComparison.Ordinal);
        Assert.DoesNotContain("line-1-x", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_executable_is_an_external_process_exception()
    {
        var start = new ExternalProcessStart(Path.Combine(Path.GetTempPath(), "no-such-tool-" + Guid.NewGuid().ToString("N") + ".exe"), [], Path.GetTempPath(), NoEnvironment, null);

        var ex = await Assert.ThrowsAsync<ExternalProcessException>(() => new ExternalProcessRunner().RunAsync(start, CancellationToken.None));
        Assert.Contains("실행하지 못했습니다", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Timeout_kills_the_tree_and_reports_timed_out()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var watch = Stopwatch.StartNew();
        var result = await new ExternalProcessRunner().RunAsync(CmdStart("ping -n 30 127.0.0.1", timeout: TimeSpan.FromSeconds(1)), CancellationToken.None);

        Assert.True(result.TimedOut);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), watch.Elapsed.ToString());
    }

    [Fact]
    public async Task Cancellation_kills_the_child_tree_then_throws()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var before = Process.GetProcessesByName("PING").Select(p => p.Id).ToHashSet();
        using var cts = new CancellationTokenSource();
        var run = new ExternalProcessRunner().RunAsync(CmdStart("ping -n 30 127.0.0.1"), cts.Token);

        // cmd 가 띄운 ping(손자 프로세스)이 보일 때까지 기다린다.
        Process? child = null;
        var watch = Stopwatch.StartNew();
        while (child is null && watch.Elapsed < TimeSpan.FromSeconds(10))
        {
            child = Process.GetProcessesByName("PING").FirstOrDefault(p => !before.Contains(p.Id));
            if (child is null)
            {
                await Task.Delay(100);
            }
        }

        Assert.NotNull(child);
        using (child)
        {
            await cts.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
            Assert.True(child.WaitForExit(5000), "ping 이 끝나지 않았습니다.");
        }
    }

    [Fact]
    public async Task Already_cancelled_token_does_not_start()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new ExternalProcessRunner().RunAsync(new ExternalProcessStart("does-not-matter", [], Path.GetTempPath(), NoEnvironment, null), cts.Token));
    }
}
