using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Execution;
using Oranda.Core.Sessions;

namespace Oranda.Oracle.Sessions;

/// <summary>물리 연결 1개. Pooling=false 이므로 이 객체를 닫으면 서버 세션이 끝난다. 소유: TSK-03-03.</summary>
internal sealed class OracleSession : ISession
{
    /// <summary>연결이 끊겼음을 뜻하는 ORA 번호(CONN-11).</summary>
    private static readonly HashSet<int> BrokenCodes =
    [
        3113, 3114, 3135, 1012, 12170, 12537, 12541, 12543, 12547, 12571, 28547, 25402, 25408, 17002, 17008, 17410,
    ];

    private readonly IEventBus _events;
    private readonly Action<Guid>? _onDisposed;
    private readonly Func<ValueTask> _closeConnection;
    private readonly Timer? _keepAlive;
    private int _state = (int)SessionState.Idle;
    private volatile bool _pending;
    private int _disposed;

    public OracleSession(Guid id, ConnectionProfile profile, ServerInfo server, SessionPurpose purpose, string dbKey,
                         OracleConnection connection, IEventBus events, Action<Guid>? onDisposed = null,
                         Func<ValueTask>? closeConnection = null)
    {
        _onDisposed = onDisposed;
        _closeConnection = closeConnection ?? connection.DisposeAsync;
        Id = id;
        Profile = profile;
        Server = server;
        Purpose = purpose;
        DbKey = dbKey;
        Connection = connection;
        _events = events;
        connection.StateChange += OnConnectionStateChange;
        if (profile.KeepAliveSeconds > 0)
        {
            var period = TimeSpan.FromSeconds(profile.KeepAliveSeconds);
            _keepAlive = new Timer(_ => _ = KeepAliveTickAsync(), null, period, period);
        }
    }

    public Guid Id { get; }

    public ConnectionProfile Profile { get; }

    public ServerInfo Server { get; }

    public SessionPurpose Purpose { get; }

    public string DbKey { get; }

    public SessionState State => (SessionState)Volatile.Read(ref _state);

    public bool HasPendingTransaction => _pending;

    /// <summary>실행 서비스가 쓰는 실제 연결. 같은 어셈블리 안에서만 본다. 명령은 <see cref="CommandGate"/> 를 쥔 동안에만 보낸다.</summary>
    internal OracleConnection Connection { get; }

    /// <summary>이 연결에 한 번에 한 명령만 보내게 하는 잠금(D-041). 연결을 쓰는 모든 기능이 함께 쓴다.</summary>
    internal SessionCommandGate CommandGate { get; } = new();

    /// <summary>끊김 오류 번호인지(ORA-03113 등).</summary>
    public static bool IsBrokenError(int oraNumber) => BrokenCodes.Contains(oraNumber);

    /// <summary>실행 서비스가 DML 실행 뒤·커밋 뒤에 알린다. 바뀔 때만 이벤트를 낸다.</summary>
    internal void SetPending(bool pending)
    {
        if (_pending == pending)
        {
            return;
        }

        _pending = pending;
        _events.Publish(new TransactionStateChanged(Id, pending));
    }

    /// <summary>실행 서비스가 세션 사용 시작·끝을 알린다(Idle 과 Busy). 끊김·닫힘 상태는 덮지 않는다.</summary>
    internal void SetBusy(bool busy)
    {
        var from = busy ? SessionState.Idle : SessionState.Busy;
        var to = busy ? SessionState.Busy : SessionState.Idle;
        Interlocked.CompareExchange(ref _state, (int)to, (int)from);
    }

    /// <summary>실행 중 받은 예외가 끊김이면 세션을 Broken 으로 돌리고 알린다. 끊김이면 true.</summary>
    internal bool ReportFailure(OracleException ex)
    {
        if (!IsBrokenError(ex.Number))
        {
            return false;
        }

        MarkBroken(new DbError($"ORA-{ex.Number:D5}", ex.Message) { Number = ex.Number });
        return true;
    }

    public async Task CommitAsync(CancellationToken ct)
    {
        await ExecAsync("COMMIT", ct);
        SetPending(false);
    }

    public async Task RollbackAsync(CancellationToken ct)
    {
        await ExecAsync("ROLLBACK", ct);
        SetPending(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _keepAlive?.Dispose();
        Connection.StateChange -= OnConnectionStateChange;
        Volatile.Write(ref _state, (int)SessionState.Closed);

        // 상태·목록·이벤트를 먼저 반영한다. 연결 닫기는 서버가 응답하지 않으면 끝나지 않으므로(UI 스레드 정지 원인) 그 뒤에 떼어 낸다.
        _onDisposed?.Invoke(Id);
        _events.Publish(new SessionClosed(Id));
        // 명령 잠금은 Dispose 하지 않는다(SessionCommandGate): 닫는 중에도 진행 중인 작업·결과 집합 정리가 놓을 수 있어야 한다.
        await SessionCloser.RunAsync(_closeConnection, "세션 " + Id.ToString("N")[..8]).ConfigureAwait(false);
    }

    private async Task ExecAsync(string sql, CancellationToken ct)
    {
        await CommandGate.EnterAsync(ct);
        try
        {
            await using var cmd = Connection.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (OracleException ex) when (ReportFailure(ex))
        {
            throw;
        }
        finally
        {
            CommandGate.Release();
        }
    }

    private async Task KeepAliveTickAsync()
    {
        if (State != SessionState.Idle || !CommandGate.TryEnter())
        {
            return; // 작업 중(실행·메타데이터·모니터 등 어느 명령이든)이면 그 자체가 활동이다. 기다리지 않고 건너뛴다.
        }

        try
        {
            await using var cmd = Connection.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM dual";
            await cmd.ExecuteScalarAsync();
        }
        catch (OracleException ex)
        {
            ReportFailure(ex);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // 이미 닫는 중.
        }
        finally
        {
            CommandGate.Release();
        }
    }

    private void OnConnectionStateChange(object sender, System.Data.StateChangeEventArgs e)
    {
        if (e.CurrentState == System.Data.ConnectionState.Closed
            && State is SessionState.Idle or SessionState.Busy
            && Volatile.Read(ref _disposed) == 0)
        {
            MarkBroken(null);
        }
    }

    private void MarkBroken(DbError? error)
    {
        while (true)
        {
            var prev = Volatile.Read(ref _state);
            if ((SessionState)prev is SessionState.Broken or SessionState.Closed)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _state, (int)SessionState.Broken, prev) == prev)
            {
                break;
            }
        }

        _keepAlive?.Dispose();

        // 미커밋 변경은 서버가 롤백했다. 호출한 쪽이 손실을 알릴 수 있게 플래그를 싣는다.
        var hadPending = _pending;
        _pending = false;
        _events.Publish(new SessionBroken(Id, hadPending, error));
    }
}
