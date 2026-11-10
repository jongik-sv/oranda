using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Security;
using Oranda.Core.Sessions;
using Oranda.Tibero.Sessions;

namespace Oranda.Tibero.Tests.Sessions;

public class TiberoSessionTests
{
    private static readonly ConnectionProfile Profile = new("t", "u", "h:1/d", Dbms: Dbms.Tibero);

    private static readonly ServerInfo Server = new("6", "i", "h", null, "D", null, false, 7,
        new ServerCapabilities(false, false, true, false, false, Dbms.Tibero), DbUniqueName: "D", SessionUser: "U", Dbms: Dbms.Tibero);

    /// <summary>
    /// 닫기가 실제로 끝날 때까지 기다린다. DisposeAsync 는 닫기(스레드 풀)를 TimedClose.Timeout(실제 시간 5초)까지만 기다리고 돌아온다(설계대로:
    /// 응답 없는 서버가 UI 를 막지 않게). 같은 어셈블리의 실서버 시험이 스레드 풀·CPU 를 잡으면 닫기가 그 안에 못 끝나 닫기 결과를 단정하는 시험이
    /// 간헐로 실패했다(win144 실서버 변수 켠 전체 실행 5번 중 3번, 단독 26번 통과 — ci-flaky F7). 시한은 부하 큰 PC 용으로 넉넉히 둔다.
    /// </summary>
    private static async Task WaitForClose(Func<bool> closed, int timeoutMs = 60_000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!closed())
        {
            Assert.True(DateTime.UtcNow < until, $"닫기가 {timeoutMs} ms 안에 끝나지 않았다.");
            await Task.Delay(10);
        }
    }

    private static (TiberoSession Session, FakeDbConnection Connection, List<OrandaEvent> Events, List<Guid> Removed) Create(
        Func<ValueTask>? close = null)
    {
        var bus = new EventBus();
        var events = new List<OrandaEvent>();
        bus.Subscribe<OrandaEvent>(events, events.Add); // 구독 주인 = 시험이 붙잡고 있는 목록(약한 참조라 따로 만든 객체는 수거될 수 있다)
        var connection = new FakeDbConnection();
        var removed = new List<Guid>();
        var session = new TiberoSession(Guid.NewGuid(), Profile, Server, SessionPurpose.Work, "D/-/U", connection, null, bus, removed.Add, close);
        return (session, connection, events, removed);
    }

    [Fact]
    public void A_new_session_is_idle_without_a_pending_transaction()
    {
        var (session, _, _, _) = Create();

        Assert.Equal(SessionState.Idle, session.State);
        Assert.False(session.HasPendingTransaction);
        Assert.Equal("D/-/U", session.DbKey);
        Assert.Equal(Dbms.Tibero, session.Server.Dbms);
        Assert.False(session.CommandGate.IsHeld);
    }

    [Fact]
    public void SetPending_publishes_only_changes()
    {
        var (session, _, events, _) = Create();

        session.SetPending(true);
        session.SetPending(true);
        session.SetPending(false);

        Assert.Equal([true, false], events.OfType<TransactionStateChanged>().Select(e => e.Pending));
    }

    [Fact]
    public void SetBusy_toggles_idle_and_busy_but_not_broken()
    {
        var (session, connection, _, _) = Create();

        session.SetBusy(true);
        Assert.Equal(SessionState.Busy, session.State);
        session.SetBusy(false);
        Assert.Equal(SessionState.Idle, session.State);

        connection.Drop();
        session.SetBusy(true);
        Assert.Equal(SessionState.Broken, session.State);
    }

    [Fact]
    public void A_dropped_connection_breaks_the_session_and_reports_lost_changes()
    {
        var (session, connection, events, _) = Create();
        session.SetPending(true);

        connection.Drop();
        connection.Drop();

        Assert.Equal(SessionState.Broken, session.State);
        Assert.False(session.HasPendingTransaction);
        var broken = Assert.Single(events.OfType<SessionBroken>());
        Assert.True(broken.HadPendingTransaction);
    }

    [Fact]
    public void Non_driver_failures_do_not_break_the_session()
    {
        var (session, _, events, _) = Create();

        Assert.False(session.ReportFailure(new InvalidOperationException("ODP-90603")));
        Assert.Equal(SessionState.Idle, session.State);
        Assert.Empty(events.OfType<SessionBroken>());
    }

    [Fact]
    public async Task Dispose_closes_once_removes_and_publishes()
    {
        var closes = 0;
        var (session, connection, events, removed) = Create(() =>
        {
            Interlocked.Increment(ref closes);
            return ValueTask.CompletedTask;
        });

        await session.DisposeAsync();
        await session.DisposeAsync();
        await WaitForClose(() => Volatile.Read(ref closes) >= 1);
        connection.Drop(); // 닫은 뒤 Closed 알림은 끊김이 아니다.

        Assert.Equal(1, closes);
        Assert.Equal(SessionState.Closed, session.State);
        Assert.Equal([session.Id], removed);
        Assert.Single(events.OfType<SessionClosed>());
        Assert.Empty(events.OfType<SessionBroken>());
    }

    [Fact]
    public async Task Default_close_disposes_the_connection()
    {
        var (session, connection, _, _) = Create();

        await session.DisposeAsync();
        await WaitForClose(() => connection.DisposeCount >= 1);

        Assert.True(connection.DisposeCount >= 1);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Dispose_after_the_connection_dropped_skips_the_broken_transaction_and_closes(bool reportsClosed)
    {
        // 드라이버 실측: 닫힌 연결의 TiberoTransaction.Dispose = NullReferenceException. 상태가 Open 인 채 끊긴 경우도 삼킨다.
        var connection = new FakeDbConnection();
        var transaction = new ThrowingTransaction(connection);
        var session = new TiberoSession(Guid.NewGuid(), Profile, Server, SessionPurpose.Work, "D/-/U", connection, transaction, new EventBus());
        if (reportsClosed)
        {
            connection.Drop();
        }

        await session.DisposeAsync();
        await WaitForClose(() => connection.DisposeCount >= 1); // 연결 닫기는 트랜잭션 정리 뒤(finally)라, 닫혔으면 정리도 끝났다

        Assert.Equal(reportsClosed ? 0 : 1, transaction.DisposeCalls);
        Assert.True(connection.DisposeCount >= 1);
        Assert.Equal(SessionState.Closed, session.State);
    }

    [Fact]
    public async Task Dispose_does_not_wait_for_a_command_that_holds_the_gate_and_frees_it_later()
    {
        var (session, connection, _, _) = Create();
        Assert.True(session.CommandGate.TryEnter()); // 아직 도는 명령

        var started = DateTime.UtcNow;
        await session.DisposeAsync();

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(4)); // 잠금은 1초만 기다리고 닫는다(TimedClose 5초보다 짧게)
        Assert.True(connection.DisposeCount >= 1);

        session.CommandGate.Release(); // 명령이 끝남 → 늦게 잡힌 정리 잠금도 곧 놓여야 한다
        for (var i = 0; i < 50 && session.CommandGate.IsHeld; i++)
        {
            await Task.Delay(20);
        }

        Assert.False(session.CommandGate.IsHeld);
    }

    [Fact]
    public async Task Keep_alive_skips_while_another_command_holds_the_gate()
    {
        var (session, _, _, _) = Create();
        Assert.True(session.CommandGate.TryEnter());
        try
        {
            await session.KeepAliveTickAsync(); // 가짜 연결은 명령을 만들면 던진다 → 건너뛰지 않으면 실패한다.
        }
        finally
        {
            session.CommandGate.Release();
        }

        Assert.Equal(SessionState.Idle, session.State);
    }
}

public class TiberoSessionManagerTests
{
    [Fact]
    public async Task Open_rejects_a_profile_of_another_dbms_before_connecting()
    {
        var manager = new TiberoSessionManager(new EventBus());
        using var credential = new SecureCredential("pw");

        await Assert.ThrowsAsync<ArgumentException>(() =>
            manager.OpenAsync(new ConnectionProfile("o", "u", "h:1/d"), credential, SessionPurpose.Work, CancellationToken.None));
        Assert.Empty(manager.Sessions);
    }

    [Fact]
    public async Task Open_without_a_usable_driver_fails_with_the_driver_message_before_connecting()
    {
        try
        {
            _ = global::Oranda.Tibero.Driver.TiberoDriver.Require(null);
            return; // 이 시험 환경엔 드라이버가 있다 — 「없을 때」 시험은 드라이버 없는 곳(CI)에서 돈다.
        }
        catch (global::Oranda.Tibero.Driver.TiberoDriverUnavailableException)
        {
        }

        var manager = new TiberoSessionManager(new EventBus());
        using var credential = new SecureCredential("pw");

        await Assert.ThrowsAnyAsync<global::Oranda.Tibero.Driver.TiberoDriverUnavailableException>(() =>
            manager.OpenAsync(new ConnectionProfile("t", "u", "h:1/d", Dbms: Dbms.Tibero), credential, SessionPurpose.Work, CancellationToken.None));
        Assert.Empty(manager.Sessions);
    }

    [Fact]
    public void Nls_statements_skip_language_and_territory()
    {
        var nls = new NlsSettings { DateFormat = "YYYY-MM-DD", Language = "AMERICAN", Territory = "AMERICA", Sort = "BINARY", TimestampFormat = "it's" };

        var statements = TiberoSessionManager.NlsStatements(nls, out var skipped);

        Assert.Equal(
            [
                "ALTER SESSION SET NLS_DATE_FORMAT = 'YYYY-MM-DD'",
                "ALTER SESSION SET NLS_TIMESTAMP_FORMAT = 'it''s'",
                "ALTER SESSION SET NLS_SORT = 'BINARY'",
            ],
            statements);
        Assert.Equal(["NLS_LANGUAGE", "NLS_TERRITORY"], skipped);
        Assert.Empty(TiberoSessionManager.NlsStatements(null, out var none));
        Assert.Empty(none);
    }
}
