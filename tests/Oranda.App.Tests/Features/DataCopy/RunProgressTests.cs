using Oranda.App.Features.DataCopy;
using Oranda.App.Features.Localization;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Transfer.Copy;
using Oranda.Testing;

namespace Oranda.App.Tests.Features.DataCopy;

/// <summary>
/// PG1: 테이블 줄의 진행률은 대상에 넣은 행을 따라가고, 적재가 끝난 테이블은 뒤따르는 마무리 보고(읽은 수 0)에 0%로 되돌아가지 않으며,
/// 점검 때 센 예상 건수와 실제로 읽은 수가 달라도 적재가 끝나는 때 100% 가 된다. 마무리 중에는 단계 칸이 그 사실을 보인다.
/// </summary>
public sealed class RunProgressTests
{
    private static readonly CopyDecisions NoDecisions = new(new HashSet<string>(), new HashSet<ForeignKeyRef>(), new HashSet<string>(), false);

    /// <summary>준비한 진행 보고를 차례로 보낸 뒤 결과를 내기 전에 멈춰 둔 상태(진행 중 화면)의 줄을 돌려주고, 끝나면 결과를 적용한다.</summary>
    private static async Task<(RunViewModel Vm, TableRunRowViewModel Row, Func<Task> Finish)> RunUntilReportsAsync(
        IReadOnlyList<CopyProgress> reports, CopyRunResult? result = null)
    {
        var h = new DataCopyHarness();
        h.Runner.Reports.AddRange(reports);
        var release = new TaskCompletionSource();
        h.Runner.Behavior = async _ => { await release.Task; return result ?? TestJobs.Result(); };
        var vm = new RunViewModel(h.Services());

        var run = vm.StartAsync(new CopySessions(new FakeSession(), new FakeSession()), TestJobs.Job("진행률"),
            TestJobs.Report(order: [TestJobs.Plan("ORDERS")]), NoDecisions, new ResolvedPeriod(null, null));
        await h.Runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        return (vm, Assert.Single(vm.Rows), async () =>
        {
            release.SetResult();
            await run.WaitAsync(TimeSpan.FromSeconds(10));
        });
    }

    private static CopyProgress Load(long read, long written, long estimated = 10_000) =>
        new("ORDERS", read, estimated, CopyRunner.PhaseLoad) { Written = written };

    private static CopyProgress LoadEnd(long read, long written) =>
        new("ORDERS", read, read, CopyRunner.PhaseLoadEnd) { Written = written };

    [Fact]
    public Task Percent_follows_the_rows_written_not_the_rows_read_while_loading() => HeadlessHost.Run(async () =>
    {
        // 읽기·쓰기를 겹치면 읽은 수가 앞선다: 8000행을 읽었지만 4000행만 넣었으면 40%.
        var (_, row, finish) = await RunUntilReportsAsync([Load(8000, 4000)]);

        Assert.Equal(8000, row.Rows);
        Assert.Equal(4000, row.Written);
        Assert.Equal(40d, row.Percent);
        Assert.True(row.HasEstimate);
        await finish();
    });

    [Fact]
    public Task Percent_falls_back_to_the_rows_read_when_the_written_count_is_unknown() => HeadlessHost.Run(async () =>
    {
        var (_, row, finish) = await RunUntilReportsAsync([new CopyProgress("ORDERS", 2500, 10_000, CopyRunner.PhaseLoad)]);

        Assert.Null(row.Written);
        Assert.Equal(25d, row.Percent);
        await finish();
    });

    [Theory]
    [InlineData(CopyRunner.PhaseSequence)]
    [InlineData(CopyRunner.PhaseEnable)]
    [InlineData(CopyRunner.PhaseCount)]
    [InlineData(CopyRunner.PhaseRestore)]
    public Task A_finishing_report_with_zero_rows_does_not_drop_a_loaded_table(string phase) => HeadlessHost.Run(async () =>
    {
        var (_, row, finish) = await RunUntilReportsAsync(
        [
            Load(9000, 9000),
            LoadEnd(10_000, 10_000),
            new CopyProgress("ORDERS", 0, null, phase), // 실행기는 시퀀스·다시 켜기·되살리기를 읽은 수 0 으로 알린다
            Load(10_000, 10_000), // 늦게 도착한 적재 보고도 값을 되돌리지 않는다
        ]);

        Assert.Equal(100d, row.Percent);
        Assert.Equal(10_000, row.Rows);
        Assert.Equal(10_000, row.Written);
        Assert.Equal(10_000, row.Estimated);
        await finish();
    });

    [Fact]
    public Task Reports_before_the_load_ends_keep_the_numbers_of_a_table_that_is_not_loaded_yet() => HeadlessHost.Run(async () =>
    {
        // 적재 중 멈춘 테이블에 되살리기 보고가 와도 읽은 수·넣은 수를 지우지 않는다(단계 글만 바뀐다).
        var (_, row, finish) = await RunUntilReportsAsync(
        [
            Load(6000, 5000),
            new CopyProgress("ORDERS", 0, null, CopyRunner.PhaseRestore),
        ]);

        Assert.Equal(6000, row.Rows);
        Assert.Equal(5000, row.Written);
        Assert.Equal(50d, row.Percent);
        Assert.Equal(CopyRunner.PhaseRestore, row.Phase);
        Assert.False(row.LoadEnded);
        await finish();
    });

    [Fact]
    public Task Reading_fewer_rows_than_the_precheck_estimate_still_fills_the_bar_when_the_load_ends() => HeadlessHost.Run(async () =>
    {
        // 점검 때 10,000건으로 셌지만 실제로는 8,000행: 적재가 끝나는 때 읽은 수가 기준이 되어 100%. 결과를 기다리지 않는다.
        var (_, row, finish) = await RunUntilReportsAsync([Load(8000, 8000), LoadEnd(8000, 8000)]);

        Assert.Null(row.Outcome);
        Assert.Equal(8000, row.Estimated);
        Assert.Equal(100d, row.Percent);
        await finish();
    });

    [Fact]
    public Task Reading_more_rows_than_the_precheck_estimate_also_ends_at_one_hundred_percent() => HeadlessHost.Run(async () =>
    {
        var (_, row, finish) = await RunUntilReportsAsync([Load(10_000, 10_000, estimated: 10_000), LoadEnd(12_500, 12_500)]);

        Assert.Equal(12_500, row.Estimated);
        Assert.Equal(100d, row.Percent);
        await finish();
    });

    [Fact]
    public Task Failed_rows_keep_the_bar_under_full_until_the_result_arrives_and_then_it_is_full() => HeadlessHost.Run(async () =>
    {
        // 실패한 행이 있으면 넣은 수(9,990)가 읽은 수(10,000)보다 적어 적재가 끝나도 100% 미만. 결과(일부 실패)가 오면 읽기를 마쳤으므로 가득 찬다.
        var result = new CopyRunResult(CopyOutcome.PartiallyFailed,
            [new TableRunResult("ORDERS", 10_000, 9_990, TimeSpan.FromSeconds(1), CopyOutcome.PartiallyFailed, null)], [], [], DateTimeOffset.Now);
        var (_, row, finish) = await RunUntilReportsAsync([Load(10_000, 9_990), LoadEnd(10_000, 9_990)], result);

        Assert.InRange(row.Percent, 99d, 99.95d);
        await finish();
        Assert.Equal(100d, row.Percent);
    });

    [Fact]
    public Task The_result_fills_the_bar_of_a_finished_table_whatever_the_estimate_was() => HeadlessHost.Run(async () =>
    {
        // 적재 끝 알림 없이(예전 실행기·알림을 못 받은 경우) 결과만 와도 가득 찬다. 예상 건수가 실제보다 크더라도.
        var result = new CopyRunResult(CopyOutcome.Succeeded,
            [new TableRunResult("ORDERS", 8_000, 8_000, TimeSpan.FromSeconds(1), CopyOutcome.Succeeded, null)], [], [], DateTimeOffset.Now);
        var (_, row, finish) = await RunUntilReportsAsync([Load(7000, 7000)], result);
        Assert.Equal(70d, row.Percent);

        await finish();

        Assert.Equal(8_000, row.Estimated);
        Assert.Equal(100d, row.Percent);
        Assert.Equal("완료", row.Phase);
    });

    [Fact]
    public Task The_phase_waits_to_finish_after_the_load_ends() => HeadlessHost.Run(async () =>
    {
        var (_, row, finish) = await RunUntilReportsAsync([Load(10_000, 10_000), LoadEnd(10_000, 10_000)]);

        Assert.True(row.LoadEnded);
        Assert.Equal("적재 끝 · 마무리 대기", row.Phase);
        await finish();
    });

    [Theory]
    [InlineData(CopyRunner.PhaseCount, "마무리 중 · 건수 비교")]
    [InlineData(CopyRunner.PhaseSequence, "마무리 중 · 시퀀스 맞추기")]
    [InlineData(CopyRunner.PhaseEnable, "마무리 중 · 다시 켜기")]
    [InlineData(CopyRunner.PhaseRestore, "마무리 중 · 되살리기")]
    public Task The_phase_shows_the_finishing_step_that_touches_a_loaded_table(string phase, string expected) => HeadlessHost.Run(async () =>
    {
        var (_, row, finish) = await RunUntilReportsAsync(
            [Load(10_000, 10_000), LoadEnd(10_000, 10_000), new CopyProgress("ORDERS", 0, null, phase)]);

        Assert.Equal(expected, row.Phase);
        await finish();
        Assert.Equal("완료", row.Phase); // 결과가 오면 최종 글
    });

    [Fact]
    public Task A_table_that_did_not_load_keeps_the_plain_phase_name_of_the_report() => HeadlessHost.Run(async () =>
    {
        var (_, row, finish) = await RunUntilReportsAsync([new CopyProgress("ORDERS", 0, null, CopyRunner.PhaseCount)]);

        Assert.Equal(CopyRunner.PhaseCount, row.Phase);
        await finish();
    });

    [Fact]
    public void The_finishing_phase_texts_have_english_entries()
    {
        string[] texts = [RunPhases.LoadEnded, RunPhases.FinishingCount, RunPhases.FinishingSequence, RunPhases.FinishingEnable, RunPhases.FinishingRestore];
        foreach (var text in texts)
        {
            Assert.True(LiteralTranslator.Table.ContainsKey(text), "literals-en 없음: " + text);
            Assert.DoesNotContain("표", text, StringComparison.Ordinal);
        }
    }
}
