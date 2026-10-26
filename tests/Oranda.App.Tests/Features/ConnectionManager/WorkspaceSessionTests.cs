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

// 소유: TSK-14-01. 같은 DB 여러 세션(접속 서비스의 더 연 세션).
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
}
