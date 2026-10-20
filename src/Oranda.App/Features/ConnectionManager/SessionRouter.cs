using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Threading;
using Oranda.App.Features.DedicatedSession;
using Oranda.App.Features.Execution;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell;
using Oranda.App.Shell.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Sessions;

namespace Oranda.App.Features.ConnectionManager;

/// <summary>접속 콤보에서 접속을 고른 결과(<see cref="SessionRouter.Select"/>).</summary>
public enum SessionSelect
{
    Selected,

    /// <summary>전용 세션 탭은 자기 세션을 쓴다.</summary>
    Dedicated,

    /// <summary>탭의 접속이 실행 중이거나 미커밋이다.</summary>
    Held,

    /// <summary>고른 접속이 닫혔다.</summary>
    Closed,
}

/// <summary>
/// 탭·문서가 쓸 세션을 고르는 한 곳(CONN-06, TSK-03-08). 접속은 여러 개를 동시에 열 수 있으므로 기능마다 「마지막 작업 세션」을 짐작하지 않고
/// 여기에 묻는다: SQL 탭은 전용 세션(D-017) → 탭에 묶인 접속(<see cref="SqlEditorViewModel.ConnectionProfileId"/>) → 지금 접속 순이다.
/// 탭에 묶인 접속이 열려 있지 않으면 null(「접속 안 됨」, <see cref="MissingProfileOf"/>)이고 지금 접속으로 대신하지 않는다.
/// 활성 탭을 바꾸면 그 탭의 접속이 지금 접속이 되고(<see cref="IConnectionService.Switch"/>), 새로 접속하면 활성 SQL 탭이 그 접속을 따른다.
/// 컨테이너마다 하나(<see cref="Resolve"/>). UI 스레드에서만 쓴다(접속 서비스 이벤트는 UI 스레드로 옮겨 받는다).
/// </summary>
public sealed class SessionRouter
{
    private static readonly ConditionalWeakTable<ServiceContainer, SessionRouter> Routers = [];
    private static readonly Lock Gate = new();

    private readonly IConnectionService _connections;
    private readonly SqlEditorHost _host;
    private readonly DedicatedSessionRegistry? _dedicated;
    private readonly ExecutionState _state;
    private readonly IEventBus? _bus;
    private readonly Dictionary<Guid, string> _names = []; // 프로필 ID → 이름(「접속 안 됨(이름)」)
    private readonly HashSet<Guid> _quietConnects = []; // 접속 안 됨 탭의 「다시 접속」: 새 접속이 활성 탭을 옮기지 않는다
    private SqlEditorViewModel? _watched;
    private bool _namesLoading;

    public SessionRouter(IConnectionService connections, SqlEditorHost host, ExecutionState state, DedicatedSessionRegistry? dedicated = null, IEventBus? bus = null)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(state);
        _connections = connections;
        _host = host;
        _state = state;
        _dedicated = dedicated;
        _bus = bus;
        connections.ActiveSessionChanged += (_, _) => Post(OnActiveSessionChanged);
        connections.Connected += (_, session) => Post(() => OnConnected(session));
        host.ActiveChanged += (_, _) => OnActiveTabChanged();
        host.TabOpened += (_, tab) => OnTabOpened(tab);
        if (dedicated is not null)
        {
            dedicated.FocusSync = OnActiveTabChanged; // 문서 전환(SQL 작업 공간 ↔ 다른 문서)·전용 세션 붙임/뗌도 같이 맞춘다
        }

        if (connections.ActiveSession is { } active)
        {
            _state.UsePrimary(active.Id); // 접속이 없으면 그대로 둔다(세션 이벤트로 이미 고른 것을 지우지 않게)
        }

        Watch(host.Active);
    }

    /// <summary>보이는 접속(<see cref="ShownSession"/>·<see cref="ShownMissing"/>)이나 지금 접속이 바뀌었다. UI 스레드에서 온다.</summary>
    public event EventHandler? Changed;

    /// <summary>컨테이너의 라우터. 처음이면 만든다(그때의 <see cref="SqlEditorHost.Current"/> 를 쓴다).</summary>
    public static SessionRouter Resolve(ServiceContainer container)
    {
        ArgumentNullException.ThrowIfNull(container);
        if (Find(container) is { } found)
        {
            return found;
        }

        // 접속 서비스를 처음 만들면 그 안에서 라우터도 만든다(ConnectionManagerCommands.Resolve → 여기). 그래서 잠금 밖에서 먼저 꺼내고,
        // 잠근 뒤 다시 본다(라우터가 둘 생기거나 두 잠금을 거꾸로 잡지 않게).
        var connections = ConnectionManagerCommands.Resolve(container);
        var dedicated = DedicatedSessionCommands.Resolve(container);
        lock (Gate)
        {
            if (Routers.TryGetValue(container, out var existing))
            {
                return existing;
            }

            var bus = container.GetService<IEventBus>();
            var router = new SessionRouter(connections, SqlEditorHost.Current, ExecutionState.For(bus), dedicated, bus);
            Routers.Add(container, router);
            if (container.GetService<ShellController>() is { } shell)
            {
                // 창 테두리(환경 색)는 활성 문서의 접속을, 창 제목 [운영] 은 열린 접속 모두를 따른다.
                void Follow()
                {
                    shell.ActiveEnvironment = router.ShownSession?.Profile.Environment;
                    shell.ProductionOpen = connections.Sessions.Any(s => s.Profile.Environment == ConnectionEnvironment.Production);
                }

                router.Changed += (_, _) => Follow();
                Follow();
            }

            return router;
        }
    }

    /// <summary>
    /// 이미 만든 라우터(접속 서비스와 함께 생긴다). 없으면 null — 만들지 않는다(시작 때 Oracle 어셈블리를 깨우지 않게, 툴바 콤보·상태 표시줄이 쓴다).
    /// </summary>
    public static SessionRouter? Find(IServiceProvider? services) =>
        services is ServiceContainer container && Routers.TryGetValue(container, out var router) ? router : null;

    /// <summary>
    /// 서비스 공급자의 지금 접속(<see cref="IConnectionService.ActiveSession"/>). 탭을 따르지 않는 도구 창·명령이 쓴다.
    /// 접속 서비스가 없는 컨테이너(시험)면 세션 관리자의 마지막 작업 세션.
    /// </summary>
    public static ISession? CurrentOf(IServiceProvider? services)
    {
        if (services?.GetService(typeof(IConnectionService)) is IConnectionService connections)
        {
            return connections.ActiveSession;
        }

        return (services?.GetService(typeof(ISessionManager)) as ISessionManager)?.Sessions
            .LastOrDefault(s => s.Purpose == SessionPurpose.Work && s.State != SessionState.Closed);
    }

    /// <summary>
    /// 활성 문서의 접속(<see cref="ShownSession"/>: 활성 SQL 탭의 세션, 전용 세션 탭이면 그 세션, 접속 안 됨 탭이면 null).
    /// 라우터가 아직 없는 컨테이너면 <see cref="CurrentOf"/>. 활성 탭에서 도는 명령(@파일 실행·통계)이 쓴다.
    /// </summary>
    public static ISession? ShownOf(IServiceProvider? services) => Find(services) is { } router ? router.ShownSession : CurrentOf(services);

    /// <summary>
    /// 탭이 쓸 세션(<see cref="SessionFor"/>). 라우터가 아직 없는 컨테이너면 탭에 묶인 프로필의 작업 세션, 묶이지 않았으면 <see cref="CurrentOf"/>.
    /// </summary>
    public static ISession? ForTab(IServiceProvider? services, SqlEditorViewModel? tab)
    {
        if (Find(services) is { } router)
        {
            return router.SessionFor(tab);
        }

        return ForProfile(services, tab?.ConnectionProfileId);
    }

    /// <summary>프로필의 열린 작업 세션(없으면 null). 프로필이 null 이면 <see cref="CurrentOf"/>. PL/SQL 편집기처럼 SQL 탭이 아닌 문서가 쓴다.</summary>
    public static ISession? ForProfile(IServiceProvider? services, Guid? profile)
    {
        if (profile is not { } profileId)
        {
            return CurrentOf(services);
        }

        return services?.GetService(typeof(IConnectionService)) is IConnectionService connections
            ? connections.SessionOf(profileId)
            : (services?.GetService(typeof(ISessionManager)) as ISessionManager)?.Sessions
                .LastOrDefault(s => s.Purpose == SessionPurpose.Work && s.State != SessionState.Closed && s.Profile.Id == profileId);
    }

    /// <summary>열린 접속(연 순서, 툴바 콤보 번호 = 위치 + 1).</summary>
    public IReadOnlyList<ISession> Sessions => _connections.Sessions;

    /// <summary>접속 이름 「번호 - 사용자@이름」(툴바 콤보와 같다). 열린 접속이 아니면 번호 없이.</summary>
    public string LabelOf(ISession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var index = _connections.Sessions.ToList().IndexOf(session);
        var name = $"{session.Profile.User}@{session.Profile.Name}";
        return index < 0 ? name : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{index + 1} - {name}");
    }

    /// <summary>열린 접속이 끊겼는지.</summary>
    public bool IsBroken(ISession session) => _connections.IsSessionBroken(session.Id) || session.State == SessionState.Broken;

    /// <summary>지금 접속. 탭을 따르지 않는 도구 창(스키마 브라우저·세션 모니터 등)이 쓴다.</summary>
    public ISession? Current => _connections.ActiveSession;

    /// <summary>
    /// 탭이 쓸 세션: 전용 세션 → 탭에 묶인 접속 → (묶이지 않았으면) 지금 접속. 묶인 접속이 열려 있지 않으면 null. 탭이 null 이면 지금 접속.
    /// </summary>
    public ISession? SessionFor(SqlEditorViewModel? tab)
    {
        if (tab is null)
        {
            return Current;
        }

        if (_dedicated?.SessionOf(tab) is { } own)
        {
            return own;
        }

        return tab.ConnectionProfileId is { } profileId ? _connections.SessionOf(profileId) : Current;
    }

    /// <summary>탭에 묶인 접속이 열려 있지 않으면 그 프로필 ID(「접속 안 됨(이름)」). 열려 있거나 묶이지 않았거나 전용 세션 탭이면 null.</summary>
    public Guid? MissingProfileOf(SqlEditorViewModel? tab) =>
        tab?.ConnectionProfileId is { } profileId && _dedicated?.SessionOf(tab) is null && _connections.SessionOf(profileId) is null
            ? profileId
            : null;

    /// <summary>프로필 이름. 열린 접속에서 먼저 찾고, 없으면 저장된 프로필 목록(처음 한 번 읽는다). 모르면 null.</summary>
    public string? ProfileName(Guid profileId)
    {
        if (_connections.Sessions.FirstOrDefault(s => s.Profile.Id == profileId) is { } open)
        {
            _names[profileId] = open.Profile.Name;
            return open.Profile.Name;
        }

        if (_names.TryGetValue(profileId, out var name))
        {
            return name;
        }

        _ = LoadNamesAsync();
        return null;
    }

    /// <summary>프로필 이름. 저장된 프로필을 아직 안 읽었으면 읽고 나서 답한다(「접속 안 됨」 묻기 글). 모르면 null.</summary>
    public async Task<string?> ProfileNameAsync(Guid profileId)
    {
        if (ProfileName(profileId) is { } name)
        {
            return name;
        }

        await LoadNamesAsync().ConfigureAwait(true);
        return _names.GetValueOrDefault(profileId);
    }

    /// <summary>
    /// 접속 안 됨 탭의 「다시 접속」(<paramref name="connect"/>). 접속하는 동안 사용자가 다른 탭으로 가도 새 접속이 그 탭을 옮기지 않는다
    /// (새 접속은 그 프로필에 묶인 탭이 쓴다). 못 열었으면 null.
    /// </summary>
    public async Task<ISession?> ReconnectAsync(Guid profileId, Func<Task<ISession?>> connect)
    {
        ArgumentNullException.ThrowIfNull(connect);
        _quietConnects.Add(profileId);
        ISession? session = null;
        try
        {
            session = await connect().ConfigureAwait(true);
            return session;
        }
        finally
        {
            if (session is null)
            {
                _quietConnects.Remove(profileId); // 열렸으면 Connected 가 지운다(이벤트가 뒤에 올 수 있다)
            }
        }
    }

    /// <summary>SQL 작업 공간 문서가 활성인지(아니면 PL/SQL 편집기·객체 상세처럼 지금 접속을 쓰는 문서다).</summary>
    public bool SqlDocumentActive => _dedicated?.SqlDocumentActive?.Invoke() is not false;

    /// <summary>활성 문서의 접속(툴바 콤보·상태 표시줄·창 테두리): SQL 작업 공간이 활성이면 활성 탭의 세션, 아니면 지금 접속.</summary>
    public ISession? ShownSession => SqlDocumentActive && _host.Active is { } tab ? SessionFor(tab) : Current;

    /// <summary>활성 SQL 탭의 묶인 접속이 열려 있지 않으면 그 프로필 ID(<see cref="ShownSession"/> 은 null).</summary>
    public Guid? ShownMissing => SqlDocumentActive ? MissingProfileOf(_host.Active) : null;

    /// <summary>
    /// 접속 콤보에서 고른 접속을 활성 SQL 탭에 묶고 지금 접속으로 바꾼다. 전용 세션 탭은 자기 세션을 쓰므로, 실행 중·미커밋 탭은 그 트랜잭션을
    /// 놓치지 않게 바꾸지 않는다. SQL 작업 공간이 활성이 아니면 지금 접속만 바꾼다.
    /// </summary>
    public SessionSelect Select(ISession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!ReferenceEquals(_connections.SessionOf(session.Profile.Id), session))
        {
            return SessionSelect.Closed; // 고르는 사이에 닫혔다: 탭을 닫힌 접속에 묶지 않는다
        }

        if (SqlDocumentActive && _host.Active is { } tab)
        {
            if (_dedicated?.SessionOf(tab) is not null)
            {
                return SessionSelect.Dedicated;
            }

            if (IsHeld(tab) && !ReferenceEquals(SessionFor(tab), session))
            {
                return SessionSelect.Held;
            }

            tab.ConnectionProfileId = session.Profile.Id;
        }

        if (!_connections.Switch(session))
        {
            return SessionSelect.Closed;
        }

        Refocus();
        RaiseChanged();
        return SessionSelect.Selected;
    }

    /// <summary><see cref="Select"/> 의 거절 이유 글(메시지 창). 고른 대로 됐으면 null.</summary>
    public static string? RefusalText(SessionSelect result) => result switch
    {
        SessionSelect.Dedicated => "전용 세션 탭은 자기 세션을 쓰므로 접속을 바꾸지 않습니다. 다른 접속은 새 SQL 탭에서 고르세요.",
        SessionSelect.Held => "이 탭은 실행 중이거나 커밋하지 않은 변경이 있어 접속을 바꾸지 않습니다. 커밋·롤백하거나 실행이 끝난 뒤 고르세요.",
        SessionSelect.Closed => "고른 접속이 닫혔습니다.",
        _ => null,
    };

    /// <summary>실행 상태가 보여 줄 세션을 활성 문서에 맞춘다(<see cref="ExecutionState.Focus"/>). 전용 세션 등록부도 이것을 부른다.</summary>
    public void Refocus()
    {
        if (!SqlDocumentActive || _host.Active is not { } tab)
        {
            _state.Focus(null);
        }
        else if (SessionFor(tab) is { } session)
        {
            _state.Focus(session.Id);
        }
        else if (MissingProfileOf(tab) is not null)
        {
            _state.FocusNothing();
        }
        else
        {
            _state.Focus(null);
        }
    }

    private void OnActiveSessionChanged()
    {
        _state.UsePrimary(Current?.Id);
        Refocus();
        RaiseChanged();
    }

    private void OnConnected(ISession session)
    {
        // 새 접속은 지금 접속이 되고 활성 SQL 탭이 따른다. SQL 작업 공간이 활성이 아니면 활성 탭의 접속이 닫혔을 때만 따른다.
        // 탭의 접속이 실행 중이거나 미커밋이면 옮기지 않는다(그 트랜잭션을 놓치거나 취소가 닿지 않게).
        if (_quietConnects.Remove(session.Profile.Id))
        {
            SwitchToActiveTab(); // 다시 접속한 사이 다른 탭으로 갔으면 지금 접속은 보이는 탭의 접속으로 둔다
        }
        else if (_host.Active is { } tab && _dedicated?.SessionOf(tab) is null
            && (SqlDocumentActive || tab.ConnectionProfileId is null || MissingProfileOf(tab) is not null))
        {
            if (!IsHeld(tab))
            {
                tab.ConnectionProfileId = session.Profile.Id;
            }
            else if (SqlDocumentActive && SwitchToActiveTab())
            {
                // 보이는 탭의 접속을 지금 접속으로 둔다(스키마 브라우저가 탭과 다른 접속을 보이지 않게). 새 접속은 콤보에 있다.
                ExecutionLog.For(_bus).AddMessage(MessageKind.Info,
                    $"이 탭은 실행 중이거나 커밋하지 않은 변경이 있어 접속을 그대로 둡니다. 새 접속 「{session.Profile.User}@{session.Profile.Name}」 은 접속 콤보에서 고르세요.");
            }
        }

        Refocus();
        RaiseChanged();
    }

    /// <summary>
    /// 탭의 접속이 실행 중이거나 미커밋인데 그 접속을 쓰는 탭이 이 탭뿐이다(다른 접속으로 옮기면 그 트랜잭션·실행을 볼 탭이 없어진다).
    /// 같은 접속을 쓰는 다른 탭이 있으면 옮겨도 된다.
    /// </summary>
    private bool IsHeld(SqlEditorViewModel tab) =>
        SessionFor(tab) is { } s && (_state.IsBusy(s.Id) || _state.IsTxnPending(s.Id) || s.HasPendingTransaction)
        && !_host.Tabs.Any(t => !ReferenceEquals(t, tab) && _dedicated?.SessionOf(t) is null && ReferenceEquals(SessionFor(t), s));

    private void OnActiveTabChanged()
    {
        Watch(_host.Active);
        SwitchToActiveTab();
        Refocus();
        RaiseChanged();
    }

    private void OnTabOpened(SqlEditorViewModel tab)
    {
        // 접속 없이 만든 탭은 지금 접속을 물려받는다(작업 공간 복원으로 다시 연 탭은 저장한 접속을 그대로 둔다).
        if (tab.ConnectionProfileId is null && Current is { } current)
        {
            tab.ConnectionProfileId = current.Profile.Id;
        }
    }

    /// <summary>활성 탭의 접속 바꿈(다른 기능이 <see cref="SqlEditorViewModel.ConnectionProfileId"/> 를 바꿨을 때)도 표시에 반영한다.</summary>
    private void Watch(SqlEditorViewModel? tab)
    {
        if (ReferenceEquals(_watched, tab))
        {
            return;
        }

        if (_watched is not null)
        {
            _watched.PropertyChanged -= OnWatchedChanged;
        }

        _watched = tab;
        if (tab is not null)
        {
            tab.PropertyChanged += OnWatchedChanged;
        }
    }

    private void OnWatchedChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SqlEditorViewModel.ConnectionProfileId))
        {
            SwitchToActiveTab(); // 보이는 접속과 지금 접속(스키마 브라우저 등)이 갈리지 않게
            Refocus();
            RaiseChanged();
        }
    }

    /// <summary>SQL 작업 공간이 활성이고 활성 탭이 열린 접속에 묶였으면 그 접속을 지금 접속으로 한다. 바꿨으면 true.</summary>
    private bool SwitchToActiveTab() =>
        SqlDocumentActive && _host.Active is { } tab && _dedicated?.SessionOf(tab) is null && tab.ConnectionProfileId is { } profileId
        && _connections.SessionOf(profileId) is { } bound && !ReferenceEquals(bound, Current)
        && _connections.Switch(bound); // ActiveSessionChanged 가 상태·표시를 맞춘다

    private async Task LoadNamesAsync()
    {
        if (_namesLoading)
        {
            return;
        }

        _namesLoading = true;
        try
        {
            var doc = await _connections.LoadAsync(CancellationToken.None).ConfigureAwait(true);
            foreach (var p in doc.Value.Profiles)
            {
                _names[p.Id] = p.Name;
            }

            RaiseChanged();
        }
#pragma warning disable CA1031 // 이름을 못 읽어도 「접속 안 됨」 만 보이면 된다.
        catch (Exception e)
#pragma warning restore CA1031
        {
            System.Diagnostics.Trace.TraceWarning("접속 프로필 이름을 읽지 못했다: {0}", e.Message);
        }
        finally
        {
            _namesLoading = false; // 나중에 더한 프로필도 다시 읽을 수 있게
        }
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

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
