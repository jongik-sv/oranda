using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Connection;
using Oranda.Core.Rollback;
using Oranda.Core.Sessions;
using Oranda.Oracle.Rollback;
using Oranda.Oracle.Sessions;
using Oranda.Testing;
using Oranda.Testing.Rollback;

namespace Oranda.Oracle.Tests.Rollback;

// 소유: rb-m2-oracle RB-10. 세션이 원장에 알리는 트랜잭션 끝(설계 7.3). DB 없음: 커밋·롤백 문장 왕복은 RB-12 통합 시험.
public sealed class OracleRollbackHooksTests : IAsyncDisposable
{
    private readonly OracleConnection _connection = new("User Id=u;Password=p;Data Source=localhost:1/x");
    private readonly RecordingRollbackLedger _ledger = new();
    private readonly RecordingRollbackCapture _capture = new();
    private readonly FakeEventBus _bus = new();
    private readonly OracleSession _session;

    public OracleRollbackHooksTests()
    {
        var info = new ServerInfo("23.26.0.0.0", "FREE", "localhost", "FREEPDB1", "FREE", "FREEPDB1", true, 1,
            new ServerCapabilities(false, true, true, false, false));
        _session = new OracleSession(Guid.NewGuid(), new ConnectionProfile("prod-example", "scott", "localhost:1521/FREEPDB1"), info,
            SessionPurpose.Work, "K", _connection, _bus, closeConnection: () => ValueTask.CompletedTask,
            rollback: new OracleRollbackHooks(_capture, _ledger, _bus));
    }

    [Fact]
    public async Task Commit_end_reads_the_commit_point_only_when_a_group_is_open()
    {
        var point = new ChangePoint(48_214_022, "SCN", DateTimeOffset.UnixEpoch, "DBMS_FLASHBACK.GET_SYSTEM_CHANGE_NUMBER");
        _capture.Point = point;

        await _session.Rollback.EndAsync(_session, TransactionEndKind.Commit, TransactionEndSource.SessionApi);
        Assert.DoesNotContain("ReadPoint", _capture.Calls); // 열린 묶음 없음: 시점 왕복을 하지 않는다

        _ledger.OpenState = new RollbackOpenState(1, 3, false, false);
        await _session.Rollback.EndAsync(_session, TransactionEndKind.Commit, TransactionEndSource.SessionApi);

        Assert.Equal(["EndAsync:Commit", "EndAsync:Commit"], _ledger.Calls);
        Assert.Equal(point, _ledger.Ends[1].CommitPoint);
        var published = _bus.PublishedOf<TransactionEnded>().Last();
        Assert.Equal((TransactionEndKind.Commit, TransactionEndSource.SessionApi, point), (published.Kind, published.Source, published.CommitPoint));
    }

    [Fact]
    public async Task Closing_a_session_with_an_open_group_ends_it_as_unknown()
    {
        _ledger.OpenState = new RollbackOpenState(1, 1, false, false);

        await _session.DisposeAsync();

        var end = Assert.Single(await EndsAsync(1));
        Assert.Equal((TransactionEndKind.Unknown, TransactionEndSource.SessionClosed, null), (end.Kind, end.Source, end.CommitPoint));
        Assert.DoesNotContain("ReadPoint", _capture.Calls); // 닫힌 연결로 시점을 읽지 않는다
    }

    [Fact]
    public async Task Closing_without_an_open_group_tells_nothing()
    {
        await _session.DisposeAsync();
        await Task.Delay(100); // 맡긴 알림이 있었다면 끝날 시간
        Assert.Empty(_ledger.Calls);
    }

    [Fact]
    public async Task Failed_commit_saves_before_and_ends_as_unknown_keeping_the_gate_free()
    {
        _ledger.OpenState = new RollbackOpenState(1, 2, false, false);

        // 열지 않은 연결: COMMIT 이 드라이버 단계에서 실패한다. 서버가 커밋했는지 모르는 경우와 같게 다룬다.
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => _session.CommitAsync(CancellationToken.None));

        Assert.Equal(["BeforeEndAsync", "EndAsync:Unknown"], _ledger.Calls);
        Assert.Contains("ProbeEnd", _capture.Calls); // 연결이 살아 있다고 보는 동안은 표본으로 판정을 시도한다
        Assert.True(_session.CommandGate.TryEnter()); // 잠금은 놓았다
        _session.CommandGate.Release();
    }

    [Fact]
    public async Task Failed_rollback_does_not_save_before_and_ends_as_unknown()
    {
        _ledger.OpenState = new RollbackOpenState(1, 2, false, false);

        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => _session.RollbackAsync(CancellationToken.None));

        Assert.Equal(["EndAsync:Unknown"], _ledger.Calls);
    }

    [Fact]
    public async Task Closing_inside_an_end_scope_reports_once_when_the_scope_ends()
    {
        _ledger.OpenState = new RollbackOpenState(1, 1, false, false);

        using (_session.BeginEndScope())
        {
            await _session.DisposeAsync(); // 커밋·DDL 을 보내는 중에 닫힘: 닫기는 범위에 맡긴다
            await Task.Delay(100);
            Assert.Empty(_ledger.Ends);
        }

        var end = Assert.Single(await EndsAsync(1));
        Assert.Equal((TransactionEndKind.Unknown, TransactionEndSource.SessionClosed), (end.Kind, end.Source));
    }

    [Fact]
    public async Task A_scope_that_reported_its_end_tells_nothing_more_on_close()
    {
        _ledger.OpenState = new RollbackOpenState(1, 1, false, false);

        using (var scope = _session.BeginEndScope())
        {
            scope.Reported();
            await _session.DisposeAsync();
        }

        await Task.Delay(100);
        Assert.Empty(_ledger.Ends);
    }

    /// <summary>스레드 풀에 맡긴 끝 알림을 기다린다(최대 5초).</summary>
    private async Task<IReadOnlyList<TransactionEnded>> EndsAsync(int count)
    {
        for (var i = 0; i < 100 && _ledger.Ends.Count < count; i++)
        {
            await Task.Delay(50);
        }

        return _ledger.Ends;
    }

    public async ValueTask DisposeAsync()
    {
        await _session.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
