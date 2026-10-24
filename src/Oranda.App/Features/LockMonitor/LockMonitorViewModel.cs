using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.Features.ConnectionManager;
using Oranda.App.Features.ExecutionPlan;
using Oranda.App.Features.SessionMonitor;
using Oranda.App.Shell;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.History;
using Oranda.Core.Monitoring;
using Oranda.Core.Plans;
using Oranda.Core.Sessions;
using Oranda.Core.Settings;
using Oranda.Core.Shell;

namespace Oranda.App.Features.LockMonitor;

/// <summary>본문에 보일 상태(시안 c). 데이터가 있으면 <see cref="Data"/>.</summary>
public enum LockViewState
{
    /// <summary>모니터 세션 없음 — 「접속이 없습니다」.</summary>
    NoConnection,

    /// <summary>첫 읽기 — 진행 막대 + 「잠금 정보를 읽는 중…」.</summary>
    Loading,

    /// <summary>대기·잠금 없음 — 「대기 중인 잠금이 없습니다」 + 거르기 풀기.</summary>
    Empty,

    /// <summary>권한 없음 — 기존 MonitorAccessBanner.</summary>
    Denied,

    Data,
}

/// <summary>
/// <c>oranda.panel.lockMonitor</c> — 대기 트리·보유자 영향 순위·DDL 잠금, 잠금 목록, 고른 세션의 SQL·잠긴 행·바인드·원인 힌트,
/// 세션 종료·연결 끊기, 세션 모니터로 이동, 복사·CSV (TSK-05-03, PRD 10장 LK-01~09).
/// <para>
/// 세션 모니터와 같은 모니터 전용 세션(<see cref="SessionPurpose.Monitor"/>)·<see cref="MonitorAccessState"/> 를 쓴다. 서버 호출은 한 번에 하나.
/// gv$sql(전문)·잠긴 행·바인드는 그 탭을 볼 때만 읽는다(LK-03). 방치 트랜잭션·데드락 기록은 TSK-05-04 가 <see cref="IdleTransactionsContent"/>·
/// <see cref="DeadlocksContent"/> 를 채운다.
/// </para>
/// </summary>
[OrandaToolPanel(LockMonitorCommands.PanelId, ToolPanelKind.Document, DockLocation.Document)]
public sealed partial class LockMonitorViewModel : ToolPanelViewModelBase, IDisposable
{
    public const int MinRefreshSeconds = 2;
    public const int MaxRefreshSeconds = 60;

    /// <summary>위쪽 탭 차례.</summary>
    public const int TreeTab = 0, RankTab = 1, IdleTab = 2, DdlTab = 3, DeadlockTab = 4;

    /// <summary>오른쪽 상세 탭 차례.</summary>
    public const int SqlTab = 0, RowTab = 1, BindTab = 2, HintTab = 3;

    /// <summary>잠긴 행 값을 못 읽었을 때 조회가 돌려주는 열 이름.</summary>
    public const string LockedRowErrorColumn = "오류";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IServiceProvider? _services;
    private readonly HashSet<SessionKey> _collapsed = [];
    private readonly LockThresholdTracker _tracker = new();
    private IMonitorService? _monitor;
    private CancellationTokenSource? _autoLoop;
    private CancellationTokenSource? _firstRead;
    private ISession? _session;
    private LockSnapshot? _snapshot;
    private Dictionary<SessionKey, LockSessionFacts> _facts = [];
    private List<LockTreeItem> _allTree = [];
    private (SessionKey? Key, int Tab)? _detailLoaded;
    private bool _ddlLoaded;
    private bool _idleLoaded;
    private bool _deadlocksLoaded;
    private bool _applyingSettings;
    private bool _thresholdPublished;
    private string _mainStatus = string.Empty;
    private LockHistoryBuffer? _history;
    private LockAlert? _pendingAlert;
    private CancellationTokenSource? _alertTimer;
    private bool _autoStarted;
    private bool _disposed;

    [ObservableProperty]
    private int _minWaitSeconds;

    [ObservableProperty]
    private string? _usernameFilter;

    [ObservableProperty]
    private string? _schemaFilter;

    [ObservableProperty]
    private int _selectedInstanceIndex;

    [ObservableProperty]
    private bool _waitingOnly = true;

    [ObservableProperty]
    private int _refreshSeconds = 5;

    [ObservableProperty]
    private bool _autoRefresh;

    [ObservableProperty]
    private int _nextRefreshIn;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoConnection), nameof(ShowLoading), nameof(ShowEmpty), nameof(ShowDenied), nameof(ShowData))]
    private LockViewState _state = LockViewState.NoConnection;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanKill))]
    private bool _isOpen;

    /// <summary>읽는 중(진행 막대). 다시 읽기 때는 본문을 그대로 둔다.</summary>
    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorText;

    /// <summary>모니터 세션이 끊긴 오류면 true(「다시 접속」).</summary>
    [ObservableProperty]
    private bool _errorIsDisconnect;

    /// <summary>오류 뒤 마지막 자료를 흐리게 보이는 중.</summary>
    [ObservableProperty]
    private bool _isStale;

    [ObservableProperty]
    private int _topTabIndex;

    [ObservableProperty]
    private int _detailTabIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(CanKill), nameof(SelectedTreeItem))]
    private SessionKey? _selectedKey;

    [ObservableProperty]
    private string _statusText = "Lock 모니터: 열면 지금 접속(또는 고른 접속)으로 모니터 전용 세션을 엽니다.";

    [ObservableProperty]
    private string _statusRight = string.Empty;

    [ObservableProperty]
    private string _treeCountText = string.Empty;

    [ObservableProperty]
    private string _lockCaption = string.Empty;

    [ObservableProperty]
    private string _emptyHint = string.Empty;

    [ObservableProperty]
    private bool _hasFilters;

    // ---- SQL 탭 ----
    [ObservableProperty]
    private string _holderCaption = string.Empty;

    [ObservableProperty]
    private string? _holderSql;

    [ObservableProperty]
    private bool _holderHasNoCurrentSql;

    [ObservableProperty]
    private string _waiterCaption = string.Empty;

    [ObservableProperty]
    private string? _waiterSql;

    [ObservableProperty]
    private string _sqlMessage = "세션을 고르면 보유자·대기자 SQL 이 여기에 보입니다.";

    // ---- 잠긴 행 탭 ----
    [ObservableProperty]
    private string? _lockedRowId;

    [ObservableProperty]
    private string _lockedRowCaption = string.Empty;

    [ObservableProperty]
    private string _lockedRowMessage = "대기 세션을 고르면 기다리는 행이 여기에 보입니다.";

    [ObservableProperty]
    private IReadOnlyList<IReadOnlyList<object?>> _lockedRowCells = [];

    [ObservableProperty]
    private string? _lockedRowSelect;

    // ---- 바인드 탭 ----
    [ObservableProperty]
    private string _bindCaption = string.Empty;

    [ObservableProperty]
    private string _bindMessage = "대기 세션을 고르면 그 SQL 의 캡처된 바인드가 여기에 보입니다.";

    [ObservableProperty]
    private IReadOnlyList<IReadOnlyList<object?>> _bindCells = [];

    // ---- DDL 잠금 탭 ----
    [ObservableProperty]
    private string? _ddlNotice;

    [ObservableProperty]
    private string? _ddlGrantSql;

    [ObservableProperty]
    private string _ddlSummary = "dba_ddl_locks · 이 탭을 볼 때 읽습니다";

    [ObservableProperty]
    private string _hintCountText = string.Empty;

    // ---- 방치 트랜잭션 탭(LK-12) ----
    /// <summary>「60 초 이상만」(기본 켬). 끄면 마지막 호출 뒤 0 초부터.</summary>
    [ObservableProperty]
    private bool _idleLongOnly = true;

    [ObservableProperty]
    private string _idleSummary = "방치 트랜잭션 · 이 탭을 볼 때 읽습니다";

    [ObservableProperty]
    private string _idleCountText = string.Empty;

    /// <summary>SQL 탭 경고 상자(방치 트랜잭션을 골랐을 때). null 이면 숨김.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIdleCallout))]
    private string? _idleCallout;

    // ---- 데드락 기록 탭(LK-12) ----
    /// <summary>읽지 않는 이유(11g·18c 이하). null 이면 목록.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeadlockUnsupported), nameof(ShowNoDeadlocks))]
    private string? _deadlockNotice;

    [ObservableProperty]
    private string _deadlockSummary = "v$diag_alert_ext · 최근 24 시간 · 읽기 전용";

    [ObservableProperty]
    private string _deadlockCountText = string.Empty;

    /// <summary>대기 그래프 순환 띠 글(「1·0388 → 2·1903 → 1·0388. Oracle 이 …」). null 이면 숨김.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCycle))]
    private string? _cycleText;

    // ---- 이력 띠(LK-10) ----
    [ObservableProperty]
    private bool _showHistoryStrip = true;

    [ObservableProperty]
    private IReadOnlyList<LockHistoryPoint> _historyPoints = [];

    [ObservableProperty]
    private string _historyLabel = "최근 30 분";

    [ObservableProperty]
    private string _historyWaitersText = "0 명";

    [ObservableProperty]
    private string _historyPeakText = string.Empty;

    [ObservableProperty]
    private string _historyMaxWaitText = "0 초";

    [ObservableProperty]
    private bool _historyOverThreshold;

    [ObservableProperty]
    private string _historyThresholdText = string.Empty;

    // ---- 알림 설정(LK-10·11, settings monitor.lockAlert) ----
    [ObservableProperty]
    private bool _alertEnabled = true;

    [ObservableProperty]
    private int _alertWaitSeconds = 120;

    [ObservableProperty]
    private int _alertWaiters = 5;

    [ObservableProperty]
    private bool _saveSnapshot = true;

    [ObservableProperty]
    private bool _notifyWhenHidden = true;

    [ObservableProperty]
    private int _historyMinutes = 30;

    [ObservableProperty]
    private bool _saveHistoryFile;

    /// <summary>창 오른쪽 아래 알림(8 초 뒤 사라짐). null 이면 숨김.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveAlert), nameof(ActiveAlertMessage), nameof(HasAlertSnapshot))]
    private LockAlert? _activeAlert;

    public LockMonitorViewModel()
    {
    }

    public LockMonitorViewModel(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
        Keys = services.GetService(typeof(ContextKeyService)) as ContextKeyService;
    }

    /// <summary>시험용: 모니터 서비스와 세션 여는 방법을 직접 준다.</summary>
    public LockMonitorViewModel(IMonitorService monitor, Func<CancellationToken, Task<ISession?>> openSession, ContextKeyService? keys = null)
    {
        _monitor = monitor;
        OpenSession = openSession;
        Keys = keys;
    }

    /// <summary>펼친 대기 트리(접힌 가지는 빠진다).</summary>
    public ObservableCollection<LockTreeItem> TreeRows { get; } = [];

    public ObservableCollection<LockItem> Locks { get; } = [];

    public ObservableCollection<ImpactItem> Impacts { get; } = [];

    public ObservableCollection<DdlLockItem> DdlLocks { get; } = [];

    public ObservableCollection<LockHint> Hints { get; } = [];

    /// <summary>인스턴스 거르기 고르기(0 = 전체).</summary>
    public ObservableCollection<string> InstanceChoices { get; } = ["인스턴스: 전체"];

    public MonitorAccessState Access { get; } = new();

    public ContextKeyService? Keys { get; set; }

    public Func<CancellationToken, Task<ISession?>>? OpenSession { get; set; }

    public Func<CancellationToken, Task<ISession?>>? ChooseSession { get; set; }

    public Action<ConnectionProfile>? Remember { get; set; }

    public TimeSpan? CloseTimeout { get; set; }

    /// <summary>세션 종료·연결 끊기 확인 창. 없으면 진행하지 않는다(안전 쪽).</summary>
    public Func<LockKillRequest, Task<bool>>? ConfirmKill { get; set; }

    /// <summary>연결 끊기를 보낼 실행 엔진. 없으면 서비스에서 꺼낸다.</summary>
    public IExecutionService? Execution { get; set; }

    /// <summary>「세션 모니터로」: 세션 모니터를 열고 그 세션을 고른다.</summary>
    public Func<SessionKey, Task>? GoToSessionMonitor { get; set; }

    /// <summary>「실제 계획」: 계획 창에 커서 계획을 보인다(모니터 세션으로, 이 창의 차례 안에서).</summary>
    public Func<ISession, CursorRef, CancellationToken, Task>? ShowPlan { get; set; }

    /// <summary>「편집기로 열기」: 새 SQL 탭에 글을 넣는다(실행하지 않는다).</summary>
    public Action<string>? OpenEditor { get; set; }

    /// <summary>「접속 관리자 열기」.</summary>
    public Func<Task>? OpenConnectionManager { get; set; }

    /// <summary>첫 읽기가 끝나면 자동 새로 고침을 켤지(시안 기본 켬). 시험은 끈다.</summary>
    public bool StartAutoRefresh { get; set; } = true;

    public Action<Action> PostToUi { get; set; } = a =>
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            a();
        }
        else
        {
            Dispatcher.UIThread.Post(a);
        }
    };

    public ISession? MonitorSession => _session;

    public bool ShowNoConnection => State == LockViewState.NoConnection;

    public bool ShowLoading => State == LockViewState.Loading;

    public bool ShowEmpty => State == LockViewState.Empty;

    public bool ShowDenied => State == LockViewState.Denied;

    public bool ShowData => State == LockViewState.Data;

    public bool HasError => ErrorText is not null;

    public bool HasSelection => SelectedKey is not null && IsOpen;

    /// <summary>종료·연결 끊기 단추: 고른 세션이 있고 모니터 계정이 ALTER SYSTEM 을 쓸 수 있을 때.</summary>
    public bool CanKill => HasSelection && _session?.Server.Capabilities.CanKillSession == true;

    public bool HasHolderSql => HolderSql is not null;

    public bool HasWaiterSql => WaiterSql is not null;

    public bool HasLockedRow => LockedRowId is not null;

    public bool HasBinds => BindCells.Count > 0;

    public bool HasDdlLocks => DdlLocks.Count > 0;

    public bool DdlDenied => DdlNotice is not null;

    public ObservableCollection<IdleTransactionItem> IdleTransactions { get; } = [];

    public ObservableCollection<DeadlockItem> Deadlocks { get; } = [];

    public bool HasIdleTransactions => IdleTransactions.Count > 0;

    public bool HasDeadlocks => Deadlocks.Count > 0;

    public bool DeadlockUnsupported => DeadlockNotice is not null;

    /// <summary>「최근 24 시간 데드락이 없습니다」(읽었고 목록이 비었을 때).</summary>
    public bool ShowNoDeadlocks => !HasDeadlocks && !DeadlockUnsupported;

    /// <summary>이력 점이 둘보다 적어 그래프 대신 안내를 보인다.</summary>
    public bool HistorySparse => HistoryPoints.Count < 2;

    /// <summary>이력 그래프 기준선(알림을 끄면 0 = 선 없음).</summary>
    public long ChartThreshold => AlertEnabled ? AlertWaitSeconds : 0;

    public bool HasIdleCallout => IdleCallout is not null;

    public bool HasCycle => CycleText is not null;

    public bool HasActiveAlert => ActiveAlert is not null;

    public string ActiveAlertMessage => ActiveAlert?.Message ?? string.Empty;

    public bool HasAlertSnapshot => ActiveAlert?.SnapshotPath is not null;

    /// <summary>찾은 순환들(순환 띠·시험용).</summary>
    public IReadOnlyList<IReadOnlyList<SessionKey>> Cycles { get; private set; } = [];

    /// <summary>이 접속의 이력(창을 닫아도 프로필마다 남는다). 접속 전에는 null.</summary>
    public LockHistoryBuffer? History => _history;

    /// <summary>임계치 판정 규칙(설정 값).</summary>
    public LockAlertRule AlertRule => new(AlertWaitSeconds, AlertWaiters);

    /// <summary>스냅샷·이력 파일 폴더(앱 데이터 폴더\lock-history). 없으면 파일을 쓰지 않는다.</summary>
    public string? HistoryFolder { get; set; }

    /// <summary>알림 설정이 바뀌면 부른다(설정 파일 저장). 시험은 비워 둔다.</summary>
    public Action<LockAlertSettings>? SaveSettings { get; set; }

    /// <summary>임계치 상태를 상태 줄 막음 칩(BlockerWatcher)에 알린다.</summary>
    public Action<LockThresholdChanged>? PublishThreshold { get; set; }

    /// <summary>「대기 트리 보기」: Lock 모니터 탭을 앞으로.</summary>
    public Func<Task>? BringToFront { get; set; }

    /// <summary>창 알림이 떠 있는 시간(시안 8 초).</summary>
    public TimeSpan AlertDuration { get; set; } = TimeSpan.FromSeconds(8);

    /// <summary>새 알림이 떴다(View 가 탭이 뒤에 있으면 창 오버레이로 띄운다).</summary>
    public event EventHandler<LockAlert>? AlertRaised;

    /// <summary>지금 화면에 있는 알림 설정 값.</summary>
    public LockAlertSettings CurrentAlertSettings => new()
    {
        Enabled = AlertEnabled,
        WaitSeconds = AlertWaitSeconds,
        Waiters = AlertWaiters,
        SaveSnapshot = SaveSnapshot,
        NotifyWhenHidden = NotifyWhenHidden,
        HistoryMinutes = HistoryMinutes,
        SaveHistoryFile = SaveHistoryFile,
    };

    /// <summary>고른 세션의 트리 줄(접혀 숨은 줄 포함). 트리에 없으면 null.</summary>
    public LockTreeItem? SelectedTreeItem => SelectedKey is { } k ? _allTree.FirstOrDefault(i => i.Key == k) : null;

    /// <summary>고른 세션 기준 (보유자, 대기자): 뿌리를 고르면 가장 오래 기다린 바로 아래 대기자, 대기자를 고르면 그 부모.</summary>
    public (LockTreeItem? Holder, LockTreeItem? Waiter) SelectedPair
    {
        get
        {
            if (SelectedTreeItem is not { } item)
            {
                return (null, null);
            }

            if (item.Parent is null)
            {
                var first = _allTree.Where(i => i.Parent == item).OrderByDescending(i => i.WaitSeconds).FirstOrDefault();
                return (item, first);
            }

            return (item.Parent, item);
        }
    }

    public LockFilter BuildFilter() => new()
    {
        MinWaitSeconds = Math.Max(0, MinWaitSeconds),
        Username = Blank(UsernameFilter),
        Schema = Blank(SchemaFilter),
        InstanceId = SelectedInstanceIndex > 0 && SelectedInstanceIndex < InstanceChoices.Count
            && int.TryParse(InstanceChoices[SelectedInstanceIndex].Split(' ')[^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var inst)
            ? inst : null,
        WaitingOrBlockingOnly = WaitingOnly,
    };

    // ---- 세션 열기·닫기 ---------------------------------------------------------------------

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_session is not null)
        {
            await RefreshAsync(ct);
            return;
        }

        if (OpenSession is null)
        {
            State = LockViewState.NoConnection;
            StatusText = "접속 없음";
            return;
        }

        ISession? session;
        try
        {
            session = await OpenSession(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            State = LockViewState.NoConnection;
            StatusText = "모니터 세션을 열지 못했습니다: " + FirstLine(ex.Message);
            return;
        }

        if (session is null)
        {
            State = LockViewState.NoConnection;
            StatusText = "접속 없음";
            return;
        }

        await AttachAsync(session, chosen: false, ct);
    }

    public async Task OpenWithOtherAccountAsync(CancellationToken ct = default)
    {
        if (ChooseSession is null)
        {
            StatusText = "다른 접속으로 열 수 없습니다(접속 정보 없음).";
            return;
        }

        ISession? session;
        try
        {
            session = await ChooseSession(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StatusText = "모니터 세션을 열지 못했습니다: " + FirstLine(ex.Message);
            return;
        }

        if (session is not null)
        {
            await AttachAsync(session, chosen: true, ct);
        }
    }

    /// <summary>「다시 접속」: 끊긴 모니터 세션을 닫고 같은 방법으로 다시 연다.</summary>
    public async Task ReconnectAsync(CancellationToken ct = default)
    {
        await StopAsync();
        await StartAsync(ct);
    }

    private async Task AttachAsync(ISession session, bool chosen, CancellationToken ct)
    {
        if (!session.Server.Capabilities.CanQueryPerformanceViews)
        {
            await ConnectionService.CloseAsync(session, timeout: CloseTimeout);
            if (_session is null)
            {
                Access.ShowDenied(session.Profile);
                Access.SetAccount(session.Profile);
                State = LockViewState.Denied;
            }

            StatusText = "권한 없음 · v$lock·v$session 을 읽을 수 없습니다";
            return;
        }

        var previous = Interlocked.Exchange(ref _session, session);
        IsOpen = true;
        ClearError();
        Access.ClearNotice();
        Access.SetAccount(session.Profile);
        if (chosen)
        {
            Remember?.Invoke(session.Profile);
        }

        if (previous is not null)
        {
            PostToUi(ClearData);
            await CloseSessionAsync(previous);
        }

        _ddlLoaded = false;
        _idleLoaded = false;
        _deadlocksLoaded = false;
        _tracker.Reset();
        _history = LockHistoryBuffer.For(session.Profile.Id, TimeSpan.FromMinutes(HistoryMinutes));
        PostToUi(UpdateHistory);
        State = LockViewState.Loading;
        UpdateKeys();
        OnPropertyChanged(nameof(CanKill));
        await RefreshAsync(ct);
        if (StartAutoRefresh && !_autoStarted && _session is not null && State is LockViewState.Data or LockViewState.Empty)
        {
            _autoStarted = true;
            AutoRefresh = true;
        }
    }

    private async Task CloseSessionAsync(ISession session)
    {
        await _gate.WaitAsync();
        try
        {
            await ConnectionService.CloseAsync(session, timeout: CloseTimeout);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>자동 새로 고침을 멈추고 모니터 세션을 닫는다(탭이 닫힐 때).</summary>
    public async Task StopAsync()
    {
        AutoRefresh = false;
        StopLoop();
        var session = Interlocked.Exchange(ref _session, null);
        IsOpen = false;
        State = LockViewState.NoConnection;
        Access.SetAccount(null);
        UpdateKeys();
        ClearThreshold();
        if (session is not null)
        {
            await CloseSessionAsync(session);
        }
    }

    /// <summary>첫 읽기 취소(Esc).</summary>
    public void CancelLoad() => _firstRead?.Cancel();

    // ---- 읽기 -------------------------------------------------------------------------------

    /// <summary>F5: 목록을 다시 읽고 고른 세션의 상세도 다시 읽는다.</summary>
    public Task RefreshAsync(CancellationToken ct = default) => ReadAsync(keepDetail: false, ct);

    /// <summary>자동 새로 고침과 같은 읽기(고른 세션의 상세는 다시 읽지 않는다). 시험이 부른다.</summary>
    internal Task RefreshKeepingDetailAsync(CancellationToken ct = default) => ReadAsync(keepDetail: true, ct);

    private async Task ReadAsync(bool keepDetail, CancellationToken ct)
    {
        var first = _snapshot is null;
        using var firstCts = first ? CancellationTokenSource.CreateLinkedTokenSource(ct) : null;
        if (firstCts is not null)
        {
            _firstRead = firstCts;
            ct = firstCts.Token;
        }

        var filter = BuildFilter();
        var watch = Stopwatch.StartNew();
        PostToUi(() =>
        {
            IsBusy = true;
            if (first && State != LockViewState.Denied)
            {
                State = LockViewState.Loading;
            }

            StatusRight = first ? "읽는 중…" : "새로 고침 중…";
        });
        var ok = await RunAsync(async (monitor, session) =>
        {
            var snap = await monitor.GetLocksAsync(session, filter, ct);
            IReadOnlyList<BlockerImpact> impacts;
            try
            {
                impacts = await monitor.GetBlockerImpactAsync(session, ct);
            }
            catch (NotSupportedException)
            {
                impacts = [];
            }

            PostToUi(() => Apply(snap, impacts, watch.Elapsed));
        }, ct);
        _firstRead = null;
        PostToUi(() => IsBusy = false);
        if (!ok)
        {
            if (first && _snapshot is null && State == LockViewState.Loading)
            {
                PostToUi(() =>
                {
                    State = _session is null ? LockViewState.NoConnection : LockViewState.Empty;
                    if (!HasError)
                    {
                        EmptyHint = "읽기를 취소했습니다. F5 로 다시 읽습니다.";
                    }
                });
            }

            return;
        }

        if (Interlocked.Exchange(ref _pendingAlert, null) is { } alert)
        {
            await RaiseAlertAsync(alert, ct);
        }

        if (!keepDetail)
        {
            _detailLoaded = null;
            _ddlLoaded = false;
            _deadlocksLoaded = false;
        }

        // 방치 트랜잭션은 그 탭을 보는 동안 새로 고침마다 다시 읽는다(경과 시간이 바뀐다).
        _idleLoaded = false;
        switch (TopTabIndex)
        {
            case DdlTab:
                await LoadDdlLocksAsync(ct);
                break;
            case IdleTab:
                await LoadIdleTransactionsAsync(ct);
                break;
            case DeadlockTab:
                await LoadDeadlocksAsync(ct);
                break;
        }

        await LoadDetailAsync(ct);
    }

    // ---- 방치 트랜잭션·데드락 기록(LK-12) ------------------------------------------------------

    /// <summary>방치 트랜잭션 탭: INACTIVE 인데 트랜잭션을 연 세션(「60 초 이상만」 이면 마지막 호출 뒤 60 초 이상).</summary>
    public Task LoadIdleTransactionsAsync(CancellationToken ct = default)
    {
        if (_idleLoaded)
        {
            return Task.CompletedTask;
        }

        return LoadIdleCoreAsync(IdleLongOnly ? 60 : 0, ct);
    }

    private async Task LoadIdleCoreAsync(int min, CancellationToken ct)
    {
        var ok = await RunAsync(async (monitor, session) =>
        {
            IReadOnlyList<IdleTransaction> rows;
            try
            {
                rows = await monitor.GetIdleTransactionsAsync(session, min, ct);
            }
            catch (NotSupportedException)
            {
                rows = [];
            }

            _idleLoaded = true;
            PostToUi(() => ApplyIdle(rows, _snapshot?.CollectedAt ?? DateTimeOffset.UtcNow));
        }, ct);

        // 방치 트랜잭션에서 고른 세션이면 SQL 탭을 그 세션 내용(prev_sql_id)으로 다시 읽는다.
        if (ok && TopTabIndex == IdleTab && SelectedKey is { } key && IdleTransactions.Any(i => i.Key == key))
        {
            _detailLoaded = null;
            await LoadDetailAsync(ct);
        }
    }

    private void ApplyIdle(IReadOnlyList<IdleTransaction> rows, DateTimeOffset collectedAt)
    {
        IdleTransactions.Clear();
        foreach (var r in rows)
        {
            IdleTransactions.Add(new IdleTransactionItem(r, collectedAt));
        }

        var blocking = IdleTransactions.Count(i => i.Row.BlockedCount > 0);
        IdleCountText = IdleTransactions.Count > 0 ? IdleTransactions.Count.ToString(CultureInfo.InvariantCulture) : string.Empty;
        IdleSummary = IdleTransactions.Count == 0
            ? "방치 트랜잭션 0"
            : string.Create(CultureInfo.InvariantCulture, $"방치 트랜잭션 {IdleTransactions.Count} · 그중 차단 중 {blocking}");
        OnPropertyChanged(nameof(HasIdleTransactions));
        UpdateStatusText();
    }

    /// <summary>데드락 기록 탭: alert 로그(v$diag_alert_ext, 19c 이상)의 최근 24 시간 ORA-00060. 11g·18c 이하는 이유만.</summary>
    public Task LoadDeadlocksAsync(CancellationToken ct = default)
    {
        if (_deadlocksLoaded || _session is not { } current)
        {
            return Task.CompletedTask;
        }

        if (DeadlockReason(current.Server.Version) is { } reason)
        {
            _deadlocksLoaded = true;
            Deadlocks.Clear();
            DeadlockNotice = reason;
            DeadlockCountText = string.Empty;
            DeadlockSummary = "alert 로그 · 이 버전은 SQL 로 읽지 않음";
            OnPropertyChanged(nameof(HasDeadlocks));
            OnPropertyChanged(nameof(ShowNoDeadlocks));
            return Task.CompletedTask;
        }

        return RunAsync(async (monitor, session) =>
        {
            IReadOnlyList<DeadlockEvent> rows;
            try
            {
                rows = await monitor.GetRecentDeadlocksAsync(session, 50, ct);
            }
            catch (NotSupportedException)
            {
                rows = [];
            }

            _deadlocksLoaded = true;
            PostToUi(() =>
            {
                var now = _snapshot?.CollectedAt ?? DateTimeOffset.UtcNow;
                Deadlocks.Clear();
                foreach (var r in rows.Where(r => r.At >= now.AddHours(-24)))
                {
                    Deadlocks.Add(new DeadlockItem(r, now));
                }

                DeadlockNotice = null;
                DeadlockCountText = Deadlocks.Count > 0 ? Deadlocks.Count.ToString(CultureInfo.InvariantCulture) : string.Empty;
                DeadlockSummary = string.Create(CultureInfo.InvariantCulture, $"v$diag_alert_ext · 최근 24 시간 · 읽기 전용 · {Deadlocks.Count}건");
                OnPropertyChanged(nameof(HasDeadlocks));
                OnPropertyChanged(nameof(ShowNoDeadlocks));
            });
        }, ct);
    }

    /// <summary>alert 로그를 SQL 로 읽지 않는 버전이면 그 이유(카탈로그 lock.deadlocks 는 19c 이상).</summary>
    public static string? DeadlockReason(string? version)
    {
        var major = int.TryParse((version ?? string.Empty).Split('.')[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var m) ? m : 0;
        return major switch
        {
            0 => null,
            < 12 => "11g 에서는 alert 로그를 SQL 로 읽을 수 없습니다(12c 부터 v$diag_alert_ext). 서버의 alert_<SID>.log(경로: v$diag_info 「Diag Trace」)를 직접 보세요.",
            < 19 => $"{major}c 에서는 alert 로그를 읽지 않습니다(19c 이상에서 v$diag_alert_ext 로 읽습니다). 서버의 alert_<SID>.log 를 직접 보세요.",
            _ => null,
        };
    }

    // ---- 이력·임계치 알림(LK-10·11) -------------------------------------------------------------

    /// <summary>새로 고침 한 번 = 이력 한 점(대기자 수·최대 대기 초). 켜 두었으면 lock-history CSV 에도 한 줄.</summary>
    private void RecordHistory(DateTimeOffset at, int waiters, long maxWait)
    {
        if (_history is not { } history)
        {
            return;
        }

        var point = new LockHistoryPoint(at, waiters, maxWait);
        history.Add(point);
        if (SaveHistoryFile && HistoryFolder is { } folder && _session is { } session)
        {
            var account = Access.AccountText;
            _ = Task.Run(() =>
            {
                try
                {
                    LockHistoryFiles.AppendHistory(folder, account.Length > 0 ? account : session.Profile.Name, point);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    PostToUi(() => StatusText = "이력 파일을 쓰지 못했습니다: " + ex.Message);
                }
            });
        }

        UpdateHistory();
    }

    private void UpdateHistory()
    {
        var points = _history?.Points ?? [];
        HistoryPoints = points;
        OnPropertyChanged(nameof(HistorySparse));
        HistoryLabel = string.Create(CultureInfo.InvariantCulture, $"최근 {HistoryMinutes} 분");
        var last = points.Count > 0 ? points[^1] : null;
        HistoryWaitersText = string.Create(CultureInfo.InvariantCulture, $"{last?.Waiters ?? 0:N0} 명");
        HistoryPeakText = _history?.Peak is { Waiters: > 0 } peak
            ? string.Create(CultureInfo.InvariantCulture, $"(최고 {peak.Waiters:N0} · {peak.At.ToLocalTime():HH:mm})")
            : string.Empty;
        HistoryMaxWaitText = LockText.Seconds(last?.MaxWaitSeconds ?? 0);
        HistoryOverThreshold = AlertEnabled && last is { } l && l.MaxWaitSeconds >= AlertWaitSeconds;
        OnPropertyChanged(nameof(ChartThreshold));
        HistoryThresholdText = AlertEnabled
            ? string.Create(CultureInfo.InvariantCulture, $"· 기준 {AlertWaitSeconds:N0} 초 ┄")
            : "· 알림 꺼짐";
        UpdateStatusRight();
    }

    /// <summary>임계치 판정: 처음 넘은 보유자가 있으면 알림을 걸어 두고(읽기가 끝난 뒤 스냅샷), 상태 줄 칩에 알린다.</summary>
    private void EvaluateThreshold(DateTimeOffset at)
    {
        if (!AlertEnabled)
        {
            ClearThreshold();
            return;
        }

        var result = _tracker.Evaluate(LockThresholdTracker.Loads(_allTree), AlertRule, at);
        if (result.NewAlert is { } alert)
        {
            _pendingAlert = alert;
        }

        if (result.IsExceeded || _thresholdPublished)
        {
            _thresholdPublished = result.IsExceeded;
            PublishThreshold?.Invoke(new LockThresholdChanged(result.IsExceeded, result.Waiters, result.MaxWaitSeconds));
        }
    }

    private void ClearThreshold()
    {
        _tracker.Reset();
        _pendingAlert = null;
        if (_thresholdPublished)
        {
            _thresholdPublished = false;
            PublishThreshold?.Invoke(new LockThresholdChanged(false, 0, 0));
        }
    }

    /// <summary>알림: 켜 두었으면 그 시점 보유자·대기자 SQL 스냅샷을 쓰고, 창 알림을 띄운다(8 초 뒤 사라짐).</summary>
    private async Task RaiseAlertAsync(LockAlert alert, CancellationToken ct)
    {
        if (SaveSnapshot && HistoryFolder is { } folder)
        {
            var path = await WriteSnapshotAsync(alert, folder, ct);
            if (path is not null)
            {
                alert = alert with { SnapshotPath = path };
            }
        }

        PostToUi(() =>
        {
            ActiveAlert = alert;
            AlertRaised?.Invoke(this, alert);
            StartAlertTimer();
        });
    }

    private async Task<string?> WriteSnapshotAsync(LockAlert alert, string folder, CancellationToken ct)
    {
        var holder = _allTree.FirstOrDefault(t => t.Key == alert.Holder);
        if (holder is null)
        {
            return null;
        }

        var entries = new List<LockSnapshotText.Entry>();
        string? path = null;
        await RunAsync(async (monitor, session) =>
        {
            var holderSql = holder.CurrentOrPreviousSqlId;
            entries.Add(new("보유자", holder, holderSql, await SqlTextAsync(monitor, session, holderSql, ct)));
            var texts = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var w in Descendants(holder).OrderByDescending(w => w.WaitSeconds).Take(20))
            {
                var id = w.Node.SqlId;
                if (id is { Length: > 0 } && !texts.ContainsKey(id))
                {
                    texts[id] = await SqlTextAsync(monitor, session, id, ct);
                }

                entries.Add(new("대기자", w, id, id is null ? null : texts.GetValueOrDefault(id)));
            }

            var text = LockSnapshotText.Build(alert, Access.AccountText, AlertRule, entries);
            path = await Task.Run(() =>
            {
                try
                {
                    return LockHistoryFiles.WriteSnapshot(folder, alert, text);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    PostToUi(() => StatusText = "스냅샷을 쓰지 못했습니다: " + ex.Message);
                    return null;
                }
            }, ct);
        }, ct, reportAsBanner: false);
        return path;
    }

    private void StartAlertTimer()
    {
        Interlocked.Exchange(ref _alertTimer, null)?.Cancel();
        if (AlertDuration <= TimeSpan.Zero || AlertDuration == Timeout.InfiniteTimeSpan)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _alertTimer = cts;
        var shown = ActiveAlert;
        _ = Task.Delay(AlertDuration, cts.Token).ContinueWith(
            t =>
            {
                if (!t.IsCanceled)
                {
                    PostToUi(() =>
                    {
                        if (ReferenceEquals(ActiveAlert, shown))
                        {
                            ActiveAlert = null;
                        }
                    });
                }
            },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>창 알림 닫기(✕). 상태 줄 칩은 남는다.</summary>
    public void DismissAlert()
    {
        Interlocked.Exchange(ref _alertTimer, null)?.Cancel();
        ActiveAlert = null;
    }

    /// <summary>「대기 트리 보기」: 탭을 앞으로, 대기 트리에서 알린 보유자를 고른다.</summary>
    public async Task ShowAlertHolderAsync()
    {
        var alert = ActiveAlert;
        DismissAlert();
        if (BringToFront is { } bring)
        {
            await bring();
        }

        TopTabIndex = TreeTab;
        if (alert is not null && _allTree.Any(t => t.Key == alert.Holder))
        {
            SelectedKey = alert.Holder;
        }
    }

    /// <summary>「트리에서 보기」(순환 띠): 대기 트리 탭에서 순환의 첫 세션을 고른다.</summary>
    public void ShowCycleInTree()
    {
        TopTabIndex = TreeTab;
        if (Cycles.Count > 0 && Cycles[0].Count > 0)
        {
            var first = Cycles[0][0];
            SelectedKey = _allTree.FirstOrDefault(t => t.Key.InstanceId == first.InstanceId && t.Key.Sid == first.Sid)?.Key ?? first;
        }
    }

    /// <summary>설정 파일 값을 화면에 넣는다(저장하지 않는다). 범위를 맞춘다.</summary>
    public void ApplySettings(LockAlertSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _applyingSettings = true;
        try
        {
            AlertEnabled = settings.Enabled;
            AlertWaitSeconds = settings.WaitSeconds;
            AlertWaiters = settings.Waiters;
            SaveSnapshot = settings.SaveSnapshot;
            NotifyWhenHidden = settings.NotifyWhenHidden;
            HistoryMinutes = settings.HistoryMinutes;
            SaveHistoryFile = settings.SaveHistoryFile;
        }
        finally
        {
            _applyingSettings = false;
        }

        UpdateHistory();
    }

    partial void OnAlertEnabledChanged(bool value)
    {
        if (!value)
        {
            ClearThreshold();
        }

        AlertSettingChanged();
    }

    partial void OnAlertWaitSecondsChanged(int value)
    {
        var clamped = Math.Clamp(value, 1, 86_400);
        if (clamped != value)
        {
            AlertWaitSeconds = clamped;
            return;
        }

        AlertSettingChanged();
    }

    partial void OnAlertWaitersChanged(int value)
    {
        var clamped = Math.Clamp(value, 1, 1000);
        if (clamped != value)
        {
            AlertWaiters = clamped;
            return;
        }

        AlertSettingChanged();
    }

    partial void OnSaveSnapshotChanged(bool value) => AlertSettingChanged();

    partial void OnNotifyWhenHiddenChanged(bool value) => AlertSettingChanged();

    partial void OnSaveHistoryFileChanged(bool value) => AlertSettingChanged();

    partial void OnHistoryMinutesChanged(int value)
    {
        var clamped = Math.Clamp(value, 5, 720);
        if (clamped != value)
        {
            HistoryMinutes = clamped;
            return;
        }

        if (_history is { } h)
        {
            h.Retention = TimeSpan.FromMinutes(clamped);
        }

        AlertSettingChanged();
    }

    private void AlertSettingChanged()
    {
        if (_applyingSettings)
        {
            return;
        }

        UpdateHistory();
        SaveSettings?.Invoke(CurrentAlertSettings);
    }

    partial void OnIdleLongOnlyChanged(bool value)
    {
        _idleLoaded = false;
        if (_session is not null && TopTabIndex == IdleTab)
        {
            _ = LoadIdleTransactionsAsync();
        }
    }

    /// <summary>오른쪽 상세 탭이 보이는 내용을 읽는다(고른 세션·탭이 바뀌었을 때만).</summary>
    public Task LoadDetailAsync(CancellationToken ct = default)
    {
        var tab = DetailTabIndex;
        var key = SelectedKey;
        if (_detailLoaded is { } done && done.Key == key && done.Tab == tab)
        {
            return Task.CompletedTask;
        }

        _detailLoaded = (key, tab);
        return tab switch
        {
            SqlTab => LoadSqlAsync(ct),
            RowTab => LoadLockedRowAsync(ct),
            BindTab => LoadBindsAsync(ct),
            _ => Task.CompletedTask,
        };
    }

    /// <summary>방치 트랜잭션 탭에서 고른 세션(그 탭이 앞에 있을 때만).</summary>
    public IdleTransactionItem? SelectedIdle =>
        TopTabIndex == IdleTab && SelectedKey is { } k ? IdleTransactions.FirstOrDefault(i => i.Key == k) : null;

    private Task LoadSqlAsync(CancellationToken ct)
    {
        if (SelectedIdle is { } idle)
        {
            return LoadIdleSqlAsync(idle, ct);
        }

        var (holder, waiter) = SelectedPair;
        IdleCallout = null;
        if (holder is null)
        {
            HolderSql = WaiterSql = null;
            HolderCaption = WaiterCaption = string.Empty;
            HolderHasNoCurrentSql = false;
            SqlMessage = "세션을 고르면 보유자·대기자 SQL 이 여기에 보입니다.";
            NotifySql();
            return Task.CompletedTask;
        }

        return RunAsync(async (monitor, session) =>
        {
            var holderText = await SqlTextAsync(monitor, session, holder.CurrentOrPreviousSqlId, ct);
            var waiterText = waiter is null ? null : await SqlTextAsync(monitor, session, waiter.Node.SqlId, ct);
            PostToUi(() =>
            {
                HolderCaption = $"보유자 · {holder.KeyText} {holder.Username} · {holder.StatusText}"
                    + (holder.Facts.TransactionStart is { } t && _snapshot is { } s
                        ? " " + LockText.Seconds((long)Math.Max(0, (s.CollectedAt - t).TotalSeconds)) : string.Empty);
                HolderHasNoCurrentSql = string.IsNullOrEmpty(holder.Node.SqlId);
                HolderSql = holderText is null ? null : Header(holder.CurrentOrPreviousSqlId) + holderText;
                WaiterCaption = waiter is null ? string.Empty : $"대기자 · {waiter.KeyText} {waiter.Username} · {waiter.WaitText}";
                WaiterSql = waiterText is null || waiter is null ? null : Header(waiter.Node.SqlId) + waiterText;
                SqlMessage = HolderSql is null && WaiterSql is null ? "SQL 을 찾지 못했습니다(커서가 공유 풀에서 나갔을 수 있습니다)." : string.Empty;
                NotifySql();
            });
        }, ct);
    }

    /// <summary>방치 트랜잭션: 「2·0644 SCOTT · INACTIVE 1,018 초 · 트랜잭션 17 분」 + 도구 안내 + 마지막 SQL(prev_sql_id).</summary>
    private Task<bool> LoadIdleSqlAsync(IdleTransactionItem idle, CancellationToken ct) => RunAsync(async (monitor, session) =>
    {
        var text = await SqlTextAsync(monitor, session, idle.Row.PrevSqlId, ct);
        PostToUi(() =>
        {
            HolderCaption = $"{LockText.Key(idle.Key)} {idle.Row.Username ?? "-"} · INACTIVE {LockText.Seconds(idle.Row.IdleSeconds)} · 트랜잭션 {LockText.Elapsed(idle.TransactionSeconds)}";
            HolderHasNoCurrentSql = false;
            IdleCallout = idle.Advice;
            HolderSql = text is null ? null : "-- prev_sql_id " + idle.Row.PrevSqlId + "\n" + text;
            WaiterCaption = string.Empty;
            WaiterSql = null;
            SqlMessage = HolderSql is null ? "마지막 SQL 을 찾지 못했습니다(커서가 공유 풀에서 나갔을 수 있습니다)." : string.Empty;
            NotifySql();
        });
    }, ct);

    private static string Header(string? sqlId) => "-- " + sqlId + "\n";

    private static async Task<string?> SqlTextAsync(IMonitorService monitor, ISession session, string? sqlId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(sqlId))
        {
            return null;
        }

        try
        {
            return (await monitor.GetSqlDetailAsync(session, new CursorRef(sqlId), ct)).FullText;
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !MonitorAccessState.IsPrivilegeError(ex) && ex is not NotSupportedException)
        {
            return null;
        }
    }

    private Task LoadLockedRowAsync(CancellationToken ct)
    {
        var (_, waiter) = SelectedPair;
        void Reset(string message)
        {
            LockedRowId = null;
            LockedRowSelect = null;
            LockedRowCaption = string.Empty;
            LockedRowCells = [];
            LockedRowMessage = message;
            OnPropertyChanged(nameof(HasLockedRow));
        }

        if (waiter is null)
        {
            Reset("대기 세션을 고르면 기다리는 행이 여기에 보입니다.");
            return Task.CompletedTask;
        }

        if (!LockText.IsRowLockWait(waiter.Node.Event))
        {
            Reset($"행 잠금 대기가 아닙니다({waiter.Node.Event ?? "대기 없음"}).");
            return Task.CompletedTask;
        }

        return RunAsync(async (monitor, session) =>
        {
            var row = await monitor.GetLockedRowAsync(session, waiter.Key, ct);
            PostToUi(() =>
            {
                if (row is null)
                {
                    Reset("잠긴 행을 찾지 못했습니다(대기가 끝났거나 row_wait_obj# 가 없습니다).");
                    return;
                }

                // 값을 못 읽으면(권한 없음·표가 아님·행 없음) 조회는 열 「오류」 하나에 이유를 담아 돌려준다(TSK-05-02 약속).
                if (row.ColumnNames is [LockedRowErrorColumn])
                {
                    Reset($"{row.Owner}.{row.Table} 의 잠긴 행({row.RowId}) 값을 읽지 못했습니다: {(row.Values.Count > 0 ? row.Values[0] : null) ?? "이유 모름"}");
                    return;
                }

                LockedRowId = row.RowId;
                LockedRowCaption = LockedRowText(row) + $" · 대기자 {waiter.KeyText}";
                LockedRowCells = [.. row.ColumnNames.Select((c, i) => (IReadOnlyList<object?>)[c, i < row.Values.Count ? row.Values[i] ?? "(null)" : string.Empty])];
                LockedRowSelect = $"SELECT * FROM \"{row.Owner}\".\"{row.Table}\" WHERE ROWID = '{row.RowId}'";
                LockedRowMessage = string.Empty;
                OnPropertyChanged(nameof(HasLockedRow));
            });
        }, ct);
    }

    /// <summary>「MES.LOT_HIST · 파일 7 · 블록 2495 · 행 2」(파일·블록·행이 없으면 표 이름만).</summary>
    public static string LockedRowText(LockedRowInfo row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var text = row.Owner + "." + row.Table;
        if (row.FileNumber is { } f)
        {
            text += string.Create(CultureInfo.InvariantCulture, $" · 파일 {f}");
        }

        if (row.BlockNumber is { } b)
        {
            text += string.Create(CultureInfo.InvariantCulture, $" · 블록 {b}");
        }

        if (row.RowNumber is { } r)
        {
            text += string.Create(CultureInfo.InvariantCulture, $" · 행 {r}");
        }

        return text;
    }

    private Task LoadBindsAsync(CancellationToken ct)
    {
        var (_, waiter) = SelectedPair;
        if (waiter is null)
        {
            BindCaption = string.Empty;
            BindCells = [];
            BindMessage = "대기 세션을 고르면 그 SQL 의 캡처된 바인드가 여기에 보입니다.";
            OnPropertyChanged(nameof(HasBinds));
            return Task.CompletedTask;
        }

        return RunAsync(async (monitor, session) =>
        {
            var binds = await monitor.GetWaitingBindsAsync(session, new WaitingBindTarget { Session = waiter.Key }, ct);
            PostToUi(() =>
            {
                BindCaption = $"대기자 {waiter.KeyText} · {waiter.Node.SqlId ?? "-"}";
                BindCells = [.. binds.Select(b => (IReadOnlyList<object?>)[b.Name, b.Position.ToString(CultureInfo.InvariantCulture), b.DataType ?? string.Empty, b.Value ?? "(null)",
                    b.CapturedAt?.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture) ?? string.Empty])];
                BindMessage = binds.Count == 0 ? "캡처된 바인드가 없습니다." : string.Empty;
                OnPropertyChanged(nameof(HasBinds));
            });
        }, ct);
    }

    /// <summary>DDL 잠금 탭(LK-09). 권한이 없으면 그 탭만 자물쇠 + 이유.</summary>
    public Task LoadDdlLocksAsync(CancellationToken ct = default)
    {
        if (_ddlLoaded)
        {
            return Task.CompletedTask;
        }

        return RunAsync(async (monitor, session) =>
        {
            try
            {
                var rows = await monitor.GetDdlLocksAsync(session, ct);
                PostToUi(() => ApplyDdl(rows, null, session));
                _ddlLoaded = true;
            }
            catch (Exception ex) when (MonitorAccessState.IsPrivilegeError(ex))
            {
                PostToUi(() => ApplyDdl([], ex, session));
                _ddlLoaded = true;
            }
        }, ct);
    }

    private void ApplyDdl(IReadOnlyList<DdlLockRow> rows, Exception? denied, ISession session)
    {
        DdlLocks.Clear();
        foreach (var r in rows)
        {
            DdlLocks.Add(new DdlLockItem(r));
        }

        if (denied is not null)
        {
            DdlNotice = "DDL 잠금은 dba_ddl_locks 권한이 필요합니다.";
            DdlGrantSql = $"GRANT SELECT ON sys.dba_ddl_locks TO {session.Profile.User.ToUpperInvariant()};";
            DdlSummary = "권한 없음 · " + FirstLine(denied.Message);
        }
        else
        {
            DdlNotice = null;
            DdlGrantSql = null;
            var excl = DdlLocks.Where(d => d.IsExclusive).Select(d => d.Key).Distinct().ToList();
            var waiting = DdlLocks.Count(d => d.IsWaiting);
            DdlSummary = DdlLocks.Count == 0
                ? "DDL·라이브러리 잠금이 없습니다 · dba_ddl_locks(권한이 없거나 뷰가 없는 DB 도 빈 목록)"
                : waiting > 0 && excl.Count > 0
                    ? $"Exclusive 를 잡은 {string.Join(", ", excl.Select(LockText.Key))} 이(가) {waiting} 세션을 막고 있습니다 · dba_ddl_locks"
                    : $"{DdlLocks.Count}행 · dba_ddl_locks";
        }

        Keys?.Set("canQueryDdlLocks", denied is null && _session is not null);
        OnPropertyChanged(nameof(HasDdlLocks));
        OnPropertyChanged(nameof(DdlDenied));
    }

    // ---- 화면 반영 ----------------------------------------------------------------------------

    private void Apply(LockSnapshot snap, IReadOnlyList<BlockerImpact> impacts, TimeSpan elapsed)
    {
        ClearError();
        Access.ClearNotice();
        _snapshot = snap;
        _facts = LockSessionFacts.From(snap.Rows);
        RebuildInstances(snap.Rows);

        var nodes = new Dictionary<SessionKey, BlockingNode>();
        _allTree = [];
        foreach (var root in snap.WaitTree.Roots)
        {
            Walk(root, 0, null);
        }

        void Walk(BlockingNode n, int depth, LockTreeItem? parent)
        {
            nodes.TryAdd(n.Session, n);
            var item = new LockTreeItem(n, depth, parent, _facts.GetValueOrDefault(n.Session, LockSessionFacts.Empty))
            {
                IsExpanded = !_collapsed.Contains(n.Session),
            };
            _allTree.Add(item);
            foreach (var w in n.Waiters)
            {
                Walk(w, depth + 1, item);
            }
        }

        _collapsed.IntersectWith(nodes.Keys);
        RebuildTreeRows();

        Locks.Clear();
        foreach (var r in snap.Rows)
        {
            Locks.Add(new LockItem(r, LockItem.ObjectFor(r, snap.Rows, nodes)));
        }

        Impacts.Clear();
        var rank = 0;
        foreach (var i in impacts)
        {
            Impacts.Add(new ImpactItem(++rank, i, _allTree.FirstOrDefault(t => t.Key == i.Holder),
                _facts.GetValueOrDefault(i.Holder, LockSessionFacts.Empty), snap.CollectedAt));
        }

        Hints.Clear();
        foreach (var h in LockHints.Build(_allTree))
        {
            Hints.Add(h);
        }

        var hintCount = Hints.Count(h => !h.IsMuted);
        HintCountText = hintCount > 0 ? hintCount.ToString(CultureInfo.InvariantCulture) : string.Empty;

        // 고른 세션이 사라졌으면 고름을 푼다(자동 새로 고침 중에는 그대로 남는다).
        if (SelectedKey is { } k && !_allTree.Any(t => t.Key == k) && !Locks.Any(l => l.Key == k))
        {
            SelectedKey = null;
        }

        var holders = _allTree.Count(t => t.IsHolder);
        var waiters = _allTree.Count - holders;
        var maxWait = _allTree.Count == 0 ? 0 : _allTree.Max(t => t.WaitSeconds);
        TreeCountText = holders + waiters == 0 ? string.Empty : string.Create(CultureInfo.InvariantCulture, $"{holders}·{waiters}");
        UpdateLockCaption();

        HasFilters = MinWaitSeconds > 0 || WaitingOnly || Blank(UsernameFilter) is not null || Blank(SchemaFilter) is not null || SelectedInstanceIndex > 0;
        State = _allTree.Count == 0 && Locks.Count == 0 ? LockViewState.Empty : LockViewState.Data;
        EmptyHint = EmptyHintText();
        _mainStatus = string.Create(CultureInfo.InvariantCulture,
            $"보유자 {holders} · 대기 {waiters} · 잠금 {Locks.Count}행 · 최대 대기 {LockText.Seconds(maxWait)}");
        UpdateStatusText();

        // 데드락 힌트: 잠금 목록(대기자 → 막는 세션)과 트리 간선으로 순환을 찾는다(11g 에서도 앱이 계산).
        Cycles = WaitCycles.Find(snap.Rows.Where(r => r.BlockingSession is not null && r.RequestedMode > 0)
            .Select(r => (r.Session, r.BlockingSession!.Value))
            .Concat(_allTree.Where(t => t.Parent is not null).Select(t => (t.Key, t.Parent!.Key))));
        CycleText = Cycles.Count == 0
            ? null
            : string.Join(" · ", Cycles.Select(WaitCycles.Text))
              + ". Oracle 이 곧 한쪽 문장을 ORA-00060 으로 되돌립니다(세션은 남음). 앱의 갱신 순서가 서로 반대인지 확인하세요.";

        RecordHistory(snap.CollectedAt, waiters, maxWait);
        EvaluateThreshold(snap.CollectedAt);
        _lastRead = string.Create(CultureInfo.InvariantCulture,
            $"{snap.CollectedAt.ToLocalTime():HH:mm:ss} 읽음 ({elapsed.TotalSeconds:0.00} s)");
        UpdateStatusRight();
        UpdateKeys();
    }

    private string _lastRead = string.Empty;

    private string EmptyHintText()
    {
        var parts = new List<string>();
        if (MinWaitSeconds > 0)
        {
            parts.Add($"「최소 대기 {MinWaitSeconds} 초」");
        }

        if (WaitingOnly)
        {
            parts.Add("「대기·차단만」");
        }

        if (Blank(UsernameFilter) is { } u)
        {
            parts.Add($"「사용자 {u}」");
        }

        if (Blank(SchemaFilter) is { } s)
        {
            parts.Add($"「스키마 {s}」");
        }

        if (SelectedInstanceIndex > 0 && SelectedInstanceIndex < InstanceChoices.Count)
        {
            parts.Add($"「{InstanceChoices[SelectedInstanceIndex]}」");
        }

        var again = AutoRefresh ? $"{RefreshSeconds} 초마다 다시 확인합니다." : "F5 로 다시 확인합니다.";
        return parts.Count == 0
            ? again + " 지금 거르기는 꺼져 있습니다."
            : again + " 지금은 " + string.Join("·", parts) + " 거르기가 켜져 있습니다.";
    }

    private void UpdateStatusRight()
    {
        var next = AutoRefresh && NextRefreshIn > 0 ? $" · 다음 새로 고침 {NextRefreshIn} 초" : string.Empty;
        var points = _history is { Count: > 0 } h ? string.Create(CultureInfo.InvariantCulture, $" · 이력 {h.Count:N0} 점") : string.Empty;
        StatusRight = _lastRead + points + next;
    }

    /// <summary>상태 줄 왼쪽: 방치 트랜잭션 탭이면 그 요약, 아니면 잠금 요약.</summary>
    private void UpdateStatusText()
    {
        if (_mainStatus.Length == 0 && TopTabIndex != IdleTab)
        {
            return;
        }

        StatusText = TopTabIndex == IdleTab && _idleLoaded ? IdleSummary : _mainStatus;
    }

    partial void OnNextRefreshInChanged(int value)
    {
        if (!IsBusy)
        {
            UpdateStatusRight();
        }
    }

    private void UpdateLockCaption()
    {
        var mine = SelectedKey is { } k ? Locks.Count(l => l.Key == k) : 0;
        LockCaption = string.Create(CultureInfo.InvariantCulture, $"{Locks.Count}행")
            + (mine > 0 ? $" · 고른 세션 {LockText.Key(SelectedKey!.Value)} 의 잠금 {mine}행 ▶" : string.Empty);
    }

    private void RebuildInstances(IReadOnlyList<LockRow> rows)
    {
        var have = InstanceChoices.Skip(1).ToList();
        foreach (var inst in rows.Select(r => r.Session.InstanceId).Distinct().OrderBy(i => i))
        {
            var text = string.Create(CultureInfo.InvariantCulture, $"인스턴스 {inst}");
            if (!have.Contains(text))
            {
                InstanceChoices.Add(text);
            }
        }
    }

    private void RebuildTreeRows()
    {
        var visible = new List<LockTreeItem>();
        foreach (var item in _allTree)
        {
            if (IsHidden(item))
            {
                continue;
            }

            visible.Add(item);
        }

        var lines = PlanTreeLines.Compute([.. visible.Select(v => v.Depth)]);
        TreeRows.Clear();
        for (var i = 0; i < visible.Count; i++)
        {
            visible[i].TreeLines = lines[i];
            TreeRows.Add(visible[i]);
        }

        bool IsHidden(LockTreeItem item)
        {
            for (var p = item.Parent; p is not null; p = p.Parent)
            {
                if (!p.IsExpanded)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>트리 가지 접기·펴기(▾·▸). 접은 상태는 새로 고침 뒤에도 남는다.</summary>
    public void ToggleExpanded(LockTreeItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!item.HasChildren)
        {
            return;
        }

        item.IsExpanded = !item.IsExpanded;
        if (item.IsExpanded)
        {
            _collapsed.Remove(item.Key);
        }
        else
        {
            _collapsed.Add(item.Key);
        }

        RebuildTreeRows();
    }

    private void ClearData()
    {
        _snapshot = null;
        _allTree = [];
        TreeRows.Clear();
        Locks.Clear();
        Impacts.Clear();
        Hints.Clear();
        DdlLocks.Clear();
        IdleTransactions.Clear();
        Deadlocks.Clear();
        Cycles = [];
        CycleText = null;
        OnPropertyChanged(nameof(HasIdleTransactions));
        OnPropertyChanged(nameof(HasDeadlocks));
        OnPropertyChanged(nameof(ShowNoDeadlocks));
        SelectedKey = null;
    }

    // ---- 거르기 ------------------------------------------------------------------------------

    /// <summary>「3 초 이상」.</summary>
    public void SetMinWaitThree() => MinWaitSeconds = 3;

    /// <summary>「거르기 풀기」: 최소 대기·사용자·스키마·인스턴스를 비우고 다시 읽는다(대기·차단만은 그대로).</summary>
    public Task ClearFiltersAsync(CancellationToken ct = default)
    {
        _suspendFilterRefresh = true;
        try
        {
            MinWaitSeconds = 0;
            UsernameFilter = null;
            SchemaFilter = null;
            SelectedInstanceIndex = 0;
        }
        finally
        {
            _suspendFilterRefresh = false;
        }

        return RefreshAsync(ct);
    }

    /// <summary>「모든 잠금 보기」: 대기·차단만을 끄고 다시 읽는다.</summary>
    public Task ShowAllLocksAsync(CancellationToken ct = default)
    {
        _suspendFilterRefresh = true;
        try
        {
            WaitingOnly = false;
            MinWaitSeconds = 0;
        }
        finally
        {
            _suspendFilterRefresh = false;
        }

        return RefreshAsync(ct);
    }

    private bool _suspendFilterRefresh;

    partial void OnMinWaitSecondsChanged(int value)
    {
        if (value < 0)
        {
            MinWaitSeconds = 0;
            return;
        }

        FilterChanged();
    }

    partial void OnWaitingOnlyChanged(bool value) => FilterChanged();

    partial void OnSelectedInstanceIndexChanged(int value) => FilterChanged();

    private void FilterChanged()
    {
        if (!_suspendFilterRefresh && _session is not null && _snapshot is not null)
        {
            _ = RefreshAsync();
        }
    }

    // ---- 선택·탭 -----------------------------------------------------------------------------

    partial void OnSelectedKeyChanged(SessionKey? value)
    {
        UpdateLockCaption();
        UpdateKeys();
        if (_session is not null)
        {
            _ = LoadDetailAsync();
        }
    }

    partial void OnDetailTabIndexChanged(int value)
    {
        if (_session is not null)
        {
            _ = LoadDetailAsync();
        }
    }

    partial void OnTopTabIndexChanged(int value)
    {
        UpdateStatusText();
        if (_session is null)
        {
            return;
        }

        switch (value)
        {
            case DdlTab:
                _ = LoadDdlLocksAsync();
                break;
            case IdleTab:
                _idleLoaded = false;
                _ = LoadIdleTransactionsAsync();
                break;
            case DeadlockTab:
                _ = LoadDeadlocksAsync();
                break;
        }

        // SQL 탭 내용이 위쪽 탭(방치 트랜잭션 여부)에 따라 달라진다. 방치 탭은 목록을 읽은 뒤에 다시 읽는다.
        _detailLoaded = null;
        if (value != IdleTab)
        {
            _ = LoadDetailAsync();
        }
    }

    partial void OnErrorTextChanged(string? value) => OnPropertyChanged(nameof(HasError));

    partial void OnHolderSqlChanged(string? value) => OnPropertyChanged(nameof(HasHolderSql));

    partial void OnWaiterSqlChanged(string? value) => OnPropertyChanged(nameof(HasWaiterSql));

    private void NotifySql()
    {
        OnPropertyChanged(nameof(HasHolderSql));
        OnPropertyChanged(nameof(HasWaiterSql));
    }

    /// <summary>Enter: 상세 탭을 앞으로(이미 SQL 이면 그대로) 내고 읽는다.</summary>
    public Task ShowDetailAsync(int tab, CancellationToken ct = default)
    {
        DetailTabIndex = tab;
        return LoadDetailAsync(ct);
    }

    // ---- 자동 새로 고침 ------------------------------------------------------------------------

    partial void OnRefreshSecondsChanged(int value)
    {
        var clamped = Math.Clamp(value, MinRefreshSeconds, MaxRefreshSeconds);
        if (clamped != value)
        {
            RefreshSeconds = clamped;
        }
    }

    partial void OnAutoRefreshChanged(bool value)
    {
        StopLoop();
        if (value && !_disposed)
        {
            var cts = new CancellationTokenSource();
            _autoLoop = cts;
            _ = LoopAsync(cts.Token);
        }

        NextRefreshIn = 0;
        UpdateKeys();
    }

    private void StopLoop()
    {
        if (Interlocked.Exchange(ref _autoLoop, null) is { } cts)
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                for (var left = Math.Clamp(RefreshSeconds, MinRefreshSeconds, MaxRefreshSeconds); left > 0; left--)
                {
                    var l = left;
                    PostToUi(() => NextRefreshIn = l);
                    await Task.Delay(TimeSpan.FromSeconds(1), ct);
                }

                if (_session is not null)
                {
                    await ReadAsync(keepDetail: true, ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 멈춤.
        }
    }

    // ---- 세션 종료·연결 끊기·이동 ---------------------------------------------------------------

    /// <summary>확인 창 내용: 대상, 사용자·트랜잭션, 「이 세션 때문에 N개 세션이 기다립니다」 + 대기자 목록, 보낼 문장.</summary>
    public LockKillRequest? BuildKillRequest(bool disconnect)
    {
        if (SelectedKey is not { } key || _session is not { } session)
        {
            return null;
        }

        var item = _allTree.FirstOrDefault(t => t.Key == key);
        var facts = _facts.GetValueOrDefault(key, LockSessionFacts.Empty);
        var idle = IdleTransactions.FirstOrDefault(i => i.Key == key);
        var profile = session.Profile;
        var rac = session.Server.IsRac;
        var waiters = item is null ? [] : Descendants(item).ToList();
        var line = string.Join(" · ", waiters.Select(w =>
        {
            var behind = w.Parent is { } p && p.Key != key ? ", " + p.KeyText + " 뒤" : string.Empty;
            return $"{w.KeyText} {w.Username} ({LockText.Seconds(w.Node.SecondsInWait)}{behind})";
        }));
        var facts2 = new List<KeyValuePair<string, string>>
        {
            new("세션", string.Create(CultureInfo.InvariantCulture, $"{key.Sid}, {key.Serial}, @{key.InstanceId}")),
            new("사용자", string.Join(" · ", new[]
            {
                item?.Username ?? facts.Username ?? idle?.Row.Username, facts.Machine ?? idle?.Row.Machine, facts.Program ?? idle?.Row.Program,
            }.Where(v => !string.IsNullOrEmpty(v)))),
        };
        if ((facts.TransactionStart ?? idle?.Row.TransactionStart) is { } t && _snapshot is { } snap)
        {
            var undo = (facts.UndoBlocks ?? idle?.Row.UndoBlocks) is { } u ? $" · undo {u.ToString("N0", CultureInfo.InvariantCulture)} 블록" : string.Empty;
            facts2.Add(new("트랜잭션", LockText.Seconds((long)Math.Max(0, (snap.CollectedAt - t).TotalSeconds)) + undo));
        }

        var statement = disconnect
            ? SessionMonitorCommands.DisconnectStatement(key, immediate: true, rac).Text
            : LockText.KillStatement(key, rac);
        var verb = disconnect ? "연결을 끊을까요?" : "종료할까요?";
        return new LockKillRequest(key, disconnect)
        {
            Question = $"세션 {LockText.Key(key)} {(disconnect ? "의 " : "을 ")}{verb}",
            HeaderTitle = $"{LockText.Environment(profile.Environment)} {profile.User.ToUpperInvariant()}@{profile.Name}",
            HeaderDetail = "· " + (session.Server.DatabaseName ?? session.Server.ServiceName ?? profile.Name)
                + (rac ? string.Create(CultureInfo.InvariantCulture, $" (RAC 인스턴스 {key.InstanceId})") : string.Empty),
            Environment = profile.Environment,
            Facts = facts2,
            WaiterCount = waiters.Count,
            WaiterLine = line,
            Note = disconnect
                ? "연결을 끊으면 열린 트랜잭션은 롤백됩니다. undo 가 크면 롤백에 시간이 걸리고 그동안 잠금이 남을 수 있습니다."
                : "종료하면 열린 트랜잭션은 롤백됩니다. undo 가 크면 롤백에 시간이 걸리고 그동안 잠금이 남을 수 있습니다.",
            Statement = statement,
        };
    }

    private IEnumerable<LockTreeItem> Descendants(LockTreeItem root)
    {
        foreach (var i in _allTree)
        {
            for (var p = i.Parent; p is not null; p = p.Parent)
            {
                if (p == root)
                {
                    yield return i;
                    break;
                }
            }
        }
    }

    /// <summary>고른 세션 종료(Delete). 확인 창 뒤 세션 모니터와 같은 Kill 경로(정책 관문)를 탄다.</summary>
    public Task KillAsync(CancellationToken ct = default) => ChangeSessionAsync(false, ct);

    /// <summary>고른 세션 연결 끊기(실행 엔진의 위험 명령 확인을 거친다).</summary>
    public Task DisconnectAsync(CancellationToken ct = default) => ChangeSessionAsync(true, ct);

    private async Task ChangeSessionAsync(bool disconnect, CancellationToken ct)
    {
        if (!CanKill || BuildKillRequest(disconnect) is not { } request)
        {
            return;
        }

        if (ConfirmKill is null || !await ConfirmKill(request))
        {
            StatusText = $"{request.ActionText} 을(를) 취소했습니다.";
            return;
        }

        var done = await RunAsync(async (monitor, session) =>
        {
            if (!disconnect)
            {
                await monitor.KillAsync(session, request.Target, true, ct);
                return;
            }

            var exec = Execution ?? _services?.GetService(typeof(IExecutionService)) as IExecutionService
                ?? throw new InvalidOperationException("실행 엔진이 없습니다.");
            var stmt = SessionMonitorCommands.DisconnectStatement(request.Target, immediate: true, session.Server.IsRac);
            var result = await exec.ExecuteAsync(session, stmt, new Dictionary<string, BindValue>(),
                new ExecutionOptions { Source = HistorySource.Monitor }, ct);
            switch (result.Status)
            {
                case ExecutionStatus.Cancelled:
                    throw new OperationCanceledException();
                case ExecutionStatus.Denied:
                    throw new UnauthorizedAccessException(result.Policy?.Message ?? "정책 관문이 막았습니다.");
                case ExecutionStatus.Failed:
                    throw new InvalidOperationException(result.Error?.Message ?? "실행하지 못했습니다.");
            }
        }, ct, reportAsBanner: false);

        if (done)
        {
            await RefreshAsync(ct);
            PostToUi(() => StatusText = $"세션 {LockText.Key(request.Target)} 을(를) {(disconnect ? "끊었습니다" : "종료했습니다")}.");
        }
    }

    /// <summary>「세션 모니터로」.</summary>
    public async Task GoToSessionAsync()
    {
        if (SelectedKey is { } key && GoToSessionMonitor is { } go)
        {
            await go(key);
        }
    }

    /// <summary>「실제 계획」: 대기자 SQL(없으면 보유자 SQL)의 커서 계획을 계획 창에.</summary>
    public Task ShowPlanAsync(CancellationToken ct = default)
    {
        var (holder, waiter) = SelectedPair;
        var sqlId = waiter?.Node.SqlId ?? holder?.CurrentOrPreviousSqlId;
        if (string.IsNullOrEmpty(sqlId) || ShowPlan is not { } show)
        {
            return Task.CompletedTask;
        }

        return RunAsync((_, session) => show(session, new CursorRef(sqlId), ct), ct, reportAsBanner: false);
    }

    /// <summary>「편집기로 열기」: 보유자·대기자 SQL 을 새 SQL 탭에(실행하지 않는다).</summary>
    public void OpenSqlInEditor()
    {
        var text = SqlForCopy();
        if (text.Length == 0)
        {
            return;
        }

        if (OpenEditor is { } open)
        {
            open(text);
        }
        else
        {
            SqlEditor.SqlEditorHost.Current.NewDocument(text).Title = "Lock 모니터 SQL";
        }
    }

    /// <summary>글을 새 SQL 탭에 넣는다(실행하지 않는다). 데드락 기록 Enter 가 쓴다.</summary>
    public void OpenTextInEditor(string text, string title)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        if (OpenEditor is { } open)
        {
            open(text);
        }
        else
        {
            SqlEditor.SqlEditorHost.Current.NewDocument(text).Title = title;
        }
    }

    /// <summary>SQL 탭의 보유자·대기자 SQL(주석 머리 포함).</summary>
    public string SqlForCopy()
    {
        var parts = new List<string>();
        if (HolderSql is { } h)
        {
            parts.Add("-- " + HolderCaption + "\n" + h.TrimEnd() + ";");
        }

        if (WaiterSql is { } w)
        {
            parts.Add("-- " + WaiterCaption + "\n" + w.TrimEnd() + ";");
        }

        return string.Join("\n\n", parts);
    }

    // ---- 복사·CSV ----------------------------------------------------------------------------

    /// <summary>「복사」: 대기 트리와 잠금 목록을 TSV 로(머리글 포함, 트리는 들여쓰기).</summary>
    public string BuildCopyText()
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join('\t', "세션", "사용자", "상태", "대기", "이벤트", "객체", "sql_id"));
        foreach (var t in _allTree)
        {
            sb.AppendLine(string.Join('\t', new string(' ', t.Depth * 2) + t.KeyText, LockText.Tsv(t.Username), t.StatusText, t.WaitText,
                LockText.Tsv(t.EventText), LockText.Tsv(t.ObjectText), LockText.Tsv(t.SqlText)));
        }

        sb.AppendLine();
        sb.AppendLine(string.Join('\t', LockItem.Columns));
        foreach (var l in Locks)
        {
            sb.AppendLine(string.Join('\t', l.Cells.Select(c => LockText.Tsv(c as string))));
        }

        return sb.ToString();
    }

    /// <summary>「CSV 내보내기」: 잠금 목록(머리글 + 행, RFC 4180 따옴표).</summary>
    public string BuildLocksCsv()
    {
        var sb = new StringBuilder();
        sb.Append(string.Join(',', LockItem.Columns.Select(LockText.Csv))).Append("\r\n");
        foreach (var l in Locks)
        {
            sb.Append(string.Join(',', l.Cells.Select(c => LockText.Csv(c as string)))).Append("\r\n");
        }

        return sb.ToString();
    }

    public static string SuggestedCsvName => string.Create(CultureInfo.InvariantCulture, $"locks-{DateTime.Now:yyyyMMdd-HHmmss}.csv");

    // ---- 공통 -------------------------------------------------------------------------------

    /// <summary>모니터 세션에서 한 번에 하나씩 실행한다. 성공하면 true. 실패는 띠(목록 읽기) 또는 상태 줄(동작)에 보인다.</summary>
    private async Task<bool> RunAsync(Func<IMonitorService, ISession, Task> work, CancellationToken ct, bool reportAsBanner = true)
    {
        if (_session is not { } session)
        {
            StatusText = "모니터 세션이 열려 있지 않습니다.";
            return false;
        }

        var monitor = _monitor ??= _services?.GetService(typeof(IMonitorService)) as IMonitorService;
        if (monitor is null)
        {
            StatusText = "모니터 서비스가 없습니다.";
            return false;
        }

        try
        {
            await _gate.WaitAsync(ct);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or OperationCanceledException)
        {
            return false;
        }

        try
        {
            await work(monitor, session);
            return true;
        }
        catch (OperationCanceledException)
        {
            PostToUi(() => StatusText = "취소했습니다.");
            return false;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PostToUi(() => ShowFailure(ex, session, reportAsBanner));
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void ShowFailure(Exception ex, ISession session, bool banner)
    {
        var message = FirstLine(ex.Message);
        if (MonitorAccessState.IsPrivilegeError(ex))
        {
            AutoRefresh = false;
            ClearData();
            Access.ShowDenied(session.Profile);
            State = LockViewState.Denied;
            StatusText = "권한 없음 · " + (OraCode(message) ?? message);
            UpdateKeys();
            return;
        }

        if (!banner)
        {
            StatusText = message;
            return;
        }

        var broken = IsDisconnect(ex) || session.State == SessionState.Broken;
        ErrorIsDisconnect = broken;
        ErrorText = broken
            ? $"모니터 세션이 끊겼습니다 — {message}. 자동 새로 고침을 멈췄습니다."
            : $"잠금 정보를 읽지 못했습니다 — {message}";
        if (broken)
        {
            AutoRefresh = false;
        }

        IsStale = _snapshot is not null;
        if (_snapshot is null && State == LockViewState.Loading)
        {
            State = LockViewState.Empty;
            EmptyHint = "F5 로 다시 읽습니다.";
        }

        StatusText = broken ? "끊김" : "오류 · " + (OraCode(message) ?? message);
        StatusRight = _snapshot is { } s
            ? $"마지막으로 읽은 {s.CollectedAt.ToLocalTime():HH:mm:ss} 자료를 흐리게 보입니다"
            : string.Empty;
    }

    private void ClearError()
    {
        ErrorText = null;
        ErrorIsDisconnect = false;
        IsStale = false;
    }

    private static bool IsDisconnect(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e.Message.Contains("ORA-03113", StringComparison.Ordinal) || e.Message.Contains("ORA-03114", StringComparison.Ordinal)
                || e.Message.Contains("ORA-03135", StringComparison.Ordinal) || e.Message.Contains("ORA-00028", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string? OraCode(string message)
    {
        var i = message.IndexOf("ORA-", StringComparison.Ordinal);
        return i >= 0 && i + 9 <= message.Length ? message.Substring(i, 9) : null;
    }

    private void UpdateKeys()
    {
        if (Keys is not { } keys)
        {
            return;
        }

        var open = _session is not null;
        keys.Set("lockMonitorOpen", open);
        keys.Set("lockSessionSelected", open && SelectedKey is not null);
        keys.Set("lockWaiterSelected", open && SelectedPair.Waiter is not null);
        keys.Set("lockCanKillSession", open && _session?.Server.Capabilities.CanKillSession == true);
        keys.Set("canQueryDdlLocks", open && DdlNotice is null);
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(CanKill));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopLoop();
        Interlocked.Exchange(ref _alertTimer, null)?.Cancel();
        ClearThreshold();
        if (Interlocked.Exchange(ref _session, null) is { } s)
        {
            _ = ConnectionService.CloseAsync(s, timeout: CloseTimeout);
        }

        UpdateKeys();
        _gate.Dispose();
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string FirstLine(string message)
    {
        var i = message.IndexOf('\n', StringComparison.Ordinal);
        return (i < 0 ? message : message[..i]).Trim();
    }
}
