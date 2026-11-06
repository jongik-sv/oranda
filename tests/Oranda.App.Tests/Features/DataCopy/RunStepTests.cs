using System.Text;
using Oranda.App.Features.DataCopy;
using Oranda.Core.Transfer.Copy;
using Oranda.Testing;

namespace Oranda.App.Tests.Features.DataCopy;

/// <summary>데이터 복사 3단계(실행·검증): 중지, 결과 표시, CSV 는 명시 저장 때만.</summary>
public sealed class RunStepTests
{
    private static readonly ResolvedPeriod Period = new(null, null);

    private static readonly CopyDecisions NoDecisions = new(new HashSet<string>(), new HashSet<ForeignKeyRef>(), new HashSet<string>(), false);

    private static CopySessions Sessions() => new(new FakeSession(), new FakeSession());

    private static PrecheckReport Report() => TestJobs.Report(order: [TestJobs.Plan("ORDERS")]);

    private static FailedRow Failed(string key) => new("ORDERS", new Dictionary<string, string?> { ["ID"] = key }, "ORA-00001: unique constraint violated");

    // xUnit 의 동기화 컨텍스트(작업 스레드 수 = CPU 수) 밖에서 돈다: 코어가 적은 macOS 러너에서 다른 UI 시험이 그 스레드를 붙잡으면
    // 중지 뒤 이어지는 코드가 차례를 얻지 못해 시간 초과가 났다(2026-11-07 CI). 시험하려는 것은 중지 동작이지 스레드 배정이 아니다.
    [Fact]
    public Task Stop_cancels_runner_token_and_shows_stopped() => Task.Run(async () =>
    {
        var h = new DataCopyHarness();
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Runner.Behavior = async ct =>
        {
            await using (ct.Register(() => cancelled.TrySetResult()))
            {
                await Task.Delay(Timeout.Infinite, ct); // 취소될 때까지 기다린다
            }

            return TestJobs.Result();
        };
        var vm = new RunViewModel(h.Services());

        var run = vm.StartAsync(Sessions(), TestJobs.Job("중지"), Report(), NoDecisions, Period);
        await h.Runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(vm.IsRunning);
        Assert.True(vm.CanStop);
        Assert.False(h.Runner.Token.IsCancellationRequested);

        vm.Stop();
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(20)));
        Assert.True(finished == run,
            $"중지 뒤 실행이 끝나지 않음: 토큰 취소={h.Runner.Token.IsCancellationRequested}, IsRunning={vm.IsRunning}, 상태={vm.StatusText}, 결과={vm.Outcome}");
        await run;

        Assert.True(h.Runner.Token.IsCancellationRequested);
        Assert.Equal(CopyOutcome.Stopped, vm.Outcome);
        Assert.Contains("중지", vm.StatusText, StringComparison.Ordinal);
        Assert.False(vm.IsRunning);
    });

    [Fact]
    public async Task Result_fills_counts_and_failed_rows_and_read_start_time()
    {
        var h = new DataCopyHarness();
        h.Runner.Behavior = _ => Task.FromResult(TestJobs.Result(CopyOutcome.PartiallyFailed, Failed("7")));
        var vm = new RunViewModel(h.Services());

        await vm.StartAsync(Sessions(), TestJobs.Job("결과"), Report(), NoDecisions, Period);

        var row = Assert.Single(vm.Rows);
        Assert.Equal("10 → 10", row.CountsText);
        Assert.True(row.CountsMatch);
        Assert.Equal("ID=7", Assert.Single(vm.FailedRows).KeyText);
        Assert.True(vm.CanRetry);
        Assert.False(string.IsNullOrEmpty(vm.ReadStartedText));
        Assert.Equal(CopyOutcome.PartiallyFailed, vm.Outcome);
    }

    [Fact]
    public async Task Last_run_is_saved_to_the_job_after_the_run()
    {
        var h = new DataCopyHarness();
        var vm = new RunViewModel(h.Services());
        var job = TestJobs.Job("마지막 실행");

        await vm.StartAsync(Sessions(), job, Report(), NoDecisions, Period);

        var saved = h.Store.Jobs[job.Id];
        Assert.Equal(CopyOutcome.Succeeded, saved.LastRun?.Outcome);
        Assert.Equal(10, saved.LastRun?.Rows);
    }

    [Fact]
    public async Task Retry_adds_confirmed_rows_to_the_target_count()
    {
        var h = new DataCopyHarness();
        // 처음 실행: 원본 10행 중 2행 실패 → 대상 8행.
        h.Runner.Behavior = _ => Task.FromResult(new CopyRunResult(CopyOutcome.PartiallyFailed,
            [new TableRunResult("ORDERS", 10, 8, TimeSpan.FromSeconds(1), CopyOutcome.PartiallyFailed, null)],
            [Failed("7"), Failed("9")], [], new DateTimeOffset(2026, 11, 6, 12, 0, 1, TimeSpan.Zero)));
        // 다시 넣기: 이번에 2행을 넣었다(실행기는 이 값을 TargetRows 에 담는다).
        h.Runner.RetryResult = new CopyRunResult(CopyOutcome.Succeeded,
            [new TableRunResult("ORDERS", 2, 2, TimeSpan.FromSeconds(1), CopyOutcome.Succeeded, null)], [], [], new DateTimeOffset(2026, 11, 6, 12, 0, 2, TimeSpan.Zero));
        var vm = new RunViewModel(h.Services());
        await vm.StartAsync(Sessions(), TestJobs.Job("다시 넣기"), Report(), NoDecisions, Period);
        Assert.Equal("10 → 8", Assert.Single(vm.Rows).CountsText);

        await vm.RetryFailedAsync();

        var row = Assert.Single(vm.Rows);
        Assert.Equal(10, row.TargetRows); // 8 + 2
        Assert.Equal("10 → 10", row.CountsText);
        Assert.True(row.CountsMatch);
        Assert.Empty(vm.FailedRows);
        Assert.Equal(CopyOutcome.Succeeded, row.Outcome); // 실패 행이 모두 해소되면 표 줄도 성공
        Assert.Equal("완료", row.Phase);
    }

    [Fact]
    public async Task Retry_keeps_the_table_row_problem_state_while_failed_rows_remain()
    {
        var h = new DataCopyHarness();
        h.Runner.Behavior = _ => Task.FromResult(TestJobs.Result(CopyOutcome.PartiallyFailed, Failed("7"), Failed("9")));
        h.Runner.RetryResult = new CopyRunResult(CopyOutcome.PartiallyFailed,
            [new TableRunResult("ORDERS", 2, 1, TimeSpan.FromSeconds(1), CopyOutcome.PartiallyFailed, null)], [Failed("9")], [],
            new DateTimeOffset(2026, 11, 6, 12, 0, 2, TimeSpan.Zero));
        var vm = new RunViewModel(h.Services());
        await vm.StartAsync(Sessions(), TestJobs.Job("남은 실패"), Report(), NoDecisions, Period);

        await vm.RetryFailedAsync();

        var row = Assert.Single(vm.Rows);
        Assert.Equal(CopyOutcome.PartiallyFailed, row.Outcome);
        Assert.Equal("문제 있음", row.Phase);
        Assert.Equal("ID=9", Assert.Single(vm.FailedRows).KeyText);
    }

    [Theory]
    [InlineData(8L, 2L, 2, 0, 10L)] // 보낸 2행 모두 넣었다
    [InlineData(8L, 1L, 2, 1, 9L)] // 1행 넣고 1행 실패 남음(확정된 표)
    [InlineData(8L, 0L, 2, 2, 8L)] // 하나도 못 넣음
    [InlineData(8L, 1L, 2, 2, 8L)] // 넣은 수가 되돌려졌다(실패 행이 모두 돌아옴): 더하지 않는다
    [InlineData(8L, -1L, 2, 2, 8L)] // 이번 건수를 모르면 앞 건수 그대로
    [InlineData(-1L, 2L, 2, 0, -1L)] // 앞 건수를 모르면 계속 모른다
    [InlineData(null, 2L, 2, 0, null)]
    public void Retried_rows_are_added_only_for_a_confirmed_table_with_a_known_count(long? before, long retried, int sent, int remaining, long? expected) =>
        Assert.Equal(expected, RunViewModel.AddRetriedRows(before, retried, sent, remaining));

    [Fact]
    public async Task Retry_does_not_add_rows_that_were_rolled_back()
    {
        var h = new DataCopyHarness();
        h.Runner.Behavior = _ => Task.FromResult(new CopyRunResult(CopyOutcome.PartiallyFailed,
            [new TableRunResult("ORDERS", 10, 8, TimeSpan.FromSeconds(1), CopyOutcome.PartiallyFailed, null)],
            [Failed("7"), Failed("9")], [], new DateTimeOffset(2026, 11, 6, 12, 0, 1, TimeSpan.Zero)));
        // 중지로 되돌렸다: 1행을 넣었지만 확정하지 못해 입력 행 2개가 모두 돌아온다.
        h.Runner.RetryResult = new CopyRunResult(CopyOutcome.Stopped,
            [new TableRunResult("ORDERS", 2, 1, TimeSpan.FromSeconds(1), CopyOutcome.Stopped, null)], [Failed("7"), Failed("9")], [],
            new DateTimeOffset(2026, 11, 6, 12, 0, 2, TimeSpan.Zero));
        var vm = new RunViewModel(h.Services());
        await vm.StartAsync(Sessions(), TestJobs.Job("되돌린 다시 넣기"), Report(), NoDecisions, Period);

        await vm.RetryFailedAsync();

        var row = Assert.Single(vm.Rows);
        Assert.Equal(8, row.TargetRows); // 8 + 1 이 아니다
        Assert.Equal(CopyOutcome.PartiallyFailed, row.Outcome);
        Assert.Equal(2, vm.FailedRows.Count);
    }

    [Fact]
    public async Task Last_run_rows_leave_out_tables_whose_target_count_is_unknown()
    {
        var h = new DataCopyHarness();
        h.Runner.Behavior = _ => Task.FromResult(new CopyRunResult(CopyOutcome.PartiallyFailed,
            [
                new TableRunResult("ORDERS", 10, 8, TimeSpan.FromSeconds(1), CopyOutcome.PartiallyFailed, null),
                new TableRunResult("CODES", 5, -1, TimeSpan.FromSeconds(1), CopyOutcome.Failed, "대상 건수를 세지 못했습니다"), // -1 = 모름
            ],
            [], [], new DateTimeOffset(2026, 11, 6, 12, 0, 1, TimeSpan.Zero)));
        var vm = new RunViewModel(h.Services());

        await vm.StartAsync(Sessions(), TestJobs.Job("알 수 없는 건수"), Report(), NoDecisions, Period);

        Assert.Equal(8, vm.LastRun!.Rows); // 8 + (-1) = 7 이 아니다
    }

    [Fact]
    public async Task Last_run_is_not_recorded_when_every_target_count_is_unknown()
    {
        var h = new DataCopyHarness();
        h.Runner.Behavior = _ => Task.FromResult(new CopyRunResult(CopyOutcome.Stopped,
            [new TableRunResult("ORDERS", 3, -1, TimeSpan.FromSeconds(1), CopyOutcome.Stopped, null)], [], [],
            new DateTimeOffset(2026, 11, 6, 12, 0, 1, TimeSpan.Zero)));
        var vm = new RunViewModel(h.Services());

        await vm.StartAsync(Sessions(), TestJobs.Job("모두 모름"), Report(), NoDecisions, Period);

        Assert.Null(vm.LastRun); // 0 도 음수도 아니라 요약 자체를 남기지 않는다(행 수를 모름으로 둘 자리가 없다)
        Assert.DoesNotContain(h.Store.Calls, c => c.StartsWith("save:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Csv_is_written_only_on_explicit_save()
    {
        var h = new DataCopyHarness();
        h.Runner.Behavior = _ => Task.FromResult(TestJobs.Result(CopyOutcome.PartiallyFailed, Failed("7"), Failed("9")));
        var vm = new RunViewModel(h.Services());
        var opened = 0;
        var buffer = new MemoryStream();
        Task<Stream?> Open()
        {
            opened++;
            return Task.FromResult<Stream?>(new NonClosingStream(buffer));
        }

        await vm.StartAsync(Sessions(), TestJobs.Job("CSV"), Report(), NoDecisions, Period);
        Assert.Equal(0, opened); // 실행이 끝나도 파일 쓰기 호출이 없다

        await vm.SaveCsvAsync(Open);

        Assert.Equal(1, opened);
        var text = Encoding.UTF8.GetString(buffer.ToArray()).TrimStart('﻿');
        Assert.StartsWith("table,key,error\r\n", text, StringComparison.Ordinal);
        Assert.Contains("ORDERS,ID=7,", text, StringComparison.Ordinal);
        Assert.Contains("ORDERS,ID=9,", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Csv_cancel_in_picker_writes_nothing()
    {
        var h = new DataCopyHarness();
        h.Runner.Behavior = _ => Task.FromResult(TestJobs.Result(CopyOutcome.PartiallyFailed, Failed("7")));
        var vm = new RunViewModel(h.Services());
        await vm.StartAsync(Sessions(), TestJobs.Job("CSV 취소"), Report(), NoDecisions, Period);

        await vm.SaveCsvAsync(() => Task.FromResult<Stream?>(null));

        Assert.Single(vm.FailedRows); // 목록은 그대로
    }

    [Fact]
    public async Task Csv_quotes_commas_and_formula_prefixes()
    {
        using var stream = new MemoryStream();
        var rows = new[] { new FailedRow("T", new Dictionary<string, string?> { ["A"] = "x,y", ["B"] = null }, "=cmd\"quote\"") };

        await FailedRowsCsv.WriteAsync(stream, rows, CancellationToken.None);

        var text = Encoding.UTF8.GetString(stream.ToArray()).TrimStart('﻿');
        Assert.Contains("T,\"A=x,y;B=NULL\",\"'=cmd\"\"quote\"\"\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_runner_reports_status_without_throwing()
    {
        var vm = new RunViewModel(new DataCopyServices());

        await vm.StartAsync(Sessions(), TestJobs.Job("실행기 없음"), Report(), NoDecisions, Period);

        Assert.False(vm.IsRunning);
        Assert.False(string.IsNullOrEmpty(vm.StatusText));
    }

    /// <summary>CSV 를 쓴 뒤에도 내용을 읽을 수 있게 닫기를 막는 스트림.</summary>
    private sealed class NonClosingStream(MemoryStream inner) : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => inner.Length;

        public override long Position { get => inner.Position; set => inner.Position = value; }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

        public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.WriteAsync(buffer, cancellationToken);
    }
}
