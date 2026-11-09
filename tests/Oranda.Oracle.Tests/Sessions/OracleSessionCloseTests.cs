using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Sessions;
using Oranda.Oracle.Sessions;

namespace Oranda.Oracle.Tests.Sessions;

/// <summary>
/// <see cref="OracleSession"/> 닫기가 호출 스레드를 막지 않는다: 상태·목록·이벤트를 먼저 바꾸고 막힌 연결 닫기는 백그라운드에서 기다린다.
/// 닫기 대기 규칙 자체(<see cref="TimedClose"/>)는 Oranda.Core.Tests 의 TimedCloseTests 가 본다.
/// </summary>
public sealed class OracleSessionCloseTests
{
    private static readonly TimeSpan Quick = TimeSpan.FromSeconds(3);

    [Fact]
    public async Task OracleSession_dispose_publishes_closed_first_then_waits_for_stuck_connection_close_in_background()
    {
        using var gate = new ManualResetEventSlim(false);
        var closed = false;
        var bus = new RecordingBus();
        var removed = new List<Guid>();
        var id = Guid.NewGuid();
        // 열지 않은 연결: 실제로 닫을 것이 없어 Dispose 는 즉시 끝나고, 막히는 닫기는 주입한 함수가 흉내 낸다.
        using var connection = new OracleConnection("User Id=u;Password=p;Data Source=localhost:1/x");
        var info = new ServerInfo("23.26.0.0.0", "FREE", "localhost", "FREEPDB1", "FREE", "FREEPDB1", true, 1,
            new ServerCapabilities(false, true, true, false, false));
        var session = new OracleSession(id, new ConnectionProfile("dev", "scott", "localhost:1521/FREEPDB1"), info, SessionPurpose.Work,
            "K", connection, bus, removed.Add,
            () =>
            {
                gate.Wait(Quick);
                closed = true;
                return ValueTask.CompletedTask;
            });

        var dispose = session.DisposeAsync().AsTask();

        // 연결 닫기가 막혀 있어도 상태·목록·이벤트는 이미 바뀌었다.
        Assert.Equal(SessionState.Closed, session.State);
        Assert.Equal([id], removed);
        Assert.Contains(bus.Events, e => e is SessionClosed c && c.SessionId == id);
        Assert.False(closed);

        gate.Set();
        await dispose.WaitAsync(Quick);
        Assert.True(closed);
    }

    private sealed class RecordingBus : IEventBus
    {
        public List<OrandaEvent> Events { get; } = [];

        public IDisposable Subscribe<TEvent>(object owner, Action<TEvent> handler)
            where TEvent : OrandaEvent
            => throw new NotSupportedException();

        public void Publish<TEvent>(TEvent e)
            where TEvent : OrandaEvent
        {
            lock (Events)
            {
                Events.Add(e);
            }
        }
    }
}
