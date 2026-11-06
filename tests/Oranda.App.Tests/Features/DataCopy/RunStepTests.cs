using System.Text;
using Oranda.App.Features.DataCopy;
using Oranda.Core.Transfer.Copy;
using Oranda.Testing;

namespace Oranda.App.Tests.Features.DataCopy;

/// <summary>데이터 복사 3단계(실행·검증): 중지, 결과 표시, CSV 는 명시 저장 때만.</summary>
public sealed class RunStepTests
{
    private static readonly ResolvedPeriod Period = new(null, null);

    private static readonly CopyDecisions NoDecisions = new(new HashSet<string>(), new HashSet<string>(), new HashSet<string>(), false);

    private static CopySessions Sessions() => new(new FakeSession(), new FakeSession());

    private static PrecheckReport Report() => TestJobs.Report(order: [TestJobs.Plan("ORDERS")]);

    private static FailedRow Failed(string key) => new("ORDERS", new Dictionary<string, string?> { ["ID"] = key }, "ORA-00001: unique constraint violated");

    [Fact]
    public async Task Stop_cancels_runner_token_and_shows_stopped()
    {
        var h = new DataCopyHarness();
        h.Runner.Behavior = async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct); // 취소될 때까지 기다린다
            return TestJobs.Result();
        };
        var vm = new RunViewModel(h.Services());

        var run = vm.StartAsync(Sessions(), TestJobs.Job("중지"), Report(), NoDecisions, Period);
        await h.Runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(vm.IsRunning);
        Assert.True(vm.CanStop);
        Assert.False(h.Runner.Token.IsCancellationRequested);

        vm.Stop();
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(h.Runner.Token.IsCancellationRequested);
        Assert.Equal(CopyOutcome.Stopped, vm.Outcome);
        Assert.Contains("중지", vm.StatusText, StringComparison.Ordinal);
        Assert.False(vm.IsRunning);
    }

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
