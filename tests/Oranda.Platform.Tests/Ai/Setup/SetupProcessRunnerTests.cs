using System.Diagnostics;
using Oranda.Platform.Ai.Setup;

namespace Oranda.Platform.Tests.Ai.Setup;

/// <summary>줄 단위 실행기를 실제 /bin/sh 로 확인한다(claude 는 부르지 않는다, Windows 에서는 건너뛴다).</summary>
public sealed class SetupProcessRunnerTests
{
    [Fact]
    public async Task Output_and_error_lines_both_arrive_and_the_exit_code_is_kept()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var process = new SystemSetupProcessRunner().Start(
            new SetupLaunch("/bin/sh", ["-c", "echo one; echo two >&2; printf 'no newline'; exit 4"]));

        var lines = new List<SetupLine>();
        await foreach (var line in process.ReadLinesAsync(timeout.Token))
        {
            lines.Add(line);
        }

        Assert.Contains(new SetupLine("one", IsError: false), lines);
        Assert.Contains(new SetupLine("two", IsError: true), lines);
        Assert.Contains(new SetupLine("no newline", IsError: false), lines);
        Assert.Equal(4, await process.WaitForExitAsync(timeout.Token));
    }

    [Fact]
    public async Task Kill_ends_the_whole_tree_and_reading_finishes()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        // 자식 sleep 이 손자로 남지 않는지 본다: sh 가 sleep 의 pid 를 먼저 찍는다.
        await using var process = new SystemSetupProcessRunner().Start(
            new SetupLaunch("/bin/sh", ["-c", "sleep 60 & echo $!; wait"]));

        int? childPid = null;
        await foreach (var line in process.ReadLinesAsync(timeout.Token))
        {
            childPid = int.Parse(line.Text, System.Globalization.CultureInfo.InvariantCulture);
            process.Kill();
        }

        await process.WaitForExitAsync(timeout.Token);
        Assert.NotNull(childPid);
        // 끝난 손자는 init 이 거둘 때까지 잠깐 보일 수 있어 3초까지 기다린다.
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (IsAlive(childPid.Value) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50, timeout.Token);
        }

        Assert.False(IsAlive(childPid.Value));
    }

    [Fact]
    public async Task Standard_input_is_closed_at_once()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var process = new SystemSetupProcessRunner().Start(new SetupLaunch("/bin/cat", []));

        await foreach (var _ in process.ReadLinesAsync(timeout.Token))
        {
        }

        Assert.Equal(0, await process.WaitForExitAsync(timeout.Token));
    }

    [Fact]
    public async Task Environment_overrides_are_applied_and_null_removes_a_variable()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var launch = new SetupLaunch(
            "/bin/sh",
            ["-c", "echo \"[$ORANDA_SETUP_A][${HOME:-none}]\""],
            new Dictionary<string, string?> { ["ORANDA_SETUP_A"] = "a", ["HOME"] = null });
        await using var process = new SystemSetupProcessRunner().Start(launch);

        var lines = new List<string>();
        await foreach (var line in process.ReadLinesAsync(timeout.Token))
        {
            lines.Add(line.Text);
        }

        Assert.Equal(["[a][none]"], lines);
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            if (p.HasExited)
            {
                return false;
            }

            // 좀비(Z)는 끝난 것으로 본다.
            var stat = $"/proc/{pid}/stat";
            return File.Exists(stat) && File.ReadAllText(stat).Split(')')[^1].TrimStart()[0] != 'Z';
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
