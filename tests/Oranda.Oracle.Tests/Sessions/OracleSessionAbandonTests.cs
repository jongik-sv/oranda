using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Sessions;
using Oranda.Oracle.Sessions;

namespace Oranda.Oracle.Tests.Sessions;

/// <summary>
/// PHC-7: 취소가 돌아오지 않는 컴파일을 버리면 세션은 바로 끊김(SessionBroken)이 되고, 연결 닫기는 잠금을 기다리지 않고 백그라운드에서 한 번 시작한다.
/// 연결은 열지 않는다.
/// </summary>
public sealed class OracleSessionAbandonTests
{
    [Fact]
    public async Task Abandon_breaks_the_session_at_once_and_starts_one_close_without_the_gate()
    {
        var bus = new EventBus();
        SessionBroken? broken = null;
        bus.Subscribe<SessionBroken>(this, e => broken = e);
        var closes = 0;
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var connection = new OracleConnection("User Id=u;Password=p;Data Source=localhost:1/x");
        await using var session = new OracleSession(Guid.NewGuid(), new ConnectionProfile("dev", "scott", "localhost:1521/FREEPDB1"),
            new ServerInfo("23.26.0.0.0", "FREE", "localhost", "FREEPDB1", "FREE", "FREEPDB1", true, 1, new ServerCapabilities(false, true, true, false, false)),
            SessionPurpose.Work, "K", connection, bus, null, () =>
            {
                Interlocked.Increment(ref closes);
                closed.TrySetResult();
                return ValueTask.CompletedTask;
            });
        await session.CommandGate.EnterAsync(default); // 돌아오지 않는 CREATE 가 잠금을 쥐고 있다

        session.Abandon("서버가 취소에 답하지 않음");

        Assert.Equal(SessionState.Broken, session.State);
        Assert.NotNull(broken);
        Assert.Equal("ORANDA-ABANDONED", broken!.Error?.Code);
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(10)); // 잠금을 쥔 채여도 닫기는 시작한다
        Assert.True(session.IsStalled); // 다음 질의는 잠금을 기다리지 않고 바로 실패한다

        session.Abandon("두 번째"); // 이미 버렸다: 다시 알리거나 닫지 않는다
        await Task.Delay(50);
        Assert.Equal(1, Volatile.Read(ref closes));
        session.CommandGate.Release();
    }
}
