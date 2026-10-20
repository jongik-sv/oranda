using System.Diagnostics;
using Oranda.App.Features.ConnectionManager;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Connection;

namespace Oranda.App.Tests.Features.ConnectionManager;

/// <summary>접속 끊기·접속 바꾸기가 Dispose 가 막힌 세션 때문에 UI 스레드를 세우지 않는다(응답 없는 서버의 ODP Logoff 대기).</summary>
public sealed class SessionCloseTests
{
    private static readonly TimeSpan Quick = TimeSpan.FromSeconds(3);

    private static ConnectionProfile Profile() => new("dev", "scott", "localhost:1521/FREEPDB1");

    [Fact]
    public Task Disconnect_on_ui_thread_updates_state_at_once_while_blocked_dispose_finishes_later() => HeadlessHost.Run(async () =>
    {
        using var gate = new ManualResetEventSlim(false);
        using var fx = new ServiceFixture();
        fx.Sessions.DisposeGate = gate;
        var saved = await fx.Service.SaveProfileAsync(Profile(), "pw", true, CancellationToken.None);
        await fx.Service.ConnectAsync(saved, null, CancellationToken.None);
        var session = (StubSession)fx.Service.ActiveSession!;
        var changed = 0;
        fx.Service.ActiveSessionChanged += (_, _) => changed++;

        var clock = Stopwatch.StartNew();
        var disconnect = fx.Service.DisconnectAsync(CancellationToken.None);

        // 막힌 Dispose 를 기다리지 않고 돌아와 화면 상태가 이미 바뀌어 있다.
        Assert.True(clock.Elapsed < Quick, $"UI 스레드가 {clock.Elapsed} 동안 막혔다");
        Assert.Null(fx.Service.ActiveSession);
        Assert.False(fx.Keys.Get("hasSession"));
        Assert.Equal(1, changed);
        Assert.False(disconnect.IsCompleted);
        Assert.NotEqual(Oranda.Core.Sessions.SessionState.Closed, session.State);

        gate.Set(); // 막힌 Dispose 가 백그라운드에서 나중에 끝난다
        await disconnect.WaitAsync(Quick);
        Assert.Equal(Oranda.Core.Sessions.SessionState.Closed, session.State);
    });

    [Fact]
    public Task Reconnect_to_same_profile_works_while_previous_dispose_is_blocked() => HeadlessHost.Run(async () =>
    {
        using var gate = new ManualResetEventSlim(false);
        using var fx = new ServiceFixture();
        fx.Sessions.DisposeGate = gate;
        var saved = await fx.Service.SaveProfileAsync(Profile(), "pw", true, CancellationToken.None);
        await fx.Service.ConnectAsync(saved, null, CancellationToken.None);
        var first = fx.Service.ActiveSession!;

        var disconnect = fx.Service.DisconnectAsync(CancellationToken.None);
        fx.Sessions.DisposeGate = null;
        var again = await fx.Service.ConnectAsync(saved, null, CancellationToken.None).WaitAsync(Quick);

        Assert.True(again.IsConnected);
        Assert.NotSame(first, fx.Service.ActiveSession);
        Assert.True(fx.Keys.Get("hasSession"));

        gate.Set();
        await disconnect.WaitAsync(Quick);
        Assert.NotNull(fx.Service.ActiveSession); // 늦게 끝난 이전 세션의 닫힘이 새 세션을 지우지 않는다
    });

    [Fact]
    public async Task Close_gives_up_waiting_after_timeout_and_warns()
    {
        using var gate = new ManualResetEventSlim(false);
        using var fx = new ServiceFixture();
        fx.Sessions.DisposeGate = gate;
        var saved = await fx.Service.SaveProfileAsync(Profile(), "pw", true, CancellationToken.None);
        await fx.Service.ConnectAsync(saved, null, CancellationToken.None);
        var session = (StubSession)fx.Service.ActiveSession!;
        var warnings = new List<string>();

        await ConnectionService.CloseAsync(session, warnings.Add, TimeSpan.FromMilliseconds(100)).WaitAsync(Quick);

        Assert.Single(warnings);
        Assert.NotEqual(Oranda.Core.Sessions.SessionState.Closed, session.State); // 아직 백그라운드에서 막혀 있다
        gate.Set();
        await HeadlessHost.WaitUntil(() => session.State == Oranda.Core.Sessions.SessionState.Closed);
    }
}
