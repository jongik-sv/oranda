using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Sessions;
using Oranda.Oracle.Sessions;

namespace Oranda.Oracle.Tests.Sessions;

/// <summary>세션·연결 닫기가 호출 스레드를 막지 않고, 제한 시간이 지나면 기다림을 그만두고 경고를 남긴다.</summary>
public sealed class SessionCloserTests
{
    private static readonly TimeSpan Quick = TimeSpan.FromSeconds(3);

    [Fact]
    public async Task RunAsync_does_not_run_dispose_on_calling_thread_and_stops_waiting_at_timeout()
    {
        using var gate = new ManualResetEventSlim(false);
        var callerThread = Environment.CurrentManagedThreadId;
        var disposeThread = -1;
        var finished = false;
        var warnings = new List<string>();

        var run = SessionCloser.RunAsync(
            () =>
            {
                disposeThread = Environment.CurrentManagedThreadId;
                gate.Wait(Quick);
                finished = true;
                return ValueTask.CompletedTask;
            },
            "세션 test", warnings.Add, TimeSpan.FromMilliseconds(100));

        await run.WaitAsync(Quick); // 막힌 Dispose 와 상관없이 제한 시간에 돌아온다
        Assert.False(finished);
        Assert.Single(warnings);
        Assert.Contains("세션 test", warnings[0], StringComparison.Ordinal);

        gate.Set(); // 막힌 Dispose 는 나중에 백그라운드에서 끝난다
        SpinWait.SpinUntil(() => finished, Quick);
        Assert.True(finished);
        Assert.NotEqual(callerThread, disposeThread);
    }

    [Fact]
    public async Task RunAsync_returns_without_warning_when_dispose_finishes_in_time()
    {
        var warnings = new List<string>();

        await SessionCloser.RunAsync(() => ValueTask.CompletedTask, "세션 ok", warnings.Add).WaitAsync(Quick);

        Assert.Empty(warnings);
    }

    [Fact]
    public async Task RunAsync_rethrows_a_failure_that_happens_within_the_timeout()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SessionCloser.RunAsync(() => throw new InvalidOperationException("boom"), "세션 bad"));
    }

    [Fact]
    public async Task OracleSession_dispose_publishes_closed_first_then_waits_for_stuck_connection_close_in_background()
    {
        using var gate = new ManualResetEventSlim(false);
        var closed = false;
        var bus = new RecordingBus();
        var removed = new List<Guid>();
        var id = Guid.NewGuid();
        // 열지 않은 연결: 실제로 닫을 것이 없어 Dispose 는 즉시 끝나고, 막히는 닫기는 주입한 함수가 흉내 낸다.
        using var connection = new OracleConnection("User Id=u;Password=p;Data Source=localhost:1/x");
        var info = new ServerInfo("23.26.0.0.0", "FREE", "localhost", "FREEPDB1", "FREE", "FREEPDB1", true, 1,
            new ServerCapabilities(false, true, true, false, false));
        var session = new OracleSession(id, new ConnectionProfile("dev", "scott", "localhost:1521/FREEPDB1"), info, SessionPurpose.Work,
            "K", connection, bus, removed.Add,
            () =>
            {
                gate.Wait(Quick);
                closed = true;
                return ValueTask.CompletedTask;
            });

        var dispose = session.DisposeAsync().AsTask();

        // 연결 닫기가 막혀 있어도 상태·목록·이벤트는 이미 바뀌었다.
        Assert.Equal(SessionState.Closed, session.State);
        Assert.Equal([id], removed);
        Assert.Contains(bus.Events, e => e is SessionClosed c && c.SessionId == id);
        Assert.False(closed);

        gate.Set();
        await dispose.WaitAsync(Quick);
        Assert.True(closed);
    }

    private sealed class RecordingBus : IEventBus
    {
        public List<OrandaEvent> Events { get; } = [];

        public IDisposable Subscribe<TEvent>(object owner, Action<TEvent> handler)
            where TEvent : OrandaEvent
            => throw new NotSupportedException();

        public void Publish<TEvent>(TEvent e)
            where TEvent : OrandaEvent
        {
            lock (Events)
            {
                Events.Add(e);
            }
        }
    }
}
