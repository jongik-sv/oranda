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

    /// <summary>탭의 접속이 실행 중이거나 미커밋이다(묻지 않는 <see cref="SessionRouter.Select"/>).</summary>
    Held,

    /// <summary>고른 접속이 닫혔다.</summary>
    Closed,

    /// <summary>탭·문서의 접속이 실행(컴파일) 중이라 바꾸지 않았다.</summary>
    Busy,

    /// <summary>이전 접속의 미커밋 확인에서 취소했다.</summary>
    Cancelled,
}

/// <summary>
/// 탭·문서가 쓸 세션을 고르는 한 곳(CONN-06, TSK-03-08). 접속은 여러 개를 동시에 열 수 있으므로 기능마다 「마지막 작업 세션」을 짐작하지 않고
/// 여기에 묻는다: SQL 탭은 전용 세션(D-017) → 탭의 작업 공간이 그 접속에서 쓰는 세션(TSK-14-01, SessionRouter.Workspaces.cs) →
/// 탭에 묶인 접속(<see cref="SqlEditorViewModel.ConnectionProfileId"/>) → 지금 접속 순이다.
/// 탭에 묶인 접속이 열려 있지 않으면 null(「접속 안 됨」, <see cref="MissingProfileOf"/>)이고 지금 접속으로 대신하지 않는다.
/// 활성 탭을 바꾸면 그 탭의 접속이 지금 접속이 되고(<see cref="IConnectionService.Switch"/>), 새로 접속하면 활성 SQL 탭이 그 접속을 따른다.
/// PL/SQL 편집기처럼 자기 접속을 가진 문서(<see cref="IConnectionDocument"/>)가 활성이면 툴바 콤보는 그 문서의 접속을 보이고 바꾼다(TSK-12-04).
/// 컨테이너마다 하나(<see cref="Resolve"/>). UI 스레드에서만 쓴다(접속 서비스 이벤트는 UI 스레드로 옮겨 받는다).
/// </summary>
public sealed partial class SessionRouter
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
    private IConnectionDocument? _watchedDocument;
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
        host.WorkspaceClosed += (_, ws) => OnWorkspaceClosed(ws);
        host.RegisterCloseGuard(GuardAsync); // 저장 확인·전용 세션 확인 다음 차례(작업 공간만 쓰던 세션의 미커밋)
        host.EmptyWorkspaceCloseGuard = ConfirmWorkspaceCloseAsync; // 탭이 없는 작업 공간을 닫을 때도 같은 확인
        state.ActiveWorkspace = () => ActiveWorkspace; // 실행을 시작한 작업 공간(함께 쓰는 세션의 안내 막대, TSK-14-03)
        state.ScopeOf = ScopeTextOf; // 상태 표시줄 「워크스페이스 세션」·「공유 세션 · …」
        state.OffersNewSession = () => SessionPerWorkspace(); // 설정을 끄면 함께 쓰는 세션이 바쁠 때 WP-14 전처럼(실행 꺼짐·취소 켜짐)
        state.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ExecutionState.ShownSessionText))
            {
                ClearFinishedNotices(); // 함께 쓰는 세션의 실행이 끝나면 안내 막대를 닫는다
            }
        };
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
            var settings = container.GetService<Shell.Settings.SettingsService>();
            var router = new SessionRouter(connections, SqlEditorHost.Current, ExecutionState.For(bus), dedicated, bus)
            {
                ConfirmSwitch = (session, ct) => ExecutionCommands.Resolve(container).ConfirmSwitchAsync(session, ct),
                ConfirmUncommitted = (session, ct) => ExecutionCommands.Resolve(container).ConfirmConnectionUncommittedAsync(session, ct),
                SessionPerWorkspace = () => settings?.Current.Workspace.SessionPerWorkspace ?? true,
            };
            if (container.GetService<Shell.Docking.DockWorkspace>() is { } docking)
            {
                router.ActiveDocument = () => docking.ActiveDocument?.Panel; // 문서를 바꾸면 전용 세션 등록부(FocusSync)가 다시 맞춘다

                // 문서 탭 × 로 닫을 때도 작업 공간만 쓰던 세션의 미커밋을 묻는다(전용 세션 확인이 이미 가드를 돌리면 그것으로 충분하다).
                var previous = docking.CloseConfirm;
                docking.CloseConfirm = document => previous?.Invoke(document) ?? router.CloseConfirmFor(document);
            }

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
    /// 접속 서비스가 없는 컨테이너(시험)면 세션 관리자의 마지막 작업 세션. 등록된 서비스만 본다(아직 접속 서비스가 없으면 접속도 없다 —
    /// 모르는 형을 찾느라 지연 어셈블리를 훑어 시작 때 Oracle 을 깨우지 않게, TSK-12-05).
    /// </summary>
    public static ISession? CurrentOf(IServiceProvider? services)
    {
        if (services?.GetRegisteredOrDefault<IConnectionService>() is { } connections)
        {
            return connections.ActiveSession;
        }

        return services?.GetRegisteredOrDefault<ISessionManager>()?.Sessions
            .LastOrDefault(s => s.Purpose == SessionPurpose.Work && s.State != SessionState.Closed);
    }

    /// <summary>
    /// 활성 문서의 접속(<see cref="ShownSession"/>: 활성 SQL 탭의 세션, 전용 세션 탭이면 그 세션, 접속 안 됨 탭이면 null).
    /// 라우터가 아직 없는 컨테이너면 <see cref="CurrentOf"/>. 활성 탭에서 도는 명령(@파일 실행·통계)이 쓴다.
    /// </summary>
    public static ISession? ShownOf(IServiceProvider? services) => Find(services) is { } router ? router.ShownSession : CurrentOf(services);

    /// <summary>
    /// 탭을 따르지 않는 도구(열 순서 바꾸기·무효 객체 컴파일·휴지통·PL/SQL 실행 창)가 DDL·블록을 보낼 세션(TSK-14-08). 고른 세션(<paramref name="picked"/>:
    /// 스키마 브라우저의 세션, 없으면 <see cref="CurrentOf"/>)과 같은 접속을 활성 SQL 작업 공간이 쓰고 있으면 그 작업 공간의 세션(<see cref="ShownSession"/>,
    /// 같은 DB 에 더 연 세션일 수 있다) — 지금 접속(프로필의 첫 세션)은 다른 작업 공간의 트랜잭션이라 DDL 의 암묵 커밋이 남의 변경을 확정할 수 있다.
    /// SQL 작업 공간이 활성이 아니거나 다른 접속이면 고른 세션 그대로.
    /// </summary>
    public static ISession? ForTool(IServiceProvider? services, ISession? picked = null)
    {
        var basis = picked ?? CurrentOf(services);
        if (basis is null || Find(services) is not { ActiveWorkspace: not null } router)
        {
            return basis;
        }

        return router.ShownSession is { State: not (SessionState.Closed or SessionState.Broken) } shown && shown.Profile.Id == basis.Profile.Id
            ? shown
            : basis;
    }

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

    /// <summary>
    /// 쓰기·커밋하는 기능(데이터 가져오기)이 쓸 탭의 세션(TSK-14-02). 라우터가 있으면 실행처럼 먼저 작업 공간의 세션을 고르고(<see cref="PrepareAsync"/>,
    /// 첫 실행 전인 자기 세션 작업 공간이면 새 세션을 연다) 못 고르면 null 이다(다른 작업 공간이 쓰는 접속의 세션으로 대신하지 않는다).
    /// 라우터가 아직 없는 컨테이너면 <see cref="ForTab"/>.
    /// </summary>
    public static Task<ISession?> ForWriteAsync(IServiceProvider? services, SqlEditorViewModel? tab, CancellationToken ct) =>
        Find(services) is { } router ? router.ForWriteAsync(tab, ct) : Task.FromResult(ForTab(services, tab));

    /// <summary><see cref="ForWriteAsync(IServiceProvider?, SqlEditorViewModel?, CancellationToken)"/> 의 라우터 쪽.</summary>
    public async Task<ISession?> ForWriteAsync(SqlEditorViewModel? tab, CancellationToken ct = default)
    {
        if (tab is not null && !await PrepareAsync(tab, ct).ConfigureAwait(true))
        {
            return null;
        }

        return SessionFor(tab);
    }

    /// <summary>프로필의 열린 작업 세션(없으면 null). 프로필이 null 이면 <see cref="CurrentOf"/>. PL/SQL 편집기처럼 SQL 탭이 아닌 문서가 쓴다.</summary>
    public static ISession? ForProfile(IServiceProvider? services, Guid? profile)
    {
        if (profile is not { } profileId)
        {
            return CurrentOf(services);
        }

        return services?.GetRegisteredOrDefault<IConnectionService>() is { } connections
            ? connections.SessionOf(profileId)
            : services?.GetRegisteredOrDefault<ISessionManager>()?.Sessions
                .LastOrDefault(s => s.Purpose == SessionPurpose.Work && s.State != SessionState.Closed && s.Profile.Id == profileId);
    }

    /// <summary>열린 접속(연 순서, 툴바 콤보 번호 = 위치 + 1). 같은 DB 에 더 연 세션은 <see cref="SessionsOf"/>·<see cref="AllSessions"/>.</summary>
    public IReadOnlyList<ISession> Sessions => _connections.Sessions;

    /// <summary>
    /// 접속 이름 「번호 - 사용자@이름」(툴바 콤보와 같다). 같은 DB 에 더 연 세션이면 뒤에 「 (2)」 처럼 그 접속 안의 순번을 붙인다(TSK-14-01).
    /// 열린 접속이 아니면 번호 없이.
    /// </summary>
    public string LabelOf(ISession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var name = $"{session.Profile.User}@{session.Profile.Name}";
        var index = _connections.SessionOf(session.Profile.Id) is { } primary ? _connections.Sessions.ToList().IndexOf(primary) : -1;
        var nth = _connections.SessionsOf(session.Profile.Id).ToList().IndexOf(session);
        if (index < 0 || nth < 0)
        {
            return name;
        }

        var label = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{index + 1} - {name}");
        return nth == 0 ? label : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{label} ({nth + 1})");
    }

    /// <summary>열린 접속이 끊겼는지.</summary>
    public bool IsBroken(ISession session) => _connections.IsSessionBroken(session.Id) || session.State == SessionState.Broken;

    /// <summary>지금 접속. 탭을 따르지 않는 도구 창(스키마 브라우저·세션 모니터 등)이 쓴다.</summary>
    public ISession? Current => _connections.ActiveSession;

    /// <summary>
    /// 탭이 쓸 세션: 전용 세션 → 작업 공간이 그 접속에서 쓰는 세션(TSK-14-01) → 탭에 묶인 접속 → (묶이지 않았으면) 지금 접속.
    /// 묶인 접속이 열려 있지 않거나 작업 공간이 쓰던 세션이 닫혔으면 null. 탭이 null 이면 지금 접속.
    /// 자기 세션 작업 공간이 아직 실행하지 않았으면 접속의 세션이다(첫 실행 때 <see cref="PrepareAsync"/> 가 고른다).
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

        if (tab.ConnectionProfileId is not { } profileId)
        {
            return Current;
        }

        if (_host.WorkspaceOf(tab) is { } ws)
        {
            if (BoundSession(ws, profileId) is { } bound)
            {
                return bound;
            }

            if (IsLost(ws, profileId))
            {
                return null;
            }
        }

        return _connections.SessionOf(profileId);
    }

    /// <summary>
    /// 탭에 묶인 접속이 열려 있지 않거나 작업 공간이 쓰던 세션이 닫혔으면 그 프로필 ID(「접속 안 됨(이름)」). 열려 있거나 묶이지 않았거나 전용 세션 탭이면 null.
    /// </summary>
    public Guid? MissingProfileOf(SqlEditorViewModel? tab) =>
        tab?.ConnectionProfileId is { } profileId && _dedicated?.SessionOf(tab) is null
        && (_connections.SessionOf(profileId) is null || (_host.WorkspaceOf(tab) is { } ws && IsLost(ws, profileId)))
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
        if (_connections.SessionOf(profileId) is { } live && ClearLost(_host.Active, profileId))
        {
            return live; // 작업 공간의 세션만 닫혔고 접속은 열려 있다: 다음 실행이 세션을 다시 고른다(자기 세션이면 새로 연다)
        }

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

    /// <summary>활성 문서의 내용(조립이 도킹의 활성 문서를 넣는다). 없으면 SQL 작업 공간만 안다.</summary>
    public Func<object?>? ActiveDocument
    {
        get;
        set
        {
            field = value;
            WatchDocument(ConnectionDocument);
        }
    }

    /// <summary>
    /// 접속 콤보로 SQL 탭의 접속을 바꾸기 전, 이전 접속의 미커밋 확인(true = 바꿔도 된다). 조립이 <see cref="ExecutionController.ConfirmSwitchAsync"/> 를 넣는다.
    /// 없으면 묻지 않고 거절한다(<see cref="SessionSelect.Held"/>).
    /// </summary>
    public Func<ISession, CancellationToken, Task<bool>>? ConfirmSwitch { get; set; }

    /// <summary>활성 문서가 자기 접속을 가진 문서(PL/SQL 편집기·작업 공간)면 그것. SQL 작업 공간이 활성이거나 그런 문서가 아니면 null.</summary>
    public IConnectionDocument? ConnectionDocument => SqlDocumentActive ? null : ActiveDocument?.Invoke() as IConnectionDocument;

    /// <summary>
    /// 활성 문서의 접속(툴바 콤보·상태 표시줄·창 테두리): SQL 작업 공간이 활성이면 활성 탭의 세션, 자기 접속을 가진 문서(PL/SQL)면 그 세션,
    /// 아니면(세션이 아직 없는 문서 포함) 지금 접속.
    /// </summary>
    public ISession? ShownSession => SqlDocumentActive && _host.Active is { } tab ? SessionFor(tab) : ConnectionDocument?.Session ?? Current;

    /// <summary>활성 SQL 탭의 묶인 접속이 열려 있지 않으면 그 프로필 ID(<see cref="ShownSession"/> 은 null).</summary>
    public Guid? ShownMissing => SqlDocumentActive ? MissingProfileOf(_host.Active) : null;

    /// <summary>
    /// 접속 콤보에서 고른 접속을 활성 SQL 탭에 묶고 지금 접속으로 바꾼다. 전용 세션 탭은 자기 세션을 쓰므로, 실행 중·미커밋 탭은 그 트랜잭션을
    /// 놓치지 않게 바꾸지 않는다. SQL 작업 공간이 활성이 아니면 지금 접속만 바꾼다.
    /// </summary>
    public SessionSelect Select(ISession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!_connections.IsOpen(session))
        {
            return SessionSelect.Closed; // 고르는 사이에 닫혔다: 탭을 닫힌 접속에 묶지 않는다
        }

        if (SqlDocumentActive && _host.Active is { } tab)
        {
            if (_dedicated?.SessionOf(tab) is not null)
            {
                return SessionSelect.Dedicated;
            }

            if (IsHeldFor(tab, session) && !ReferenceEquals(SessionFor(tab), session))
            {
                return SessionSelect.Held;
            }

            var changed = !ReferenceEquals(SessionFor(tab), session);
            tab.ConnectionProfileId = session.Profile.Id;
            if (changed)
            {
                BindChoice(tab, session);
            }
        }

        if (!_connections.Switch(session))
        {
            return SessionSelect.Closed;
        }

        Refocus();
        RaiseChanged();
        return SessionSelect.Selected;
    }

    /// <summary>
    /// 툴바 접속 콤보에서 고른 접속을 활성 문서에 묶는다(TSK-12-04). <see cref="Select"/> 와 같되, SQL 탭의 이전 접속에 미커밋이 있으면
    /// 거절하지 않고 묻는다(<see cref="ConfirmSwitch"/>: 커밋 / 롤백 / 그대로 두고 바꾸기 / 취소). 실행 중이면 <see cref="SessionSelect.Busy"/>.
    /// PL/SQL 편집기처럼 자기 접속을 가진 문서가 활성이면 그 문서의 접속을 바꾼다(다음 컴파일·실행부터 고른 DB). 고른 대로 되면 지금 접속도 바뀐다.
    /// </summary>
    public async Task<SessionSelect> SelectAsync(ISession session, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!_connections.IsOpen(session))
        {
            return SessionSelect.Closed;
        }

        if (SqlDocumentActive && _host.Active is { } tab)
        {
            if (_dedicated?.SessionOf(tab) is not null)
            {
                return SessionSelect.Dedicated;
            }

            if (SessionFor(tab) is { } old && !ReferenceEquals(old, session) && IsHeldFor(tab, session))
            {
                if (_state.IsBusy(old.Id))
                {
                    return SessionSelect.Busy;
                }

                if (ConfirmSwitch is not { } confirm)
                {
                    return SessionSelect.Held;
                }

                if (!await confirm(old, ct).ConfigureAwait(true))
                {
                    return SessionSelect.Cancelled;
                }

                if (!_connections.IsOpen(session))
                {
                    return SessionSelect.Closed; // 묻는 사이에 닫혔다
                }
            }

            var changed = !ReferenceEquals(SessionFor(tab), session);
            tab.ConnectionProfileId = session.Profile.Id; // 묻는 사이 다른 탭으로 갔어도 고를 때의 탭을 바꾼다
            if (changed)
            {
                BindChoice(tab, session);
            }
        }
        else if (ConnectionDocument is { } doc && !ReferenceEquals(doc.Session, session))
        {
            if (doc.IsSessionBusy || !doc.UseSession(session))
            {
                return SessionSelect.Busy;
            }
        }

        if (!_connections.Switch(session))
        {
            return SessionSelect.Closed;
        }

        SwitchToActiveTab(); // 묻는 사이 다른 탭으로 갔으면 지금 접속은 보이는 탭의 접속으로 둔다
        Refocus();
        RaiseChanged();
        return SessionSelect.Selected;
    }

    /// <summary><see cref="Select"/>·<see cref="SelectAsync"/> 의 거절 이유 글(메시지 창). 고른 대로 됐거나 사용자가 취소했으면 null.</summary>
    public static string? RefusalText(SessionSelect result) => result switch
    {
        SessionSelect.Dedicated => "전용 세션 탭은 자기 세션을 쓰므로 접속을 바꾸지 않습니다. 다른 접속은 새 SQL 탭에서 고르세요.",
        SessionSelect.Held => "이 탭은 실행 중이거나 커밋하지 않은 변경이 있어 접속을 바꾸지 않습니다. 커밋·롤백하거나 실행이 끝난 뒤 고르세요.",
        SessionSelect.Busy => "실행(컴파일) 중이라 접속을 바꾸지 않습니다. 끝나거나 취소한 뒤 고르세요.",
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
        else if (IsPendingOwn(tab) && SessionFor(tab) is { } connection)
        {
            _state.FocusPending(connection.Profile); // 첫 실행 때 자기 세션을 연다: 같은 DB 의 다른 세션 실행에 막히지 않는다
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
        _state.UsePrimary(Current?.Id); // 같은 DB 에 세션을 더 열면 실행 상태가 그 세션을 지금 접속으로 잡는다: 지금 접속으로 되돌린다
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
        if (SqlDocumentActive && _host.Active is { } active)
        {
            FillProfile(active);
        }

        Watch(_host.Active);
        WatchDocument(ConnectionDocument);
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

    /// <summary>
    /// 작업 공간 안 탭의 접속이 비어 있으면(접속 전에 만든 탭) 작업 공간이 쓰는 프로필로 채운다(TSK-14-07): 묶인 세션의 프로필(지금 접속의 것을 먼저),
    /// 없으면 같은 작업 공간의 다른 탭의 접속, 그것도 없으면 지금 접속. 비어 있으면 작업 공간 묶기를 건너뛰어 같은 작업 공간 안에서 다른 세션으로 실행했다.
    /// </summary>
    private void FillProfile(SqlEditorViewModel tab)
    {
        if (tab.ConnectionProfileId is not null || _dedicated?.SessionOf(tab) is not null || _host.WorkspaceOf(tab) is not { } ws)
        {
            return;
        }

        var current = Current?.Profile.Id;
        var bound = _bound.TryGetValue(ws, out var map) ? map.Where(kv => _connections.IsOpen(kv.Value)).Select(kv => kv.Key).ToList() : [];
        var sibling = ws.Tabs.Where(t => !ReferenceEquals(t, tab) && _dedicated?.SessionOf(t) is null).Select(t => t.ConnectionProfileId).OfType<Guid>().FirstOrDefault();
        Guid? profile = current is { } c && bound.Contains(c) ? c
            : bound.Count > 0 ? bound[0]
            : sibling != Guid.Empty ? sibling
            : current;
        if (profile is not null)
        {
            tab.ConnectionProfileId = profile;
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

    /// <summary>
    /// SQL 작업 공간이 활성이고 활성 탭이 열린 접속에 묶였으면 그 접속을, 자기 접속을 가진 문서(PL/SQL)가 활성이면 그 문서의 열린 접속을
    /// 지금 접속으로 한다(스키마 브라우저 등이 보이는 문서와 다른 접속을 보이지 않게). 바꿨으면 true.
    /// </summary>
    private bool SwitchToActiveTab()
    {
        var bound = SqlDocumentActive
            ? _host.Active is { } tab && _dedicated?.SessionOf(tab) is null && tab.ConnectionProfileId is { } profileId ? _connections.SessionOf(profileId) : null
            : ConnectionDocument?.Session is { } own && ReferenceEquals(_connections.SessionOf(own.Profile.Id), own) ? own : null;
        return bound is not null && !ReferenceEquals(bound, Current) && _connections.Switch(bound); // ActiveSessionChanged 가 상태·표시를 맞춘다
    }

    /// <summary>활성 문서(PL/SQL)의 접속이 바뀌면(콤보·복원 탭 붙이기·보이는 탭 바꿈) 표시와 지금 접속을 맞춘다.</summary>
    private void WatchDocument(IConnectionDocument? doc)
    {
        if (ReferenceEquals(_watchedDocument, doc))
        {
            return;
        }

        if (_watchedDocument is not null)
        {
            _watchedDocument.SessionChanged -= OnDocumentSessionChanged;
        }

        _watchedDocument = doc;
        if (doc is not null)
        {
            doc.SessionChanged += OnDocumentSessionChanged;
        }
    }

    private void OnDocumentSessionChanged(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, ConnectionDocument))
        {
            return;
        }

        SwitchToActiveTab();
        Refocus();
        RaiseChanged();
    }

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

    private void RaiseChanged()
    {
        _state.RefreshShown(); // 세션은 그대로여도 함께 쓰는 작업 공간 수·활성 작업 공간이 바뀌었을 수 있다(상태 표시줄 글·sessionBusyElsewhere)
        Changed?.Invoke(this, EventArgs.Empty);
    }

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
