using Oranda.Core.Connection;
using Oranda.Core.Monitoring;
using Oranda.Core.Sessions;

namespace Oranda.App.Tests.Features.BlockerAlert;

/// <summary>타이머를 시험이 직접 돌리는 시계. <see cref="Advance"/> 가 기한이 된 타이머 콜백을 순서대로 부른다.</summary>
internal sealed class FakeTimeProvider : TimeProvider
{
    private readonly List<FakeTimer> _timers = [];
    private DateTimeOffset _now = new(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => _now.UtcTicks;

    public override DateTimeOffset GetUtcNow() => _now;

    public int ActiveTimers => _timers.Count(t => !t.Disposed);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new FakeTimer(callback, state, dueTime == Timeout.InfiniteTimeSpan ? null : _now + dueTime, period);
        _timers.Add(timer);
        return timer;
    }

    public void Advance(TimeSpan by)
    {
        var target = _now + by;
        while (true)
        {
            var next = _timers.Where(t => !t.Disposed && t.Next is { } n && n <= target).OrderBy(t => t.Next).FirstOrDefault();
            if (next is null)
            {
                break;
            }

            _now = next.Next!.Value;
            next.Next = next.Period == Timeout.InfiniteTimeSpan || next.Period == TimeSpan.Zero ? null : _now + next.Period;
            next.Callback(next.State);
        }

        _now = target;
    }

    private sealed class FakeTimer(TimerCallback callback, object? state, DateTimeOffset? next, TimeSpan period) : ITimer
    {
        public TimerCallback Callback { get; } = callback;

        public object? State { get; } = state;

        public DateTimeOffset? Next { get; set; } = next;

        public TimeSpan Period { get; } = period;

        public bool Disposed { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();

        public void Dispose() => Disposed = true;

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>DB 없는 모니터 세션. 닫힌 횟수를 센다.</summary>
internal sealed class FakeMonitorSession(ConnectionProfile profile, ServerInfo server) : ISession
{
    public Guid Id { get; } = Guid.NewGuid();

    public ConnectionProfile Profile { get; } = profile;

    public ServerInfo Server { get; } = server;

    public SessionPurpose Purpose => SessionPurpose.Monitor;

    public string DbKey => "FREE/FREEPDB1/SCOTT";

    public SessionState State { get; private set; } = SessionState.Idle;

    public bool HasPendingTransaction => false;

    public int Disposed { get; private set; }

    public Task CommitAsync(CancellationToken ct) => Task.CompletedTask;

    public Task RollbackAsync(CancellationToken ct) => Task.CompletedTask;

    public ValueTask DisposeAsync()
    {
        Disposed++;
        State = SessionState.Closed;
        return ValueTask.CompletedTask;
    }
}

internal static class BlockerTestData
{
    public static readonly ServerCapabilities Dba = new(true, true, true, true, true);

    public static readonly ServerCapabilities NoPrivilege = new(false, false, false, false, false);

    public static ConnectionProfile Profile(Guid? id = null) => new("p", "scott", "h:1521/s", Id: id ?? new Guid("b1f6c2d4-0000-4000-8000-0000000000aa"));

    public static ServerInfo Server(ServerCapabilities caps, long sid = 42) =>
        new("23.26.0.0.0", "FREE", "h", "FREEPDB1", "FREE", "FREEPDB1", true, sid, caps);

    public static BlockerSnapshot Snapshot(int blocked, long maxIdle = 0, long lastCall = 0) =>
        new(blocked) { MaxIdleBlockerMinutes = maxIdle, LastCallElapsedSeconds = lastCall };
}
