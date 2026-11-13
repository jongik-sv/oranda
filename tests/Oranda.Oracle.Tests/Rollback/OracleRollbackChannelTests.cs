using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Execution;
using Oranda.Core.Rollback;
using Oranda.Core.Sessions;
using Oranda.Oracle.Rollback;
using Oranda.Oracle.Sessions;

namespace Oranda.Oracle.Tests.Rollback;

// 소유: rb-m2-oracle RB-10. 통로의 잠금 규칙(DB 없음). 실제 질의·오류 분류·RETURNING 은 RB-12 통합 시험.
public sealed class OracleRollbackChannelTests : IAsyncDisposable
{
    private static readonly IReadOnlyDictionary<string, BindValue> NoBinds = new Dictionary<string, BindValue>();
    private readonly OracleConnection _connection = new("User Id=u;Password=p;Data Source=localhost:1/x");
    private readonly OracleSession _session;

    public OracleRollbackChannelTests()
    {
        var info = new ServerInfo("23.26.0.0.0", "FREE", "localhost", "FREEPDB1", "FREE", "FREEPDB1", true, 1,
            new ServerCapabilities(false, true, true, false, false));
        _session = new OracleSession(Guid.NewGuid(), new ConnectionProfile("dev-example", "scott", "localhost:1521/FREEPDB1"), info,
            SessionPurpose.Work, "K", _connection, new NullBus(), closeConnection: () => ValueTask.CompletedTask);
    }

    [Fact]
    public async Task Gated_channel_waits_for_the_command_gate_and_gives_up_on_cancel()
    {
        await _session.CommandGate.EnterAsync(CancellationToken.None); // 다른 기능이 연결을 쓰는 중
        using var cts = new CancellationTokenSource();
        var query = OracleRollbackChannel.Gated(_session).QueryAsync("SELECT 1 FROM DUAL", NoBinds, 1, cts.Token);
        await Task.Delay(50);
        Assert.False(query.IsCompleted); // 잠금을 기다린다

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => query);
        Assert.False(_session.CommandGate.TryEnter()); // 남이 쥔 잠금은 그대로
        _session.CommandGate.Release();
    }

    [Fact]
    public async Task Gated_channel_goes_on_once_the_gate_is_released_and_releases_it_after()
    {
        await _session.CommandGate.EnterAsync(CancellationToken.None);
        var query = OracleRollbackChannel.Gated(_session).QueryAsync("SELECT 1 FROM DUAL", NoBinds, 1, CancellationToken.None);
        Assert.False(query.IsCompleted);

        _session.CommandGate.Release();
        // 열지 않은 연결: 잠금을 얻은 뒤 드라이버 단계에서 실패하고, 그 실패도 통로 계약 예외로 온다.
        var error = await Assert.ThrowsAsync<RollbackChannelException>(() => query);
        Assert.Equal(OracleRollbackChannel.ClientErrorCode, error.Error.Code);
        Assert.True(_session.CommandGate.TryEnter()); // 통로가 잡은 잠금은 놓았다
        _session.CommandGate.Release();
    }

    [Fact]
    public async Task Locked_channel_does_not_wait_for_the_gate_the_caller_holds()
    {
        await _session.CommandGate.EnterAsync(CancellationToken.None); // 호출자(실행 엔진)가 쥔 잠금
        var channel = OracleRollbackChannel.Locked(_session);

        var send = channel.ExecuteAsync("SAVEPOINT ORANDA$RB", CancellationToken.None);
        var finished = await Task.WhenAny(send, Task.Delay(TimeSpan.FromSeconds(5)));

        Assert.Same(send, finished); // 잠금을 다시 기다리지 않고 바로 드라이버로 간다(열지 않은 연결이라 실패)
        await Assert.ThrowsAsync<RollbackChannelException>(() => send);
        _session.CommandGate.Release();
    }

    [Theory]
    [InlineData(100, 0, 100)]           // maxRows 0 → 넘침 확인 한 행
    [InlineData(100, 1, 200)]           // 시점 읽기: 한 행 + 넘침 확인 한 행
    [InlineData(100, 100_000, 100_000)] // 1000행
    [InlineData(40_000, 100_000, 8_360_000)] // 넓은 행은 8MB 안(209행)
    [InlineData(20_000_000, 10, 20_000_000)] // 한 행이 상한보다 커도 한 행은 받는다
    public void Fetch_size_is_bounded_by_rows_and_bytes(long rowSize, int maxRows, long expected) =>
        Assert.Equal(expected, OracleRollbackChannel.FetchSizeFor(rowSize, maxRows));

    public async ValueTask DisposeAsync()
    {
        await _session.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private sealed class NullBus : IEventBus
    {
        public void Publish<T>(T e) where T : OrandaEvent
        {
        }

        public IDisposable Subscribe<T>(object owner, Action<T> handler) where T : OrandaEvent => new Nothing();

        private sealed class Nothing : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}
