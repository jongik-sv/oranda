using Oranda.App.Features.DataCopy;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Transfer.Copy;
using Oranda.Testing;

namespace Oranda.App.Tests.Features.DataCopy;

/// <summary>
/// PF1 화면 쪽: 진행 줄의 멈춤 표시(결정 70)·넣은 수, 끝난 표의 시간 나눔(결정 74), 작업 옵션의 읽기·쓰기 겹치기(결정 69)·대기 한도(결정 73) 저장.
/// </summary>
public sealed class RunWaitTests
{
    private static readonly CopyDecisions NoDecisions = new(new HashSet<string>(), new HashSet<ForeignKeyRef>(), new HashSet<string>(), false);

    [Fact]
    public Task Wait_reports_show_in_the_row_until_an_empty_list_clears_them_and_written_follows() => HeadlessHost.Run(async () =>
    {
        var h = new DataCopyHarness();
        h.Runner.Reports.Add(new CopyProgress("ORDERS", 6000, 10_000, "적재") { Written = 5000 });
        h.Runner.Reports.Add(new CopyProgress("ORDERS", 6000, 10_000, "적재")
        {
            Written = 5000,
            Waits = [new CopyWait(CopyWaitSide.Target, 2, TimeSpan.FromSeconds(75))],
        });
        h.Runner.Reports.Add(new CopyProgress("ORDERS", 8000, 10_000, "적재")); // 보통 진행 보고는 멈춤 표시를 두고, 넣은 수도 앞 값을 둔다
        var release = new TaskCompletionSource();
        h.Runner.Behavior = async _ => { await release.Task; return TestJobs.Result(); };
        var vm = new RunViewModel(h.Services());

        var run = vm.StartAsync(new CopySessions(new FakeSession(), new FakeSession()), TestJobs.Job("멈춤"), TestJobs.Report(order: [TestJobs.Plan("ORDERS")]),
            NoDecisions, new ResolvedPeriod(null, null));
        await h.Runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var row = Assert.Single(vm.Rows);
        Assert.Equal("배치 2 쓰는 중 · 2분째 대기", row.WaitText);
        Assert.Equal(5000, row.Written);
        Assert.Equal(8000, row.Rows);

        release.SetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(string.Empty, row.WaitText); // 끝나면 결과가 정한다(멈춤 표시를 지운다)
    });

    [Fact]
    public Task Table_times_from_the_result_show_as_text_after_the_run() => HeadlessHost.Run(async () =>
    {
        var h = new DataCopyHarness();
        var times = new CopyTableTimes(TimeSpan.FromSeconds(1.2), TimeSpan.FromSeconds(0.05), TimeSpan.FromSeconds(3.4), 1234);
        h.Runner.Behavior = _ => Task.FromResult(new CopyRunResult(CopyOutcome.Succeeded,
            [new TableRunResult("ORDERS", 10, 10, TimeSpan.FromSeconds(5), CopyOutcome.Succeeded, null) { Times = times }], [], [], DateTimeOffset.Now));
        var vm = new RunViewModel(h.Services());

        await vm.StartAsync(new CopySessions(new FakeSession(), new FakeSession()), TestJobs.Job("시간"), TestJobs.Report(order: [TestJobs.Plan("ORDERS")]),
            NoDecisions, new ResolvedPeriod(null, null));

        Assert.Equal("읽기 1.20초 · 변환 0.05초 · 쓰기 3.40초 · 1,234행/초", Assert.Single(vm.Rows).TimesText);
    });

    [Fact]
    public Task Table_times_with_the_load_span_show_the_basis_the_table_total_and_the_rest() => HeadlessHost.Run(async () =>
    {
        var h = new DataCopyHarness();
        var times = new CopyTableTimes(TimeSpan.FromSeconds(1.2), TimeSpan.FromSeconds(0.05), TimeSpan.FromSeconds(3.4), 66_811)
        {
            Load = TimeSpan.FromSeconds(4.49), Total = TimeSpan.FromSeconds(10.7), FirstRow = TimeSpan.FromSeconds(0.3),
        };
        h.Runner.Behavior = _ => Task.FromResult(new CopyRunResult(CopyOutcome.Succeeded,
            [new TableRunResult("ORDERS", 10, 10, TimeSpan.FromSeconds(5), CopyOutcome.Succeeded, null) { Times = times }], [], [], DateTimeOffset.Now));
        var vm = new RunViewModel(h.Services());

        await vm.StartAsync(new CopySessions(new FakeSession(), new FakeSession()), TestJobs.Job("시간 기준"), TestJobs.Report(order: [TestJobs.Plan("ORDERS")]),
            NoDecisions, new ResolvedPeriod(null, null));

        Assert.Equal("적재 구간 4.49초 · 읽기 1.20초(첫 행까지 0.30초) · 변환 0.05초 · 쓰기 3.40초 · 66,811행/초 · 테이블 전체 10.70초 · 기타 6.21초",
            Assert.Single(vm.Rows).TimesText);
    });

    [Fact]
    public async Task A_result_with_abandoned_sessions_closes_the_session_pair_blocks_retry_and_asks_for_a_new_precheck()
    {
        // 리뷰 PF1-3: 실행기가 돌아오지 않는 명령 때문에 세션을 버렸다 → 앱이 그 세션 쌍을 닫는다(다음 점검이 새로 연다).
        var h = new DataCopyHarness();
        var failed = new FailedRow("ORDERS", new Dictionary<string, string?> { ["ID"] = "1" }, "ORA-00001");
        h.Runner.Behavior = _ => Task.FromResult(TestJobs.Result(CopyOutcome.Failed, failed) with { TargetAbandoned = true });
        var vm = await h.OpenAsync(TestJobs.Job("버린 세션", rules: TestJobs.Rule("ORDERS")));
        await vm.GoToPrecheckAsync();

        await vm.StartRunAsync();

        Assert.Equal(1, h.Runner.Calls);
        Assert.True(vm.Run.SessionsAbandoned);
        Assert.False(vm.HasSessions);
        Assert.NotEmpty(h.OpenedSessions);
        Assert.All(h.OpenedSessions, s => Assert.Equal(Oranda.Core.Sessions.SessionState.Closed, s.State));
        Assert.False(vm.Run.CanRetry); // 버린 세션으로 실패 행을 다시 넣지 않는다
        Assert.False(vm.Precheck.CanRun); // 다시 점검해야 실행할 수 있다
        Assert.Equal(DataCopyText.SessionsAbandoned, vm.StatusText);
    }

    [Fact]
    public void Overlap_and_wait_limit_options_load_and_save()
    {
        var h = new DataCopyHarness();
        var editor = new RuleEditorViewModel(h.Services());
        Assert.True(editor.OverlapReadWrite); // 새 작업은 겹치기 켬
        Assert.Equal(0, editor.WaitLimitMinutes);

        editor.SetConnections([h.Source, h.Target]);
        editor.Load(TestJobs.Job("옵션") with { Options = new CopyOptions { SerialReadWrite = true, WaitLimitMinutes = 15 } }, h.Source, h.Target);
        Assert.False(editor.OverlapReadWrite);
        Assert.Equal(15, editor.WaitLimitMinutes);

        editor.OverlapReadWrite = true;
        editor.WaitLimitMinutes = 5;
        var options = editor.BuildJob().Options;
        Assert.False(options.SerialReadWrite);
        Assert.Equal(5, options.WaitLimitMinutes);
    }

    /// <summary>PF3: 화면에 칸이 없는 옵션(LOB 미리 받기 끄기 — 파일에서만 켬)은 규칙 편집기가 저장해도 지워지지 않는다.</summary>
    [Fact]
    public void Options_without_a_box_on_screen_survive_saving_from_the_editor()
    {
        var h = new DataCopyHarness();
        var editor = new RuleEditorViewModel(h.Services());
        editor.SetConnections([h.Source, h.Target]);
        editor.Load(TestJobs.Job("옵션") with { Options = new CopyOptions { NoLobPrefetch = true, BatchSize = 300 } }, h.Source, h.Target);

        editor.BatchSize = 400;
        var options = editor.BuildJob().Options;

        Assert.True(options.NoLobPrefetch);
        Assert.Equal(400, options.BatchSize);
    }
}
