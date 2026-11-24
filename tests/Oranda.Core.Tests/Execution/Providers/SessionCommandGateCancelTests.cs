using Oranda.Core.Connection;
using Oranda.Core.Execution.Providers;
using Oranda.Core.Sessions;

namespace Oranda.Core.Tests.Execution.Providers;

/// <summary>
/// PHC-11: 잠금을 기다리다 취소한 일은 그 뒤 잠금이 풀려도 잡지 않는다. <see cref="SemaphoreSlim"/> 은 취소된 대기자를 줄에서 빼기 전에
/// 놓인 잠금을 넘겨줄 수 있다 — 그러면 상한으로 그만둔 커밋이 나중에 서버로 간다.
/// </summary>
public sealed class SessionCommandGateCancelTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task A_waiter_cancelled_before_the_release_never_enters()
    {
        // 취소 → 바로 놓기: 취소 정리와 넘겨주기가 겨루는 순간을 여러 번 만든다.
        for (var i = 0; i < 300; i++)
        {
            var gate = new SessionCommandGate();
            await gate.EnterAsync(default);
            using var cts = new CancellationTokenSource();
            var waiter = Task.Run(() => gate.EnterAsync(cts.Token));
            await Task.Yield();
            await cts.CancelAsync();
            gate.Release();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter.WaitAsync(Generous));
            Assert.False(gate.IsHeld); // 넘겨받았더라도 곧바로 놓았다
            Assert.True(gate.TryEnter()); // 다른 일이 잡을 수 있다
            gate.Release();
        }
    }

    [Fact]
    public async Task A_commit_given_up_at_the_limit_is_not_sent_after_the_gate_is_released()
    {
        var gate = new SessionCommandGate();
        var session = new GateSession(gate);
        await gate.EnterAsync(default); // 멈춘 컴파일이 잠금을 쥐었다

        var r = await BoundedTransactionEnd.RunAsync(session, commit: true, TimeSpan.FromMilliseconds(100));
        gate.Release(); // 컴파일이 끝났다

        Assert.Equal(TransactionEndOutcome.TimedOut, r.Outcome);
        await Task.Delay(300);
        Assert.Equal(0, session.Sent); // COMMIT 은 드라이버에 가지 않았다
        Assert.False(gate.IsHeld);
    }

    /// <summary>잠금을 잡은 뒤에만 「보냄」을 세는 가짜 세션(실제 세션의 커밋과 같은 순서).</summary>
    private sealed class GateSession(SessionCommandGate gate) : ISession
    {
        private int _sent;

        public int Sent => Volatile.Read(ref _sent);

        public Guid Id { get; } = Guid.NewGuid();

        public ConnectionProfile Profile { get; } = new("dev", "scott", "localhost:1521/FREEPDB1");

        public ServerInfo Server => throw new NotSupportedException();

        public SessionPurpose Purpose => SessionPurpose.Work;

        public string DbKey => "K";

        public SessionState State => SessionState.Idle;

        public bool HasPendingTransaction => true;

        public Task CommitAsync(CancellationToken ct) => SendAsync(ct);

        public Task RollbackAsync(CancellationToken ct) => SendAsync(ct);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private async Task SendAsync(CancellationToken ct)
        {
            await gate.EnterAsync(ct);
            try
            {
                Interlocked.Increment(ref _sent);
            }
            finally
            {
                gate.Release();
            }
        }
    }
}
