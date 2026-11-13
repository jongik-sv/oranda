using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Rollback;
using Oranda.Core.Sessions;
using Oranda.Testing.Rollback;
using Oranda.Tibero.Rollback;
using Oranda.Tibero.Sessions;
using Oranda.Tibero.Tests.Driver;

namespace Oranda.Tibero.Tests.Rollback;

/// <summary>세션 API 커밋·롤백이 원장에 끝을 알린다(설계 7.3·7.4·13.5 끝, 가짜 드라이버).</summary>
[Collection("FakeDriver")]
public sealed class TiberoSessionRollbackTests : IDisposable
{
    private static readonly ServerInfo Server = new("6", "i", "h", null, "D", null, false, 7,
        new ServerCapabilities(false, false, true, false, false, Dbms.Tibero), DbUniqueName: "D", SessionUser: "U", Dbms: Dbms.Tibero);

    private readonly EventBus _bus = new();
    private readonly List<string> _order = [];
    private readonly List<TransactionEnded> _published = [];
    private readonly RecordingRollbackLedger _ledger = new();
    private readonly RecordingRollbackCapture _capture = new();

    public TiberoSessionRollbackTests()
    {
        FakeDriverHost.Reset();
        _bus.Subscribe<TransactionEnded>(_published, e =>
        {
            lock (_published)
            {
                _published.Add(e);
            }
        });
    }

    public void Dispose() => FakeDriverHost.Reset();

    [Fact]
    public async Task Commit_saves_the_open_group_first_then_reads_the_commit_point_and_ends_the_group_before_the_event()
    {
        _ledger.OpenState = new RollbackOpenState(1, 2, false, false);
        _capture.Point = new ChangePoint(42, "TSN", DateTimeOffset.UnixEpoch, "DBMS_FLASHBACK.GET_SYSTEM_CHANGE_NUMBER");
        FakeDriverHost.SetHandler(cmd =>
        {
            lock (_order)
            {
                _order.Add(cmd.CommandText + " after " + string.Join(",", _ledger.Calls));
            }

            return -1;
        });
        await using var session = NewSession();

        await session.CommitAsync(CancellationToken.None);

        Assert.Equal(["COMMIT after BeforeEndAsync"], _order);
        Assert.Equal(["ReadPoint"], _capture.Calls);
        var ended = Assert.Single(_ledger.Ends);
        Assert.Equal((TransactionEndKind.Commit, TransactionEndSource.SessionApi, 42L), (ended.Kind, ended.Source, ended.CommitPoint!.Number));
        Assert.Same(ended, Assert.Single(_published));
        Assert.False(session.CommandGate.IsHeld);
    }

    [Fact]
    public async Task Rollback_does_not_save_first_and_reads_no_point()
    {
        _ledger.OpenState = new RollbackOpenState(1, 2, false, false);
        FakeDriverHost.SetHandler(_ => -1);
        await using var session = NewSession();

        await session.RollbackAsync(CancellationToken.None);

        Assert.DoesNotContain(_ledger.Calls, c => c.StartsWith("BeforeEnd", StringComparison.Ordinal));
        Assert.Empty(_capture.Calls);
        var ended = Assert.Single(_ledger.Ends);
        Assert.Equal(TransactionEndKind.Rollback, ended.Kind);
        Assert.Null(ended.CommitPoint);
    }

    [Fact]
    public async Task Failed_commit_does_not_end_the_group_and_the_event_subscriber_can_use_the_session_gate()
    {
        _ledger.OpenState = new RollbackOpenState(1, 1, false, false);
        FakeDriverHost.SetHandler(cmd => cmd.CommandText == TiberoSession.CommitSql ? throw FakeDriverHost.ServerError(-12032, "TBR-12032: deadlock") : -1);
        await using var session = NewSession();

        await Assert.ThrowsAnyAsync<Exception>(() => session.CommitAsync(CancellationToken.None));
        Assert.Empty(_ledger.Ends);

        var gateFree = false;
        _bus.Subscribe<TransactionEnded>(this, _ => gateFree = session.CommandGate.TryEnter() && Release(session));
        await session.RollbackAsync(CancellationToken.None);
        Assert.True(gateFree); // 이벤트는 명령 잠금을 놓은 뒤에 난다
    }

    private static bool Release(TiberoSession session)
    {
        session.CommandGate.Release();
        return true;
    }

    private TiberoSession NewSession()
    {
        var driver = FakeDriverHost.Driver;
        var connection = driver.CreateConnection("Data Source=x");
        connection.Open();
        var profile = new ConnectionProfile("t", "u", "h:1/d", Dbms: Dbms.Tibero);
        return new TiberoSession(Guid.NewGuid(), profile, Server, SessionPurpose.Work, "D/-/U", connection, null, _bus, driver: driver,
            rollback: new TiberoRollbackHooks(_ledger, _capture, _bus));
    }
}
