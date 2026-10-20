using Avalonia.Threading;
using Oranda.App.Features.ConnectionManager;
using Oranda.App.Features.DedicatedSession;
using Oranda.App.Features.Execution;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Tests.Features.Execution;
using Oranda.App.Tests.Features.SqlEditor;
using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Sessions;
using Oranda.Core.Workspaces;

namespace Oranda.App.Tests.Features.ConnectionManager;

// 소유: TSK-03-08. 동시 접속 여러 개(CONN-06): 접속 서비스의 세션 목록과 탭 → 세션을 고르는 SessionRouter.
public sealed class MultiConnectionTests
{
    private static ConnectionProfile Profile(string name, string user = "scott") => new(name, user, "localhost:1521/FREEPDB1");

    private static async Task<ConnectionProfile> SaveAsync(ServiceFixture fx, string name) =>
        await fx.Service.SaveProfileAsync(Profile(name), "pw", true, CancellationToken.None);

    private static async Task<ISession> ConnectAsync(ServiceFixture fx, ConnectionProfile profile)
    {
        var outcome = await fx.Service.ConnectAsync(profile, null, CancellationToken.None);
        Assert.True(outcome.IsConnected, outcome.Message);
        fx.Events.Publish(new SessionOpened(outcome.Session!.Id, outcome.Session.Profile, outcome.Session.Server, SessionPurpose.Work));
        Pump();
        return outcome.Session;
    }

    // ---------- 접속 서비스 ----------

    [Fact]
    public Task A_second_connection_keeps_the_first_open_and_becomes_the_current_one() => Ui.Run(async () =>
    {
        using var fx = new ServiceFixture();
        var a = await ConnectAsync(fx, await SaveAsync(fx, "a"));
        var b = await ConnectAsync(fx, await SaveAsync(fx, "b"));

        Assert.Equal([a, b], fx.Service.Sessions);
        Assert.Same(b, fx.Service.ActiveSession);
        Assert.NotEqual(SessionState.Closed, a.State);
        Assert.Same(a, fx.Service.SessionOf(a.Profile.Id));
    });

    [Fact]
    public Task Connecting_an_open_profile_again_switches_to_it_without_opening_a_new_session() => Ui.Run(async () =>
    {
        using var fx = new ServiceFixture();
        var pa = await SaveAsync(fx, "a");
        var a = await ConnectAsync(fx, pa);
        await ConnectAsync(fx, await SaveAsync(fx, "b"));
        ISession? raised = null;
        fx.Service.Connected += (_, s) => raised = s;

        var again = await fx.Service.ConnectAsync(pa, null, CancellationToken.None);

        Assert.True(again.IsConnected);
        Assert.Same(a, again.Session);
        Assert.Same(a, fx.Service.ActiveSession);
        Assert.Same(a, raised);
        Assert.Equal(2, fx.Sessions.Calls.Count);
        Assert.Equal(2, fx.Service.Sessions.Count);
    });

    [Fact]
    public Task Reconnecting_a_broken_connection_reuses_its_slot_and_closes_the_old_session() => Ui.Run(async () =>
    {
        using var fx = new ServiceFixture();
        var pa = await SaveAsync(fx, "a");
        var a = await ConnectAsync(fx, pa);
        var b = await ConnectAsync(fx, await SaveAsync(fx, "b"));
        fx.Events.Publish(new SessionBroken(a.Id, false, null));
        Assert.True(fx.Service.IsSessionBroken(a.Id));
        Assert.False(fx.Service.IsBroken); // 지금 접속(b)은 살아 있다

        var fresh = await ConnectAsync(fx, pa);

        Assert.NotSame(a, fresh);
        Assert.Equal([fresh, b], fx.Service.Sessions);
        Assert.Same(fresh, fx.Service.ActiveSession);
        Assert.Equal(SessionState.Closed, a.State);
    });

    [Fact]
    public Task Disconnecting_the_current_connection_makes_the_last_opened_one_current() => Ui.Run(async () =>
    {
        using var fx = new ServiceFixture();
        var a = await ConnectAsync(fx, await SaveAsync(fx, "a"));
        var b = await ConnectAsync(fx, await SaveAsync(fx, "b"));
        var c = await ConnectAsync(fx, await SaveAsync(fx, "c"));
        fx.Service.Switch(a);

        await fx.Service.DisconnectAsync(b, CancellationToken.None); // 지금 접속이 아닌 것
        Assert.Same(a, fx.Service.ActiveSession);
        Assert.Equal([a, c], fx.Service.Sessions);

        await fx.Service.DisconnectAsync(CancellationToken.None); // 지금 접속(a)
        Assert.Same(c, fx.Service.ActiveSession);
        Assert.Equal(SessionState.Closed, a.State);
        Assert.True(fx.Keys.Get("hasSession"));
    });

    [Fact]
    public Task Opening_more_than_the_limit_fails_without_closing_anything() => Ui.Run(async () =>
    {
        using var fx = new ServiceFixture();
        for (var i = 0; i < ConnectionService.MaxSessions; i++)
        {
            await ConnectAsync(fx, await SaveAsync(fx, "p" + i));
        }

        var over = await fx.Service.ConnectAsync(await SaveAsync(fx, "over"), null, CancellationToken.None);

        Assert.Equal(ConnectStatus.Failed, over.Status);
        Assert.Equal(ConnectionService.MaxSessions, fx.Service.Sessions.Count);
        Assert.Equal(ConnectionService.MaxSessions, fx.Sessions.Calls.Count);
    });

    [Fact]
    public Task Switch_rejects_a_session_that_is_not_open() => Ui.Run(async () =>
    {
        using var fx = new ServiceFixture();
        await ConnectAsync(fx, await SaveAsync(fx, "a"));

        Assert.False(fx.Service.Switch(new FakeSession(Profile("x"))));
    });

    // ---------- SessionRouter ----------

    private sealed class RouterRig : IDisposable
    {
        public RouterRig()
        {
            State = new ExecutionState(Fx.Events);
            Registry = new DedicatedSessionRegistry(Host, State, Fx.Events);
            Router = new SessionRouter(Fx.Service, Host, State, Registry);
        }

        public ServiceFixture Fx { get; } = new();

        public SqlEditorHost Host { get; } = new();

        public ExecutionState State { get; }

        public DedicatedSessionRegistry Registry { get; }

        public SessionRouter Router { get; }

        public void Dispose() => Fx.Dispose();
    }

    [Fact]
    public Task A_new_tab_takes_the_current_connection_and_keeps_it_after_switching() => Ui.Run(async () =>
    {
        using var rig = new RouterRig();
        var a = await ConnectAsync(rig.Fx, await SaveAsync(rig.Fx, "a"));
        var tabA = rig.Host.NewDocument();
        Assert.Equal(a.Profile.Id, tabA.ConnectionProfileId);

        var b = await ConnectAsync(rig.Fx, await SaveAsync(rig.Fx, "b")); // 활성 탭(tabA)이 새 접속을 따른다
        Assert.Same(b, rig.Router.SessionFor(tabA));
        tabA.ConnectionProfileId = a.Profile.Id;
        var tabB = rig.Host.NewDocument();
        rig.Router.Select(b);

        Assert.Same(a, rig.Router.SessionFor(tabA));
        Assert.Same(b, rig.Router.SessionFor(tabB));
    });

    [Fact]
    public Task Activating_a_tab_makes_its_connection_current() => Ui.Run(async () =>
    {
        using var rig = new RouterRig();
        var a = await ConnectAsync(rig.Fx, await SaveAsync(rig.Fx, "a"));
        var tabA = rig.Host.NewDocument();
        var b = await ConnectAsync(rig.Fx, await SaveAsync(rig.Fx, "b"));
        var tabB = rig.Host.NewDocument();
        Assert.Equal(b.Profile.Id, tabB.ConnectionProfileId);
        tabA.ConnectionProfileId = a.Profile.Id;

        rig.Host.Active = tabA;
        Pump();
        Assert.Same(a, rig.Fx.Service.ActiveSession);
        Assert.Equal(a.Id, rig.State.SessionId);
        Assert.Same(a, rig.Router.ShownSession);

        rig.Host.Active = tabB;
        Pump();
        Assert.Same(b, rig.Fx.Service.ActiveSession);
        Assert.Equal(b.Id, rig.State.SessionId);
    });

    [Fact]
    public Task A_new_connection_rebinds_only_the_active_tab() => Ui.Run(async () =>
    {
        using var rig = new RouterRig();
        var a = await ConnectAsync(rig.Fx, await SaveAsync(rig.Fx, "a"));
        var first = rig.Host.NewDocument();
        var second = rig.Host.NewDocument();

        var b = await ConnectAsync(rig.Fx, await SaveAsync(rig.Fx, "b"));

        Assert.Equal(b.Profile.Id, second.ConnectionProfileId);
        Assert.Equal(a.Profile.Id, first.ConnectionProfileId);
    });

    [Fact]
    public Task A_tab_whose_connection_was_closed_has_no_session_instead_of_the_current_one() => Ui.Run(async () =>
    {
        using var rig = new RouterRig();
        var pa = await SaveAsync(rig.Fx, "alpha");
        var a = await ConnectAsync(rig.Fx, pa);
        var tabA = rig.Host.NewDocument();
        await ConnectAsync(rig.Fx, await SaveAsync(rig.Fx, "b"));
        tabA.ConnectionProfileId = a.Profile.Id;
        rig.Host.Active = tabA;
        Pump();

        await rig.Fx.Service.DisconnectAsync(a, CancellationToken.None);
        Pump();

        Assert.Null(rig.Router.SessionFor(tabA));
        Assert.Equal(a.Profile.Id, rig.Router.MissingProfileOf(tabA));
        Assert.Equal(a.Profile.Id, rig.Router.ShownMissing);
        Assert.True(rig.State.FocusedNothing);
        Assert.Equal(SessionRunState.NoSession, rig.State.ShownRunState);
        Assert.NotNull(rig.Router.Current); // 다른 접속은 남는다

        // 다시 접속하면 같은 탭이 그 접속을 다시 쓴다.
        var again = await ConnectAsync(rig.Fx, pa);
        Assert.Same(again, rig.Router.SessionFor(tabA));
        Assert.Null(rig.Router.MissingProfileOf(tabA));
        Assert.False(rig.State.FocusedNothing);
    });

    [Fact]
    public Task Profile_name_of_a_closed_connection_is_read_from_the_saved_profiles() => Ui.Run(async () =>
    {
        using var rig = new RouterRig();
        var pa = await SaveAsync(rig.Fx, "alpha");
        var changed = 0;
        rig.Router.Changed += (_, _) => changed++;

        Assert.Null(rig.Router.ProfileName(pa.Id)); // 처음에는 읽는 중
        await WaitAsync(() => rig.Router.ProfileName(pa.Id) is not null);

        Assert.Equal("alpha", rig.Router.ProfileName(pa.Id));
        Assert.True(changed > 0);
    });

    [Fact]
    public Task Selecting_a_connection_for_a_dedicated_session_tab_is_refused() => Ui.Run(async () =>
    {
        using var rig = new RouterRig();
        var a = await ConnectAsync(rig.Fx, await SaveAsync(rig.Fx, "a"));
        var b = await ConnectAsync(rig.Fx, await SaveAsync(rig.Fx, "b"));
        var tab = rig.Host.NewDocument();
        var own = new FakeSession(a.Profile);
        rig.Registry.Attach(tab, own);

        Assert.Equal(SessionSelect.Dedicated, rig.Router.Select(a));
        Assert.Same(own, rig.Router.SessionFor(tab));
        Assert.Same(b, rig.Fx.Service.ActiveSession);
        Assert.Equal(own.Id, rig.State.FocusedDedicated);
    });

    [Fact]
    public Task An_uncommitted_tab_keeps_its_connection_and_it_stays_current_when_another_connects() => Ui.Run(async () =>
    {
        using var rig = new RouterRig();
        var a = await ConnectAsync(rig.Fx, await SaveAsync(rig.Fx, "a"));
        var tab = rig.Host.NewDocument();
        rig.State.ApplyTransaction(a.Id, true);

        var b = await ConnectAsync(rig.Fx, await SaveAsync(rig.Fx, "b"));

        Assert.Same(a, rig.Router.SessionFor(tab));
        Assert.Same(a, rig.Fx.Service.ActiveSession); // 보이는 접속과 지금 접속(스키마 브라우저)이 갈리지 않는다
        Assert.Equal(SessionSelect.Held, rig.Router.Select(b));
        Assert.Same(a, rig.Router.SessionFor(tab));
        Assert.NotNull(SessionRouter.RefusalText(SessionSelect.Held));
    });

    [Fact]
    public Task A_tab_sharing_an_uncommitted_connection_with_another_tab_follows_a_new_connection() => Ui.Run(async () =>
    {
        using var rig = new RouterRig();
        var a = await ConnectAsync(rig.Fx, await SaveAsync(rig.Fx, "a"));
        var first = rig.Host.NewDocument();
        rig.State.ApplyTransaction(a.Id, true);
        var second = rig.Host.NewDocument(); // 같은 접속(a), 미커밋은 first 가 본다

        var b = await ConnectAsync(rig.Fx, await SaveAsync(rig.Fx, "b"));

        Assert.Same(b, rig.Router.SessionFor(second));
        Assert.Same(a, rig.Router.SessionFor(first));
        Assert.Same(b, rig.Fx.Service.ActiveSession);
    });

    [Fact]
    public Task Rebinding_the_active_tab_makes_its_connection_current() => Ui.Run(async () =>
    {
        using var rig = new RouterRig();
        var a = await ConnectAsync(rig.Fx, await SaveAsync(rig.Fx, "a"));
        var b = await ConnectAsync(rig.Fx, await SaveAsync(rig.Fx, "b"));
        var tab = rig.Host.NewDocument();

        tab.ConnectionProfileId = a.Profile.Id;

        Assert.Same(a, rig.Fx.Service.ActiveSession);
        Assert.Same(a, rig.Router.ShownSession);
        Assert.NotSame(b, rig.Router.SessionFor(tab));
    });

    [Fact]
    public Task Selecting_a_closed_connection_leaves_the_tab_alone() => Ui.Run(async () =>
    {
        using var rig = new RouterRig();
        var a = await ConnectAsync(rig.Fx, await SaveAsync(rig.Fx, "a"));
        var tab = rig.Host.NewDocument();
        var gone = new FakeSession(Profile("gone"));

        Assert.Equal(SessionSelect.Closed, rig.Router.Select(gone));
        Assert.Equal(a.Profile.Id, tab.ConnectionProfileId);
    });

    [Fact]
    public void Execution_does_not_fall_back_to_the_current_connection_for_a_tab_without_its_connection()
    {
        var current = new FakeSession(Profile("cur"));
        var tab = new SqlEditorViewModel(Guid.NewGuid(), "select 1 from dual") { ConnectionProfileId = Guid.NewGuid() };
        var unbound = new SqlEditorViewModel(Guid.NewGuid(), "select 1 from dual");
        var state = new ExecutionState();
        var controller = new ExecutionController(new ExecutionDeps
        {
            State = state,
            Log = new ExecutionLog(null, state),
            Dialogs = new FakeDialogs(),
            Session = () => current,
            TabSession = _ => null,
            MissingConnection = t => t.ConnectionProfileId,
            Execution = () => throw new NotSupportedException(),
            Language = () => throw new NotSupportedException(),
        });

        Assert.Null(controller.SessionFor(tab));
        Assert.Same(current, new ExecutionController(new ExecutionDeps
        {
            State = state,
            Log = new ExecutionLog(null, state),
            Dialogs = new FakeDialogs(),
            Session = () => current,
            Execution = () => throw new NotSupportedException(),
            Language = () => throw new NotSupportedException(),
        }).SessionFor(unbound));
    }

    private static ExecutionController MissingRig(FakeDialogs dialogs, ISession? current, Func<SqlEditorViewModel, Guid?> missing,
        Func<Guid, CancellationToken, Task<ISession?>>? reconnect = null, ExecutionState? state = null, IReadOnlyList<ISession>? sessions = null,
        SqlEditorHost? host = null)
    {
        state ??= new ExecutionState();
        host ??= new SqlEditorHost();
        return new ExecutionController(new ExecutionDeps
        {
            Host = () => host,
            State = state,
            Log = new ExecutionLog(null, state),
            Dialogs = dialogs,
            Session = () => current,
            MissingConnection = missing,
            ProfileName = _ => Task.FromResult<string?>("closed"),
            WorkSessions = sessions is null ? null : () => sessions,
            Reconnect = reconnect ?? ((_, _) => Task.FromResult<ISession?>(null)),
            Execution = () => throw new NotSupportedException(),
            Language = () => throw new NotSupportedException(),
        });
    }

    [Fact]
    public async Task Running_a_tab_without_its_connection_asks_and_cancel_runs_nothing()
    {
        var current = new FakeSession(Profile("cur"));
        var tab = new SqlEditorViewModel(Guid.NewGuid(), "select 1 from dual") { ConnectionProfileId = Guid.NewGuid() };
        var dialogs = new FakeDialogs { MissingAnswer = MissingConnectionChoice.Cancel };
        var controller = MissingRig(dialogs, current, t => t.ConnectionProfileId);

        Assert.False(await controller.RunAsync(RunMode.Statement, tab: tab)); // 엔진을 부르면 NotSupportedException
        Assert.Equal([("closed", (string?)"scott@cur")], dialogs.MissingAsked);
        Assert.NotEqual(current.Profile.Id, tab.ConnectionProfileId);
    }

    [Fact]
    public async Task Choosing_the_current_connection_moves_the_tab_to_it()
    {
        var current = new FakeSession(Profile("cur"));
        var host = new SqlEditorHost();
        var tab = host.NewDocument("select 1 from dual");
        tab.ConnectionProfileId = Guid.NewGuid();
        var dialogs = new FakeDialogs { MissingAnswer = MissingConnectionChoice.UseCurrent };
        var controller = MissingRig(dialogs, current, t => t.ConnectionProfileId == current.Profile.Id ? null : t.ConnectionProfileId, host: host);

        Assert.True(await controller.EnsureConnectionAsync(tab));
        Assert.Equal(current.Profile.Id, tab.ConnectionProfileId);
    }

    [Fact]
    public async Task Reconnecting_opens_the_tab_connection_and_a_busy_or_failed_reconnect_does_not_run()
    {
        var closed = Guid.NewGuid();
        var reopened = new FakeSession(Profile("closed"));
        var open = false;
        var host = new SqlEditorHost();
        var tab = host.NewDocument("select 1 from dual");
        tab.ConnectionProfileId = closed;
        var dialogs = new FakeDialogs { MissingAnswer = MissingConnectionChoice.Reconnect };
        var failing = MissingRig(dialogs, null, t => open ? null : t.ConnectionProfileId, host: host);
        Assert.False(await failing.EnsureConnectionAsync(tab)); // 지금 접속이 없으면 「바꾸기」 단추는 없다
        Assert.Null(dialogs.MissingAsked[0].Current);

        var state = new ExecutionState();
        var controller = MissingRig(dialogs, null, t => open ? null : t.ConnectionProfileId, (id, _) =>
        {
            Assert.Equal(closed, id);
            open = true;
            return Task.FromResult<ISession?>(reopened);
        }, state, host: host);
        Assert.True(await controller.EnsureConnectionAsync(tab));
        Assert.Equal(closed, tab.ConnectionProfileId);
    }

    [Fact]
    public async Task App_exit_asks_about_uncommitted_changes_on_each_connection()
    {
        var a = new FakeSession(Profile("a")) { HasPendingTransaction = true };
        var clean = new FakeSession(Profile("clean"));
        var b = new FakeSession(Profile("b")) { HasPendingTransaction = true };
        var dialogs = new FakeDialogs { UncommittedAnswer = UncommittedChoice.Rollback };
        var controller = MissingRig(dialogs, b, _ => null, sessions: [a, clean, b]);

        Assert.True(await controller.ConfirmCloseAsync());
        Assert.Equal(["scott@a", "scott@b"], dialogs.ConnectionsAsked);
        Assert.False(a.HasPendingTransaction);
        Assert.False(b.HasPendingTransaction);

        a.HasPendingTransaction = b.HasPendingTransaction = true;
        dialogs.UncommittedAnswer = UncommittedChoice.Cancel;
        dialogs.ConnectionsAsked.Clear();
        Assert.False(await controller.ConfirmCloseAsync());
        Assert.Equal(["scott@a"], dialogs.ConnectionsAsked); // 하나라도 취소하면 거기서 멈춘다
        Assert.True(b.HasPendingTransaction);
    }

    [Fact]
    public async Task A_broken_connection_with_lost_changes_does_not_block_exit()
    {
        var a = new FakeSession(Profile("a")) { HasPendingTransaction = true };
        a.State = SessionState.Broken;
        var dialogs = new FakeDialogs { UncommittedAnswer = UncommittedChoice.Cancel };
        var controller = MissingRig(dialogs, a, _ => null, sessions: [a]);

        Assert.True(await controller.ConfirmCloseAsync());
        Assert.Empty(dialogs.ConnectionsAsked); // 커밋·롤백할 수 없으니 묻지 않는다
        Assert.Contains(controller.Log.Messages, m => m.Text.Contains("되돌려졌습니다", StringComparison.Ordinal));
    }

    [Fact]
    public Task Reconnecting_for_a_tab_does_not_move_the_tab_the_user_switched_to() => Ui.Run(async () =>
    {
        using var rig = new RouterRig();
        var p = await SaveAsync(rig.Fx, "p");
        var q = await ConnectAsync(rig.Fx, await SaveAsync(rig.Fx, "q"));
        var missing = rig.Host.NewDocument();
        missing.ConnectionProfileId = p.Id; // 닫힌 접속에 묶인 탭
        var other = rig.Host.NewDocument(); // q

        var session = await rig.Router.ReconnectAsync(p.Id, async () =>
        {
            rig.Host.Active = other; // 접속하는 동안 다른 탭으로 갔다
            return await ConnectAsync(rig.Fx, p);
        });

        Assert.NotNull(session);
        Assert.Equal(q.Profile.Id, other.ConnectionProfileId);
        Assert.Same(session, rig.Router.SessionFor(missing));
        Assert.Same(q, rig.Fx.Service.ActiveSession); // 보이는 탭(other)의 접속
    });

    [Fact]
    public Task Connection_labels_carry_the_combo_number() => Ui.Run(async () =>
    {
        using var rig = new RouterRig();
        var a = await ConnectAsync(rig.Fx, await SaveAsync(rig.Fx, "a"));
        var b = await ConnectAsync(rig.Fx, await SaveAsync(rig.Fx, "b"));

        Assert.Equal("1 - scott@a", rig.Router.LabelOf(a));
        Assert.Equal("2 - scott@b", rig.Router.LabelOf(b));
        Assert.Equal("scott@x", rig.Router.LabelOf(new FakeSession(Profile("x"))));
    });

    // ---------- 툴바 접속 콤보·상태 표시줄 ----------

    [Fact]
    public Task Combo_lists_open_connections_in_open_order_and_follows_the_active_tab() => Ui.Run(async () =>
    {
        using var rig = new RouterRig();
        var a = await ConnectAsync(rig.Fx, await SaveAsync(rig.Fx, "a"));
        var tabA = rig.Host.NewDocument();
        var b = await ConnectAsync(rig.Fx, await SaveAsync(rig.Fx, "b"));
        var tabB = rig.Host.NewDocument();
        tabA.ConnectionProfileId = a.Profile.Id; // 활성 탭이 아니므로 지금 접속(b)은 그대로
        var combo = new ConnectionComboViewModel(rig.Fx.Events, router: () => rig.Router);
        var status = new ConnectionStatusViewModel(rig.Fx.Events, router: () => rig.Router);

        Assert.Equal(["1 - scott@a", "2 - scott@b"], combo.Items.Select(i => i.Label));
        Assert.Same(b, combo.Selected?.Session);
        Assert.StartsWith("scott@b", status.Text, StringComparison.Ordinal);

        rig.Host.Active = tabA;
        Pump();
        Assert.Same(a, combo.Selected?.Session);
        Assert.StartsWith("scott@a", status.Text, StringComparison.Ordinal);
        Assert.Same(tabB, rig.Host.Tabs[^1]);
    });

    [Fact]
    public Task Choosing_in_the_combo_moves_the_active_tab_to_that_connection() => Ui.Run(async () =>
    {
        using var rig = new RouterRig();
        var a = await ConnectAsync(rig.Fx, await SaveAsync(rig.Fx, "a"));
        await ConnectAsync(rig.Fx, await SaveAsync(rig.Fx, "b"));
        var tab = rig.Host.NewDocument();
        var combo = new ConnectionComboViewModel(rig.Fx.Events, router: () => rig.Router);

        combo.Selected = combo.Items[0];
        Pump();

        Assert.Equal(a.Profile.Id, tab.ConnectionProfileId);
        Assert.Same(a, rig.Fx.Service.ActiveSession);
        Assert.Same(a, combo.Selected?.Session);
    });

    [Fact]
    public Task Combo_and_status_show_not_connected_with_the_profile_name_for_a_closed_connection() => Ui.Run(async () =>
    {
        using var rig = new RouterRig();
        var a = await ConnectAsync(rig.Fx, await SaveAsync(rig.Fx, "alpha"));
        var tab = rig.Host.NewDocument();
        await ConnectAsync(rig.Fx, await SaveAsync(rig.Fx, "b"));
        tab.ConnectionProfileId = a.Profile.Id;
        var combo = new ConnectionComboViewModel(rig.Fx.Events, router: () => rig.Router);
        var status = new ConnectionStatusViewModel(rig.Fx.Events, router: () => rig.Router);

        await rig.Fx.Service.DisconnectAsync(a, CancellationToken.None);
        await WaitAsync(() => combo.PlaceholderText == "접속 안 됨(alpha)");

        Assert.Null(combo.Selected);
        Assert.Equal(["1 - scott@b"], combo.Items.Select(i => i.Label));
        Assert.Equal("접속 안 됨(alpha)", status.Text);
        Assert.Null(status.Current);
    });

    [Fact]
    public Task Restoring_a_workspace_keeps_tab_connections_without_connecting_and_shows_them_not_connected() => Ui.Run(async () =>
    {
        using var rig = new RouterRig();
        var alpha = await SaveAsync(rig.Fx, "alpha"); // 저장만 하고 접속하지 않는다
        var b = await ConnectAsync(rig.Fx, await SaveAsync(rig.Fx, "b"));
        var bound = new WorkspaceTab { Id = Guid.NewGuid(), Title = "bound", Content = "select 1 from dual", ConnectionProfileId = alpha.Id };
        var free = new WorkspaceTab { Id = Guid.NewGuid(), Title = "free", Content = "select 2 from dual" };
        var combo = new ConnectionComboViewModel(rig.Fx.Events, router: () => rig.Router);

        var ws = rig.Host.Restore(new Workspace { Id = "w", Name = "w", Tabs = [bound, free], ActiveTabId = bound.Id });
        await WaitAsync(() => combo.PlaceholderText == "접속 안 됨(alpha)");

        Assert.Single(rig.Fx.Service.Sessions); // 자동 접속하지 않는다
        Assert.Single(rig.Fx.Sessions.Calls);
        var restored = ws.Tabs.Single(t => t.Title == "bound");
        Assert.Equal(alpha.Id, restored.ConnectionProfileId);
        Assert.Null(rig.Router.SessionFor(restored));
        Assert.Equal(alpha.Id, rig.Router.ShownMissing);
        Assert.True(rig.State.FocusedNothing);
        Assert.Null(combo.Selected);
        Assert.Same(b, rig.Router.SessionFor(ws.Tabs.Single(t => t.Title == "free"))); // 접속 없이 저장한 탭은 지금 접속을 따른다
    });

    [Fact]
    public Task Combo_view_shows_the_items_and_switch_session_opens_the_list() => Ui.Run(async () =>
    {
        using var rig = new RouterRig();
        await ConnectAsync(rig.Fx, await SaveAsync(rig.Fx, "a"));
        await ConnectAsync(rig.Fx, await SaveAsync(rig.Fx, "b"));
        var vm = new ConnectionComboViewModel(rig.Fx.Events, router: () => rig.Router);
        var view = new ConnectionComboView { DataContext = vm };
        var window = new Avalonia.Controls.Window { Width = 400, Height = 100, Content = view };
        window.Show();
        Pump();

        Assert.Equal(2, view.Combo.ItemCount);
        Assert.Equal(1, view.Combo.SelectedIndex);
        Assert.True(vm.OpenList());
        Pump();
        Assert.True(view.Combo.IsDropDownOpen);
        window.Close();

        var empty = new ConnectionComboViewModel(null, router: () => null);
        Assert.False(empty.OpenList()); // 열린 접속이 없으면 접속 관리자를 연다
        Assert.Equal("접속 안 됨", empty.PlaceholderText);
    });

    // ---------- 실행 상태(세션마다) ----------

    [Fact]
    public Task Each_work_session_keeps_its_own_transaction_and_run_state() => Ui.Run(() =>
    {
        var bus = new EventBus();
        var state = new ExecutionState(bus);
        var a = new FakeSession(Profile("a"));
        var b = new FakeSession(Profile("b"));
        bus.Publish(new SessionOpened(a.Id, a.Profile, a.Server, SessionPurpose.Work));
        bus.Publish(new SessionOpened(b.Id, b.Profile, b.Server, SessionPurpose.Work));
        Pump();

        bus.Publish(new TransactionStateChanged(a.Id, true));
        state.BeginRun(b.Id);
        Pump();

        Assert.Equal(b.Id, state.SessionId); // 마지막에 연 것이 지금 접속
        Assert.False(state.TxnPending);
        Assert.True(state.Busy);
        Assert.Equal([a.Id], state.PendingWorkSessions);

        state.UsePrimary(a.Id);
        Assert.True(state.TxnPending);
        Assert.False(state.Busy);
        Assert.True(state.IsRunning(b.Id));
    });

    /// <summary>UI 스레드면 밀린 UI 작업(접속 서비스 이벤트를 옮긴 것)을 처리한다.</summary>
    private static void Pump()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            Pump();
            await Task.Delay(10);
        }

        Assert.True(condition());
    }
}
