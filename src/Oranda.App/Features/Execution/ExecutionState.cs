using System.Globalization;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
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
/// <c>readOnlySession</c>·<c>hasExecError</c>). 이벤트 버스(<c>SessionOpened</c> 등)만 보고 갱신한다 — 그래서 상태 표시줄 구간은
/// 시작 때 Oracle·Language 어셈블리를 만지지 않는다. 이벤트 버스마다 하나(<see cref="For"/>).
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
                SyncKeys();
            }
        }
    }

    public bool HasSession => _runState != SessionRunState.NoSession;

    /// <summary>실행 중이거나 취소 중(문맥 키 <c>sessionBusy</c>).</summary>
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
                SyncKeys();
            }
        }
    }

    /// <summary>자동 커밋(화면 수준: 켜면 성공한 변경 문장 뒤에 바로 커밋한다).</summary>
    public bool AutoCommit
    {
        get => _autoCommit;
        set
        {
            if (SetProperty(ref _autoCommit, value))
            {
                OnPropertyChanged(nameof(TransactionText));
            }
        }
    }

    public string LastRunText
    {
        get => _lastRunText;
        private set => SetProperty(ref _lastRunText, value);
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
            }
        }
    }

    /// <summary>상태 표시줄 2번 구간 글.</summary>
    public string SessionText => _runState switch
    {
        SessionRunState.Idle => "연결됨",
        SessionRunState.Running => "실행 중 " + FormatElapsed(_elapsed),
        SessionRunState.Cancelling => "취소 중",
        SessionRunState.Broken => "끊김",
        _ => string.Empty,
    };

    /// <summary>상태 표시줄 3번 구간 글. 비어 있으면 구간을 접는다.</summary>
    public string TransactionText => !HasSession
        ? string.Empty
        : _txnPending ? "미커밋" : _autoCommit ? "자동 커밋" : string.Empty;

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

    // ---- 컨트롤러가 부른다 -------------------------------------------------------------------

    /// <summary>실행을 시작했다. 경과 시간을 세기 시작한다.</summary>
    public void BeginRun()
    {
        _startedUtc = DateTime.UtcNow;
        Elapsed = TimeSpan.Zero;
        RunState = SessionRunState.Running;
        HasExecError = false;
        _lastError = null;
        _lastErrorStatement = null;
        _lastErrorTab = null;
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
        StopTimer();
        if (_runState is SessionRunState.Running or SessionRunState.Cancelling)
        {
            RunState = SessionRunState.Idle;
        }
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
    public void Tick() => Elapsed = DateTime.UtcNow - _startedUtc;

    // ---- 이벤트 ----------------------------------------------------------------------------

    private void OnOpened(SessionOpened e)
    {
        if (e.Purpose != SessionPurpose.Work)
        {
            return;
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
        if (e.SessionId != _sessionId)
        {
            return;
        }

        StopTimer();
        _sessionId = null;
        _profile = null;
        _txnPending = false;
        OnPropertyChanged(nameof(ReadOnly));
        OnPropertyChanged(nameof(Profile));
        OnPropertyChanged(nameof(TxnPending));
        OnPropertyChanged(nameof(TransactionText));
        RunState = SessionRunState.NoSession;
    }

    private void OnBroken(SessionBroken e)
    {
        if (e.SessionId != _sessionId)
        {
            return;
        }

        StopTimer();
        TxnPending = e.HadPendingTransaction;
        RunState = SessionRunState.Broken;
    }

    private void OnTransaction(TransactionStateChanged e)
    {
        if (e.SessionId == _sessionId)
        {
            TxnPending = e.Pending;
        }
    }

    private void OnExecuted(StatementExecuted e)
    {
        // 상태 표시줄의 「마지막 실행」은 사용자가 편집기에서 보낸 실행만 센다(스키마 브라우저·모니터의 내부 질의 제외).
        if (e.SessionId != _sessionId || e.Options.SkipHistory || e.Options.Source is not (HistorySource.Editor or HistorySource.Script or HistorySource.GridEdit))
        {
            return;
        }

        LastRunText = DescribeRun(e.Result);
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

    private void SyncKeys()
    {
        if (_keys is not { } keys)
        {
            return;
        }

        keys.Set("sessionBusy", Busy);
        keys.Set("txnPending", _txnPending);
        keys.Set("readOnlySession", ReadOnly);
        keys.Set("hasExecError", _hasExecError);
    }

    private void StartTimer()
    {
        StopTimer();
        if (Application.Current is null)
        {
            return;
        }

        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => Tick());
        _timer.Start();
    }

    private void StopTimer()
    {
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
