using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.Features.ConnectionManager;
using Oranda.App.Shell;
using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Execution;
using Oranda.Core.History;
using Oranda.Core.Metadata;
using Oranda.Core.Monitoring;
using Oranda.Core.Sessions;
using Oranda.Core.Shell;

namespace Oranda.App.Features.SessionMonitor;

/// <summary>
/// <c>oranda.panel.sessionMonitor</c> — 세션 목록·필터·자동 새로 고침·델타, 블로킹 트리, 세션 상세, Kill·Disconnect, SQL 트레이스 (TSK-09-01, SESS-01~06).
/// <para>
/// 전용 모니터 세션(<see cref="SessionPurpose.Monitor"/>, CONN-07)에서만 조회한다. 세션은 <see cref="StartAsync"/> 가 열고 <see cref="StopAsync"/> 가 닫는다.
/// 모든 서버 호출은 한 번에 하나만(같은 연결) 보낸다. 서비스는 처음 쓸 때 꺼낸다(시작 경로에서 Oracle 어셈블리를 올리지 않는다, D-006).
/// </para>
/// </summary>
[OrandaToolPanel("oranda.panel.sessionMonitor", ToolPanelKind.Document, DockLocation.Document)]
public sealed partial class SessionMonitorViewModel : ToolPanelViewModelBase, IDisposable
{
    /// <summary>자동 새로 고침 간격 한계(초).</summary>
    public const int MinRefreshSeconds = 2;

    /// <inheritdoc cref="MinRefreshSeconds"/>
    public const int MaxRefreshSeconds = 60;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IServiceProvider? _services;
    private IMonitorService? _monitor;
    private CancellationTokenSource? _autoLoop;
    private ISession? _session;
    private bool _disposed;

    [ObservableProperty]
    private string? _usernameFilter;

    [ObservableProperty]
    private string? _textFilter;

    [ObservableProperty]
    private bool _activeOnly;

    [ObservableProperty]
    private bool _includeBackground;

    [ObservableProperty]
    private int _refreshSeconds = 5;

    [ObservableProperty]
    private bool _autoRefresh;

    [ObservableProperty]
    private bool _deltaMode;

    [ObservableProperty]
    private bool _traceWaits = true;

    [ObservableProperty]
    private bool _traceBinds;

    [ObservableProperty]
    private SessionItem? _selectedSession;

    [ObservableProperty]
    private SessionDetail? _detail;

    [ObservableProperty]
    private string _detailText = string.Empty;

    [ObservableProperty]
    private string _statusText = "세션 모니터: 열면 접속 계정(또는 고른 접속)으로 전용 세션을 열어 조회합니다.";

    [ObservableProperty]
    private string? _tracePath;

    [ObservableProperty]
    private bool _isOpen;

    /// <summary>시험·디자이너용: 서비스 없이 만든다.</summary>
    public SessionMonitorViewModel()
    {
    }

    /// <summary>셸(DI)이 쓰는 생성자. 서비스는 처음 쓸 때 꺼낸다.</summary>
    public SessionMonitorViewModel(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
        Keys = services.GetService(typeof(ContextKeyService)) as ContextKeyService;
        if (Keys is not null && services.GetService(typeof(IEventBus)) is IEventBus bus)
        {
            SessionMonitorCommands.AttachPrivilegeKeys(bus, Keys);
        }
    }

    /// <summary>시험용: 모니터 서비스와 세션 여는 방법을 직접 준다.</summary>
    public SessionMonitorViewModel(IMonitorService monitor, Func<CancellationToken, Task<ISession?>> openSession, ContextKeyService? keys = null)
    {
        _monitor = monitor;
        OpenSession = openSession;
        Keys = keys;
    }

    public ObservableCollection<SessionItem> Sessions { get; } = [];

    public ObservableCollection<BlockingItem> BlockingRoots { get; } = [];

    /// <summary>메뉴 활성 키(<c>monitorSessionOpen</c>·<c>monitorSessionSelected</c>·<c>canKillSession</c>·<c>canTraceSessions</c>)를 쓰는 곳. 없으면 쓰지 않는다.</summary>
    public ContextKeyService? Keys { get; set; }

    /// <summary>전용 모니터 세션을 여는 방법. 셸은 <see cref="SessionMonitorCommands.OpenMonitorSessionAsync"/> 를 넣는다.</summary>
    public Func<CancellationToken, Task<ISession?>>? OpenSession { get; set; }

    /// <summary>「다른 접속으로 열기…」: 프로필을 고르고(비밀번호 묻기 포함) 그 프로필로 모니터 전용 세션을 연다. 취소하면 null.</summary>
    public Func<CancellationToken, Task<ISession?>>? ChooseSession { get; set; }

    /// <summary>「다른 접속으로 열기」로 연 프로필을 다음에 열 때의 기본값으로 기억하는 방법. 없으면 기억하지 않는다.</summary>
    public Action<ConnectionProfile>? Remember { get; set; }

    /// <summary>세션 닫기를 기다리는 최대 시간. null 이면 기본(<c>SessionCloser.Timeout</c>). 시험이 줄인다.</summary>
    public TimeSpan? CloseTimeout { get; set; }

    /// <summary>권한 없음 안내 띠와 「모니터 접속: …」 표시.</summary>
    public MonitorAccessState Access { get; } = new();

    /// <summary>Kill·Disconnect 확인 대화상자(대상 세션 정보 표시). 없으면 진행하지 않는다(안전 쪽).</summary>
    public Func<string, Task<bool>>? Confirm { get; set; }

    /// <summary>Disconnect 를 보낼 실행 엔진. 없으면 서비스에서 꺼낸다.</summary>
    public IExecutionService? Execution { get; set; }

    /// <summary>UI 스레드로 넘기는 방법. 시험은 바로 실행하도록 바꾼다.</summary>
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

    /// <summary>지금 쓰는 모니터 세션. 열리지 않았으면 null.</summary>
    public ISession? MonitorSession => _session;

    public bool CanKill => _session?.Server.Capabilities.CanKillSession == true;

    public bool CanTrace => _session?.Server.Capabilities.CanTraceSessions == true;

    /// <summary>모니터 세션을 열고 첫 목록을 읽는다. 이미 열려 있으면 새로 고침만 한다.</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_session is not null)
        {
            await RefreshAsync(ct);
            return;
        }

        if (OpenSession is null)
        {
            StatusText = "모니터 세션을 열 수 없습니다(접속 정보 없음).";
            return;
        }

        ISession? session;
        try
        {
            session = await OpenSession(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StatusText = "모니터 세션을 열지 못했습니다: " + FirstLine(ex.Message);
            return;
        }

        if (session is null)
        {
            StatusText = "모니터 세션을 열지 않았습니다.";
            return;
        }

        await AttachAsync(session, chosen: false, ct);
    }

    /// <summary>
    /// 「다른 접속으로 열기」. 고른 프로필의 전용 세션으로 바꾼다(메인 작업 접속은 그대로).
    /// 고른 계정도 V$ 뷰를 못 읽으면 이미 열려 있던 모니터 접속을 그대로 두고 알리기만 한다.
    /// </summary>
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

        if (session is null)
        {
            if (_session is null)
            {
                StatusText = "모니터 세션을 열지 않았습니다.";
            }

            return;
        }

        await AttachAsync(session, chosen: true, ct);
    }

    /// <summary>열린 세션을 점검해 모니터 세션으로 삼는다. 권한이 없으면 닫고 안내 띠를 켠다(이미 열린 세션이 있으면 그대로 둔다).</summary>
    private async Task AttachAsync(ISession session, bool chosen, CancellationToken ct)
    {
        if (!session.Server.Capabilities.CanQueryPerformanceViews)
        {
            await ConnectionService.CloseAsync(session, timeout: CloseTimeout);
            if (_session is null)
            {
                Access.ShowDenied(session.Profile);
                Access.SetAccount(null);
            }

            StatusText = $"계정 '{session.Profile.User}' 은(는) v$session 을 읽을 권한이 없어 세션 모니터를 쓸 수 없습니다.";
            return;
        }

        var previous = Interlocked.Exchange(ref _session, session);
        IsOpen = true;
        Access.ClearNotice();
        Access.SetAccount(session.Profile);
        OnPropertyChanged(nameof(CanKill));
        OnPropertyChanged(nameof(CanTrace));
        UpdateKeys();
        if (chosen)
        {
            Remember?.Invoke(session.Profile);
        }

        if (previous is not null)
        {
            PostToUi(() =>
            {
                Sessions.Clear();
                BlockingRoots.Clear();
                SelectedSession = null;
            });
            await CloseSessionAsync(previous);
        }

        await RefreshAsync(ct);
    }

    /// <summary>세션을 쓰는 중인 작업이 끝나기를 기다린 뒤, UI 스레드를 붙들지 않고 제한 시간까지만 기다려 닫는다.</summary>
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

    /// <summary>자동 새로 고침을 멈추고 모니터 세션을 닫는다.</summary>
    public async Task StopAsync()
    {
        AutoRefresh = false;
        StopLoop();
        var session = Interlocked.Exchange(ref _session, null);
        IsOpen = false;
        UpdateKeys();
        Access.SetAccount(null);
        if (session is not null)
        {
            await CloseSessionAsync(session);
        }
    }

    /// <summary>목록을 다시 읽는다(F5). 블로킹 트리도 함께 갱신한다.</summary>
    public Task RefreshAsync(CancellationToken ct = default) => RunAsync(async (monitor, session) =>
    {
        var filter = new SessionFilter
        {
            Username = UsernameFilter,
            ActiveOnly = ActiveOnly,
            IncludeBackground = IncludeBackground,
            Text = TextFilter,
        };
        var rows = await monitor.GetSessionsAsync(session, filter, ct);
        var tree = await monitor.GetBlockingTreeAsync(session, ct);
        PostToUi(() => Apply(rows, tree));
    }, ct);

    /// <summary>선택한 세션의 상세(현재·직전 SQL, 실제 계획, 트랜잭션, 잠금, 통계).</summary>
    public Task LoadDetailAsync(CancellationToken ct = default)
    {
        if (SelectedSession is not { } item)
        {
            return Task.CompletedTask;
        }

        return RunAsync(async (monitor, session) =>
        {
            var detail = await monitor.GetSessionDetailAsync(session, item.Row.Key, ct);
            PostToUi(() =>
            {
                Detail = detail;
                DetailText = FormatDetail(detail);
            });
        }, ct);
    }

    /// <summary>선택한 세션을 Kill 한다. 대상 정보를 보여 주는 확인 뒤, 실행 엔진의 위험 명령 확인을 거친다.</summary>
    public Task KillAsync(bool immediate, CancellationToken ct = default) =>
        ChangeSessionAsync(immediate ? "Kill(IMMEDIATE)" : "Kill", async (monitor, session, row) =>
            await monitor.KillAsync(session, row.Key, immediate, ct), ct);

    /// <summary>선택한 세션을 Disconnect 한다(<c>POST_TRANSACTION</c> 또는 <c>IMMEDIATE</c>).</summary>
    public Task DisconnectAsync(bool immediate, CancellationToken ct = default) =>
        ChangeSessionAsync(immediate ? "Disconnect(IMMEDIATE)" : "Disconnect", async (_, session, row) =>
        {
            var exec = Execution ?? _services?.GetService(typeof(IExecutionService)) as IExecutionService
                ?? throw new InvalidOperationException("실행 엔진이 없습니다.");
            var stmt = SessionMonitorCommands.DisconnectStatement(row.Key, immediate, session.Server.IsRac);
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
        }, ct);

    /// <summary>선택한 세션의 SQL 트레이스를 켜거나 끈다. 켜면 트레이스 파일 경로를 보여 준다.</summary>
    public Task SetTraceAsync(bool enable, CancellationToken ct = default)
    {
        if (SelectedSession is not { } item)
        {
            return Task.CompletedTask;
        }

        var options = new TraceOptions { Waits = TraceWaits, Binds = TraceBinds };
        return RunAsync(async (monitor, session) =>
        {
            var path = await monitor.SetTraceAsync(session, item.Row.Key, enable, options, ct);
            PostToUi(() =>
            {
                TracePath = path;
                StatusText = enable
                    ? $"SID {item.Row.Key.Sid} 트레이스를 켰습니다. 파일: {path ?? "(알 수 없음)"}"
                    : $"SID {item.Row.Key.Sid} 트레이스를 껐습니다.";
            });
        }, ct);
    }

    /// <summary>Kill·Disconnect 확인 대화상자에 보일 대상 세션 정보.</summary>
    public static string DescribeTarget(SessionRow row, string action)
    {
        ArgumentNullException.ThrowIfNull(row);
        var sb = new StringBuilder();
        sb.Append("세션을 ").Append(action).AppendLine(" 합니다.");
        sb.Append(CultureInfo.InvariantCulture, $"SID {row.Key.Sid}, SERIAL# {row.Key.Serial}, 인스턴스 {row.Key.InstanceId}").AppendLine();
        sb.Append("사용자: ").Append(row.Username ?? "-").Append(" / OS: ").Append(row.OsUser ?? "-").AppendLine();
        sb.Append("머신: ").Append(row.Machine ?? "-").AppendLine();
        sb.Append("프로그램: ").Append(row.Program ?? "-").Append(" / 모듈: ").Append(row.Module ?? "-").AppendLine();
        sb.Append("상태: ").Append(row.Status).Append(" / SQL ID: ").Append(row.SqlId ?? "-");
        return sb.ToString();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopLoop();
        if (Interlocked.Exchange(ref _session, null) is { } s)
        {
            _ = ConnectionService.CloseAsync(s, timeout: CloseTimeout);
        }

        UpdateKeys();
        _gate.Dispose();
    }

    partial void OnSelectedSessionChanged(SessionItem? value)
    {
        Keys?.Set("monitorSessionSelected", value is not null && _session is not null);
        if (value is null)
        {
            Detail = null;
            DetailText = string.Empty;
        }
    }

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
        if (value)
        {
            StartLoop();
        }
    }

    partial void OnDeltaModeChanged(bool value)
    {
        foreach (var s in Sessions)
        {
            s.ShowDeltas = value;
        }
    }

    private async Task ChangeSessionAsync(string action, Func<IMonitorService, ISession, SessionRow, Task> change, CancellationToken ct)
    {
        if (SelectedSession is not { } item)
        {
            return;
        }

        if (Confirm is null || !await Confirm(DescribeTarget(item.Row, action)))
        {
            StatusText = $"{action} 을(를) 취소했습니다.";
            return;
        }

        var done = false;
        await RunAsync(async (monitor, session) =>
        {
            await change(monitor, session, item.Row);
            done = true;
        }, ct);

        if (done)
        {
            await RefreshAsync(ct);
            PostToUi(() => StatusText = $"SID {item.Row.Key.Sid} 세션을 {action} 했습니다.");
        }
    }

    /// <summary>모니터 세션에서 한 번에 하나씩 실행한다. 실패는 상태 줄에 보인다.</summary>
    private async Task RunAsync(Func<IMonitorService, ISession, Task> work, CancellationToken ct)
    {
        if (_session is not { } session)
        {
            StatusText = "모니터 세션이 열려 있지 않습니다.";
            return;
        }

        var monitor = _monitor ??= _services?.GetService(typeof(IMonitorService)) as IMonitorService;
        if (monitor is null)
        {
            StatusText = "모니터 서비스가 없습니다.";
            return;
        }

        try
        {
            await _gate.WaitAsync(ct);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            await work(monitor, session);
        }
        catch (OperationCanceledException)
        {
            PostToUi(() => StatusText = "취소했습니다.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PostToUi(() => ShowFailure(ex, session));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 서버 오류를 상태 줄에 보인다. 권한 부족(ORA-00942·ORA-01031)이면 점검을 통과했어도 일부 뷰를 못 읽는 것이므로
    /// 목록을 비우고 안내 띠를 켠다(자동 새로 고침은 멈춘다).
    /// </summary>
    private void ShowFailure(Exception ex, ISession session)
    {
        StatusText = FirstLine(ex.Message);
        if (!MonitorAccessState.IsPrivilegeError(ex))
        {
            return;
        }

        AutoRefresh = false;
        Sessions.Clear();
        BlockingRoots.Clear();
        SelectedSession = null;
        Access.ShowDenied(session.Profile);
    }

    private void Apply(IReadOnlyList<SessionRow> rows, BlockingTree tree)
    {
        Access.ClearNotice();
        var selected = SelectedSession?.Row.Key;
        Sessions.Clear();
        foreach (var r in rows)
        {
            Sessions.Add(new SessionItem(r) { ShowDeltas = DeltaMode });
        }

        SelectedSession = selected is { } k ? Sessions.FirstOrDefault(s => s.Row.Key == k) : null;
        ApplyTree(tree);
        var blocked = rows.Count(r => r.BlockingSession is not null);
        StatusText = string.Create(CultureInfo.InvariantCulture,
            $"세션 {rows.Count}개 · 대기 중 {blocked}개 · {DateTime.Now:HH:mm:ss} 갱신");
    }

    private void ApplyTree(BlockingTree tree)
    {
        BlockingRoots.Clear();
        foreach (var root in tree.Roots)
        {
            BlockingRoots.Add(new BlockingItem(root));
        }
    }

    private void StartLoop()
    {
        var cts = new CancellationTokenSource();
        _autoLoop = cts;
        _ = LoopAsync(cts.Token);
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
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(RefreshSeconds, MinRefreshSeconds, MaxRefreshSeconds)), ct);
                if (_session is not null)
                {
                    await RefreshAsync(ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 멈춤.
        }
    }

    private void UpdateKeys()
    {
        if (Keys is not { } keys)
        {
            return;
        }

        var open = _session is not null;
        keys.Set("monitorSessionOpen", open);
        keys.Set("monitorSessionSelected", open && SelectedSession is not null);
        keys.Set("canKillSession", open && CanKill);
        keys.Set("canTraceSessions", open && CanTrace);
    }

    /// <summary>상세를 글자 표로 만든다(상세 탭).</summary>
    public static string FormatDetail(SessionDetail detail)
    {
        ArgumentNullException.ThrowIfNull(detail);
        var sb = new StringBuilder();
        var r = detail.Session;
        sb.Append(CultureInfo.InvariantCulture, $"SID {r.Key.Sid}, SERIAL# {r.Key.Serial}, 인스턴스 {r.Key.InstanceId} · {r.Username ?? "-"} · {r.Status}").AppendLine();
        sb.Append("대기: ").Append(r.Event ?? "-").Append(" (").Append(r.WaitClass ?? "-").Append(')').AppendLine();
        sb.AppendLine();
        sb.Append("[현재 SQL] ").AppendLine(r.SqlId ?? "-");
        sb.AppendLine(detail.CurrentSqlText ?? "(없음)");
        sb.AppendLine();
        sb.Append("[직전 SQL] ").AppendLine(r.PrevSqlId ?? "-");
        sb.AppendLine(detail.PreviousSqlText ?? "(없음)");
        foreach (var section in detail.Sections)
        {
            sb.AppendLine();
            sb.Append('[').Append(section.Title).AppendLine("]");
            AppendTable(sb, section);
        }

        return sb.ToString();
    }

    private static void AppendTable(StringBuilder sb, DetailSection section)
    {
        if (section.Rows.Count == 0)
        {
            sb.AppendLine("(없음)");
            return;
        }

        if (section.Columns.Count == 1)
        {
            foreach (var row in section.Rows)
            {
                sb.AppendLine(Cell(row.Count > 0 ? row[0] : null));
            }

            return;
        }

        var widths = section.Columns.Select((c, i) => Math.Min(40, Math.Max(c.Length,
            section.Rows.Max(row => i < row.Count ? Cell(row[i]).Length : 0)))).ToArray();
        sb.AppendLine(string.Join("  ", section.Columns.Select((c, i) => c.PadRight(widths[i]))).TrimEnd());
        foreach (var row in section.Rows)
        {
            sb.AppendLine(string.Join("  ", widths.Select((w, i) => Cut(i < row.Count ? Cell(row[i]) : string.Empty, w).PadRight(w))).TrimEnd());
        }
    }

    private static string Cell(object? v) => v switch
    {
        null => string.Empty,
        DateTime d => d.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString() ?? string.Empty,
    };

    private static string Cut(string s, int width) => s.Length <= width ? s : s[..(width - 1)] + "…";

    private static string FirstLine(string message)
    {
        var i = message.IndexOf('\n', StringComparison.Ordinal);
        return (i < 0 ? message : message[..i]).Trim();
    }
}

/// <summary>세션 목록 한 줄.</summary>
public sealed partial class SessionItem(SessionRow row) : ObservableObject
{
    private static readonly (string Name, string Short)[] DeltaNames =
    [
        ("CPU used by this session", "CPU"),
        ("session logical reads", "LIO"),
        ("physical reads", "PIO"),
        ("execute count", "EXEC"),
        ("redo size", "REDO"),
    ];

    [ObservableProperty]
    private bool _showDeltas;

    public SessionRow Row { get; } = row;

    public string KeyText => Row.Key.InstanceId == 1
        ? string.Create(CultureInfo.InvariantCulture, $"{Row.Key.Sid},{Row.Key.Serial}")
        : string.Create(CultureInfo.InvariantCulture, $"{Row.Key.Sid},{Row.Key.Serial},@{Row.Key.InstanceId}");

    public string Username => Row.Username ?? "(백그라운드)";

    public string Status => Row.Status;

    public string Event => Row.Event ?? string.Empty;

    public string SqlId => Row.SqlId ?? string.Empty;

    public string Program => Row.Program ?? string.Empty;

    public string Machine => Row.Machine ?? string.Empty;

    public string Module => Row.Module ?? string.Empty;

    public string Elapsed => Row.LastCallElapsedSeconds is { } s ? s.ToString(CultureInfo.InvariantCulture) + "s" : string.Empty;

    public string BlockedBy => Row.BlockingSession is { } b ? b.Sid.ToString(CultureInfo.InvariantCulture) : string.Empty;

    public bool IsBlocked => Row.BlockingSession is not null;

    /// <summary>델타 모드일 때 직전 조회 대비 증가량(0 이 아닌 것만).</summary>
    public string DeltaText => !ShowDeltas
        ? string.Empty
        : string.Join(" ", DeltaNames
            .Where(d => Row.Deltas.TryGetValue(d.Name, out var v) && v != 0)
            .Select(d => string.Create(CultureInfo.InvariantCulture, $"{d.Short}+{Row.Deltas[d.Name]}")));

    partial void OnShowDeltasChanged(bool value) => OnPropertyChanged(nameof(DeltaText));
}

/// <summary>블로킹 트리 노드(뿌리 = 최종 블로커).</summary>
public sealed class BlockingItem(BlockingNode node)
{
    public BlockingNode Node { get; } = node;

    public IReadOnlyList<BlockingItem> Children { get; } = [.. node.Waiters.Select(w => new BlockingItem(w))];

    public string Title
    {
        get
        {
            var n = Node;
            var sb = new StringBuilder();
            sb.Append(CultureInfo.InvariantCulture, $"SID {n.Session.Sid},{n.Session.Serial}");
            if (n.Session.InstanceId != 1)
            {
                sb.Append(CultureInfo.InvariantCulture, $" @{n.Session.InstanceId}");
            }

            sb.Append("  ").Append(n.Username ?? "-");
            if (n.Event is { Length: > 0 } e)
            {
                sb.Append("  ").Append(e);
                if (n.SecondsInWait is { } s)
                {
                    sb.Append(CultureInfo.InvariantCulture, $" ({s}s)");
                }
            }

            if (n.LockedObject is { Length: > 0 } o)
            {
                sb.Append("  ← ").Append(o);
            }

            if (n.SqlId is { Length: > 0 } q)
            {
                sb.Append("  SQL ").Append(q);
            }

            return sb.ToString();
        }
    }
}
