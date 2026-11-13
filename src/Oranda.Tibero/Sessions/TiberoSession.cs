using System.Data;
using System.Data.Common;
using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Execution;
using Oranda.Core.Execution.Providers;
using Oranda.Core.Rollback;
using Oranda.Core.Sessions;
using Oranda.Tibero.Driver;
using Oranda.Tibero.Execution;
using Oranda.Tibero.Rollback;

namespace Oranda.Tibero.Sessions;

/// <summary>
/// Tibero 물리 연결 1개(Oracle <c>OracleSession</c> 대응). Pooling=false 라서 이 객체를 닫으면 서버 세션이 끝난다(실측: 풀링이면 닫아도 서버 세션이 남는다).
/// 연결은 ADO.NET 기본 형식(<see cref="DbConnection"/>)으로만 들고, 드라이버 고유 형식은 <see cref="TiberoDriver"/> 어댑터만 쓴다.
/// <para>
/// T3~T5 계약: 같은 어셈블리의 기능(실행·결과·메타데이터·계획·모니터 등)은 <see cref="CommandGate"/> 를 쥔 동안에만 <see cref="Connection"/> 에
/// 명령을 보낸다(D-041, 한 연결에 한 명령). 명령은 <see cref="CreateCommand"/> 로 만든다(이름 바인드). 드라이버 예외를 받으면
/// <see cref="ReportFailure"/> 로 넘겨 끊김이면 세션을 Broken 으로 돌린다. DML 뒤·커밋 뒤에는 <see cref="SetPending"/>, 작업 시작·끝에는 <see cref="SetBusy"/>.
/// </para>
/// </summary>
internal sealed class TiberoSession : ISession
{
    internal const string KeepAliveSql = "SELECT 1 FROM DUAL";

    private static readonly TiberoErrorClassifier Classifier = new();

    private readonly IEventBus _events;
    private readonly Action<Guid>? _onDisposed;
    private readonly Func<ValueTask> _closeConnection;
    private readonly Timer? _keepAlive;
    private int _state = (int)SessionState.Idle;
    private int _endInFlight;
    private volatile bool _pending;
    private int _disposed;

    /// <param name="transaction">
    /// 연결을 연 쪽이 잡아 둔 트랜잭션(<see cref="DbConnection.BeginTransaction()"/>). Tibero 는 트랜잭션 객체가 없으면 문장마다 자동 커밋하고,
    /// 이 객체의 Commit·Rollback·Dispose 를 부르면 자동 커밋으로 돌아간다(tb-exec 실측) → 세션이 닫힐 때까지 두고 닫을 때만 정리한다(SAFE-04).
    /// </param>
    /// <param name="driver">연결을 만든 드라이버 어댑터. 서버 없는 단위 시험(가짜 연결)만 null.</param>
    /// <param name="rollback">되돌리기 원장에 트랜잭션 끝을 알리는 자리(설계 7.3·13.5). null 이면 끔 구현(알리지 않음).</param>
    public TiberoSession(Guid id, ConnectionProfile profile, ServerInfo server, SessionPurpose purpose, string dbKey,
                         DbConnection connection, DbTransaction? transaction, IEventBus events, Action<Guid>? onDisposed = null,
                         Func<ValueTask>? closeConnection = null, TiberoDriver? driver = null, TiberoRollbackHooks? rollback = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(events);
        Rollback = rollback ?? new TiberoRollbackHooks(null, null, events);
        Driver = driver;
        _onDisposed = onDisposed;
        _closeConnection = closeConnection ?? (() => CloseAsync(connection, transaction));
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

    /// <summary>실제 연결. 같은 어셈블리 안에서만 본다. 명령은 <see cref="CommandGate"/> 를 쥔 동안에만 보낸다.</summary>
    internal DbConnection Connection { get; }

    /// <summary>
    /// 이 세션 연결의 드라이버 어댑터(docs/design/tibero-driver-adapter.md 2장). 실행·결과 기능이 공급자 멤버(LOB·OUT 값·매개변수 종류)를 쓸 때 이것을 쓴다.
    /// 서버 없는 단위 시험의 가짜 세션만 null.
    /// </summary>
    internal TiberoDriver? Driver { get; }

    /// <summary>되돌리기 원장에 트랜잭션 끝을 알리는 자리(설계 7.3). 실행 서비스도 같은 것을 쓴다.</summary>
    internal TiberoRollbackHooks Rollback { get; }

    /// <summary>이 연결에 한 번에 한 명령만 보내게 하는 잠금(D-041). 연결을 쓰는 모든 기능이 함께 쓴다. 재진입하지 않는다.</summary>
    internal SessionCommandGate CommandGate { get; } = new();

    /// <summary>이 세션 연결의 명령(이름 바인드 켬). <see cref="CommandGate"/> 를 쥔 쪽이 만든다.</summary>
    internal DbCommand CreateCommand(string? sql = null)
    {
        var command = Connection.CreateCommand();
        Driver?.SetBindByName(command);
        if (sql is not null)
        {
            command.CommandText = sql;
        }

        return command;
    }

    /// <summary>
    /// 이 세션의 커밋·롤백 문장. 트랜잭션 객체 메서드(<see cref="DbTransaction.Commit"/> 등)를 쓰면 자동 커밋으로 돌아가므로
    /// 사용자·기능의 커밋·롤백은 늘 SQL 문장으로 보낸다(같은 연결을 쓰는 모든 경로의 규칙).
    /// </summary>
    internal const string CommitSql = "COMMIT";

    /// <inheritdoc cref="CommitSql"/>
    internal const string RollbackSql = "ROLLBACK";

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

    /// <summary>
    /// 실행 중 받은 예외가 끊김(<see cref="DbErrorKind.Disconnected"/>)이면 세션을 Broken 으로 돌리고 알린다. 끊김이면 true.
    /// <c>catch (Exception ex) when (session.ReportFailure(ex)) { throw; }</c> 처럼 쓴다.
    /// </summary>
    internal bool ReportFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (Classifier.FromException(exception) is not { Kind: DbErrorKind.Disconnected } error)
        {
            return false;
        }

        MarkBroken(error);
        return true;
    }

    /// <summary>
    /// 커밋(설계 7.3·7.4·13.5): 한 번의 명령 잠금 안에서 열린 묶음 저장(<see cref="TiberoRollbackHooks.BeforeEndAsync"/>) → <c>COMMIT</c> →
    /// 미커밋 표시 내림(잠금을 쥔 채 — 놓은 뒤에 지우면 그 사이 다른 DML 이 켠 플래그까지 지워진다) → 커밋 시점 읽기·원장 끝 처리(<see cref="TransactionEndSource.SessionApi"/>).
    /// COMMIT 이 실패하면 트랜잭션이 남았는지 확인해(<c>LOCAL_TRANSACTION_ID</c>) 끝났으면 「모름」, 남았으면 알리지 않는다. 끊겨서 확인할 수 없으면 「모름」.
    /// </summary>
    public Task CommitAsync(CancellationToken ct) => EndTransactionAsync(CommitSql, TransactionEndKind.Commit, ct);

    /// <summary>롤백. 롤백 앞에서는 묶음을 저장하지 않는다(7.4). 끝난 뒤 원장에 알린다. 실패했을 때는 커밋과 같은 규칙.</summary>
    public Task RollbackAsync(CancellationToken ct) => EndTransactionAsync(RollbackSql, TransactionEndKind.Rollback, ct);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        // 기다리지 않는다: 도는 중인 Keep Alive 는 명령 잠금이 막고, 응답 없는 서버면 그 틱이 끝나지 않을 수 있다.
        _keepAlive?.Dispose();

        Connection.StateChange -= OnConnectionStateChange;
        var before = (SessionState)Interlocked.Exchange(ref _state, (int)SessionState.Closed);
        if (before != SessionState.Broken) // 끊긴 세션은 끊길 때 이미 알렸다
        {
            // 닫으면 서버가 미커밋을 롤백한다(RB-13 실측)지만, 닫기 직전 실행 중이던 문장의 결과는 모른다 → 「모름」(7.3).
            EndOpenGroup(TransactionEndKind.Unknown, TransactionEndSource.SessionClosed);
        }

        // 상태·목록·이벤트를 먼저 반영한다. 연결 닫기는 서버가 응답하지 않으면 끝나지 않을 수 있으므로(UI 스레드 정지 원인) 그 뒤에 떼어 낸다.
        _onDisposed?.Invoke(Id);
        _events.Publish(new SessionClosed(Id));
        // 명령 잠금은 정리하지 않는다: 닫는 중에도 진행 중인 작업·결과 집합 정리가 놓을 수 있어야 한다.
        await TimedClose.RunAsync(_closeConnection, "Tibero 세션 " + Id.ToString("N")[..8]).ConfigureAwait(false);
    }

    private async Task EndTransactionAsync(string sql, TransactionEndKind kind, CancellationToken ct)
    {
        await CommandGate.EnterAsync(ct).ConfigureAwait(false);
        using var scope = BeginEndScope();
        try
        {
            if (kind == TransactionEndKind.Commit)
            {
                await Rollback.BeforeEndAsync(this, ct).ConfigureAwait(false);
            }

            try
            {
                await SendAsync(sql, ct).ConfigureAwait(false);
            }
            catch
            {
                if (await TransactionEndedAfterFailureAsync().ConfigureAwait(false))
                {
                    await Rollback.EndAsync(this, TransactionEndKind.Unknown, TransactionEndSource.SessionApi).ConfigureAwait(false);
                    scope.Reported();
                }

                throw;
            }

            SetPending(false);
            await Rollback.EndAsync(this, kind, TransactionEndSource.SessionApi).ConfigureAwait(false);
            scope.Reported();
        }
        finally
        {
            CommandGate.Release();
        }
    }

    /// <summary>명령 잠금을 쥔 채 문장 하나를 보낸다(드라이버 호출은 동기 → 스레드 풀). 끊김 오류면 세션을 Broken 으로 돌리고 다시 던진다.</summary>
    private async Task SendAsync(string sql, CancellationToken ct)
    {
        try
        {
            await Task.Run(
                async () =>
                {
                    await using var cmd = CreateCommand(sql);
                    await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                },
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ReportFailure(ex))
        {
            throw;
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
            return false;
        }

        if (State is SessionState.Broken or SessionState.Closed)
        {
            return true;
        }

        try
        {
            return await Task.Run(async () =>
            {
                await using var cmd = CreateCommand(TiberoExecutionService.TransactionIdSql);
                var id = await cmd.ExecuteScalarAsync(CancellationToken.None).ConfigureAwait(false);
                return Driver is { } driver ? driver.IsProviderNull(id) : id is null or DBNull;
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (TiberoErrorClassifier.IsDriverException(ex))
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
    /// 범위를 연 쪽이 끝을 알린다(끊김이 커밋 결과를 「롤백」으로 덮지 않게, 기록과 끝 알림이 엇갈리지 않게).
    /// 범위가 끝날 때까지 알리지 못했는데 세션이 끊겼으면 그때 <paramref name="ifBroken"/>(끝낼 수 있는 문장 = 「모름」, 그 밖의 쓰기 = 「롤백」)으로,
    /// 닫혔으면 「모름」으로 알린다.
    /// </summary>
    internal EndScope BeginEndScope(TransactionEndKind ifBroken = TransactionEndKind.Unknown)
    {
        Interlocked.Increment(ref _endInFlight);
        return new EndScope(this, ifBroken);
    }

    /// <summary><see cref="BeginEndScope"/> 의 범위.</summary>
    internal sealed class EndScope(TiberoSession session, TransactionEndKind ifBroken) : IDisposable
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

            if (Interlocked.Decrement(ref session._endInFlight) == 0 && !_reported)
            {
                switch (session.State)
                {
                    case SessionState.Broken:
                        session.EndOpenGroup(ifBroken, TransactionEndSource.SessionBroken);
                        break;
                    case SessionState.Closed: // 범위 안에서 닫혔다: 닫기가 건너뛴 알림을 여기서 한다
                        session.EndOpenGroup(TransactionEndKind.Unknown, TransactionEndSource.SessionClosed);
                        break;
                }
            }
        }
    }

    /// <summary>
    /// 세션이 끊기거나 닫힐 때 열린 묶음의 끝을 원장에 알린다(설계 7.3). 열린 묶음이 없거나 끝낼 수 있는 문장이 진행 중이면(그쪽이 알린다) 하지 않는다.
    /// 연결을 더 쓰지 않는 끝이라 판정·시점 읽기 없이(<see cref="TiberoRollbackHooks.EndAsync"/> 가 끊김·닫힘 출처는 건너뛴다)
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
                    await Rollback.EndAsync(this, kind, source).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // 원장의 동기 예외도 닫기·끊김 처리를 막지 않는다.
            }
        });
    }

    internal async Task KeepAliveTickAsync()
    {
        if (State != SessionState.Idle || !CommandGate.TryEnter())
        {
            return; // 작업 중(실행·메타데이터·모니터 등 어느 명령이든)이면 그 자체가 활동이다. 기다리지 않고 건너뛴다.
        }

        try
        {
            await using var cmd = CreateCommand(KeepAliveSql);
            await cmd.ExecuteScalarAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (TiberoErrorClassifier.IsDriverException(ex))
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

    /// <summary>
    /// 연결 닫기. 트랜잭션 정리(<see cref="DbTransaction.DisposeAsync"/> = 롤백 + 자동 커밋으로 돌아감)는 명령 잠금을 쥐었을 때만 하고,
    /// 연결을 닫을 때까지 잠금을 놓지 않는다 — 정리와 닫기 사이에 다른 명령이 자동 커밋 모드로 끼어들지 못하게.
    /// 잠금을 <see cref="CleanupGateWait"/> 안에 못 잡으면(명령이 아직 도는 중) 트랜잭션은 건드리지 않고 바로 닫는다(서버가 세션 끝에 롤백).
    /// </summary>
    private async ValueTask CloseAsync(DbConnection connection, DbTransaction? transaction)
    {
        var enter = CommandGate.EnterForCleanupAsync();
        bool held;
        using (var delayCancel = new CancellationTokenSource()) // 먼저 잡히면 Delay 타이머를 취소한다(CA2027)
        {
            held = await Task.WhenAny(enter, Task.Delay(CleanupGateWait, delayCancel.Token)).ConfigureAwait(false) == enter;
            await delayCancel.CancelAsync().ConfigureAwait(false);
        }

        if (!held)
        {
            // 늦게 잡히면 바로 놓는다(그 뒤의 정리 작업이 기다리다 막히지 않게).
            _ = enter.ContinueWith(_ => CommandGate.Release(), CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
        }

        try
        {
            // 닫힌 연결이면 건너뛴다: 서버 세션이 이미 없어 롤백할 것이 없고, 드라이버 TiberoTransaction.Dispose 가 NullReferenceException 을 던진다(실측 6.2.15).
            if (held && transaction is not null && connection.State == ConnectionState.Open)
            {
                await transaction.DisposeAsync().ConfigureAwait(false); // 미커밋 내용은 버려진다(닫기 = 롤백, Oracle 과 같음)
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NullReferenceException || TiberoErrorClassifier.IsDriverException(ex))
        {
            // 이미 끊긴 연결: 트랜잭션 정리는 실패해도 연결은 닫는다. NullReferenceException = 위 드라이버 결함(상태가 Open 인 채 끊긴 연결).
        }
        finally
        {
            try
            {
                await TiberoConnectionOpener.CloseAsync(connection).ConfigureAwait(false);
            }
            finally
            {
                if (held)
                {
                    CommandGate.Release();
                }
            }
        }
    }

    /// <summary>닫을 때 트랜잭션 정리를 위해 명령 잠금을 기다리는 최대 시간.</summary>
    internal static readonly TimeSpan CleanupGateWait = TimeSpan.FromSeconds(1);

    private void OnConnectionStateChange(object sender, StateChangeEventArgs e)
    {
        if (e.CurrentState == ConnectionState.Closed
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
