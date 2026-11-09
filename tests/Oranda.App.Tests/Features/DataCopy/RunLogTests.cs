using Oranda.App.Features.DataCopy;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Transfer.Copy;
using Oranda.Testing;

namespace Oranda.App.Tests.Features.DataCopy;

/// <summary>실행·검증 단계의 작업 로그: 실행기의 로그 줄은 진행 줄을 바꾸지 않고 쌓이며, 상한을 넘으면 앞을 버리고 버린 수를 보인다.</summary>
public sealed class RunLogTests
{
    private static readonly CopyDecisions NoDecisions = new(new HashSet<string>(), new HashSet<ForeignKeyRef>(), new HashSet<string>(), false);

    private static CopySessions Sessions() => new(new FakeSession(), new FakeSession());

    private static CopyLogEntry Entry(string text, CopyLogKind kind = CopyLogKind.Info, string phase = "적재", string? table = "ORDERS") =>
        new(new DateTimeOffset(2026, 11, 9, 14, 3, 21, TimeSpan.FromHours(9)), phase, table, text, kind);

    [Fact]
    public Task Runner_log_lines_fill_the_log_without_touching_progress_rows() => HeadlessHost.Run(async () =>
    {
        var h = new DataCopyHarness();
        h.Runner.Reports.Add(new CopyProgress("ORDERS", 5000, 10_000, "적재"));
        h.Runner.Reports.Add(CopyProgress.ForLog(Entry("원본: SELECT \"ID\" FROM \"SRC\".\"ORDERS\"", CopyLogKind.Statement)));
        h.Runner.Reports.Add(CopyProgress.ForLog(Entry("배치 1 · 5,000행 보냄 · 누적 5,000행")));
        var release = new TaskCompletionSource();
        h.Runner.Behavior = async _ => { await release.Task; return TestJobs.Result(); };
        var vm = new RunViewModel(h.Services());

        var run = vm.StartAsync(Sessions(), TestJobs.Job("로그"), TestJobs.Report(order: [TestJobs.Plan("ORDERS")]), NoDecisions, new ResolvedPeriod(null, null));
        await h.Runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var row = Assert.Single(vm.Rows);
        Assert.Equal(5000, row.Rows); // 로그 줄(Rows 0)이 진행 값을 0 으로 되돌리지 않는다
        Assert.Equal("적재", row.Phase);
        Assert.Equal(2, vm.Log.Lines.Count);
        Assert.True(vm.Log.Lines[0].IsStatement);
        Assert.Contains("[적재] ORDERS · 원본: SELECT", vm.Log.Lines[0].Text, StringComparison.Ordinal);
        Assert.Equal(0, vm.DetailTab); // 도는 동안은 작업 로그 탭

        release.SetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, vm.Log.Lines.Count); // 끝난 뒤에도 남는다
    });

    [Fact]
    public Task A_new_run_clears_the_previous_log() => HeadlessHost.Run(async () =>
    {
        var h = new DataCopyHarness();
        h.Runner.Reports.Add(CopyProgress.ForLog(Entry("한 줄")));
        var vm = new RunViewModel(h.Services());
        var report = TestJobs.Report(order: [TestJobs.Plan("ORDERS")]);

        await vm.StartAsync(Sessions(), TestJobs.Job("로그"), report, NoDecisions, new ResolvedPeriod(null, null));
        await vm.StartAsync(Sessions(), TestJobs.Job("로그"), report, NoDecisions, new ResolvedPeriod(null, null));

        Assert.Single(vm.Log.Lines);
    });

    [Fact]
    public Task Failed_rows_switch_the_detail_tab_after_the_run() => HeadlessHost.Run(async () =>
    {
        var h = new DataCopyHarness();
        var failed = new FailedRow("ORDERS", new Dictionary<string, string?> { ["ID"] = "7" }, "ORA-00001");
        h.Runner.Behavior = _ => Task.FromResult(TestJobs.Result(CopyOutcome.PartiallyFailed, failed));
        var vm = new RunViewModel(h.Services());

        await vm.StartAsync(Sessions(), TestJobs.Job("로그"), TestJobs.Report(order: [TestJobs.Plan("ORDERS")]), NoDecisions, new ResolvedPeriod(null, null));

        Assert.Equal(1, vm.DetailTab);
    });

    [Fact]
    public Task Runner_exception_is_written_to_the_log() => HeadlessHost.Run(async () =>
    {
        var h = new DataCopyHarness();
        h.Runner.Behavior = _ => throw new InvalidOperationException("세션이 끊겼습니다");
        var vm = new RunViewModel(h.Services());

        await vm.StartAsync(Sessions(), TestJobs.Job("로그"), TestJobs.Report(order: [TestJobs.Plan("ORDERS")]), NoDecisions, new ResolvedPeriod(null, null));

        var last = vm.Log.Lines[^1];
        Assert.True(last.IsError);
        Assert.Contains("세션이 끊겼습니다", last.Text, StringComparison.Ordinal);
    });

    [Fact]
    public void Log_keeps_at_most_the_cap_and_reports_dropped_lines()
    {
        var log = new CopyRunLogViewModel();
        for (var i = 0; i < CopyRunLogViewModel.MaxLines + 1; i++)
        {
            log.Append(Entry("줄 " + i));
        }

        Assert.True(log.Lines.Count <= CopyRunLogViewModel.MaxLines);
        Assert.Equal(CopyRunLogViewModel.DropChunk, log.Dropped);
        Assert.True(log.HasDropped);
        Assert.EndsWith("줄 " + CopyRunLogViewModel.MaxLines, log.Lines[^1].Text, StringComparison.Ordinal);
        Assert.EndsWith("줄 " + CopyRunLogViewModel.DropChunk, log.Lines[0].Text, StringComparison.Ordinal);

        var text = log.AllText();
        Assert.StartsWith("… 앞의 500줄은 버렸습니다", text, StringComparison.Ordinal);
        Assert.Contains("줄 " + CopyRunLogViewModel.MaxLines + Environment.NewLine, text, StringComparison.Ordinal);

        log.Clear();
        Assert.False(log.HasLines);
        Assert.False(log.HasDropped);
        Assert.Equal(string.Empty, log.AllText());
    }

    [Fact]
    public void Line_kinds_drive_the_colors()
    {
        var log = new CopyRunLogViewModel();
        log.Append(Entry("a", CopyLogKind.Warning));
        log.Append("마무리", "b", CopyLogKind.Error);

        Assert.True(log.Lines[0].IsWarning);
        Assert.Contains("알림: a", log.Lines[0].Text, StringComparison.Ordinal);
        Assert.True(log.Lines[1].IsError);
        Assert.Contains("[마무리] 오류: b", log.Lines[1].Text, StringComparison.Ordinal);
    }
}
