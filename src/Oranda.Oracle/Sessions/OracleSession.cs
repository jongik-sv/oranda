using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Execution;
using Oranda.Core.Execution.Providers;
using Oranda.Core.Rollback;
using Oranda.Core.Sessions;
using Oranda.Oracle.Rollback;

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
    private int _endInFlight;
    private int _disposed;

    public OracleSession(Guid id, ConnectionProfile profile, ServerInfo server, SessionPurpose purpose, string dbKey,
                         OracleConnection connection, IEventBus events, Action<Guid>? onDisposed = null,
                         Func<ValueTask>? closeConnection = null, OracleRollbackHooks? rollback = null)
    {
        Rollback = rollback ?? new OracleRollbackHooks(null, null, events);
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

    /// <summary>되돌리기 원장에 트랜잭션 끝을 알리는 자리(설계 7.3). 실행 서비스도 같은 것을 쓴다.</summary>
    internal OracleRollbackHooks Rollback { get; }

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

        MarkBroken(Execution.OracleErrorClassifier.ToDbError(ex));
        return true;
    }

    /// <summary>
    /// 커밋(설계 7.3·7.4·13.5): 한 번의 명령 잠금 안에서 열린 묶음 저장(<see cref="OracleRollbackHooks.BeforeEndAsync"/>) → <c>COMMIT</c> →
    /// 미커밋 표시 내림 → 커밋 시점 읽기·원장 끝 처리(<see cref="TransactionEndSource.SessionApi"/>).
    /// COMMIT 이 실패하면 트랜잭션이 남았는지 확인해(<c>LOCAL_TRANSACTION_ID</c>) 끝났으면(ORA-02091 등) 「모름」, 남았으면 알리지 않는다.
    /// 끊겨서 확인할 수 없으면 「모름」(서버가 커밋했는지 모른다).
    /// </summary>
    public Task CommitAsync(CancellationToken ct) => EndTransactionAsync("COMMIT", TransactionEndKind.Commit, ct);

    /// <summary>롤백. 롤백 앞에서는 묶음을 저장하지 않는다(7.4). 끝난 뒤 원장에 알린다. 실패했을 때는 커밋과 같은 규칙.</summary>
    public Task RollbackAsync(CancellationToken ct) => EndTransactionAsync("ROLLBACK", TransactionEndKind.Rollback, ct);

    private async Task EndTransactionAsync(string sql, TransactionEndKind kind, CancellationToken ct)
    {
        await CommandGate.EnterAsync(ct);
        using var scope = BeginEndScope();
        try
        {
            if (kind == TransactionEndKind.Commit)
            {
                await Rollback.BeforeEndAsync(this, ct);
            }

            try
            {
                await SendAsync(sql, ct);
            }
            catch
            {
                if (await TransactionEndedAfterFailureAsync())
                {
                    await Rollback.EndAsync(this, TransactionEndKind.Unknown, TransactionEndSource.SessionApi);
                    scope.Reported();
                }

                throw;
            }

            SetPending(false);
            await Rollback.EndAsync(this, kind, TransactionEndSource.SessionApi);
            scope.Reported();
        }
        finally
        {
            CommandGate.Release();
        }
    }

    /// <summary>
    /// 실패한 COMMIT·ROLLBACK 뒤 원장에 「모름」 끝을 알려야 하는지(명령 잠금 안). 열린 묶음이 없으면 거짓. 끊겼으면 참, 확인 질의가 실패해도 참.
    /// 트랜잭션이 남아 있으면 거짓(보내기 전 취소, 끝내지 않은 서버 오류: 원장은 열린 묶음을 그대로 둔다).
    /// </summary>
    private async Task<bool> TransactionEndedAfterFailureAsync()
    {
        if (Rollback.Ledger.Open(Id) is null)
        {
            return false; // 알릴 묶음이 없다
        }

        if (State is SessionState.Broken or SessionState.Closed)
        {
            return true;
        }

        try
        {
            await using var cmd = Connection.CreateCommand();
            cmd.CommandText = "SELECT DBMS_TRANSACTION.LOCAL_TRANSACTION_ID FROM dual";
            var id = await cmd.ExecuteScalarAsync(CancellationToken.None);
            return id is null or DBNull;
        }
        catch (OracleException ex)
        {
            ReportFailure(ex);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            return true;
        }
    }

    /// <summary>
    /// 트랜잭션을 끝낼 수 있는 문장을 보내는 동안 켜 두는 범위(명령 잠금 안). 그동안 끊기면 <see cref="MarkBroken"/> 은 원장에 따로 알리지 않고
    /// 범위를 연 쪽이 끝을 알린다(끊김이 커밋 결과를 「롤백」으로 덮지 않게). 닫기(<see cref="DisposeAsync"/>)도 같다.
    /// 범위가 끝날 때까지 알리지 못했는데 세션이 끊겼거나 닫혔으면 그때 「모름」(SessionBroken·SessionClosed)으로 알린다.
    /// </summary>
    internal EndScope BeginEndScope()
    {
        Interlocked.Increment(ref _endInFlight);
        return new EndScope(this);
    }

    /// <summary><see cref="BeginEndScope"/> 의 범위.</summary>
    internal sealed class EndScope(OracleSession session) : IDisposable
    {
        private bool _reported;
        private int _disposed;

        /// <summary>범위를 연 쪽이 원장에 끝을 알렸다.</summary>
        public void Reported() => _reported = true;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            if (Interlocked.Decrement(ref session._endInFlight) != 0 || _reported)
            {
                return;
            }

            // 범위 안에서 끊기거나 닫혔는데 끝을 알리지 못했다: 그동안 끊김·닫힘 처리는 알림을 범위에 맡겼으므로 여기서 「모름」으로 알린다.
            switch (session.State)
            {
                case SessionState.Broken:
                    session.EndOpenGroup(TransactionEndKind.Unknown, TransactionEndSource.SessionBroken);
                    break;
                case SessionState.Closed:
                    session.EndOpenGroup(TransactionEndKind.Unknown, TransactionEndSource.SessionClosed);
                    break;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _keepAlive?.Dispose();
        Connection.StateChange -= OnConnectionStateChange;
        var before = (SessionState)Interlocked.Exchange(ref _state, (int)SessionState.Closed);

        // 상태·목록·이벤트를 먼저 반영한다. 연결 닫기는 서버가 응답하지 않으면 끝나지 않으므로(UI 스레드 정지 원인) 그 뒤에 떼어 낸다.
        _onDisposed?.Invoke(Id);
        _events.Publish(new SessionClosed(Id));
        if (before != SessionState.Broken) // 끊긴 세션은 끊길 때 이미 알렸다
        {
            EndOpenGroup(TransactionEndKind.Unknown, TransactionEndSource.SessionClosed); // 닫으며 서버가 한 일(암묵 커밋·롤백)은 모른다(7.3)
        }

        // 명령 잠금은 Dispose 하지 않는다(SessionCommandGate): 닫는 중에도 진행 중인 작업·결과 집합 정리가 놓을 수 있어야 한다.
        await TimedClose.RunAsync(_closeConnection, "세션 " + Id.ToString("N")[..8]).ConfigureAwait(false);
    }

    /// <summary>명령 잠금을 쥔 채 문장 하나를 보낸다. 끊김 오류면 세션을 Broken 으로 돌리고 다시 던진다.</summary>
    private async Task SendAsync(string sql, CancellationToken ct)
    {
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
    }

    /// <summary>
    /// 세션이 끊기거나 닫힐 때 열린 묶음의 끝을 원장에 알린다(설계 7.3). 열린 묶음이 없거나 커밋·롤백이 진행 중이면(그쪽이 알린다) 하지 않는다.
    /// 연결을 더 쓰지 않는 끝이라 판정·시점 읽기 없이(<see cref="OracleRollbackHooks.EndAsync"/> 가 끊김·닫힘 출처는 건너뛴다)
    /// 스레드 풀에 맡긴다: 예외 필터·드라이버 콜백·닫기를 막지 않는다. 원장 실패는 삼킨다.
    /// </summary>
    private void EndOpenGroup(TransactionEndKind kind, TransactionEndSource source)
    {
        if (Volatile.Read(ref _endInFlight) > 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                if (Rollback.Ledger.Open(Id) is not null)
                {
                    await Rollback.EndAsync(this, kind, source);
                }
            }
            catch (Exception)
            {
                // 원장의 동기 예외도 닫기·끊김 처리를 막지 않는다.
            }
        });
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
        EndOpenGroup(TransactionEndKind.Rollback, TransactionEndSource.SessionBroken); // 미커밋 변경은 서버가 되돌렸다
    }
}
