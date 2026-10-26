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

// 소유: TSK-14-01. 같은 DB 여러 세션(접속 서비스의 더 연 세션)과 작업 공간 → 세션 연결(SessionRouter.Workspaces).
public sealed class WorkspaceSessionTests
{
    private sealed class Rig : IDisposable
    {
        public Rig()
        {
            Fx.Sessions.PublishOpened = true;
            State = new ExecutionState(Fx.Events);
            Registry = new DedicatedSessionRegistry(Host, State, Fx.Events);
            Router = new SessionRouter(Fx.Service, Host, State, Registry);
        }

        public ServiceFixture Fx { get; } = new();

        public SqlEditorHost Host { get; } = new();

        public ExecutionState State { get; }

        public DedicatedSessionRegistry Registry { get; }

        public SessionRouter Router { get; }

        public IConnectionService Service => Fx.Service;

        /// <summary>비밀번호를 저장하지 않는 프로필로 접속한다(더 연 세션이 메모리의 비밀번호를 쓰는지 보려고).</summary>
        public async Task<ISession> ConnectAsync(string name = "a")
        {
            var profile = await Fx.Service.SaveProfileAsync(new ConnectionProfile(name, "scott", "localhost:1521/FREEPDB1"), null, false, default);
            var outcome = await Fx.Service.ConnectAsync(profile, "pw-" + name, default);
            Assert.True(outcome.IsConnected, outcome.Message);
            Pump();
            return outcome.Session!;
        }

        /// <summary>작업 공간 둘(A 는 기본 작업 공간의 탭, B 는 새 작업 공간의 첫 탭).</summary>
        public (SqlWorkspaceViewModel A, SqlEditorViewModel TabA, SqlWorkspaceViewModel B, SqlEditorViewModel TabB) TwoWorkspaces()
        {
            var a = Host.EnsureWorkspace();
            var tabA = Host.NewDocument();
            var b = Host.NewWorkspace();
            return (a, tabA, b, b.Tabs[0]);
        }

        public void Dispose() => Fx.Dispose();
    }

    private static void Pump()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.RunJobs();
        }
    }

    // ---------- 접속 서비스: 같은 DB 에 세션 더 열기 ----------

    [Fact]
    public Task A_second_session_on_the_same_profile_uses_the_remembered_password_and_keeps_the_current_connection() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var a = await rig.ConnectAsync();

        var outcome = await rig.Service.OpenSessionAsync(a.Profile.Id, default);
        Pump();

        Assert.True(outcome.IsConnected, outcome.Message);
        var extra = outcome.Session!;
        Assert.NotSame(a, extra);
        Assert.Equal("pw-a", rig.Fx.Sessions.Calls[^1].Password); // 다시 묻지 않고 메모리의 비밀번호로 열었다
        Assert.Empty(rig.Fx.Credentials.Items); // 저장소에는 쓰지 않았다
        Assert.Equal([a], rig.Service.Sessions); // 접속(콤보 번호)은 그대로 하나
        Assert.Equal([a, extra], rig.Service.SessionsOf(a.Profile.Id));
        Assert.Equal([a, extra], rig.Service.AllSessions);
        Assert.Same(a, rig.Service.ActiveSession);
        Assert.Equal(a.Id, rig.State.SessionId); // 실행 상태의 지금 접속도 그대로
        Assert.True(rig.Service.IsOpen(extra));
        Assert.Equal("1 - scott@a (2)", rig.Router.LabelOf(extra));
        Assert.Equal("1 - scott@a", rig.Router.LabelOf(a));
    });

    [Fact]
    public Task Opening_a_session_without_an_open_connection_or_over_the_limit_fails_with_a_reason() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        Assert.False((await rig.Service.OpenSessionAsync(Guid.NewGuid(), default)).IsConnected);
        Assert.False((await rig.Service.OpenSessionAsync(Guid.Empty, default)).IsConnected);

        var a = await rig.ConnectAsync();
        for (var i = 1; i < ConnectionService.MaxSessionsPerProfile; i++)
        {
            Assert.True((await rig.Service.OpenSessionAsync(a.Profile.Id, default)).IsConnected);
        }

        var over = await rig.Service.OpenSessionAsync(a.Profile.Id, default);
        Assert.False(over.IsConnected);
        Assert.Contains(ConnectionService.MaxSessionsPerProfile.ToString(System.Globalization.CultureInfo.InvariantCulture), over.Message, StringComparison.Ordinal);
        Assert.Equal(ConnectionService.MaxSessionsPerProfile, rig.Service.SessionsOf(a.Profile.Id).Count);
    });

    [Fact]
    public Task Disconnecting_the_connection_promotes_the_next_session_into_its_slot_and_the_last_one_forgets_the_password() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var a = await rig.ConnectAsync("a");
        var b = await rig.ConnectAsync("b");
        rig.Service.Switch(a);
        var extra = (await rig.Service.OpenSessionAsync(a.Profile.Id, default)).Session!;

        await rig.Service.DisconnectAsync(a, default);
        Pump();

        Assert.Equal([extra, b], rig.Service.Sessions); // 같은 자리(1번)에 더 연 세션이 올라왔다
        Assert.Same(extra, rig.Service.SessionOf(a.Profile.Id));
        Assert.Same(extra, rig.Service.ActiveSession);
        Assert.Equal(SessionState.Closed, a.State);

        await rig.Service.DisconnectAsync(extra, default);
        Pump();
        Assert.Equal([b], rig.Service.Sessions);
        var again = await rig.Service.OpenSessionAsync(a.Profile.Id, default);
        Assert.False(again.IsConnected); // 접속이 없으면 더 열 수 없다(비밀번호도 메모리에서 지웠다)
    });

    [Fact]
    public Task Disconnecting_a_second_session_leaves_the_connection_alone() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var a = await rig.ConnectAsync();
        var extra = (await rig.Service.OpenSessionAsync(a.Profile.Id, default)).Session!;

        await rig.Service.DisconnectAsync(extra, default);
        Pump();

        Assert.Equal([a], rig.Service.SessionsOf(a.Profile.Id));
        Assert.Same(a, rig.Service.ActiveSession);
        Assert.False(rig.Service.IsOpen(extra));
        Assert.Equal(SessionState.Closed, extra.State);
    });

    // ---------- 작업 공간 → 세션 ----------

    [Fact]
    public Task Two_workspaces_get_two_sessions_and_tabs_of_one_workspace_share_one() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var a = await rig.ConnectAsync();
        var (wsA, tabA, wsB, tabB) = rig.TwoWorkspaces();
        Assert.Equal(a.Profile.Id, tabB.ConnectionProfileId);

        // 실행 전: 아무 세션도 더 열지 않았다(미리 열지 않는다).
        Assert.Single(rig.Service.SessionsOf(a.Profile.Id));
        Assert.True(await rig.Router.PrepareAsync(tabA));
        Assert.Same(a, rig.Router.SessionFor(tabA)); // 처음 실행한 작업 공간은 접속의 세션을 쓴다(작업 공간 하나면 지금과 같다)
        Assert.Single(rig.Service.SessionsOf(a.Profile.Id));

        Assert.True(rig.Router.IsPendingOwn(tabB));
        Assert.True(await rig.Router.PrepareAsync(tabB));
        var sessionB = rig.Router.SessionFor(tabB)!;
        Assert.NotSame(a, sessionB);
        Assert.Equal([a, sessionB], rig.Service.SessionsOf(a.Profile.Id));
        Assert.False(rig.Router.IsPendingOwn(tabB));

        // 같은 작업 공간의 다른 탭은 같은 세션.
        rig.Host.Active = tabB;
        var tabB2 = rig.Host.NewDocument();
        Assert.Same(wsB, rig.Host.WorkspaceOf(tabB2));
        Assert.Same(sessionB, rig.Router.SessionFor(tabB2));
        Assert.True(await rig.Router.PrepareAsync(tabB2));
        Assert.Equal(2, rig.Service.SessionsOf(a.Profile.Id).Count); // 더 열지 않았다
        Assert.Equal([wsA], rig.Router.WorkspacesOn(a));
        Assert.Equal([wsB], rig.Router.WorkspacesOn(sessionB));
    });

    [Fact]
    public Task A_workspace_waiting_for_its_own_session_is_not_blocked_by_another_workspace_run() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var a = await rig.ConnectAsync();
        var (_, tabA, _, tabB) = rig.TwoWorkspaces();
        await rig.Router.PrepareAsync(tabA);
        rig.State.BeginRun(a.Id); // A 가 오래 도는 중

        rig.Host.Active = tabA;
        Pump();
        Assert.True(rig.State.ShownBusy);

        rig.Host.Active = tabB;
        Pump();
        Assert.True(rig.State.FocusedPending);
        Assert.False(rig.State.ShownBusy); // sessionBusy 는 세션 기준: B 는 아직 자기 세션이 없고 A 의 실행에 막히지 않는다
        Assert.False(rig.State.ShownTxnPending);

        Assert.True(await rig.Router.PrepareAsync(tabB));
        Pump();
        var sessionB = rig.Router.SessionFor(tabB)!;
        Assert.False(rig.State.IsBusy(sessionB.Id));
        Assert.Equal(sessionB.Id, rig.State.FocusedSessionId);
        Assert.False(rig.State.ShownBusy);
        rig.State.EndRun(a.Id);
    });

    [Fact]
    public Task With_the_setting_off_workspaces_share_the_connection_session_as_before() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        rig.Router.SessionPerWorkspace = () => false;
        var a = await rig.ConnectAsync();
        var (_, tabA, _, tabB) = rig.TwoWorkspaces();
        await rig.Router.PrepareAsync(tabA);
        await rig.Router.PrepareAsync(tabB);
        rig.State.BeginRun(a.Id);

        rig.Host.Active = tabB;
        Pump();

        Assert.Same(a, rig.Router.SessionFor(tabB));
        Assert.Single(rig.Service.SessionsOf(a.Profile.Id));
        Assert.False(rig.Router.IsPendingOwn(tabB));
        Assert.True(rig.State.ShownBusy); // 같은 세션을 쓰므로 지금처럼 막힌다
        rig.State.EndRun(a.Id);
    });

    [Fact]
    public Task A_workspace_choice_overrides_the_setting() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var a = await rig.ConnectAsync();
        var (wsA, tabA, wsB, tabB) = rig.TwoWorkspaces();
        wsB.SessionMode = WorkspaceSessionMode.Shared;
        await rig.Router.PrepareAsync(tabA);
        await rig.Router.PrepareAsync(tabB);
        Assert.Same(a, rig.Router.SessionFor(tabB));

        rig.Router.SessionPerWorkspace = () => false;
        var wsC = rig.Host.NewWorkspace();
        wsC.SessionMode = WorkspaceSessionMode.Own;
        await rig.Router.PrepareAsync(wsC.Tabs[0]);
        Assert.NotSame(a, rig.Router.SessionFor(wsC.Tabs[0]));
        Assert.Equal(WorkspaceSessionMode.Own, rig.Router.ModeOf(wsC));
        Assert.Equal(WorkspaceSessionMode.Shared, rig.Router.ModeOf(wsA));
    });

    [Fact]
    public Task Choosing_another_workspace_session_shares_it_and_a_new_session_moves_away() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var a = await rig.ConnectAsync();
        var (wsA, tabA, wsB, tabB) = rig.TwoWorkspaces();
        await rig.Router.PrepareAsync(tabA);
        await rig.Router.PrepareAsync(tabB);
        var sessionB = rig.Router.SessionFor(tabB)!;

        // A 가 B 의 세션을 고른다: 다 대 일(트랜잭션도 함께). A 의 이전 세션(접속)은 닫지 않는다.
        rig.Host.Active = tabA;
        Pump();
        Assert.Equal(SessionSelect.Selected, await rig.Router.SelectAsync(sessionB));
        Assert.Same(sessionB, rig.Router.SessionFor(tabA));
        Assert.Equal([wsA, wsB], rig.Router.WorkspacesOn(sessionB));
        Assert.Equal(WorkspaceSessionMode.Shared, wsA.SessionMode);
        Assert.Same(a, rig.Service.ActiveSession); // 지금 접속은 접속(1번)으로 둔다(도구 창이 쓴다)
        Assert.Equal(sessionB.Id, rig.State.FocusedSessionId);

        // 같은 세션을 쓰는 동안 B 가 실행 중이면 A 도 막힌다(세션 기준).
        rig.State.BeginRun(sessionB.Id);
        Assert.True(rig.State.ShownBusy);
        rig.State.EndRun(sessionB.Id);

        // 「새 세션」: A 만의 세션을 연다.
        var fresh = await rig.Router.NewSessionAsync();
        Assert.NotNull(fresh);
        Assert.NotSame(sessionB, fresh);
        Assert.Same(fresh, rig.Router.SessionFor(tabA));
        Assert.Equal(WorkspaceSessionMode.Own, wsA.SessionMode);
        Assert.Equal([wsB], rig.Router.WorkspacesOn(sessionB));
        Assert.Equal(3, rig.Service.SessionsOf(a.Profile.Id).Count);
    });

    [Fact]
    public Task Leaving_a_second_session_nobody_uses_closes_it_unless_it_has_uncommitted_changes() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var a = await rig.ConnectAsync();
        var (_, tabA, _, tabB) = rig.TwoWorkspaces();
        await rig.Router.PrepareAsync(tabA);
        await rig.Router.PrepareAsync(tabB);
        var sessionB = rig.Router.SessionFor(tabB)!;

        rig.Host.Active = tabB;
        Pump();
        Assert.Equal(SessionSelect.Selected, await rig.Router.SelectAsync(a)); // B 가 접속의 세션으로 옮긴다
        Pump();
        await WaitAsync(() => !rig.Service.IsOpen(sessionB));
        Assert.Equal([a], rig.Service.SessionsOf(a.Profile.Id));

        // 미커밋을 「그대로 두고 바꾸기」 로 남긴 세션은 닫지 않는다.
        var kept = await rig.Router.NewSessionAsync();
        Assert.NotNull(kept);
        rig.State.ApplyTransaction(kept!.Id, true);
        rig.Router.ConfirmSwitch = (_, _) => Task.FromResult(true);
        Assert.Equal(SessionSelect.Selected, await rig.Router.SelectAsync(a));
        Pump();
        Assert.True(rig.Service.IsOpen(kept));
    });

    [Fact]
    public Task A_closed_session_turns_its_workspaces_into_not_connected_and_reconnect_picks_a_session_again() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var a = await rig.ConnectAsync();
        var (_, tabA, wsB, tabB) = rig.TwoWorkspaces();
        await rig.Router.PrepareAsync(tabA);
        await rig.Router.PrepareAsync(tabB);
        var sessionB = rig.Router.SessionFor(tabB)!;

        await rig.Service.DisconnectAsync(sessionB, default);
        Pump();

        Assert.Equal(a.Profile.Id, rig.Router.MissingProfileOf(tabB)); // 다른 세션으로 몰래 실행하지 않는다
        Assert.Null(rig.Router.SessionFor(tabB));
        Assert.Null(rig.Router.MissingProfileOf(tabA));
        Assert.True(await rig.Router.PrepareAsync(tabB)); // 접속 안 됨은 실행 쪽(EnsureConnectionAsync)이 먼저 묻는다: 여기서는 세션을 열지 않는다
        Assert.Null(rig.Router.SessionFor(tabB));
        Assert.Equal(2, rig.Fx.Sessions.Calls.Count);

        rig.Host.Active = tabB;
        Pump();
        Assert.True(rig.State.FocusedNothing);
        var connected = 0;
        var back = await rig.Router.ReconnectAsync(a.Profile.Id, () => { connected++; return Task.FromResult<ISession?>(null); });
        Assert.Same(a, back);
        Assert.Equal(0, connected); // 접속은 열려 있다: 새로 접속하지 않고 표시만 지운다
        Assert.Null(rig.Router.MissingProfileOf(tabB));
        Assert.True(await rig.Router.PrepareAsync(tabB));
        var again = rig.Router.SessionFor(tabB)!;
        Assert.NotSame(a, again); // 자기 세션이라 새로 열었다
        Assert.NotSame(sessionB, again);
        Assert.Equal([wsB], rig.Router.WorkspacesOn(again));
    });

    [Fact]
    public Task Choosing_the_current_connection_for_a_not_connected_workspace_clears_it() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var a = await rig.ConnectAsync();
        var (_, tabA, _, tabB) = rig.TwoWorkspaces();
        await rig.Router.PrepareAsync(tabA);
        await rig.Router.PrepareAsync(tabB);
        await rig.Service.DisconnectAsync(rig.Router.SessionFor(tabB)!, default);
        Pump();
        Assert.NotNull(rig.Router.MissingProfileOf(tabB));

        rig.Router.UseConnection(tabB, a);

        Assert.Null(rig.Router.MissingProfileOf(tabB));
        Assert.True(rig.Router.IsPendingOwn(tabB));
    });

    [Fact]
    public Task Closing_a_workspace_asks_once_for_its_own_session_and_closes_it() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var a = await rig.ConnectAsync();
        var (_, tabA, wsB, tabB) = rig.TwoWorkspaces();
        await rig.Router.PrepareAsync(tabA);
        await rig.Router.PrepareAsync(tabB);
        var sessionB = rig.Router.SessionFor(tabB)!;
        rig.State.ApplyTransaction(sessionB.Id, true);
        var asked = new List<ISession>();
        var answer = false;
        rig.Router.ConfirmUncommitted = (s, _) =>
        {
            asked.Add(s);
            return Task.FromResult(answer);
        };

        Assert.False(await rig.Host.TryCloseWorkspaceAsync(wsB, default)); // 취소
        Assert.Contains(wsB, rig.Host.Workspaces);
        Assert.True(rig.Service.IsOpen(sessionB));

        answer = true;
        rig.State.ApplyTransaction(sessionB.Id, false); // 확인 창이 커밋했다
        rig.State.ApplyTransaction(sessionB.Id, true);
        Assert.True(await rig.Host.TryCloseWorkspaceAsync(wsB, default));
        await WaitAsync(() => !rig.Service.IsOpen(sessionB));
        Assert.Equal([sessionB, sessionB], asked); // 세션마다 한 번씩(닫기 두 번)
        Assert.True(rig.Service.IsOpen(a)); // 접속의 세션은 닫지 않는다
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task Reconnecting_a_broken_connection_keeps_its_workspace_on_the_new_session(bool perWorkspace) => Ui.Run(async () =>
    {
        using var rig = new Rig();
        rig.Router.SessionPerWorkspace = () => perWorkspace;
        var a = await rig.ConnectAsync();
        rig.Host.EnsureWorkspace();
        var tab = rig.Host.NewDocument();
        Assert.True(await rig.Router.PrepareAsync(tab));
        Assert.Same(a, rig.Router.SessionFor(tab));

        rig.Fx.Events.Publish(new SessionBroken(a.Id, false, null));
        Pump();
        var again = await rig.Service.ConnectAsync(a.Profile, "pw-a", default); // 끊긴 접속을 다시 열면 같은 자리에 새 세션
        Pump();

        Assert.True(again.IsConnected, again.Message);
        Assert.NotSame(a, again.Session);
        Assert.Same(again.Session, rig.Router.SessionFor(tab)); // WP-14 전처럼 새 세션을 따른다(「접속 안 됨」 이 아니다)
        Assert.Null(rig.Router.MissingProfileOf(tab));
        Assert.True(await rig.Router.PrepareAsync(tab));
        Assert.Single(rig.Service.SessionsOf(a.Profile.Id));
    });

    [Fact]
    public Task Moving_a_workspace_off_an_uncommitted_session_asks_even_if_another_tab_of_it_uses_the_session() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var a = await rig.ConnectAsync();
        var (_, tabA, wsB, tabB) = rig.TwoWorkspaces();
        await rig.Router.PrepareAsync(tabA);
        await rig.Router.PrepareAsync(tabB);
        var sessionB = rig.Router.SessionFor(tabB)!;
        rig.Host.Active = tabB;
        var tabB2 = rig.Host.NewDocument();
        Assert.Same(wsB, rig.Host.WorkspaceOf(tabB2));
        rig.State.ApplyTransaction(sessionB.Id, true);
        var asked = 0;
        rig.Router.ConfirmSwitch = (_, _) =>
        {
            asked++;
            return Task.FromResult(false);
        };

        Assert.Equal(SessionSelect.Cancelled, await rig.Router.SelectAsync(a)); // 작업 공간째 옮기므로 같은 작업 공간의 다른 탭은 「남는 탭」 이 아니다
        Assert.Null(await rig.Router.NewSessionAsync());
        Assert.Equal(2, asked);
        Assert.Same(sessionB, rig.Router.SessionFor(tabB));
        Assert.Same(sessionB, rig.Router.SessionFor(tabB2));
    });

    [Fact]
    public Task Running_twice_while_the_own_session_opens_opens_one_session() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var a = await rig.ConnectAsync();
        var (_, tabA, wsB, tabB) = rig.TwoWorkspaces();
        await rig.Router.PrepareAsync(tabA);
        rig.Host.Active = tabB;
        var tabB2 = rig.Host.NewDocument();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Fx.Sessions.OpenGate = gate;
        var calls = rig.Fx.Sessions.Calls.Count;

        var first = rig.Router.PrepareAsync(tabB);
        var second = rig.Router.PrepareAsync(tabB2); // 같은 작업 공간의 다른 탭에서 열리는 사이 또 실행
        gate.SetResult();

        Assert.True(await first);
        Assert.True(await second);
        Assert.Equal(calls + 1, rig.Fx.Sessions.Calls.Count); // 하나만 더 열었다(둘을 열고 하나를 닫지 않는다)
        Assert.Equal(2, rig.Service.SessionsOf(a.Profile.Id).Count);
        Assert.Same(rig.Router.SessionFor(tabB), rig.Router.SessionFor(tabB2));
        Assert.Equal([wsB], rig.Router.WorkspacesOn(rig.Router.SessionFor(tabB)!));
    });

    [Fact]
    public Task A_writing_feature_before_the_first_run_opens_the_workspace_own_session_not_the_connection_one() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var a = await rig.ConnectAsync();
        var (_, tabA, wsB, tabB) = rig.TwoWorkspaces();
        await rig.Router.PrepareAsync(tabA);
        Assert.True(rig.Router.IsPendingOwn(tabB));
        Assert.Same(a, rig.Router.SessionFor(tabB)); // 읽기용으로는 접속의 세션을 보인다

        var session = await rig.Router.ForWriteAsync(tabB);

        Assert.NotNull(session);
        Assert.NotSame(a, session); // 가져오기처럼 쓰고 커밋하는 기능은 A 의 트랜잭션에 섞이지 않는다
        Assert.Same(session, rig.Router.BoundSession(wsB, a.Profile.Id));
        Assert.Same(session, rig.Router.SessionFor(tabB));
    });

    [Fact]
    public Task A_writing_feature_gets_no_session_when_the_own_session_cannot_open() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        await rig.ConnectAsync();
        var (_, tabA, _, tabB) = rig.TwoWorkspaces();
        await rig.Router.PrepareAsync(tabA);
        rig.Fx.Sessions.Behavior = (_, _) => new InvalidOperationException("listener down");

        Assert.Null(await rig.Router.ForWriteAsync(tabB)); // 접속의 세션으로 대신하지 않는다
    });

    [Fact]
    public Task Choosing_another_database_in_the_combo_keeps_the_workspace_mode_and_binds_at_the_first_run() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var a = await rig.ConnectAsync("a");
        var b = await rig.ConnectAsync("b");
        var ws = rig.Host.EnsureWorkspace();
        var tab = rig.Host.NewDocument();
        Assert.Equal(b.Profile.Id, tab.ConnectionProfileId);

        Assert.Equal(SessionSelect.Selected, await rig.Router.SelectAsync(a));

        Assert.Equal(a.Profile.Id, tab.ConnectionProfileId);
        Assert.Null(ws.SessionMode); // 다른 DB 로 바꾸기만 했다: 설정을 따르는 그대로
        Assert.Null(rig.Router.BoundSession(ws, a.Profile.Id)); // 세션은 첫 실행 때 고른다
        Assert.Same(a, rig.Router.SessionFor(tab));
    });

    [Fact]
    public Task Closing_the_workspace_that_held_the_connection_session_refocuses_the_waiting_one() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var a = await rig.ConnectAsync();
        var (wsA, tabA, _, tabB) = rig.TwoWorkspaces();
        await rig.Router.PrepareAsync(tabA);
        rig.Host.Active = tabB;
        Pump();
        Assert.True(rig.State.FocusedPending);

        Assert.True(await rig.Host.TryCloseWorkspaceAsync(wsA, default));
        Pump();

        Assert.Same(tabB, rig.Host.Active);
        Assert.False(rig.State.FocusedPending); // 접속의 세션을 이제 아무도 안 쓴다: 첫 실행 때 그것을 차지한다
        Assert.Equal(a.Id, rig.State.FocusedSessionId);
    });

    [Fact]
    public Task Closing_a_workspace_without_tabs_still_asks_for_its_own_session() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var a = await rig.ConnectAsync();
        var (_, tabA, wsB, tabB) = rig.TwoWorkspaces();
        await rig.Router.PrepareAsync(tabA);
        await rig.Router.PrepareAsync(tabB);
        var sessionB = rig.Router.SessionFor(tabB)!;
        rig.State.ApplyTransaction(sessionB.Id, true);
        Assert.True(await rig.Host.CloseAsync(tabB, default)); // 탭만 닫으면 묻지 않는다(세션은 작업 공간에 남는다)
        Assert.Empty(wsB.Tabs);
        var asked = 0;
        rig.Router.ConfirmUncommitted = (_, _) =>
        {
            asked++;
            return Task.FromResult(false);
        };

        Assert.False(await rig.Host.TryCloseWorkspaceAsync(wsB, default)); // 탭이 없어도 묻고, 취소하면 닫지 않는다
        Assert.Equal(1, asked);
        Assert.Contains(wsB, rig.Host.Workspaces);
        Assert.True(rig.Service.IsOpen(sessionB));
        Assert.True(rig.Service.IsOpen(a));
    });

    [Fact]
    public Task Changing_only_the_session_mode_marks_the_workspace_for_autosave() => Ui.Run(() =>
    {
        using var rig = new Rig();
        var (_, _, wsB, _) = rig.TwoWorkspaces();
        Assert.True(wsB.ChangedSinceAutosave(out var before));
        wsB.MarkAutosaved(before);

        wsB.SessionMode = WorkspaceSessionMode.Shared;

        Assert.True(wsB.ChangedSinceAutosave(out _), "세션 방식만 바뀌어도 자동 저장 표지가 달라져야 한다.");
    });

    [Fact]
    public Task Closing_a_workspace_keeps_a_session_another_workspace_still_uses() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var a = await rig.ConnectAsync();
        var (wsA, tabA, wsB, tabB) = rig.TwoWorkspaces();
        await rig.Router.PrepareAsync(tabA);
        await rig.Router.PrepareAsync(tabB);
        var sessionB = rig.Router.SessionFor(tabB)!;
        var wsC = rig.Host.NewWorkspace();
        rig.Host.Active = wsC.Tabs[0];
        Pump();
        await rig.Router.SelectAsync(sessionB); // C 가 B 의 세션을 함께 쓴다
        rig.State.ApplyTransaction(sessionB.Id, true);
        var asked = 0;
        rig.Router.ConfirmUncommitted = (_, _) =>
        {
            asked++;
            return Task.FromResult(true);
        };

        Assert.True(await rig.Host.TryCloseWorkspaceAsync(wsB, default));
        Pump();

        Assert.Equal(0, asked); // 함께 쓰는 세션은 닫지 않으므로 묻지 않는다
        Assert.True(rig.Service.IsOpen(sessionB));
        Assert.Equal([wsC], rig.Router.WorkspacesOn(sessionB));
        Assert.Same(a, rig.Router.SessionFor(tabA));
        Assert.Contains(wsA, rig.Host.Workspaces);
    });

    [Fact]
    public Task The_workspace_choice_round_trips_through_the_workspace_file_and_restore_opens_no_session() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var a = await rig.ConnectAsync();
        var (wsA, _, wsB, _) = rig.TwoWorkspaces();
        wsB.SessionMode = WorkspaceSessionMode.Shared;
        var saved = wsB.ToWorkspace();
        Assert.Equal("shared", saved.Session);
        Assert.Null(wsA.ToWorkspace().Session); // 고르지 않았으면 설정을 따른다
        var json = System.Text.Json.JsonSerializer.Serialize(saved);
        Assert.Contains("\"Session\":\"shared\"", json, StringComparison.Ordinal);

        var calls = rig.Fx.Sessions.Calls.Count;
        var restored = rig.Host.Restore(saved with { Id = "restored", Session = "own" });
        rig.Host.Active = restored.Tabs.FirstOrDefault() ?? rig.Host.NewDocument();
        Pump();

        Assert.Equal(WorkspaceSessionMode.Own, restored.SessionMode);
        Assert.Equal(calls, rig.Fx.Sessions.Calls.Count); // 복원·활성만으로는 세션을 열지 않는다
        Assert.Null(WorkspaceSessionModes.Parse("weird"));
        Assert.Equal([a], rig.Service.SessionsOf(a.Profile.Id));
    });

    [Fact]
    public Task Exit_confirmation_covers_second_sessions_with_their_labels() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var a = await rig.ConnectAsync();
        var (_, tabA, _, tabB) = rig.TwoWorkspaces();
        await rig.Router.PrepareAsync(tabA);
        await rig.Router.PrepareAsync(tabB);
        var sessionB = rig.Router.SessionFor(tabB)!;
        rig.State.ApplyTransaction(sessionB.Id, true);
        var dialogs = new FakeDialogs { UncommittedAnswer = UncommittedChoice.Cancel };
        var controller = new ExecutionController(new ExecutionDeps
        {
            Host = () => rig.Host,
            State = rig.State,
            Log = new ExecutionLog(null, rig.State),
            Dialogs = dialogs,
            Session = () => rig.Router.Current,
            WorkSessions = () => rig.Router.AllSessions,
            ConnectionLabel = rig.Router.LabelOf,
            Execution = () => throw new NotSupportedException(),
            Language = () => throw new NotSupportedException(),
        });

        Assert.Equal([sessionB.Id], rig.State.PendingWorkSessions);
        Assert.False(await controller.ConfirmCloseAsync());
        Assert.Equal(["1 - scott@a (2)"], dialogs.ConnectionsAsked);
        Assert.Same(a, rig.Router.Current);
    });

    private static async Task WaitAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, "조건이 5초 안에 맞지 않았다.");
            Pump();
            await Task.Delay(10);
        }
    }
}
