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
using Oranda.Core.Features;
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
/// 실행 화면이 보여 주는 세션 상태(ui-shell.md 1.2 의 2·3·4·6번 구간 + 문맥 키 <c>sessionBusy</c>·<c>txnPending</c>·
/// <c>readOnlySession</c>·<c>hasExecError</c>·<c>dedicatedSession</c>). 이벤트 버스(<c>SessionOpened</c> 등)만 보고 갱신한다 — 그래서 상태 표시줄 구간은
/// 시작 때 Oracle·Language 어셈블리를 만지지 않는다. 이벤트 버스마다 하나(<see cref="For"/>).
/// <para>
/// 세션마다 상태를 따로 둔다: 열린 작업 세션(접속, CONN-06)마다 하나(<c>SessionOpened</c> 로 생긴다), 전용 세션 SQL 탭(D-017)마다 하나
/// (<see cref="AddDedicated"/>). 매개변수 없는 멤버(<see cref="RunState"/>·<see cref="TxnPending"/>·<see cref="BeginRun()"/> 등)는
/// 「지금 접속」 작업 세션(<see cref="UsePrimary"/>)의 상태이고, 세션 ID 를 받는 멤버는 그 세션의 상태다.
/// 상태 표시줄·문맥 키는 활성 문서의 세션(<see cref="Focus"/>)을 보여 준다(<c>Shown*</c> 멤버).
/// </para>
/// </summary>
public sealed class ExecutionState : ObservableObject
{
    private static readonly ConditionalWeakTable<IEventBus, ExecutionState> PerBus = [];
    private static readonly Lock Gate = new();

    private static readonly string[] PrimaryProperties =
    [
        nameof(SessionId), nameof(Profile), nameof(RunState), nameof(Busy), nameof(HasSession), nameof(ReadOnly),
        nameof(TxnPending), nameof(TransactionText), nameof(SessionText), nameof(LastRunText), nameof(Elapsed),
    ];

    private readonly Dictionary<Guid, Track> _tracks = [];
    private readonly HashSet<Guid> _released = []; // 닫은 세션(세션 ID 는 다시 쓰이지 않는다)
    private readonly Track _none = new(null, dedicated: false) { Run = SessionRunState.NoSession }; // 지금 접속이 없을 때
    private Guid? _sessionId;
    private Guid? _focus;
    private bool _focusNone;
    private Track? _pending; // 첫 실행 때 자기 세션을 열 워크스페이스(TSK-14-01): 아직 세션이 없어 남의 실행에 막히지 않는다
    private bool _autoCommit;
    private bool _hasExecError;
    private DbError? _lastError;
    private SqlStatement? _lastErrorStatement;
    private Guid? _lastErrorTab;
    private ScriptErrorMode _errorMode = ScriptErrorMode.Ask;
    private bool _dbmsOutputEnabled = true;
    private bool _blankLineDelimiter;
    private DispatcherTimer? _timer;
    private ContextKeyService? _keys;
    private IFeatureGate? _features;

    /// <summary>세션 하나의 실행 상태.</summary>
    private sealed class Track(ConnectionProfile? profile, bool dedicated)
    {
        public ConnectionProfile? Profile { get; } = profile;

        /// <summary>전용 세션 SQL 탭(D-017)의 세션. 아니면 작업 세션(접속).</summary>
        public bool Dedicated { get; } = dedicated;

        public SessionRunState Run { get; set; } = SessionRunState.Idle;

        public bool TxnPending { get; set; }

        public DateTime StartedUtc { get; set; }

        public TimeSpan Elapsed { get; set; }

        /// <summary>마지막 실행의 요약. 글은 보일 때 지금 언어로 만든다.</summary>
        public RunSummary? LastRun { get; set; }

        public string LastRunText => LastRun is { } run ? DescribeRun(run) : string.Empty;

        public bool Busy => Run is SessionRunState.Running or SessionRunState.Cancelling;

        /// <summary>탭 닫기 확인 중(D-017). 실행 중은 아니지만 새 실행·커밋을 받지 않는다(확인한 뒤 닫힐 세션이다).</summary>
        public bool Closing { get; set; }

        /// <summary>지금 실행을 시작한 작업 공간(<see cref="ActiveWorkspace"/>, TSK-14-03). 모르면 null.</summary>
        public object? Owner { get; set; }
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

    /// <summary>지금 접속 작업 세션의 상태. 없으면 「세션 없음」 상태(그래도 실행 시작·끝은 받는다).</summary>
    private Track P => _sessionId is { } id && _tracks.TryGetValue(id, out var t) ? t : _none;

    /// <summary>지금 접속 작업 세션 ID.</summary>
    public Guid? SessionId => _sessionId;

    public ConnectionProfile? Profile => P.Profile;

    public SessionRunState RunState => P.Run;

    public bool HasSession => RunState != SessionRunState.NoSession;

    /// <summary>지금 접속이 실행 중이거나 취소 중. 문맥 키 <c>sessionBusy</c> 는 <see cref="ShownBusy"/>.</summary>
    public bool Busy => P.Busy;

    public bool ReadOnly => P.Profile?.ReadOnly ?? false;

    public bool TxnPending => P.TxnPending;

    /// <summary>자동 커밋(화면 수준: 켜면 성공한 변경 문장 뒤에 바로 커밋한다). 모든 접속·전용 세션 탭에 같이 적용한다.</summary>
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

    public string LastRunText => P.LastRunText;

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

    public TimeSpan Elapsed => P.Elapsed;

    /// <summary>지금 접속의 상태 글. 상태 표시줄 2번 구간은 <see cref="ShownSessionText"/>.</summary>
    public string SessionText => DescribeSession(P.Run, P.Elapsed);

    /// <summary>지금 접속의 트랜잭션 글. 상태 표시줄 3번 구간은 <see cref="ShownTransactionText"/>.</summary>
    public string TransactionText => DescribeTransaction(HasSession, P.TxnPending);

    /// <summary>미커밋 트랜잭션이 있는 작업 세션(접속) ID. 앱 종료 확인이 접속마다 묻는다.</summary>
    public IReadOnlyList<Guid> PendingWorkSessions => [.. _tracks.Where(kv => !kv.Value.Dedicated && kv.Value.TxnPending).Select(kv => kv.Key)];

    // ---- 보이는 세션(활성 문서의 세션) -----------------------------------------------------------

    /// <summary>보이는 세션이 전용 세션이면 그 ID. 아니면 null.</summary>
    public Guid? FocusedDedicated => Shown is { Dedicated: true } ? _focus : null;

    /// <summary>보이는 세션이 없다(<see cref="FocusNothing"/>: 활성 SQL 탭에 묶인 접속이 열려 있지 않다).</summary>
    public bool FocusedNothing => _focusNone;

    /// <summary>보이는 세션 ID(<see cref="Focus"/> 로 고른 것, 없으면 지금 접속). <see cref="FocusNothing"/>·<see cref="FocusPending"/> 이면 null.</summary>
    public Guid? FocusedSessionId => _focusNone || _pending is not null ? null : _focus ?? _sessionId;

    /// <summary>보이는 문서가 첫 실행 때 자기 세션을 연다(<see cref="FocusPending"/>, TSK-14-01).</summary>
    public bool FocusedPending => _pending is not null;

    /// <summary>보이는 세션이 전용 세션이다(상태 표시줄 「전용 세션」, 문맥 키 <c>dedicatedSession</c>).</summary>
    public bool IsDedicatedShown => Shown is { Dedicated: true };

    /// <summary>
    /// 보이는 세션: <see cref="FocusNothing"/> 이면 없음(접속이 닫힌 탭), <see cref="Focus"/> 로 고른 세션이 있으면 그것, 아니면 지금 접속.
    /// </summary>
    private Track? Shown => _focusNone ? null : _pending ?? (_focus is { } id && _tracks.TryGetValue(id, out var t) ? t : P);

    public SessionRunState ShownRunState => Shown?.Run ?? SessionRunState.NoSession;

    public bool ShownHasSession => ShownRunState != SessionRunState.NoSession;

    /// <summary>보이는 세션이 실행 중이거나 취소 중(문맥 키 <c>sessionBusy</c>).</summary>
    public bool ShownBusy => Shown is { } t && (t.Busy || t.Closing);

    /// <summary>
    /// 보이는 세션이 다른 작업 공간이 시작한 실행으로 바쁘다(문맥 키 <c>sessionBusyElsewhere</c>, TSK-14-03): 함께 쓰는 세션이라 이 작업 공간의
    /// 실행은 막히지만, 실행 명령은 켜 두고 누르면 「새 세션에서 실행」 안내 막대를 보인다. 시작한 작업 공간을 모르면 false(지금처럼 꺼짐).
    /// </summary>
    public bool ShownBusyElsewhere => Shown is { Busy: true, Owner: { } owner } && ActiveWorkspace?.Invoke() is { } active && !ReferenceEquals(owner, active)
        && (OffersNewSession?.Invoke() ?? true);

    /// <summary>
    /// 함께 쓰는 세션이 바쁠 때 「새 세션에서 실행」 을 권할지(세션 라우터가 넣는다: 설정 「워크스페이스마다 새 세션」). 끄면 WP-14 전처럼 실행이 꺼지고
    /// 취소가 켜진다(TRD 14.5). null 이면 권한다.
    /// </summary>
    public Func<bool>? OffersNewSession { get; set; }

    /// <summary>실행을 시작하는 작업 공간(조립·세션 라우터가 넣는다: 활성 SQL 작업 공간, 아니면 null). 실행을 시작할 때 기억한다.</summary>
    public Func<object?>? ActiveWorkspace { get; set; }

    /// <summary>세션의 지금 실행을 시작한 작업 공간(<see cref="ActiveWorkspace"/>). 실행 중이 아니거나 모르면 null.</summary>
    public object? RunOwnerOf(Guid sessionId) => TrackOf(sessionId) is { Busy: true } t ? t.Owner : null;

    /// <summary>
    /// 상태 표시줄 2번 구간 앞에 붙일 세션 범위 글(TSK-14-03, 세션 라우터가 넣는다: 「워크스페이스 세션」·「공유 세션 · 다른 워크스페이스 1개와 함께」).
    /// 인자 = 보이는 작업 세션 ID. null 이면 붙이지 않는다(작업 공간 하나·세션 하나면 WP-14 전과 같다).
    /// </summary>
    public Func<Guid, string?>? ScopeOf { get; set; }

    /// <summary>보이는 세션에 미커밋 트랜잭션이 있다(문맥 키 <c>txnPending</c>).</summary>
    public bool ShownTxnPending => Shown?.TxnPending ?? false;

    /// <summary>보이는 세션의 접속(확인 대화상자 머리 띠).</summary>
    public ConnectionProfile? ShownProfile => Shown?.Profile;

    public bool ShownReadOnly => ShownProfile?.ReadOnly ?? false;

    /// <summary>조회 전용 Role 하나만 골랐다(D-006, <see cref="IFeatureGate.ReadOnlyRole"/>). 게이트가 없으면 false.</summary>
    public bool ReadOnlyRole => _features?.ReadOnlyRole ?? false;

    /// <summary>문맥 키 <c>readOnlySession</c>: 보이는 세션의 프로필이 읽기 전용이거나 조회 전용 Role(TRD 10.7.4). 세션이 없어도 Role 이면 참.</summary>
    public bool ReadOnlySessionKey => ShownReadOnly || ReadOnlyRole;

    public string ShownLastRunText => Shown?.LastRunText ?? string.Empty;

    /// <summary>
    /// 상태 표시줄 2번 구간 글. 전용 세션 탭이면 앞에 「전용 세션」을 붙인다. 첫 실행 때 자기 세션을 열 작업 공간이면 「새 세션 · 첫 실행 때 연결」,
    /// 같은 DB 여러 세션이면 앞에 세션 범위(<see cref="ScopeOf"/>, TSK-14-03).
    /// </summary>
    public string ShownSessionText => Shown is not { } t
        ? string.Empty
        : t.Dedicated
            ? UiText.Get("Execution.status.dedicated", "전용 세션") + " · " + DescribeSession(t.Run, t.Elapsed)
            : ReferenceEquals(t, _pending)
                ? UiText.Get("Execution.status.pendingOwn", "새 세션 · 첫 실행 때 연결")
                : FocusedSessionId is { } id && ScopeOf?.Invoke(id) is { Length: > 0 } scope
                    ? scope + " · " + DescribeSession(t.Run, t.Elapsed)
                    : DescribeSession(t.Run, t.Elapsed);

    /// <summary>
    /// 보이는 세션 값을 다시 알리고 문맥 키를 맞춘다(TSK-14-03): 세션은 그대로인데 함께 쓰는 작업 공간·활성 작업 공간이 바뀌었을 때 세션 라우터가 부른다.
    /// </summary>
    public void RefreshShown() => RaiseShown();

    /// <summary>상태 표시줄 3번 구간 글. 비어 있으면 구간을 접는다.</summary>
    public string ShownTransactionText => Shown is { } t ? DescribeTransaction(t.Run != SessionRunState.NoSession, t.TxnPending) : string.Empty;

    /// <summary>
    /// 조회 전용 Role 을 읽을 기능 게이트(TSK-07-05). 게이트 <see cref="IFeatureGate.Changed"/> 는 아무 스레드에서나 오므로 UI 스레드로 옮겨
    /// <c>readOnlySession</c> 키를 다시 맞춘다. null 이거나 같은 게이트면 그대로(게이트를 떼지 않는다).
    /// </summary>
    public void UseFeatures(IFeatureGate? features)
    {
        if (features is null || ReferenceEquals(_features, features))
        {
            return;
        }

        if (_features is { } old)
        {
            old.Changed -= OnFeaturesChanged;
        }

        _features = features;
        features.Changed += OnFeaturesChanged;
        RaiseFeatures(); // 처음 맞춤은 부른 자리에서(UseKeys 와 같다)
    }

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
        SessionRunState.Idle => UiText.Get("Execution.status.connected", "연결됨"),
        SessionRunState.Running => UiText.Format("Execution.status.running", "실행 중 {0}", FormatElapsed(elapsed)),
        SessionRunState.Cancelling => UiText.Get("Execution.status.cancelling", "취소 중"),
        SessionRunState.Broken => UiText.Get("Execution.status.broken", "끊김"),
        _ => string.Empty,
    };

    private string DescribeTransaction(bool hasSession, bool pending) => !hasSession
        ? string.Empty
        : pending ? UiText.Get("Execution.status.uncommitted", "미커밋")
        : _autoCommit ? UiText.Get("Execution.status.autoCommit", "자동 커밋")
        : string.Empty;

    // ---- 지금 접속(매개변수 없는 멤버) -------------------------------------------------------------

    /// <summary>실행을 시작했다. 경과 시간을 세기 시작한다.</summary>
    public void BeginRun() => Begin(P, null); // PL/SQL 실행 창 등 작업 공간 밖의 실행

    public void MarkCancelling() => Cancelling(P);

    public void EndRun() => End(P);

    public void SetError(SqlStatement stmt, DbError? error, Guid? tab)
    {
        _lastError = error;
        _lastErrorStatement = stmt;
        _lastErrorTab = tab;
        HasExecError = true;
    }

    /// <summary>커밋·롤백 뒤처럼 컨트롤러가 알고 있는 트랜잭션 상태를 반영한다.</summary>
    public void ApplyTransaction(bool pending) => SetTxn(P, pending);

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
        foreach (var t in _tracks.Values.Append(_none).Where(t => t.Busy))
        {
            t.Elapsed = now - t.StartedUtc;
        }

        RaiseAll();
    }

    /// <summary>
    /// 매개변수 없는 멤버가 가리킬 「지금 접속」 작업 세션을 바꾼다(접속 콤보·탭 전환). 열린 작업 세션이 아니면 무시한다. null = 지금 접속 없음.
    /// </summary>
    public void UsePrimary(Guid? workSessionId)
    {
        if (workSessionId is { } id && (!_tracks.TryGetValue(id, out var t) || t.Dedicated))
        {
            return;
        }

        if (_sessionId == workSessionId)
        {
            return;
        }

        _sessionId = workSessionId;
        RaiseAll();
    }

    // ---- 세션별 ----------------------------------------------------------------------------
    // 모르는 세션 ID(시험의 가짜 세션처럼 SessionOpened 없이 쓰인 세션)는 지금 접속과 같다. 닫은 세션은 무시한다.

    /// <summary>
    /// 전용 세션의 상태를 만든다. 세션을 연 쪽이 돌려받은 세션으로 바로 부른다(<c>SessionOpened</c> 는 UI 스레드로 늦게 올 수 있다).
    /// </summary>
    public void AddDedicated(Guid sessionId, ConnectionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (_tracks.TryAdd(sessionId, new Track(profile, dedicated: true)))
        {
            RaiseShown();
        }
    }

    /// <summary>전용 세션의 상태를 지운다(세션을 닫았다).</summary>
    public void RemoveDedicated(Guid sessionId)
    {
        if (_tracks.TryGetValue(sessionId, out var t) && t.Dedicated)
        {
            Forget(sessionId);
        }
    }

    public bool IsDedicated(Guid sessionId) => _tracks.TryGetValue(sessionId, out var t) && t.Dedicated;

    /// <summary>
    /// 상태 표시줄·문맥 키가 보여 줄 세션(활성 문서의 세션: 전용 세션 또는 작업 세션). null 이면 지금 접속.
    /// </summary>
    public void Focus(Guid? sessionId)
    {
        if (_focus == sessionId && !_focusNone && _pending is null)
        {
            return;
        }

        _focus = sessionId;
        _focusNone = false;
        _pending = null;
        RaiseShown();
    }

    /// <summary>
    /// 활성 문서(워크스페이스)가 첫 실행 때 <paramref name="profile"/> 의 새 세션을 연다(TSK-14-01): 상태 표시줄은 「새 세션 · 첫 실행 때 연결」(TSK-14-03), 문맥 키
    /// <c>sessionBusy</c>·<c>txnPending</c> 은 꺼진다(같은 DB 의 다른 세션이 실행 중이어도 이 문서는 실행할 수 있다).
    /// </summary>
    public void FocusPending(ConnectionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (_pending?.Profile == profile && !_focusNone)
        {
            return;
        }

        _pending = new Track(profile, dedicated: false);
        _focus = null;
        _focusNone = false;
        RaiseShown();
    }

    /// <summary>활성 문서에 세션이 없다(접속이 닫힌 SQL 탭): 상태 표시줄은 「세션 없음」 을 보이고 문맥 키는 꺼진다.</summary>
    public void FocusNothing()
    {
        if (_focusNone)
        {
            return;
        }

        _focusNone = true;
        _focus = null;
        _pending = null;
        RaiseShown();
    }

    /// <summary>실행 중·취소 중이거나 닫기 확인 중(<see cref="SetClosing"/>)이면 true. 이미 닫은 세션은 false.</summary>
    public bool IsBusy(Guid sessionId) => TrackOf(sessionId) is { } t && (t.Busy || t.Closing);

    /// <summary>실제로 실행 중·취소 중이면 true(<see cref="IsBusy"/> 와 달리 닫기 확인 중은 넣지 않는다).</summary>
    public bool IsRunning(Guid sessionId) => TrackOf(sessionId) is { Busy: true };

    public bool IsBroken(Guid sessionId) => TrackOf(sessionId) is { Run: SessionRunState.Broken };

    public bool IsTxnPending(Guid sessionId) => TrackOf(sessionId) is { TxnPending: true };

    /// <summary>세션이 자기 상태를 갖는지(모르는 ID 는 지금 접속 상태로 대신하므로, 접속마다 묻는 쪽이 남의 상태를 보지 않게 가린다).</summary>
    public bool IsTracked(Guid sessionId) => _tracks.ContainsKey(sessionId);

    /// <summary>
    /// 전용 세션 탭의 닫기 확인이 도는 동안 그 세션을 「바쁨」으로 둔다: 확인을 마친 뒤 닫히기 전에 새 실행·커밋이 끼어들면
    /// 미커밋 없이 닫힌다고 판단한 세션에 변경이 남는다. 상태 표시줄 글은 바꾸지 않는다. 전용 세션이 아니면 아무것도 안 한다.
    /// </summary>
    public void SetClosing(Guid sessionId, bool closing)
    {
        if (_tracks.TryGetValue(sessionId, out var t) && t.Dedicated && t.Closing != closing)
        {
            t.Closing = closing;
            RaiseShown();
        }
    }

    public void BeginRun(Guid sessionId)
    {
        if (TrackOf(sessionId) is { } t)
        {
            Begin(t, ActiveWorkspace?.Invoke()); // 결과 그리드 등에서 바로 시작하는 실행: 지금 활성 작업 공간이 시작했다
        }
    }

    /// <summary>
    /// 실행을 시작한다. <paramref name="owner"/> = 실행을 시작한 작업 공간(실행한 탭의 작업 공간, TSK-14-03). 세션을 여는 사이 다른 작업 공간으로
    /// 옮겨 가도 시작한 쪽을 기억한다(<see cref="ShownBusyElsewhere"/>). 모르면 null.
    /// </summary>
    public void BeginRun(Guid sessionId, object? owner)
    {
        if (TrackOf(sessionId) is { } t)
        {
            Begin(t, owner);
        }
    }

    public void MarkCancelling(Guid sessionId)
    {
        if (TrackOf(sessionId) is { } t)
        {
            Cancelling(t);
        }
    }

    public void EndRun(Guid sessionId)
    {
        if (TrackOf(sessionId) is { } t)
        {
            End(t);
        }
    }

    public void ApplyTransaction(Guid sessionId, bool pending)
    {
        if (TrackOf(sessionId) is { } t)
        {
            SetTxn(t, pending);
        }
    }

    // ---- 상태 바꾸기 --------------------------------------------------------------------------

    private Track? TrackOf(Guid sessionId) =>
        _tracks.TryGetValue(sessionId, out var t) ? t : _released.Contains(sessionId) ? null : P;

    private void Begin(Track t, object? owner)
    {
        t.StartedUtc = DateTime.UtcNow;
        t.Elapsed = TimeSpan.Zero;
        t.Run = SessionRunState.Running;
        t.Owner = owner;
        ClearError();
        StartTimer();
        RaiseAll();
    }

    private void Cancelling(Track t)
    {
        if (t.Run == SessionRunState.Running)
        {
            t.Run = SessionRunState.Cancelling;
            RaiseAll();
        }
    }

    private void End(Track t)
    {
        if (t.Busy)
        {
            // 세션 없이 시작한 실행(지금 접속이 없을 때)은 「세션 없음」 으로 돌아간다.
            t.Run = ReferenceEquals(t, _none) ? SessionRunState.NoSession : SessionRunState.Idle;
        }

        t.Owner = null;

        StopTimerIfIdle();
        RaiseAll();
    }

    private void SetTxn(Track t, bool pending)
    {
        if (t.TxnPending != pending)
        {
            t.TxnPending = pending;
            RaiseAll();
        }
    }

    private void Forget(Guid sessionId)
    {
        _tracks.Remove(sessionId);
        _released.Add(sessionId); // 늦게 끝난 실행의 상태 갱신이 다른 세션으로 넘어가지 않게
        if (_sessionId == sessionId)
        {
            _sessionId = null;
        }

        StopTimerIfIdle();
        RaiseAll();
    }

    // ---- 이벤트 ----------------------------------------------------------------------------

    private void OnOpened(SessionOpened e)
    {
        if (e.Purpose != SessionPurpose.Work)
        {
            return; // 모니터·전용 세션은 작업 세션이 아니다(전용 세션은 연 쪽이 AddDedicated 로 등록).
        }

        // 새 접속이 지금 접속이 된다(ConnectionService 와 같다). 접속 콤보로 바꾸면 UsePrimary 가 다시 고른다.
        _tracks[e.SessionId] = new Track(e.Profile, dedicated: false);
        _sessionId = e.SessionId;
        ClearError();
        RaiseAll();
    }

    private void OnClosed(SessionClosed e)
    {
        if (_tracks.ContainsKey(e.SessionId))
        {
            Forget(e.SessionId);
        }
    }

    private void OnBroken(SessionBroken e)
    {
        if (!_tracks.TryGetValue(e.SessionId, out var t))
        {
            return;
        }

        t.TxnPending = e.HadPendingTransaction;
        t.Run = SessionRunState.Broken;
        StopTimerIfIdle();
        RaiseAll();
    }

    private void OnTransaction(TransactionStateChanged e)
    {
        if (_tracks.TryGetValue(e.SessionId, out var t))
        {
            SetTxn(t, e.Pending);
        }
    }

    private void OnExecuted(StatementExecuted e)
    {
        // 상태 표시줄의 「마지막 실행」은 사용자가 편집기에서 보낸 실행만 센다(스키마 브라우저·모니터의 내부 질의 제외).
        if (e.Options.SkipHistory || e.Options.Source is not (HistorySource.Editor or HistorySource.Script or HistorySource.GridEdit))
        {
            return;
        }

        if (_tracks.TryGetValue(e.SessionId, out var t))
        {
            t.LastRun = new RunSummary(e.Result.Status, e.Result.RowsAffected, e.Result.Elapsed, e.Result.Error?.Number);
            RaiseAll();
        }
    }

    /// <summary>「마지막 실행」 요약(결과 전체를 붙잡지 않는다): 상태·영향 행 수·소요 시간·오류 번호.</summary>
    public sealed record RunSummary(ExecutionStatus Status, long? RowsAffected, TimeSpan Elapsed, int? ErrorNumber);

    /// <summary>「마지막 실행」 글: 행 수·소요 시간·오류 번호.</summary>
    public static string DescribeRun(ExecutionResult r) =>
        DescribeRun(new RunSummary(r.Status, r.RowsAffected, r.Elapsed, r.Error?.Number));

    private static string DescribeRun(RunSummary r)
    {
        var time = UiText.Format("Execution.status.run.seconds", "{0}초", r.Elapsed.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture));
        return r.Status switch
        {
            ExecutionStatus.Succeeded when r.RowsAffected is { } n =>
                UiText.Format("Execution.status.run.rows", "{0}행 · {1}", n.ToString("N0", CultureInfo.InvariantCulture), time),
            ExecutionStatus.Succeeded => UiText.Format("Execution.status.run.done", "완료 · {0}", time),
            ExecutionStatus.Failed => (r.ErrorNumber is > 0 ? $"ORA-{r.ErrorNumber:00000}" : UiText.Get("Execution.status.run.error", "오류")) + " · " + time,
            ExecutionStatus.Cancelled => UiText.Get("Execution.status.run.cancelled", "취소됨"),
            _ => UiText.Get("Execution.status.run.denied", "거부됨"),
        };
    }

    // ---- 내부 ------------------------------------------------------------------------------

    /// <summary>지금 접속 값과 보이는 세션 값이 바뀌었을 수 있다: 모두 알린다.</summary>
    private void RaiseAll()
    {
        foreach (var name in PrimaryProperties)
        {
            OnPropertyChanged(name);
        }

        RaiseShown();
    }

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
        keys.Set("sessionBusyElsewhere", ShownBusyElsewhere);
        keys.Set("txnPending", ShownTxnPending);
        keys.Set("readOnlySession", ReadOnlySessionKey);
        keys.Set("hasExecError", _hasExecError);
        keys.Set("dedicatedSession", Shown is { Dedicated: true, Run: not SessionRunState.Broken });
        keys.Set("tabConnectionMissing", _focusNone); // 접속 안 됨 탭도 실행을 눌러 「다시 접속」 을 고를 수 있게(CONN-06)
    }

    private void OnFeaturesChanged(object? sender, EventArgs e) => Post(RaiseFeatures);

    private void RaiseFeatures()
    {
        OnPropertyChanged(nameof(ReadOnlyRole));
        OnPropertyChanged(nameof(ReadOnlySessionKey));
        SyncKeys();
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
        if (_none.Busy || _tracks.Values.Any(t => t.Busy))
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
