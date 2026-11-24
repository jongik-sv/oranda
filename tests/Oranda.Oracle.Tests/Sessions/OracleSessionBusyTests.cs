using System.Diagnostics;
using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Sessions;
using Oranda.Oracle.Sessions;

namespace Oranda.Oracle.Tests.Sessions;

/// <summary>
/// PHC-3: 누가 명령 잠금을 쥐고 있으면(멈춘 DDL·컴파일 등) 세션은 「명령이 도는 중」이고, 닫는 경로의 커밋·롤백은 그 잠금 뒤에서 상한 안에 돌아온다.
/// 연결은 열지 않는다(잠금 뒤에서 기다리다 취소되므로 연결에 닿지 않는다).
/// </summary>
public sealed class OracleSessionBusyTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromMilliseconds(200);

    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task A_held_gate_makes_the_session_busy_and_a_bounded_end_returns_in_time()
    {
        await using var connection = new OracleConnection("User Id=u;Password=p;Data Source=localhost:1/x");
        await using var session = new OracleSession(Guid.NewGuid(), new ConnectionProfile("dev", "scott", "localhost:1521/FREEPDB1"),
            new ServerInfo("23.26.0.0.0", "FREE", "localhost", "FREEPDB1", "FREE", "FREEPDB1", true, 1, new ServerCapabilities(false, true, true, false, false)),
            SessionPurpose.Work, "K", connection, new EventBus(), null, () => ValueTask.CompletedTask);
        Assert.False(session.IsCommandRunning);

        await session.CommandGate.EnterAsync(default); // 멈춘 컴파일이 잠금을 쥐었다
        try
        {
            Assert.True(session.IsCommandRunning);
            foreach (var commit in new[] { true, false })
            {
                var watch = Stopwatch.StartNew();
                var r = await BoundedTransactionEnd.RunAsync(session, commit, Limit);
                Assert.Equal(TransactionEndOutcome.TimedOut, r.Outcome);
                Assert.True(watch.Elapsed < Generous, $"{watch.Elapsed} 걸림");
            }
        }
        finally
        {
            session.CommandGate.Release();
        }

        Assert.False(session.IsCommandRunning);
        session.SetBusy(true); // 실행 서비스가 실행을 알린다
        Assert.True(session.IsCommandRunning);
        session.SetBusy(false);
        Assert.False(session.IsCommandRunning);
    }
}
