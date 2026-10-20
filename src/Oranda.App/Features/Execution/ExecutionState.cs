using System.Globalization;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.Features.Localization;
using Oranda.App.Shell;
using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Execution;
using Oranda.Core.History;
using Oranda.Core.Sessions;

namespace Oranda.App.Features.Execution;

/// <summary>작업 세션의 실행 상태(상태 표시줄 2번 구간).</summary>
public enum SessionRunState
{
    NoSession,
    Idle,
    Running,
    Cancelling,
    Broken,
}

/// <summary>
/// 실행 화면이 보여 주는 작업 세션 상태(ui-shell.md 1.2 의 2·3·4·6번 구간 + 문맥 키 <c>sessionBusy</c>·<c>txnPending</c>·
/// <c>readOnlySession</c>·<c>hasExecError</c>·<c>dedicatedSession</c>). 이벤트 버스(<c>SessionOpened</c> 등)만 보고 갱신한다 — 그래서 상태 표시줄 구간은
/// 시작 때 Oracle·Language 어셈블리를 만지지 않는다. 이벤트 버스마다 하나(<see cref="For"/>).
/// <para>
/// 매개변수 없는 멤버(<see cref="RunState"/>·<see cref="TxnPending"/>·<see cref="BeginRun()"/> 등)는 공유 작업 세션의 상태다.
/// 전용 세션 SQL 탭(D-017)은 세션마다 상태를 따로 두고(<see cref="AddDedicated"/>, 세션 ID 를 받는 멤버), 상태 표시줄·문맥 키는
/// 활성 탭의 세션(<see cref="Focus"/>)을 보여 준다(<c>Shown*</c> 멤버).
/// </para>
/// </summary>
public sealed class ExecutionState : ObservableObject
{
    private static readonly ConditionalWeakTable<IEventBus, ExecutionState> PerBus = [];
    private static readonly Lock Gate = new();

    private Guid? _sessionId;
    private ConnectionProfile? _profile;
    private SessionRunState _runState = SessionRunState.NoSession;
    private bool _txnPending;
    private bool _autoCommit;
    private string _lastRunText = string.Empty;
    private bool _hasExecError;
    private DbError? _lastError;
    private SqlStatement? _lastErrorStatement;
    private Guid? _lastErrorTab;
    private ScriptErrorMode _errorMode = ScriptErrorMode.Ask;
    private bool _dbmsOutputEnabled = true;
    private bool _blankLineDelimiter;
    private DateTime _startedUtc;
    private TimeSpan _elapsed;
    private DispatcherTimer? _timer;
    private ContextKeyService? _keys;
    private readonly Dictionary<Guid, Track> _dedicated = [];
    private readonly HashSet<Guid> _released = []; // 닫은 전용 세션(세션 ID 는 다시 쓰이지 않는다)
    private Guid? _focus;

    /// <summary>전용 세션 하나의 실행 상태(공유 작업 세션의 상태는 위 필드).</summary>
    private sealed class Track(ConnectionProfile profile)
    {
        public ConnectionProfile Profile { get; } = profile;

        public SessionRunState Run { get; set; } = SessionRunState.Idle;

        public bool TxnPending { get; set; }

        public DateTime StartedUtc { get; set; }

        public TimeSpan Elapsed { get; set; }

        public string LastRunText { get; set; } = string.Empty;

        public bool Busy => Run is SessionRunState.Running or SessionRunState.Cancelling;

        /// <summary>탭 닫기 확인 중(D-017). 실행 중은 아니지만 새 실행·커밋을 받지 않는다(확인한 뒤 닫힐 세션이다).</summary>
        public bool Closing { get; set; }
    }

    public ExecutionState(IEventBus? bus = null)
    {
        if (bus is null)
        {
            return;
        }

        bus.Subscribe<SessionOpened>(this, e => Post(() => OnOpened(e)));
        bus.Subscribe<SessionClosed>(this, e => Post(() => OnClosed(e)));
        bus.Subscribe<SessionBroken>(this, e => Post(() => OnBroken(e)));
        bus.Subscribe<TransactionStateChanged>(this, e => Post(() => OnTransaction(e)));
        bus.Subscribe<StatementExecuted>(this, e => Post(() => OnExecuted(e)));
    }

    /// <summary>이 이벤트 버스의 상태. 버스가 없으면(시험) 버스 없는 공용 상태.</summary>
    public static ExecutionState For(IEventBus? bus)
    {
        lock (Gate)
        {
            if (bus is null)
            {
                return Fallback ??= new ExecutionState();
            }

            if (!PerBus.TryGetValue(bus, out var state))
            {
                state = new ExecutionState(bus);
                PerBus.Add(bus, state);
            }

            return state;
        }
    }

    private static ExecutionState? Fallback { get; set; }

    public Guid? SessionId => _sessionId;

    public ConnectionProfile? Profile => _profile;

    public SessionRunState RunState
    {
        get => _runState;
        private set
        {
            if (SetProperty(ref _runState, value))
            {
                OnPropertyChanged(nameof(Busy));
                OnPropertyChanged(nameof(SessionText));
                OnPropertyChanged(nameof(HasSession));
                RaiseShown();
            }
        }
    }

    public bool HasSession => _runState != SessionRunState.NoSession;

    /// <summary>공유 작업 세션이 실행 중이거나 취소 중. 문맥 키 <c>sessionBusy</c> 는 <see cref="ShownBusy"/>.</summary>
    public bool Busy => _runState is SessionRunState.Running or SessionRunState.Cancelling;

    public bool ReadOnly => _profile?.ReadOnly ?? false;

    public bool TxnPending
    {
        get => _txnPending;
        private set
        {
            if (SetProperty(ref _txnPending, value))
            {
                OnPropertyChanged(nameof(TransactionText));
                RaiseShown();
            }
        }
    }

    /// <summary>자동 커밋(화면 수준: 켜면 성공한 변경 문장 뒤에 바로 커밋한다). 전용 세션 탭에도 같이 적용한다.</summary>
    public bool AutoCommit
    {
        get => _autoCommit;
        set
        {
            if (SetProperty(ref _autoCommit, value))
            {
                OnPropertyChanged(nameof(TransactionText));
                RaiseShown();
            }
        }
    }

    public string LastRunText
    {
        get => _lastRunText;
        private set
        {
            if (SetProperty(ref _lastRunText, value))
            {
                RaiseShown();
            }
        }
    }

    public bool HasExecError
    {
        get => _hasExecError;
        private set
        {
            if (SetProperty(ref _hasExecError, value))
            {
                SyncKeys();
            }
        }
    }

    public DbError? LastError => _lastError;

    public SqlStatement? LastErrorStatement => _lastErrorStatement;

    /// <summary>마지막 오류가 난 편집기 탭(오류 문장으로 이동).</summary>
    public Guid? LastErrorTab => _lastErrorTab;

    public ScriptErrorMode ErrorMode
    {
        get => _errorMode;
        set => SetProperty(ref _errorMode, value);
    }

    /// <summary>DBMS_OUTPUT 창에 줄을 모을지(<c>oranda.exec.toggleDbmsOutput</c>).</summary>
    public bool DbmsOutputEnabled
    {
        get => _dbmsOutputEnabled;
        set => SetProperty(ref _dbmsOutputEnabled, value);
    }

    /// <summary>빈 줄로 문장을 나눌지(<c>oranda.exec.toggleBlankLineDelimiter</c>).</summary>
    public bool BlankLineDelimiter
    {
        get => _blankLineDelimiter;
        set => SetProperty(ref _blankLineDelimiter, value);
    }

    public TimeSpan Elapsed
    {
        get => _elapsed;
        private set
        {
            if (SetProperty(ref _elapsed, value))
            {
                OnPropertyChanged(nameof(SessionText));
                RaiseShown();
            }
        }
    }

    /// <summary>공유 작업 세션의 상태 글. 상태 표시줄 2번 구간은 <see cref="ShownSessionText"/>.</summary>
    public string SessionText => DescribeSession(_runState, _elapsed);

    /// <summary>공유 작업 세션의 트랜잭션 글. 상태 표시줄 3번 구간은 <see cref="ShownTransactionText"/>.</summary>
    public string TransactionText => DescribeTransaction(HasSession, _txnPending);

    // ---- 보이는 세션(활성 탭의 세션) ------------------------------------------------------------

    /// <summary>활성 탭이 전용 세션 탭이면 그 세션 ID. 아니면 null(공유 작업 세션을 보여 준다).</summary>
    public Guid? FocusedDedicated => Shown is null ? null : _focus;

    /// <summary>보이는 세션이 전용 세션이다(상태 표시줄 「전용 세션」, 문맥 키 <c>dedicatedSession</c>).</summary>
    public bool IsDedicatedShown => Shown is not null;

    private Track? Shown => _focus is { } id && _dedicated.TryGetValue(id, out var t) ? t : null;

    public SessionRunState ShownRunState => Shown?.Run ?? _runState;

    public bool ShownHasSession => ShownRunState != SessionRunState.NoSession;

    /// <summary>보이는 세션이 실행 중이거나 취소 중(문맥 키 <c>sessionBusy</c>).</summary>
    public bool ShownBusy => Shown is { } t ? t.Busy || t.Closing : Busy;

    /// <summary>보이는 세션에 미커밋 트랜잭션이 있다(문맥 키 <c>txnPending</c>).</summary>
    public bool ShownTxnPending => Shown?.TxnPending ?? _txnPending;

    /// <summary>보이는 세션의 접속(확인 대화상자 머리 띠).</summary>
    public ConnectionProfile? ShownProfile => Shown?.Profile ?? _profile;

    public bool ShownReadOnly => ShownProfile?.ReadOnly ?? false;

    public string ShownLastRunText => Shown?.LastRunText ?? _lastRunText;

    /// <summary>상태 표시줄 2번 구간 글. 전용 세션 탭이면 앞에 「전용 세션」을 붙인다.</summary>
    public string ShownSessionText => Shown is { } t ? UiText.Get("Execution.status.dedicated", "전용 세션") + " · " + DescribeSession(t.Run, t.Elapsed) : SessionText;

    /// <summary>상태 표시줄 3번 구간 글. 비어 있으면 구간을 접는다.</summary>
    public string ShownTransactionText => Shown is { } t ? DescribeTransaction(true, t.TxnPending) : TransactionText;

    /// <summary>문맥 키를 이 값들로 맞출 대상(셸의 <see cref="ContextKeyService"/>).</summary>
    public void UseKeys(ContextKeyService? keys)
    {
        if (keys is null || ReferenceEquals(_keys, keys))
        {
            return;
        }

        _keys = keys;
        SyncKeys();
    }

    public static string FormatElapsed(TimeSpan t) =>
        t.TotalHours >= 1
            ? t.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : t.ToString(@"mm\:ss", CultureInfo.InvariantCulture);

    private static string DescribeSession(SessionRunState state, TimeSpan elapsed) => state switch
    {
        SessionRunState.Idle => "연결됨",
        SessionRunState.Running => "실행 중 " + FormatElapsed(elapsed),
        SessionRunState.Cancelling => "취소 중",
        SessionRunState.Broken => "끊김",
        _ => string.Empty,
    };

    private string DescribeTransaction(bool hasSession, bool pending) => !hasSession
        ? string.Empty
        : pending ? "미커밋" : _autoCommit ? "자동 커밋" : string.Empty;

    // ---- 컨트롤러가 부른다 -------------------------------------------------------------------

    /// <summary>실행을 시작했다. 경과 시간을 세기 시작한다.</summary>
    public void BeginRun()
    {
        _startedUtc = DateTime.UtcNow;
        Elapsed = TimeSpan.Zero;
        RunState = SessionRunState.Running;
        ClearError();
        StartTimer();
    }

    public void MarkCancelling()
    {
        if (_runState == SessionRunState.Running)
        {
            RunState = SessionRunState.Cancelling;
        }
    }

    public void EndRun()
    {
        if (_runState is SessionRunState.Running or SessionRunState.Cancelling)
        {
            RunState = SessionRunState.Idle;
        }

        StopTimerIfIdle();
    }

    public void SetError(SqlStatement stmt, DbError? error, Guid? tab)
    {
        _lastError = error;
        _lastErrorStatement = stmt;
        _lastErrorTab = tab;
        HasExecError = true;
    }

    /// <summary>커밋·롤백 뒤처럼 컨트롤러가 알고 있는 트랜잭션 상태를 반영한다.</summary>
    public void ApplyTransaction(bool pending) => TxnPending = pending;

    public void ClearError()
    {
        _lastError = null;
        _lastErrorStatement = null;
        _lastErrorTab = null;
        HasExecError = false;
    }

    /// <summary>끝 시각을 지금으로 보고 경과 시간을 갱신한다(시험이 시계를 쓰지 않고 부른다).</summary>
    public void Tick()
    {
        var now = DateTime.UtcNow;
        foreach (var t in _dedicated.Values.Where(t => t.Busy))
        {
            t.Elapsed = now - t.StartedUtc;
        }

        Elapsed = now - _startedUtc;
        RaiseShown();
    }

    // ---- 세션별(전용 세션 탭, D-017) ------------------------------------------------------------
    // 세션 ID 가 전용 세션이 아니면(공유 작업 세션) 위의 매개변수 없는 멤버와 같다.

    /// <summary>
    /// 전용 세션의 상태를 만든다. 세션을 연 쪽이 돌려받은 세션으로 바로 부른다(<c>SessionOpened</c> 는 UI 스레드로 늦게 올 수 있다).
    /// </summary>
    public void AddDedicated(Guid sessionId, ConnectionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (_dedicated.TryAdd(sessionId, new Track(profile)))
        {
            RaiseShown();
        }
    }

    /// <summary>전용 세션의 상태를 지운다(세션을 닫았다).</summary>
    public void RemoveDedicated(Guid sessionId)
    {
        if (_dedicated.Remove(sessionId))
        {
            _released.Add(sessionId); // 늦게 끝난 실행의 상태 갱신이 공유 작업 세션으로 넘어가지 않게
            StopTimerIfIdle();
            RaiseShown();
        }
    }

    public bool IsDedicated(Guid sessionId) => _dedicated.ContainsKey(sessionId);

    /// <summary>상태 표시줄·문맥 키가 보여 줄 세션: 활성 탭의 전용 세션 ID. null 이면 공유 작업 세션.</summary>
    public void Focus(Guid? dedicatedSessionId)
    {
        if (_focus == dedicatedSessionId)
        {
            return;
        }

        _focus = dedicatedSessionId;
        RaiseShown();
    }

    /// <summary>실행 중·취소 중이거나 닫기 확인 중(<see cref="SetClosing"/>)이면 true. 이미 닫은 전용 세션은 false.</summary>
    public bool IsBusy(Guid sessionId) => _dedicated.TryGetValue(sessionId, out var t) ? t.Busy || t.Closing : !_released.Contains(sessionId) && Busy;

    /// <summary>실제로 실행 중·취소 중이면 true(<see cref="IsBusy"/> 와 달리 닫기 확인 중은 넣지 않는다).</summary>
    public bool IsRunning(Guid sessionId) => _dedicated.TryGetValue(sessionId, out var t) ? t.Busy : !_released.Contains(sessionId) && Busy;

    public bool IsBroken(Guid sessionId) => _dedicated.TryGetValue(sessionId, out var t)
        ? t.Run == SessionRunState.Broken
        : !_released.Contains(sessionId) && _runState == SessionRunState.Broken;

    public bool IsTxnPending(Guid sessionId) => _dedicated.TryGetValue(sessionId, out var t) ? t.TxnPending : !_released.Contains(sessionId) && _txnPending;

    /// <summary>
    /// 전용 세션 탭의 닫기 확인이 도는 동안 그 세션을 「바쁨」으로 둔다: 확인을 마친 뒤 닫히기 전에 새 실행·커밋이 끼어들면
    /// 미커밋 없이 닫힌다고 판단한 세션에 변경이 남는다. 상태 표시줄 글은 바꾸지 않는다. 전용 세션이 아니면 아무것도 안 한다.
    /// </summary>
    public void SetClosing(Guid sessionId, bool closing)
    {
        if (_dedicated.TryGetValue(sessionId, out var t) && t.Closing != closing)
        {
            t.Closing = closing;
            RaiseShown();
        }
    }

    public void BeginRun(Guid sessionId)
    {
        if (!_dedicated.TryGetValue(sessionId, out var t))
        {
            if (!_released.Contains(sessionId))
            {
                BeginRun();
            }

            return;
        }

        t.StartedUtc = DateTime.UtcNow;
        t.Elapsed = TimeSpan.Zero;
        t.Run = SessionRunState.Running;
        ClearError();
        StartTimer();
        RaiseShown();
    }

    public void MarkCancelling(Guid sessionId)
    {
        if (!_dedicated.TryGetValue(sessionId, out var t))
        {
            if (!_released.Contains(sessionId))
            {
                MarkCancelling();
            }

            return;
        }

        if (t.Run == SessionRunState.Running)
        {
            t.Run = SessionRunState.Cancelling;
            RaiseShown();
        }
    }

    public void EndRun(Guid sessionId)
    {
        if (!_dedicated.TryGetValue(sessionId, out var t))
        {
            if (!_released.Contains(sessionId))
            {
                EndRun();
            }

            return;
        }

        if (t.Busy)
        {
            t.Run = SessionRunState.Idle;
        }

        StopTimerIfIdle();
        RaiseShown();
    }

    public void ApplyTransaction(Guid sessionId, bool pending)
    {
        if (!_dedicated.TryGetValue(sessionId, out var t))
        {
            if (!_released.Contains(sessionId))
            {
                ApplyTransaction(pending);
            }

            return;
        }

        if (t.TxnPending != pending)
        {
            t.TxnPending = pending;
            RaiseShown();
        }
    }

    // ---- 이벤트 ----------------------------------------------------------------------------

    private void OnOpened(SessionOpened e)
    {
        if (e.Purpose != SessionPurpose.Work)
        {
            return; // 모니터·전용 세션은 공유 작업 세션을 바꾸지 않는다(전용 세션은 연 쪽이 AddDedicated 로 등록).
        }

        _sessionId = e.SessionId;
        _profile = e.Profile;
        _txnPending = false;
        ClearError();
        LastRunText = string.Empty;
        OnPropertyChanged(nameof(ReadOnly));
        OnPropertyChanged(nameof(Profile));
        OnPropertyChanged(nameof(TxnPending));
        OnPropertyChanged(nameof(TransactionText));
        RunState = SessionRunState.Idle;
    }

    private void OnClosed(SessionClosed e)
    {
        if (_dedicated.ContainsKey(e.SessionId))
        {
            RemoveDedicated(e.SessionId);
            return;
        }

        if (e.SessionId != _sessionId)
        {
            return;
        }

        _sessionId = null;
        _profile = null;
        _txnPending = false;
        OnPropertyChanged(nameof(ReadOnly));
        OnPropertyChanged(nameof(Profile));
        OnPropertyChanged(nameof(TxnPending));
        OnPropertyChanged(nameof(TransactionText));
        RunState = SessionRunState.NoSession;
        StopTimerIfIdle();
    }

    private void OnBroken(SessionBroken e)
    {
        if (_dedicated.TryGetValue(e.SessionId, out var t))
        {
            t.TxnPending = e.HadPendingTransaction;
            t.Run = SessionRunState.Broken;
            StopTimerIfIdle();
            RaiseShown();
            return;
        }

        if (e.SessionId != _sessionId)
        {
            return;
        }

        TxnPending = e.HadPendingTransaction;
        RunState = SessionRunState.Broken;
        StopTimerIfIdle();
    }

    private void OnTransaction(TransactionStateChanged e)
    {
        if (_dedicated.ContainsKey(e.SessionId))
        {
            ApplyTransaction(e.SessionId, e.Pending);
        }
        else if (e.SessionId == _sessionId)
        {
            TxnPending = e.Pending;
        }
    }

    private void OnExecuted(StatementExecuted e)
    {
        // 상태 표시줄의 「마지막 실행」은 사용자가 편집기에서 보낸 실행만 센다(스키마 브라우저·모니터의 내부 질의 제외).
        if (e.Options.SkipHistory || e.Options.Source is not (HistorySource.Editor or HistorySource.Script or HistorySource.GridEdit))
        {
            return;
        }

        if (_dedicated.TryGetValue(e.SessionId, out var t))
        {
            t.LastRunText = DescribeRun(e.Result);
            RaiseShown();
        }
        else if (e.SessionId == _sessionId)
        {
            LastRunText = DescribeRun(e.Result);
        }
    }

    /// <summary>「마지막 실행」 글: 행 수·소요 시간·오류 번호.</summary>
    public static string DescribeRun(ExecutionResult r)
    {
        var time = r.Elapsed.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture) + "초";
        return r.Status switch
        {
            ExecutionStatus.Succeeded when r.RowsAffected is { } n => $"{n.ToString("N0", CultureInfo.InvariantCulture)}행 · {time}",
            ExecutionStatus.Succeeded => "완료 · " + time,
            ExecutionStatus.Failed => (r.Error is { Number: > 0 } err ? $"ORA-{err.Number:00000}" : "오류") + " · " + time,
            ExecutionStatus.Cancelled => "취소됨",
            _ => "거부됨",
        };
    }

    // ---- 내부 ------------------------------------------------------------------------------

    /// <summary>보이는 세션 값(<c>Shown*</c>)이 바뀌었을 수 있다: 알리고 문맥 키를 맞춘다.</summary>
    private void RaiseShown()
    {
        OnPropertyChanged(nameof(ShownSessionText));
        OnPropertyChanged(nameof(ShownTransactionText));
        OnPropertyChanged(nameof(ShownTxnPending));
        SyncKeys();
    }

    private void SyncKeys()
    {
        if (_keys is not { } keys)
        {
            return;
        }

        keys.Set("sessionBusy", ShownBusy);
        keys.Set("txnPending", ShownTxnPending);
        keys.Set("readOnlySession", ShownReadOnly);
        keys.Set("hasExecError", _hasExecError);
        keys.Set("dedicatedSession", Shown is { Run: not SessionRunState.Broken });
    }

    private void StartTimer()
    {
        if (_timer is not null || Application.Current is null)
        {
            return;
        }

        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => Tick());
        _timer.Start();
    }

    /// <summary>실행 중인 세션이 하나도 없으면 경과 시간 타이머를 멈춘다.</summary>
    private void StopTimerIfIdle()
    {
        if (Busy || _dedicated.Values.Any(t => t.Busy))
        {
            return;
        }

        _timer?.Stop();
        _timer = null;
    }

    /// <summary>버스 이벤트는 어느 스레드에서든 온다. 앱이 떠 있으면 UI 스레드로 옮긴다.</summary>
    private static void Post(Action action)
    {
        if (Application.Current is null || Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(action);
        }
    }
}
