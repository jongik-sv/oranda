using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Oranda.App.Features.SessionMonitor;
using Oranda.Core.Catalog;
using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Monitoring;
using Oranda.Core.Sessions;

namespace Oranda.App.Features.BlockerAlert;

/// <summary>칩 종류. 우선순위는 막고 있음 &gt; 오래 열린 미커밋 &gt; 감시 꺼짐.</summary>
public enum BlockerAlertKind
{
    None,
    Blocking,
    LongTransaction,
    Disabled,
}

/// <summary>감시를 끈 이유. <see cref="Unsupported"/> 는 이 서버에 조회 자체가 없어 칩도 숨긴다.</summary>
public enum BlockerDisabledReason
{
    None,
    NoPrivilege,
    NoCredential,
    Unsupported,
}

/// <summary>칩이 지금 보여 줄 값. 문구는 View 쪽이 만든다.</summary>
public sealed record BlockerAlertState(BlockerAlertKind Kind)
{
    public static readonly BlockerAlertState Hidden = new(BlockerAlertKind.None);

    public int BlockedCount { get; init; }

    /// <summary>서버가 막고 있는 이 세션을 끊기까지 남은 분. 설정이 없거나 막고 있지 않으면 null.</summary>
    public long? MinutesUntilKilled { get; init; }

    /// <summary>미커밋이 이어진 분(서버 값이 있으면 그것, 없으면 앱이 센 값).</summary>
    public int PendingMinutes { get; init; }

    public BlockerDisabledReason Reason { get; init; }

    /// <summary>true 면 내 세션이 아니라 Lock 모니터의 임계치 알림(LK-11): <see cref="BlockedCount"/> = 기준을 넘은 보유자 뒤 대기자 수.</summary>
    public bool FromLockMonitor { get; init; }

    /// <summary>Lock 모니터 임계치 알림일 때 가장 오래 기다린 초.</summary>
    public long MaxWaitSeconds { get; init; }
}

/// <summary>
/// 내 세션이 남을 막고 있음 감시(TSK-13-02). 공유 작업 세션에 미커밋 트랜잭션이 있을 때만 15초마다
/// 그 프로필의 모니터 세션으로 <see cref="IMonitorService.GetMyBlockingAsync"/> 를 부른다(작업 세션으로는 조회하지 않는다).
/// 모니터 세션은 처음 필요할 때 열고, 접속이 끊기면 바로, 미커밋이 끝나면 <see cref="MonitorIdleClose"/> 뒤에 닫는다.
/// V$ 권한 오류는 감시를 끄고 한 번만 알린다(오류 창 없음). 화면 요소를 모르는 일반 클래스라 시험이 시계를 직접 돌린다.
/// <para>oranda1 TSK-05-04: Lock 모니터가 보내는 <see cref="LockMonitor.LockThresholdChanged"/> 도 받아, 내 세션이 막고 있지 않을 때
/// 「⚠ N 세션 막음 · M 초」(빨강, <see cref="BlockerAlertState.FromLockMonitor"/>)를 띄운다. 이 경로는 조회를 하지 않는다.</para>
/// </summary>
public sealed class BlockerWatcher : IDisposable
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

    /// <summary>미커밋이 이만큼 이어지면 알린다(설정 화면은 1차 범위 밖이라 상수).</summary>
    public static readonly TimeSpan LongTransaction = TimeSpan.FromMinutes(10);

    /// <summary>미커밋이 끝난 뒤 모니터 세션을 닫기까지 기다리는 시간.</summary>
    public static readonly TimeSpan MonitorIdleClose = TimeSpan.FromSeconds(60);

    private readonly Func<ConnectionProfile, CancellationToken, Task<ISession?>> _openMonitor;
    private readonly Func<ISession, long, CancellationToken, Task<BlockerSnapshot>> _query;
    private readonly TimeProvider _time;
    private readonly Action<string> _log;
    private readonly List<IDisposable> _subscriptions = [];
    private readonly HashSet<Guid> _disabledShown = [];
    private readonly SemaphoreSlim _monitorGate = new(1, 1);
    private readonly object _gate = new();

    private Guid? _workId;
    private ConnectionProfile? _profile;
    private long _sid;
    private bool _pending;
    private int _epoch;
    private long _pendingSinceTicks;
    private BlockerDisabledReason _off;
    private bool _offVisible;
    private bool _longNoticed;
    private BlockerSnapshot? _snapshot;
    private LockMonitor.LockThresholdChanged? _lockAlert;
    private BlockerAlertState _state = BlockerAlertState.Hidden;
    private CancellationTokenSource? _pollCts;
    private ITimer? _pollTimer;
    private ITimer? _longTimer;
    private ITimer? _closeTimer;
    private ISession? _monitor;
    private Task _pollTask = Task.CompletedTask;
    private int _polling;
    private bool _disposed;

    public BlockerWatcher(
        IEventBus bus,
        Func<ConnectionProfile, CancellationToken, Task<ISession?>> openMonitor,
        Func<ISession, long, CancellationToken, Task<BlockerSnapshot>> query,
        TimeProvider? time = null,
        Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(openMonitor);
        ArgumentNullException.ThrowIfNull(query);
        _openMonitor = openMonitor;
        _query = query;
        _time = time ?? TimeProvider.System;
        _log = log ?? (message => Trace.TraceWarning("{0}", message));
        _subscriptions.Add(bus.Subscribe<SessionOpened>(this, OnOpened));
        _subscriptions.Add(bus.Subscribe<SessionClosed>(this, e => EndWork(e.SessionId)));
        _subscriptions.Add(bus.Subscribe<SessionBroken>(this, e => EndWork(e.SessionId)));
        _subscriptions.Add(bus.Subscribe<TransactionStateChanged>(this, OnTransaction));
        _subscriptions.Add(bus.Subscribe<LockMonitor.LockThresholdChanged>(this, OnLockThreshold));
    }

    /// <summary>칩 내용이 바뀌었다. 어느 스레드에서든 올 수 있다.</summary>
    public event EventHandler? Changed;

    /// <summary>미커밋이 <see cref="LongTransaction"/> 을 넘겼다. 트랜잭션마다 한 번.</summary>
    public event EventHandler? LongTransactionNoticed;

    public BlockerAlertState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <summary>모니터 세션이 열려 있는지(시험용).</summary>
    public bool HasMonitorSession => _monitor is not null;

    /// <summary>진행 중인 조회가 끝날 때까지 기다린다(시험용).</summary>
    public Task WhenIdleAsync() => _pollTask;

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        foreach (var s in _subscriptions)
        {
            s.Dispose();
        }

        StopWork();
        Task.Run(CloseMonitorNowAsync).GetAwaiter().GetResult();
    }

    private void OnLockThreshold(LockMonitor.LockThresholdChanged e)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _lockAlert = e.Exceeded ? e : null;
        }

        Recompute();
    }

    private void OnOpened(SessionOpened e)
    {
        if (e.Purpose != SessionPurpose.Work)
        {
            return; // 이 감시기가 연 모니터 세션과 전용 세션은 보지 않는다.
        }

        StopWork();
        _ = CloseMonitorNowAsync();
        lock (_gate)
        {
            _workId = e.SessionId;
            _profile = e.Profile;
            _sid = e.Server.SessionId;
            _off = e.Server.Capabilities.CanQueryPerformanceViews ? BlockerDisabledReason.None : BlockerDisabledReason.NoPrivilege;
        }
    }

    private void EndWork(Guid sessionId)
    {
        lock (_gate)
        {
            if (_workId != sessionId)
            {
                return;
            }

            _workId = null;
            _profile = null;
        }

        StopWork();
        _ = CloseMonitorNowAsync();
    }

    private void OnTransaction(TransactionStateChanged e)
    {
        bool start;
        lock (_gate)
        {
            if (_disposed || _workId != e.SessionId || _pending == e.Pending)
            {
                return;
            }

            _pending = e.Pending;
            start = e.Pending;
            if (start)
            {
                _epoch++;
                _pendingSinceTicks = _time.GetTimestamp();
                _longNoticed = false;
                _snapshot = null;
                _pollCts = new CancellationTokenSource();
                _closeTimer?.Dispose();
                _closeTimer = null;
                if (_off != BlockerDisabledReason.None && _profile is { } p && _off != BlockerDisabledReason.Unsupported)
                {
                    _offVisible = _off == BlockerDisabledReason.NoCredential || _disabledShown.Add(p.Id); // 권한 없음은 같은 접속에서 처음 한 번만 보인다.
                }
            }
        }

        if (start)
        {
            StartTimers();
        }
        else
        {
            StopWork();
            ScheduleClose();
        }

        Recompute();
    }

    private void StartTimers()
    {
        int epoch;
        CancellationToken ct;
        bool canPoll;
        lock (_gate)
        {
            epoch = _epoch;
            ct = _pollCts!.Token;
            canPoll = _off == BlockerDisabledReason.None;
            _longTimer = _time.CreateTimer(_ => OnLongTimer(epoch), null, LongTransaction, Timeout.InfiniteTimeSpan);
            if (canPoll)
            {
                _pollTimer = _time.CreateTimer(_ => StartPoll(epoch, ct), null, TimeSpan.Zero, PollInterval);
            }
        }
    }

    /// <summary>
    /// 저장된 비밀번호가 없어 꺼진 감시를 사용자 동작으로 켠다. <paramref name="openInteractive"/> 가 비밀번호를 물어 모니터 세션을 연다
    /// (취소하면 null 이고 회색 칩이 그대로 남는다). 켜졌으면 true.
    /// </summary>
    public async Task<bool> EnableAsync(Func<ConnectionProfile, CancellationToken, Task<ISession?>> openInteractive, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(openInteractive);
        ConnectionProfile? profile;
        lock (_gate)
        {
            if (_off != BlockerDisabledReason.NoCredential || _profile is null)
            {
                return false;
            }

            profile = _profile;
        }

        var session = await openInteractive(profile, ct).ConfigureAwait(false);
        if (session is null)
        {
            return false;
        }

        await _monitorGate.WaitAsync(ct).ConfigureAwait(false);
        var accepted = false;
        try
        {
            lock (_gate)
            {
                // 묻는 동안 접속이 바뀌었으면 새 세션은 버린다.
                accepted = !_disposed && _profile?.Id == profile.Id && _off == BlockerDisabledReason.NoCredential;
                if (accepted)
                {
                    _off = BlockerDisabledReason.None;
                    _offVisible = false;
                }
            }

            if (!accepted)
            {
                await session.DisposeAsync().ConfigureAwait(false);
                return false;
            }

            if (_monitor is not null)
            {
                await _monitor.DisposeAsync().ConfigureAwait(false);
            }

            _monitor = session;
        }
        finally
        {
            _monitorGate.Release();
        }

        lock (_gate)
        {
            if (_pending && _pollCts is { } cts && _pollTimer is null)
            {
                var current = _epoch;
                var token = cts.Token;
                _pollTimer = _time.CreateTimer(_ => StartPoll(current, token), null, TimeSpan.Zero, PollInterval);
            }
            else if (!_pending)
            {
                ScheduleCloseLocked();
            }
        }

        Recompute();
        return true;
    }

    private void StartPoll(int epoch, CancellationToken ct)
    {
        lock (_gate)
        {
            if (epoch != _epoch || !_pending)
            {
                return;
            }
        }

        if (Interlocked.CompareExchange(ref _polling, 1, 0) != 0)
        {
            return; // 앞 조회가 아직 끝나지 않았다. 겹치지 않게 이번 주기는 건너뛴다.
        }

        _pollTask = PollAsync(epoch, ct);
    }

    private async Task PollAsync(int epoch, CancellationToken ct)
    {
        try
        {
            await _monitorGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                ConnectionProfile? profile;
                long sid;
                lock (_gate)
                {
                    if (epoch != _epoch || !_pending || _off != BlockerDisabledReason.None)
                    {
                        return;
                    }

                    profile = _profile;
                    sid = _sid;
                }

                if (profile is null)
                {
                    return;
                }

                if (_monitor is null)
                {
                    _monitor = await _openMonitor(profile, ct).ConfigureAwait(false);
                    if (_monitor is null)
                    {
                        TurnOff(epoch, BlockerDisabledReason.NoCredential, "저장된 비밀번호가 없어 막고 있는 세션 감시를 켜지 못했다.");
                        return;
                    }
                }

                var snapshot = await _query(_monitor, sid, ct).ConfigureAwait(false);
                lock (_gate)
                {
                    if (epoch != _epoch || !_pending)
                    {
                        return;
                    }

                    _snapshot = snapshot;
                }

                Recompute();
            }
            finally
            {
                _monitorGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // 미커밋이 끝났거나 접속이 끊겨 취소됐다.
        }
        catch (Exception ex) when (MonitorAccessState.IsPrivilegeError(ex))
        {
            TurnOff(epoch, BlockerDisabledReason.NoPrivilege, "V$ 조회 권한이 없어 막고 있는 세션 감시를 끈다: " + FirstLine(ex.Message));
            await CloseMonitorNowAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is CatalogQueryNotFoundException or NotSupportedException)
        {
            TurnOff(epoch, BlockerDisabledReason.Unsupported, "이 서버 버전에는 막고 있는 세션 조회가 없어 감시를 끈다.");
            await CloseMonitorNowAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log("막고 있는 세션 조회 실패(다음 주기에 다시 시도): " + FirstLine(ex.Message));
            await CloseMonitorNowAsync().ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref _polling, 0);
        }
    }

    private void TurnOff(int epoch, BlockerDisabledReason reason, string logLine)
    {
        lock (_gate)
        {
            if (epoch != _epoch)
            {
                return;
            }

            _off = reason;
            _snapshot = null;
            _pollTimer?.Dispose();
            _pollTimer = null;
            if (reason != BlockerDisabledReason.Unsupported && _profile is { } p)
            {
                _offVisible = reason == BlockerDisabledReason.NoCredential || _disabledShown.Add(p.Id);
            }
        }

        _log(logLine);
        Recompute();
    }

    private void OnLongTimer(int epoch)
    {
        lock (_gate)
        {
            if (epoch != _epoch || !_pending || _longNoticed)
            {
                return;
            }

            _longNoticed = true;
        }

        Recompute();
        LongTransactionNoticed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>미커밋이 끝났거나 접속이 끊겼을 때: 타이머를 멈추고 진행 중인 조회를 취소한다.</summary>
    private void StopWork()
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            _pending = false;
            _epoch++;
            _snapshot = null;
            _longNoticed = false;
            _offVisible = false;
            _pollTimer?.Dispose();
            _pollTimer = null;
            _longTimer?.Dispose();
            _longTimer = null;
            cts = _pollCts;
            _pollCts = null;
        }

        cts?.Cancel(); // 토큰을 쥔 조회·타이머가 아직 있을 수 있어 Dispose 하지 않는다.
        Recompute();
    }

    private void ScheduleClose()
    {
        lock (_gate)
        {
            ScheduleCloseLocked();
        }
    }

    private void ScheduleCloseLocked()
    {
        if (_disposed)
        {
            return;
        }

        var epoch = _epoch;
        _closeTimer?.Dispose();
        _closeTimer = _time.CreateTimer(_ => CloseIfStillIdle(epoch), null, MonitorIdleClose, Timeout.InfiniteTimeSpan);
    }

    private void CloseIfStillIdle(int epoch)
    {
        lock (_gate)
        {
            if (_pending || epoch != _epoch)
            {
                return;
            }
        }

        _ = CloseMonitorNowAsync();
    }

    private async Task CloseMonitorNowAsync()
    {
        // 취소는 StopWork 가 이미 했다. 진행 중인 조회가 게이트를 놓은 뒤에 닫는다.
        await _monitorGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await DisposeMonitorAsync().ConfigureAwait(false);
        }
        finally
        {
            _monitorGate.Release();
        }
    }

    private async Task DisposeMonitorAsync()
    {
        var monitor = _monitor;
        _monitor = null;
        if (monitor is null)
        {
            return;
        }

        try
        {
            await monitor.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log("모니터 세션을 닫지 못했다: " + FirstLine(ex.Message));
        }
    }

    private void Recompute()
    {
        BlockerAlertState next;
        lock (_gate)
        {
            next = Compute();
            if (next == _state)
            {
                return;
            }

            _state = next;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private BlockerAlertState Compute()
    {
        // 내 세션이 막고 있으면 그것이 먼저, 아니면 Lock 모니터 임계치 알림.
        if (_pending && _snapshot is { BlockedCount: > 0 })
        {
            return ComputeWork();
        }

        if (_lockAlert is { } lockAlert)
        {
            return new BlockerAlertState(BlockerAlertKind.Blocking)
            {
                BlockedCount = lockAlert.Waiters,
                MaxWaitSeconds = lockAlert.MaxWaitSeconds,
                FromLockMonitor = true,
            };
        }

        return ComputeWork();
    }

    private BlockerAlertState ComputeWork()
    {
        if (!_pending)
        {
            return BlockerAlertState.Hidden;
        }

        var local = (int)_time.GetElapsedTime(_pendingSinceTicks).TotalMinutes;
        var minutes = _snapshot?.TransactionAgeSeconds is { } age ? (int)(age / 60) : local;
        if (_snapshot is { BlockedCount: > 0 } s)
        {
            return new BlockerAlertState(BlockerAlertKind.Blocking)
            {
                BlockedCount = s.BlockedCount,
                MinutesUntilKilled = s.MinutesUntilKilled,
                PendingMinutes = minutes,
            };
        }

        if (_longNoticed)
        {
            return new BlockerAlertState(BlockerAlertKind.LongTransaction) { PendingMinutes = Math.Max(minutes, (int)LongTransaction.TotalMinutes), Reason = _off };
        }

        return _offVisible && _off is BlockerDisabledReason.NoPrivilege or BlockerDisabledReason.NoCredential
            ? new BlockerAlertState(BlockerAlertKind.Disabled) { Reason = _off }
            : BlockerAlertState.Hidden;
    }

    private static string FirstLine(string message)
    {
        var i = message.IndexOf('\n', StringComparison.Ordinal);
        return (i < 0 ? message : message[..i]).Trim();
    }
}
