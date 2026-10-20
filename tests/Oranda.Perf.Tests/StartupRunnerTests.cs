using System.Diagnostics;
using Oranda.Perf.Common;

namespace Oranda.Perf.Tests;

/// <summary>시작 측정 도구(옵션 해석, 표식 읽기, 가짜 앱 실행, 메모리 읽기) 시험. 가짜 앱 시험은 환경 변수를 쓰므로 한 묶음에서 순서대로 돈다.</summary>
[Collection("FakeApp")]
public class StartupRunnerTests
{
    private static string FakeExe()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Oranda.sln")))
        {
            dir = dir.Parent;
        }

        var root = dir?.FullName ?? throw new InvalidOperationException("Oranda.sln 을 찾지 못했다.");
        var config = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ? "Release" : "Debug";
        var name = OperatingSystem.IsWindows() ? "Oranda.Perf.FakeApp.exe" : "Oranda.Perf.FakeApp";
        return Path.Combine(root, "tests", "Oranda.Perf.FakeApp", "bin", config, "net10.0", name);
    }

    private static StartupOptions Options(string? dataDir = null, bool cold = false, int timeoutS = 20, int idleS = 0) =>
        new(FakeExe(), 1, Path.GetTempPath(), 1000, false, timeoutS, cold, dataDir, idleS, 0);

    private static StartupRun RunFake(string mode, StartupOptions opt)
    {
        Environment.SetEnvironmentVariable("FAKE_MODE", mode);
        try
        {
            return StartupRunner.RunOnce(opt, 1);
        }
        finally
        {
            Environment.SetEnvironmentVariable("FAKE_MODE", null);
        }
    }

    // ---- 옵션 ----

    [Fact]
    public void Options_parse_all_flags()
    {
        var o = StartupOptions.Parse(["--exe", "a.exe", "--runs", "7", "--out", "o", "--budget-ms", "1500.5", "--timeout-s", "9", "--idle-s", "10", "--discard", "2", "--data-dir", "d", "--cold-copy", "--enforce"]);
        Assert.Equal(new StartupOptions("a.exe", 7, "o", 1500.5, true, 9, true, "d", 10, 2), o);
    }

    [Fact]
    public void Options_have_defaults()
    {
        var o = StartupOptions.Parse([]);
        Assert.Null(o.Exe);
        Assert.Equal(5, o.Runs);
        Assert.False(o.ColdCopy);
        Assert.Null(o.DataDir);
        Assert.Equal(0, o.Discard);
    }

    [Theory]
    [InlineData("--runs")]
    [InlineData("--runs", "0")]
    [InlineData("--runs", "abc")]
    [InlineData("--timeout-s", "-1")]
    [InlineData("--budget-ms", "0")]
    [InlineData("--budget-ms", "x")]
    [InlineData("--idle-s", "-1")]
    [InlineData("--nope")]
    public void Options_reject_bad_input(params string[] args)
    {
        Assert.Throws<ArgumentException>(() => StartupOptions.Parse(args));
    }

    // ---- 표식 ----

    [Theory]
    [InlineData("ORANDA_PERF stage=first-render ms=293.4", "first-render", 293.4)]
    [InlineData("ORANDA_PERF ms=5 stage=appbuilder", "appbuilder", 5.0)]
    [InlineData("ORANDA_PERF stage=input-ready ms=1e3", "input-ready", 1000.0)]
    public void Mark_lines_are_parsed(string line, string stage, double ms)
    {
        Assert.True(StartupRunner.TryParseMark(line, out var s, out var v));
        Assert.Equal(stage, s);
        Assert.Equal(ms, v, 6);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData("ORANDA_PERF stage=first-render")]
    [InlineData("ORANDA_PERF ms=3")]
    [InlineData("ORANDA_PERF stage=x ms=abc")]
    [InlineData("  ORANDA_PERF stage=x ms=3")]
    public void Non_mark_lines_are_ignored(string? line)
    {
        Assert.False(StartupRunner.TryParseMark(line, out _, out _));
    }

    // ---- 가짜 앱 ----

    [Fact]
    public void Normal_run_collects_all_marks_and_memory_and_cleans_up()
    {
        var data = Path.Combine(Path.GetTempPath(), "oranda-perf-test-" + Guid.NewGuid().ToString("N"));
        var run = RunFake("ok", Options(dataDir: data));
        Assert.True(run.Ok);
        Assert.Equal(10, run.AppBuilderMs);
        Assert.Equal(20, run.WindowCreatedMs);
        Assert.Equal(30, run.FirstRenderMs);
        Assert.Equal(40, run.InputReadyMs);
        Assert.True(run.WorkingSetMb > 1, "작업 집합을 읽어야 한다");
        if (OperatingSystem.IsWindows())
        {
            Assert.True(run.PrivateMb > 1);
            Assert.True(run.CommitMb >= run.PrivateMb);
        }

        // 고정 데이터 폴더는 도구가 지우지 않는다(레이아웃 복원 시험용).
        Assert.True(File.Exists(Path.Combine(data, "data.txt")));
        Directory.Delete(data, recursive: true);
    }

    [Fact]
    public void Default_data_dir_is_a_fresh_temp_folder_deleted_afterwards()
    {
        var before = Directory.GetDirectories(Path.GetTempPath(), "oranda-perf-data-*").Length;
        var run = RunFake("ok", Options());
        Assert.True(run.Ok);
        Assert.Equal(before, Directory.GetDirectories(Path.GetTempPath(), "oranda-perf-data-*").Length);
    }

    [Fact]
    public void Cold_copy_runs_from_a_copy_and_removes_it()
    {
        var data = Path.Combine(Path.GetTempPath(), "oranda-perf-test-" + Guid.NewGuid().ToString("N"));
        var run = RunFake("ok", Options(dataDir: data, cold: true));
        Assert.True(run.Ok);
        var ranFrom = File.ReadAllText(Path.Combine(data, "data.txt"));
        Assert.Contains("oranda-perf-cold-", ranFrom, StringComparison.Ordinal);
        Assert.False(Directory.Exists(ranFrom.TrimEnd(Path.DirectorySeparatorChar)), "복사본은 지워야 한다");
        Directory.Delete(data, recursive: true);
    }

    [Fact]
    public void App_without_input_ready_mark_still_finishes_after_grace_period()
    {
        var run = RunFake("old", Options());
        Assert.True(run.Ok);
        Assert.Equal(30, run.FirstRenderMs);
        Assert.Null(run.InputReadyMs);
    }

    [Fact]
    public void App_that_crashes_after_one_mark_is_reported_not_ok_without_throwing()
    {
        var run = RunFake("crash", Options());
        Assert.False(run.Ok);
        Assert.Equal(10, run.AppBuilderMs);
        Assert.Null(run.FirstRenderMs);
        Assert.Null(run.PrivateMb);
    }

    [Fact]
    public void App_that_never_marks_times_out_and_is_killed()
    {
        var sw = Stopwatch.StartNew();
        var run = RunFake("nomarks", Options(timeoutS: 2));
        Assert.False(run.Ok);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30));
        Assert.Empty(Process.GetProcessesByName("Oranda.Perf.FakeApp"));
    }

    [Fact]
    public void Missing_exe_throws_file_not_found()
    {
        var opt = new StartupOptions(Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid().ToString("N")), 1, ".", 1000, false, 5, false, null, 0, 0);
        Assert.Throws<FileNotFoundException>(() => StartupRunner.RunOnce(opt, 1));
        Assert.Throws<FileNotFoundException>(() => StartupRunner.RunOnce(opt with { Exe = null }, 1));
    }

    // ---- 메모리 ----

    [Fact]
    public void Memory_probe_reads_this_process()
    {
        var m = MemoryProbe.Read(Environment.ProcessId);
        Assert.True(m.WorkingSetMb > 5);
        if (OperatingSystem.IsWindows())
        {
            Assert.True(m.PrivateMb > 5);
            Assert.True(m.PrivateMb <= m.WorkingSetMb + 1);
            Assert.True(m.CommitMb >= m.PrivateMb);
        }
    }

    [Fact]
    public void Memory_probe_returns_empty_sample_for_a_missing_process()
    {
        var m = MemoryProbe.Read(int.MaxValue);
        Assert.Equal(new MemorySample(null, null, null), m);
    }

    [Theory]
    [InlineData("Footprint: 131 MB (16384 bytes per page)", 131.0)]
    [InlineData("  Footprint: 1.5 GB", 1536.0)]
    [InlineData("Footprint: 512 KB", 0.5)]
    [InlineData("Footprint: 1048576 B", 1.0)]
    [InlineData("Oranda.App [4242]: 64-bit    Footprint: 45 MB (16384 bytes per page)\n  Dirty  Clean", 45.0)]
    public void Footprint_output_is_parsed_to_mb(string text, double expected)
    {
        Assert.Equal(expected, MemoryProbe.ParseFootprint(text)!.Value, 6);
    }

    [Theory]
    [InlineData("")]
    [InlineData("no such process")]
    [InlineData("Footprint: abc MB")]
    public void Footprint_output_without_a_total_is_null(string text)
    {
        Assert.Null(MemoryProbe.ParseFootprint(text));
    }
}
