using Avalonia.Controls;
using Avalonia.Threading;
using Oranda.App.Features.ConnectionManager;
using Oranda.App.Features.DedicatedSession;
using Oranda.App.Features.Execution;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Tests.Features.SqlEditor;
using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Sessions;

namespace Oranda.App.Tests.Features.ConnectionManager;

// 소유: TSK-14-07(D-039). 세션 대체와 수명: 기본 세션이 닫히면 올라온 추가 세션을 몰래 따르지 않는다, 끊긴 접속을 다시 연 경우만 따른다,
// 끊긴 추가 세션 정리, 작업 공간 닫기·툴바 접속 끊기의 미커밋 확인, 여는 중 중복·선택 덮어쓰기, 자기 실행 중 세션 옮기기, 접속 없는 탭.
public sealed class SessionLifecycleTests
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

        public async Task<ISession> ConnectAsync(string name = "a")
        {
            var profile = await Fx.Service.SaveProfileAsync(new ConnectionProfile(name, "scott", "localhost:1521/FREEPDB1"), null, false, default);
            var outcome = await Fx.Service.ConnectAsync(profile, "pw-" + name, default);
            Assert.True(outcome.IsConnected, outcome.Message);
            Pump();
            return outcome.Session!;
        }

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

    // ---------- 1·12: 기본 세션 대체 ----------

    [Fact]
    public Task Closing_the_connection_session_turns_its_workspace_into_not_connected_instead_of_moving_it_onto_the_promoted_session() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var a = await rig.ConnectAsync();
        var (_, tabA, _, tabB) = rig.TwoWorkspaces();
        await rig.Router.PrepareAsync(tabA); // A → a
        rig.Host.Active = tabB;
        Pump();
        await rig.Router.PrepareAsync(tabB); // B → s2
        var s2 = rig.Router.SessionFor(tabB)!;
        rig.State.ApplyTransaction(s2.Id, true); // B 의 미커밋
        rig.Router.ConfirmSwitch = (_, _) => Task.FromResult(true); // 「그대로 두고 바꾸기」
        var s3 = await rig.Router.NewSessionAsync(); // B → s3, s2 는 미커밋을 쥔 채 아무도 안 쓴다
        Pump();
        Assert.Same(s3, rig.Router.SessionFor(tabB));

        await rig.Service.DisconnectAsync(a, default);
        Pump();

        Assert.Same(s2, rig.Service.SessionOf(a.Profile.Id)); // 접속 서비스는 그대로 올린다(콤보 번호 자리)
        Assert.Equal(a.Profile.Id, rig.Router.MissingProfileOf(tabA)); // A 는 「접속 안 됨」: B 가 남긴 미커밋 세션으로 몰래 옮기지 않는다
        Assert.Null(rig.Router.SessionFor(tabA));
        Assert.Empty(rig.Router.WorkspacesOn(s2));
        Assert.True(await rig.Router.PrepareAsync(tabA)); // 접속 안 됨은 실행 쪽이 먼저 묻는다: 여기서 묶지 않는다
        Assert.Null(rig.Router.SessionFor(tabA));
        Assert.Same(s3, rig.Router.SessionFor(tabB)); // B 는 그대로
    });

    [Fact]
    public Task Shared_workspaces_on_a_closed_connection_session_all_become_not_connected_together() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        rig.Router.SessionPerWorkspace = () => false;
        var a = await rig.ConnectAsync();
        var (_, tabA, _, tabB) = rig.TwoWorkspaces();
        await rig.Router.PrepareAsync(tabA);
        await rig.Router.PrepareAsync(tabB);
        var extra = (await rig.Service.OpenSessionAsync(a.Profile.Id, default)).Session!;
        Pump();

        await rig.Service.DisconnectAsync(a, default);
        Pump();

        Assert.Same(extra, rig.Service.SessionOf(a.Profile.Id));
        Assert.Null(rig.Router.SessionFor(tabA)); // 먼저 물은 쪽만 따라가지 않는다: 둘 다 같은 결과
        Assert.Null(rig.Router.SessionFor(tabB));
        Assert.Equal(a.Profile.Id, rig.Router.MissingProfileOf(tabA));
        Assert.Equal(a.Profile.Id, rig.Router.MissingProfileOf(tabB));
    });

    [Fact]
    public Task Reconnecting_a_broken_connection_moves_every_workspace_that_used_it_onto_the_new_session() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        rig.Router.SessionPerWorkspace = () => false;
        var a = await rig.ConnectAsync();
        var (_, tabA, _, tabB) = rig.TwoWorkspaces();
        await rig.Router.PrepareAsync(tabA);
        await rig.Router.PrepareAsync(tabB);

        rig.Fx.Events.Publish(new SessionBroken(a.Id, false, null));
        Pump();
        var again = await rig.Service.ConnectAsync(a.Profile, "pw-a", default);
        Pump();

        Assert.True(again.IsConnected, again.Message);
        Assert.Same(again.Session, rig.Router.SessionFor(tabA));
        Assert.Same(again.Session, rig.Router.SessionFor(tabB)); // 함께 쓰던 둘이 갈리지 않는다
        Assert.Null(rig.Router.MissingProfileOf(tabB));
    });

    [Fact]
    public Task A_workspace_on_a_promoted_session_still_follows_when_that_connection_is_reconnected() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var a = await rig.ConnectAsync();
        var (_, tabA, _, tabB) = rig.TwoWorkspaces();
        await rig.Router.PrepareAsync(tabA);
        await rig.Router.PrepareAsync(tabB);
        var s2 = rig.Router.SessionFor(tabB)!;
        await rig.Service.DisconnectAsync(a, default); // s2 가 접속 자리로 올라온다(B 는 원래 s2 를 썼다)
        Pump();
        Assert.Same(s2, rig.Router.SessionFor(tabB));

        rig.Fx.Events.Publish(new SessionBroken(s2.Id, false, null));
        Pump();
        var again = await rig.Service.ConnectAsync(a.Profile, "pw-a", default);
        Pump();

        Assert.NotSame(s2, again.Session);
        Assert.Same(again.Session, rig.Router.SessionFor(tabB)); // 같은 자리에 다시 연 접속은 따른다
    });

    // ---------- 11: 끊긴 추가 세션 ----------

    [Fact]
    public Task A_broken_second_session_is_closed_so_its_workspace_can_pick_a_session_again() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var a = await rig.ConnectAsync();
        var (_, tabA, wsB, tabB) = rig.TwoWorkspaces();
        await rig.Router.PrepareAsync(tabA);
        await rig.Router.PrepareAsync(tabB);
        var s2 = rig.Router.SessionFor(tabB)!;

        rig.Fx.Events.Publish(new SessionBroken(s2.Id, false, null));
        await WaitAsync(() => !rig.Service.IsOpen(s2));

        Assert.Equal([a], rig.Service.SessionsOf(a.Profile.Id)); // 상한에도 잡히지 않는다
        Assert.Equal(a.Profile.Id, rig.Router.MissingProfileOf(tabB)); // 「접속 안 됨」 → 다시 접속·지금 접속으로
        Assert.Same(a, rig.Router.SessionFor(tabA));

        rig.Host.Active = tabB;
        Pump();
        var back = await rig.Router.ReconnectAsync(a.Profile.Id, () => Task.FromResult<ISession?>(null));
        Assert.Same(a, back);
        Assert.True(await rig.Router.PrepareAsync(tabB));
        var fresh = rig.Router.SessionFor(tabB)!;
        Assert.NotSame(a, fresh);
        Assert.NotSame(s2, fresh);
        Assert.Equal([wsB], rig.Router.WorkspacesOn(fresh));
    });

    // ---------- 13: 작업 공간 닫기 ----------

    [Fact]
    public Task Closing_the_only_workspace_on_an_uncommitted_connection_session_asks_first() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var a = await rig.ConnectAsync();
        var wsA = rig.Host.EnsureWorkspace();
        var tabA = rig.Host.NewDocument();
        await rig.Router.PrepareAsync(tabA);
        rig.State.ApplyTransaction(a.Id, true);
        var asked = new List<ISession>();
        var answer = false;
        rig.Router.ConfirmUncommitted = (s, _) =>
        {
            asked.Add(s);
            return Task.FromResult(answer);
        };
        var wsC = rig.Host.NewWorkspace();

        Assert.False(await rig.Host.TryCloseWorkspaceAsync(wsA, default)); // 취소: 다음 작업 공간이 그 트랜잭션을 물려받지 않게 닫지 않는다
        Assert.Equal([a], asked);
        Assert.Contains(wsA, rig.Host.Workspaces);

        answer = true;
        rig.State.ApplyTransaction(a.Id, false); // 확인 창이 롤백했다
        rig.State.ApplyTransaction(a.Id, true);
        Assert.True(await rig.Host.TryCloseWorkspaceAsync(wsA, default));
        Assert.Equal([a, a], asked);
        Assert.True(rig.Service.IsOpen(a)); // 접속의 세션은 닫지 않는다
        Assert.Contains(wsC, rig.Host.Workspaces);
    });

    [Fact]
    public Task Closing_one_of_two_workspaces_sharing_the_connection_session_does_not_ask() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        rig.Router.SessionPerWorkspace = () => false;
        var a = await rig.ConnectAsync();
        var (wsA, tabA, _, tabB) = rig.TwoWorkspaces();
        await rig.Router.PrepareAsync(tabA);
        await rig.Router.PrepareAsync(tabB);
        rig.State.ApplyTransaction(a.Id, true);
        var asked = 0;
        rig.Router.ConfirmUncommitted = (_, _) =>
        {
            asked++;
            return Task.FromResult(false);
        };

        Assert.True(await rig.Host.TryCloseWorkspaceAsync(wsA, default)); // 남는 작업 공간이 그 트랜잭션을 함께 쓴다
        Assert.Equal(0, asked);
        Assert.Same(a, rig.Router.SessionFor(tabB));
    });

    // ---------- 6·14: 여는 중 ----------

    [Fact]
    public Task Run_in_a_new_session_pressed_twice_opens_one_session_and_runs_once() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var a = await rig.ConnectAsync();
        var (_, tabA, wsB, tabB) = rig.TwoWorkspaces();
        wsB.SessionMode = WorkspaceSessionMode.Shared;
        await rig.Router.PrepareAsync(tabA);
        await rig.Router.PrepareAsync(tabB);
        rig.Host.Active = tabA;
        Pump();
        rig.State.BeginRun(a.Id);
        rig.Host.Active = tabB;
        Pump();
        var reruns = 0;
        Assert.True(rig.Router.OfferNewSession(tabB, () =>
        {
            reruns++;
            return Task.CompletedTask;
        }));
        var notice = wsB.BusyNotice!;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Fx.Sessions.OpenGate = gate;
        var calls = rig.Fx.Sessions.Calls.Count;

        var first = notice.RunInNewSession();
        var second = notice.RunInNewSession(); // 여는 사이 또 눌렀다
        rig.Fx.Sessions.OpenGate = null;
        gate.SetResult();
        await first;
        await second;
        Pump();

        Assert.Equal(1, reruns); // 같은 SQL 을 두 번 보내지 않는다
        Assert.Equal(calls + 1, rig.Fx.Sessions.Calls.Count);
        Assert.Equal(2, rig.Service.SessionsOf(a.Profile.Id).Count);
        Assert.NotSame(a, rig.Router.SessionFor(tabB));
        rig.State.EndRun(a.Id);
    });

    [Fact]
    public Task The_run_in_new_session_button_turns_off_as_soon_as_it_is_pressed() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var a = await rig.ConnectAsync();
        var (_, tabA, wsB, tabB) = rig.TwoWorkspaces();
        wsB.SessionMode = WorkspaceSessionMode.Shared;
        await rig.Router.PrepareAsync(tabA);
        await rig.Router.PrepareAsync(tabB);
        rig.Host.Active = tabA;
        Pump();
        rig.State.BeginRun(a.Id);
        rig.Host.Active = tabB;
        Pump();
        var bar = new SessionBusyBar { DataContext = wsB };
        var window = new Window { Width = 900, Height = 120, Content = new StackPanel { Children = { bar } } };
        window.Show();
        try
        {
            Pump();
            Assert.True(rig.Router.OfferNewSession(tabB, () => Task.CompletedTask));
            Assert.True(bar.RunInNewButton.IsEnabled);

            rig.Fx.Sessions.Behavior = (_, _) => new InvalidOperationException("listener down");
            bar.RunInNewButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            await WaitAsync(() => bar.RunInNewButton.IsEnabled); // 못 열었으면 막대가 남고 다시 누를 수 있다
            Assert.NotNull(wsB.BusyNotice);
            rig.Fx.Sessions.Behavior = (_, _) => null;

            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            rig.Fx.Sessions.OpenGate = gate;

            bar.RunInNewButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Pump();
            Assert.False(bar.RunInNewButton.IsEnabled); // 누르는 즉시 꺼진다

            rig.Fx.Sessions.OpenGate = null;
            gate.SetResult();
            await WaitAsync(() => wsB.BusyNotice is null);
            Assert.False(bar.IsVisible);
        }
        finally
        {
            window.Close();
            rig.State.EndRun(a.Id);
        }
    });

    [Fact]
    public Task New_session_pressed_twice_while_opening_opens_one_session() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var a = await rig.ConnectAsync();
        var (_, tabA, wsB, tabB) = rig.TwoWorkspaces();
        await rig.Router.PrepareAsync(tabA);
        rig.Host.Active = tabB;
        Pump();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Fx.Sessions.OpenGate = gate;
        var calls = rig.Fx.Sessions.Calls.Count;

        var first = rig.Router.NewSessionAsync();
        var second = rig.Router.NewSessionAsync();
        var run = rig.Router.PrepareAsync(tabB); // 여는 사이 실행해도 같은 열기를 기다린다
        rig.Fx.Sessions.OpenGate = null;
        gate.SetResult();
        var s1 = await first;
        var s2 = await second;
        Assert.True(await run);
        Pump();

        Assert.NotNull(s1);
        Assert.Same(s1, s2);
        Assert.Equal(calls + 1, rig.Fx.Sessions.Calls.Count);
        Assert.Same(s1, rig.Router.SessionFor(tabB));
        Assert.Equal([wsB], rig.Router.WorkspacesOn(s1!));
    });

    [Fact]
    public Task A_session_picked_while_the_own_session_opens_is_kept_and_the_opened_one_is_closed() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var a = await rig.ConnectAsync();
        var (_, tabA, _, tabB) = rig.TwoWorkspaces();
        await rig.Router.PrepareAsync(tabA);
        var other = (await rig.Service.OpenSessionAsync(a.Profile.Id, default)).Session!;
        Pump();
        rig.Host.Active = tabB;
        Pump();

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Fx.Sessions.OpenGate = gate;
        var prepare = rig.Router.PrepareAsync(tabB); // 자기 세션을 여는 중
        Pump();
        Assert.Equal(SessionSelect.Selected, await rig.Router.SelectAsync(other)); // 그 사이 콤보로 고른다
        Pump();
        rig.Fx.Sessions.OpenGate = null;
        gate.SetResult();
        Assert.True(await prepare);
        Pump();

        Assert.Same(other, rig.Router.SessionFor(tabB)); // 사용자가 고른 묶음을 덮지 않는다
        await WaitAsync(() => rig.Service.SessionsOf(a.Profile.Id).Count == 2); // 막 연 세션은 버려 두지 않고 닫는다
        Assert.Equal([a, other], rig.Service.SessionsOf(a.Profile.Id));
    });

    // ---------- 9: 자기 실행 중 공유 세션 옮기기 ----------

    [Fact]
    public Task A_workspace_cannot_move_off_a_shared_session_while_its_own_run_is_going() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        rig.Router.SessionPerWorkspace = () => false;
        var a = await rig.ConnectAsync();
        var (_, tabA, wsB, tabB) = rig.TwoWorkspaces();
        await rig.Router.PrepareAsync(tabA);
        await rig.Router.PrepareAsync(tabB);
        var other = (await rig.Service.OpenSessionAsync(a.Profile.Id, default)).Session!;
        Pump();
        rig.Host.Active = tabB;
        Pump();
        rig.State.BeginRun(a.Id, wsB); // B 가 시작한 실행(A 도 같은 세션을 쓴다)

        Assert.Equal(SessionSelect.Busy, await rig.Router.SelectAsync(other));
        Assert.Equal(SessionSelect.Held, rig.Router.Select(other));
        Assert.Null(await rig.Router.NewSessionAsync());
        Assert.Same(a, rig.Router.SessionFor(tabB)); // 옮기면 그 실행을 취소할 수 없게 된다
        rig.State.EndRun(a.Id);

        rig.State.BeginRun(a.Id, rig.Host.WorkspaceOf(tabA)); // 남(A)의 실행이면 지금처럼 옮길 수 있다(공유)
        Assert.Equal(SessionSelect.Selected, await rig.Router.SelectAsync(other));
        Assert.Same(other, rig.Router.SessionFor(tabB));
        rig.State.EndRun(a.Id);
    });

    // ---------- 15: 툴바 접속 끊기 ----------

    [Fact]
    public Task Disconnecting_the_connection_closes_every_session_of_that_database_asking_for_each_uncommitted_one() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var a = await rig.ConnectAsync();
        var (_, tabA, _, tabB) = rig.TwoWorkspaces();
        await rig.Router.PrepareAsync(tabA);
        await rig.Router.PrepareAsync(tabB);
        var s2 = rig.Router.SessionFor(tabB)!;
        var s3 = (await rig.Service.OpenSessionAsync(a.Profile.Id, default)).Session!;
        rig.State.ApplyTransaction(s2.Id, true);
        var asked = new List<ISession>();
        var answer = false;
        rig.Router.ConfirmUncommitted = (s, _) =>
        {
            asked.Add(s);
            return Task.FromResult(answer);
        };

        Assert.False(await rig.Router.DisconnectAsync(a)); // 취소하면 하나도 끊지 않는다
        Assert.Equal([s2], asked);
        Assert.Equal([a, s2, s3], rig.Service.SessionsOf(a.Profile.Id));

        rig.State.BeginRun(s3.Id);
        Assert.False(await rig.Router.DisconnectAsync(a)); // 그 DB 세션 하나라도 실행 중이면 끊지 않는다
        Assert.Equal([s2], asked);
        rig.State.EndRun(s3.Id);

        answer = true;
        Assert.True(await rig.Router.DisconnectAsync(a));
        Pump();
        Assert.Empty(rig.Service.SessionsOf(a.Profile.Id)); // 추가 세션이 접속 자리로 올라오지 않는다
        Assert.False(rig.Service.IsOpen(s2));
        Assert.False(rig.Service.IsOpen(s3));
        Assert.Equal(a.Profile.Id, rig.Router.MissingProfileOf(tabA));
        Assert.Equal(a.Profile.Id, rig.Router.MissingProfileOf(tabB));
    });

    [Fact]
    public Task Disconnecting_a_second_session_closes_only_that_one() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var a = await rig.ConnectAsync();
        var (_, tabA, _, tabB) = rig.TwoWorkspaces();
        await rig.Router.PrepareAsync(tabA);
        await rig.Router.PrepareAsync(tabB);
        var s2 = rig.Router.SessionFor(tabB)!;

        Assert.True(await rig.Router.DisconnectAsync(s2));
        Pump();

        Assert.Equal([a], rig.Service.SessionsOf(a.Profile.Id));
        Assert.Same(a, rig.Router.SessionFor(tabA));
    });

    // ---------- 16: 접속이 비어 있는 탭 ----------

    [Fact]
    public Task A_tab_without_a_connection_takes_the_profile_of_its_workspace_session() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var a = await rig.ConnectAsync();
        var (_, tabA, wsB, tabB) = rig.TwoWorkspaces();
        await rig.Router.PrepareAsync(tabA);
        rig.Host.Active = tabB;
        var tabB2 = rig.Host.NewDocument();
        Assert.Same(wsB, rig.Host.WorkspaceOf(tabB2));
        tabB2.ConnectionProfileId = null; // 접속 없이 만든 탭(복원·접속 전 탭)
        rig.Host.Active = tabB;
        Pump();
        await rig.Router.PrepareAsync(tabB);
        var s2 = rig.Router.SessionFor(tabB)!;
        Assert.NotSame(a, s2);

        Assert.Equal(a.Profile.Id, tabB2.ConnectionProfileId); // 묶을 때 채운다
        Assert.Same(s2, rig.Router.SessionFor(tabB2)); // 같은 작업 공간 = 같은 세션(지금 접속 a 가 아니다)

        tabB2.ConnectionProfileId = null;
        rig.Host.Active = tabB2; // 활성이 될 때도 채운다
        Pump();
        Assert.Equal(a.Profile.Id, tabB2.ConnectionProfileId);
        Assert.Same(s2, rig.Router.SessionFor(tabB2));

        tabB2.ConnectionProfileId = null;
        Assert.True(await rig.Router.PrepareAsync(tabB2)); // 실행 전에도
        Assert.Same(s2, rig.Router.SessionFor(tabB2));
    });
}
