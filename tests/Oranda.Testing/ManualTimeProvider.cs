namespace Oranda.Testing;

/// <summary>
/// 시험이 손으로 돌리는 시계(<see cref="Advance"/>). 타이머는 <see cref="Advance"/> 안에서 그 시각 순서대로 그 스레드에서 돈다.
/// <see cref="CancellationTokenSource"/>(TimeSpan, TimeProvider) 의 CancelAfter 처럼 <see cref="ITimer.Change"/> 로 다시 거는 타이머도 따른다.
/// 실제 시간에 기대지 않으므로 부하가 걸려도 판정이 흔들리지 않는다(K4).
/// </summary>
public sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now;

    public ManualTimeProvider(DateTimeOffset? start = null)
    {
        _now = start ?? new DateTimeOffset(2026, 11, 9, 9, 0, 0, TimeSpan.Zero);
    }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp()
    {
        lock (_gate)
        {
            return _now.UtcTicks;
        }
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    /// <summary>걸려 있는(아직 울릴) 타이머 수.</summary>
    public int PendingTimers
    {
        get
        {
            lock (_gate)
            {
                return _timers.Count(t => t.Due is not null);
            }
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var timer = new ManualTimer(this, callback, state);
        lock (_gate)
        {
            _timers.Add(timer);
        }

        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>시계를 <paramref name="by"/> 만큼 돌린다. 그 사이 울릴 타이머를 시각 순서대로 부른다.</summary>
    public void Advance(TimeSpan by)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(by, TimeSpan.Zero);
        DateTimeOffset target;
        lock (_gate)
        {
            target = _now + by;
        }

        while (true)
        {
            ManualTimer? next;
            lock (_gate)
            {
                next = _timers.Where(t => t.Due is { } d && d <= target).OrderBy(t => t.Due).FirstOrDefault();
                if (next is null)
                {
                    _now = target;
                    return;
                }

                _now = next.Due!.Value;
                next.Due = next.Period > TimeSpan.Zero && next.Period != Timeout.InfiniteTimeSpan ? _now + next.Period : null;
            }

            next.Callback(next.State); // 잠금 밖에서: 콜백이 Change·Dispose 를 불러도 된다
        }
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        public TimerCallback Callback { get; } = callback;

        public object? State { get; } = state;

        public DateTimeOffset? Due { get; set; }

        public TimeSpan Period { get; private set; } = Timeout.InfiniteTimeSpan;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            // 실제 타이머처럼 무한(-1 ms) 말고 음수는 받지 않는다(지난 시각으로 걸어 시계가 거꾸로 가지 않게).
            if (dueTime < TimeSpan.Zero && dueTime != Timeout.InfiniteTimeSpan)
            {
                throw new ArgumentOutOfRangeException(nameof(dueTime));
            }

            lock (owner._gate)
            {
                if (!owner._timers.Contains(this))
                {
                    return false;
                }

                Period = period;
                Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
                return true;
            }
        }

        public void Dispose()
        {
            lock (owner._gate)
            {
                owner._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
