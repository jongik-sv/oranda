using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.Features.ConnectionManager;
using Oranda.App.Features.Execution;
using Oranda.App.Features.Localization;
using Oranda.App.Shell;
using Oranda.App.Shell.Composition;
using Oranda.Core.Events;
using Oranda.Core.Shell;

namespace Oranda.App.Features.Sessions;

/// <summary>
/// <c>oranda.panel.sessions</c> — 「세션」 도구 창(TSK-14-04, D-034): 열린 작업 세션 모두(접속마다 그 접속과 같은 DB 에 더 연 세션)를 한 줄씩 보인다.
/// 줄 = 「번호 - 사용자@이름 (순번)」, SID, 쓰는 방식·연결된 워크스페이스 수·상태(대기·실행 중·끊김·미커밋).
/// 우클릭 = 이 세션 쓰기(활성 SQL 작업 공간을 이 세션에 연결)·커밋·롤백·세션 닫기, 줄 두 번 누르기·Enter = 이 세션 쓰기(TSK-14-06). 기본 배치에서는 숨겨 두고 세션이 둘 이상이 되면
/// <see cref="SessionsAutoShow"/> 가 보인다. 조회만 한다(SID 는 접속 때 읽어 둔 값, 쓰기 SQL 없음).
/// 라우터·실행 상태는 이 창이 처음 쓰일 때 가져온다(시작 때 접속 서비스를 만들면 Oracle 어셈블리를 깨운다, D-006).
/// </summary>
[OrandaToolPanel("oranda.panel.sessions", ToolPanelKind.Tool, DockLocation.Left, Order = 30)]
public sealed partial class SessionsViewModel : ToolPanelViewModelBase, IDisposable
{
    private readonly ServiceContainer? _container;
    private readonly IEventBus? _bus;
    private readonly List<IDisposable> _subscriptions = [];
    private readonly Dictionary<Guid, SessionRow> _byId = [];
    private ISessionOps? _ops;
    private SessionRouter? _router;
    private ExecutionState? _state;
    private bool _started;
    private bool _wired;
    private bool _refreshQueued;

    /// <summary>시험·디자이너용: 서비스 없이 만든다(빈 목록).</summary>
    public SessionsViewModel()
    {
    }

    // 셸(DI)이 쓰는 생성자. 이벤트 버스만 받고 접속 서비스·라우터는 처음 Rows 를 읽을 때 찾는다.
    // 매개변수에 접속 서비스 형을 두면 셸의 생성자 고르기가 지연 어셈블리를 훑어 시작 때 Oracle 을 깨운다(OutlineViewModel 주석).
    public SessionsViewModel(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _container = services as ServiceContainer;
        _bus = services.GetRegisteredOrDefault<IEventBus>();
        if (_container is not null)
        {
            _ops = new ContainerSessionOps(_container);
        }
    }

    /// <summary>시험용: 라우터·실행 상태·동작을 직접 넣는다.</summary>
    internal SessionsViewModel(SessionRouter router, ExecutionState state, ISessionOps ops, IEventBus? bus = null)
    {
        _router = router;
        _state = state;
        _ops = ops;
        _bus = bus;
        PostToUi = a => a();
    }

    /// <summary>화면을 UI 스레드로 넘기는 방법. 시험은 바로 실행하도록 바꾼다.</summary>
    public Action<Action> PostToUi { get; set; } = a =>
    {
        if (Application.Current is null || Dispatcher.UIThread.CheckAccess())
        {
            a();
        }
        else
        {
            Dispatcher.UIThread.Post(a);
        }
    };

    private ObservableCollection<SessionRow> RowsCore { get; } = [];

    /// <summary>열린 작업 세션들(접속 순서, 접속마다 첫째가 접속). 처음 읽을 때 이벤트를 듣기 시작한다.</summary>
    public ObservableCollection<SessionRow> Rows
    {
        get
        {
            Start();
            return RowsCore;
        }
    }

    [ObservableProperty]
    public partial SessionRow? Selected { get; set; }

    /// <summary>아래 상태 줄: 「열린 세션 3」, 없으면 안내.</summary>
    [ObservableProperty]
    public partial string StatusMessage { get; private set; } = string.Empty;

    /// <summary>열린 세션이 하나도 없을 때의 안내(빈 화면).</summary>
    [ObservableProperty]
    public partial bool IsEmpty { get; private set; } = true;

    [ObservableProperty]
    public partial string EmptyHint { get; private set; } = UiText.Get("Sessions.empty", "열린 세션이 없습니다. 접속하면 여기에 세션이 보입니다.");

    private void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        if (_bus is not null)
        {
            _subscriptions.Add(_bus.Subscribe<SessionOpened>(this, _ => QueueRefresh()));
            _subscriptions.Add(_bus.Subscribe<SessionClosed>(this, _ => QueueRefresh()));
        }

        Hook();
        Refresh();
    }

    /// <summary>라우터·실행 상태를 가져와 바뀜을 듣는다(접속 서비스가 아직 없으면 아무것도 안 하고, 세션이 열릴 때 다시 시도한다).</summary>
    private bool Hook()
    {
        _router ??= SessionRouter.Find(_container);
        if (_router is null)
        {
            return false;
        }

        _state ??= ExecutionState.For(_bus);
        if (!_wired)
        {
            _wired = true;
            _router.Changed += OnRouterChanged;
            _state.PropertyChanged += OnStateChanged;
        }

        return true;
    }

    private void OnRouterChanged(object? sender, EventArgs e) => QueueRefresh();

    private void OnStateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => QueueRefresh();

    /// <summary>
    /// 한 번에 모아 다시 만든다: 실행 중에는 실행 상태가 자주 바뀌고, 세션을 열고 닫는 이벤트는 접속 서비스의 목록이 갱신되기 전에 올 수 있어
    /// 한 박자 뒤(UI 스레드 대기열 끝)에 읽는다.
    /// </summary>
    private void QueueRefresh()
    {
        if (_refreshQueued)
        {
            return;
        }

        _refreshQueued = true;
        PostToUi(() =>
        {
            _refreshQueued = false;
            Refresh();
        });
    }

    /// <summary>지금 열린 세션으로 목록을 맞춘다. 같은 세션의 행은 그대로 두고 값만 바꾼다.</summary>
    public void Refresh()
    {
        if (!Hook())
        {
            ReplaceRows([]);
            return;
        }

        var sessions = _router!.AllSessions;
        var rows = new List<SessionRow>(sessions.Count);
        foreach (var session in sessions)
        {
            if (!_byId.TryGetValue(session.Id, out var row))
            {
                row = new SessionRow(session);
            }

            row.Update(_router, _state!);
            rows.Add(row);
        }

        ReplaceRows(rows);
    }

    private void ReplaceRows(List<SessionRow> rows)
    {
        _byId.Clear();
        foreach (var row in rows)
        {
            _byId[row.Session.Id] = row;
        }

        for (var i = RowsCore.Count - 1; i >= 0; i--)
        {
            if (!rows.Contains(RowsCore[i]))
            {
                RowsCore.RemoveAt(i);
            }
        }

        for (var i = 0; i < rows.Count; i++)
        {
            if (i >= RowsCore.Count || !ReferenceEquals(RowsCore[i], rows[i]))
            {
                var at = RowsCore.IndexOf(rows[i]);
                if (at >= 0)
                {
                    RowsCore.Move(at, i);
                }
                else
                {
                    RowsCore.Insert(i, rows[i]);
                }
            }
        }

        if (Selected is not null && !RowsCore.Contains(Selected))
        {
            Selected = null;
        }

        IsEmpty = RowsCore.Count == 0;
        StatusMessage = IsEmpty ? string.Empty : UiText.Format("Sessions.header", "열린 세션 {0}", RowsCore.Count);
        EmptyHint = UiText.Get("Sessions.empty", "열린 세션이 없습니다. 접속하면 여기에 세션이 보입니다.");
    }

    // ---- 우클릭 명령 -------------------------------------------------------------------------

    /// <summary>
    /// 「이 세션 쓰기」: 활성 SQL 작업 공간의 탭(전용 세션 탭 제외)이 있고 그 작업 공간이 아직 이 세션을 쓰지 않으면 쓸 수 있다
    /// (<see cref="SessionRouter.CanUseSession"/>, TSK-14-10: 효과 없을 때 켜지지 않는다. 첫 실행 때 새 세션을 열 차례면 보이는 접속 세션도 고를 수 있다).
    /// </summary>
    public bool CanUse(SessionRow? row) =>
        row is not null && !row.IsBroken && _router is { } router && router.CanUseSession(row.Session);

    public bool CanCommit(SessionRow? row) => _ops is not null && row is { IsPending: true, IsRunning: false, IsBroken: false };

    public bool CanRollback(SessionRow? row) => CanCommit(row);

    public bool CanClose(SessionRow? row) => row is not null && _router is not null;

    /// <summary>
    /// 이 세션 쓰기: 활성 워크스페이스를 이 세션에 연결한다(툴바 콤보에서 고르는 것과 같다, <see cref="SessionRouter.SelectAsync"/>).
    /// 이전 세션의 미커밋 확인은 기존 창이 맡고, 거절되면 이유를 메시지 창에 남긴다.
    /// </summary>
    public async Task UseAsync(SessionRow? row, CancellationToken ct = default)
    {
        if (row is null || !Hook())
        {
            return;
        }

        var result = await _router!.SelectAsync(row.Session, ct).ConfigureAwait(true);
        if (SessionRouter.RefusalText(result) is { } text)
        {
            ExecutionLog.For(_bus).AddMessage(MessageKind.Warning, text);
        }

        Refresh();
    }

    /// <summary>
    /// 줄 두 번 누르기·Enter: 「이 세션 쓰기」 를 쓸 수 있으면(SQL·MyBatis 작업 공간이 활성이고 아직 이 세션을 쓰지 않으면) 그것을 한다. 아니면 아무것도 안 한다.
    /// </summary>
    public Task ActivateAsync(SessionRow? row, CancellationToken ct = default) => Hook() && CanUse(row) ? UseAsync(row, ct) : Task.CompletedTask;

    public async Task CommitAsync(SessionRow? row, CancellationToken ct = default)
    {
        if (CanCommit(row) && _ops is { } ops)
        {
            await ops.CommitAsync(row!.Session, ct).ConfigureAwait(true);
            Refresh();
        }
    }

    public async Task RollbackAsync(SessionRow? row, CancellationToken ct = default)
    {
        if (CanRollback(row) && _ops is { } ops)
        {
            await ops.RollbackAsync(row!.Session, ct).ConfigureAwait(true);
            Refresh();
        }
    }

    /// <summary>
    /// 세션 닫기: 실행 중이면 알리고 닫지 않는다. 미커밋이면 이 세션에 대해 한 번 묻고(커밋/롤백/취소) 취소하면 닫지 않는다.
    /// 접속(기본 세션)도 같은 규칙으로 닫을 수 있고, 더 연 세션이 남아 있으면 처음 연 것이 그 자리의 접속이 된다(툴바 「접속 끊기」 는 그 DB 세션을 모두 끊는다).
    /// 닫으면 그 세션을 쓰던 워크스페이스는 「접속 안 됨」 이 되어 다음 실행 때 기존 창이 다시 고르게 한다: 그 자리로 올라온 세션으로 몰래 옮기지 않는다
    /// (<see cref="SessionRouter.BoundSession"/>, D-039).
    /// </summary>
    public async Task CloseAsync(SessionRow? row, CancellationToken ct = default)
    {
        if (row is null || !Hook() || _ops is not { } ops)
        {
            return;
        }

        var session = row.Session;
        if (_state!.IsRunning(session.Id))
        {
            ExecutionLog.For(_bus).AddMessage(MessageKind.Warning, $"「{_router!.LabelOf(session)}」 에서 실행 중입니다. 실행을 취소하거나 끝난 뒤 세션을 닫으세요.");
            return;
        }

        if (!await ops.ConfirmUncommittedAsync(session, ct).ConfigureAwait(true))
        {
            return;
        }

        await ops.DisconnectAsync(session, ct).ConfigureAwait(true);
        Refresh();
    }

    public void Dispose()
    {
        foreach (var subscription in _subscriptions)
        {
            subscription.Dispose();
        }

        _subscriptions.Clear();
        if (_wired)
        {
            _router!.Changed -= OnRouterChanged;
            _state!.PropertyChanged -= OnStateChanged;
            _wired = false;
        }
    }
}
