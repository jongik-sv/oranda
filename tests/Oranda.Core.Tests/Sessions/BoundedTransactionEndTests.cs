using System.Diagnostics;
using Oranda.Core.Connection;
using Oranda.Core.Sessions;

namespace Oranda.Core.Tests.Sessions;

/// <summary>PHC-3: 닫는 경로의 커밋·롤백은 잠금 대기까지 넣어 상한 안에 돌아온다.</summary>
public sealed class BoundedTransactionEndTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromMilliseconds(200);

    /// <summary>상한을 넘었는지 볼 넉넉한 시한(시험 PC 부하).</summary>
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task An_end_that_finishes_in_time_is_done()
    {
        var session = new EndSession(_ => Task.CompletedTask);

        var r = await BoundedTransactionEnd.RunAsync(session, commit: true, Limit);

        Assert.Equal(TransactionEndOutcome.Done, r.Outcome);
        Assert.Null(r.Error);
        Assert.Equal(["commit"], session.Calls);
    }

    [Fact]
    public async Task An_end_that_fails_in_time_reports_the_error()
    {
        var session = new EndSession(_ => Task.FromException(new InvalidOperationException("boom")));

        var r = await BoundedTransactionEnd.RunAsync(session, commit: false, Limit);

        Assert.Equal(TransactionEndOutcome.Failed, r.Outcome);
        Assert.Equal("boom", r.Error?.Message);
        Assert.Equal(["rollback"], session.Calls);
    }

    [Fact]
    public async Task Waiting_on_a_held_gate_is_cancelled_at_the_limit()
    {
        var seen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new EndSession(async ct =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, ct); // 다른 명령이 잠금을 쥐고 있다
            }
            finally
            {
                seen.TrySetResult(ct.IsCancellationRequested);
            }
        });
        var watch = Stopwatch.StartNew();

        var r = await BoundedTransactionEnd.RunAsync(session, commit: true, Limit);

        Assert.Equal(TransactionEndOutcome.TimedOut, r.Outcome);
        Assert.True(watch.Elapsed < Generous, $"{watch.Elapsed} 걸림");
        Assert.True(await seen.Task.WaitAsync(Generous)); // 잠금 대기를 그만두게 토큰을 취소했다
    }

    [Fact]
    public async Task An_end_that_ignores_the_token_is_left_behind_at_the_limit()
    {
        var never = new TaskCompletionSource();
        var session = new EndSession(_ => never.Task); // 서버에 보낸 명령이 돌아오지 않는다
        var watch = Stopwatch.StartNew();

        var r = await BoundedTransactionEnd.RunAsync(session, commit: false, Limit);

        Assert.Equal(TransactionEndOutcome.TimedOut, r.Outcome);
        Assert.True(watch.Elapsed < Generous, $"{watch.Elapsed} 걸림");
        never.SetException(new InvalidOperationException("늦은 실패는 관찰만 한다"));
    }

    [Fact]
    public void A_session_is_running_a_command_only_while_busy_by_default()
    {
        var session = new EndSession(_ => Task.CompletedTask);
        ISession view = session;

        Assert.False(view.IsCommandRunning);
        session.State = SessionState.Busy;
        Assert.True(view.IsCommandRunning);
    }

    private sealed class EndSession(Func<CancellationToken, Task> end) : ISession
    {
        public List<string> Calls { get; } = [];

        public Guid Id { get; } = Guid.NewGuid();

        public ConnectionProfile Profile { get; } = new("dev", "scott", "localhost:1521/FREEPDB1");

        public ServerInfo Server { get; } = new("23.26.0.0.0", "FREE", "localhost", "FREEPDB1", "FREE", "FREEPDB1", true, 1,
            new ServerCapabilities(false, true, true, false, false));

        public SessionPurpose Purpose => SessionPurpose.Work;

        public string DbKey => "FREE/FREEPDB1/SCOTT";

        public SessionState State { get; set; } = SessionState.Idle;

        public bool HasPendingTransaction => true;

        public Task CommitAsync(CancellationToken ct)
        {
            lock (Calls)
            {
                Calls.Add("commit");
            }

            return end(ct);
        }

        public Task RollbackAsync(CancellationToken ct)
        {
            lock (Calls)
            {
                Calls.Add("rollback");
            }

            return end(ct);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
