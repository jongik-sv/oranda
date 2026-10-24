using System.Globalization;
using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Monitoring;
using Oranda.Core.Plans;
using Oranda.Core.Security;
using Oranda.Core.Sessions;
using Oranda.Oracle.Connection;
using Oranda.Oracle.Execution;
using Oranda.Oracle.IntegrationTests.Execution;
using Oranda.Oracle.IntegrationTests.Integration;
using Oranda.Oracle.Monitoring;
using Oranda.Oracle.Sessions;

namespace Oranda.Oracle.IntegrationTests.Monitoring;

// 소유: TSK-05-02. Lock 모니터 조회.

/// <summary>DB 없이 도는 부분: 행 값 SELECT 식별자, 유형 바인드, 값 꼴, 행 변환.</summary>
public class LockMonitorLogicTests
{
    [Fact]
    public void Locked_row_select_quotes_dictionary_names_and_binds_rowid()
    {
        Assert.Equal("SELECT * FROM \"SCOTT\".\"Emp Table\" WHERE ROWID = CHARTOROWID(:r)",
            OracleMonitorService.LockedRowSelect("SCOTT", "Emp Table"));
    }

    [Theory]
    [InlineData("SCOTT", "EMP\" WHERE 1=1 --")]
    [InlineData("SC\"OTT", "EMP")]
    [InlineData("", "EMP")]
    [InlineData("SCOTT", "")]
    [InlineData("SCOTT", "EMP\0")]
    public void Locked_row_select_refuses_names_that_cannot_be_quoted(string owner, string table)
    {
        Assert.Null(OracleMonitorService.LockedRowSelect(owner, table));
    }

    [Fact]
    public void Lock_types_bind_defaults_to_tm_tx_ul_and_drops_non_alphanumerics()
    {
        Assert.Equal(",TM,TX,UL,", OracleMonitorService.LockTypesBind([]));
        Assert.Equal(",TM,TX,UL,", OracleMonitorService.LockTypesBind(["", " ", ",,"]));
        Assert.Equal(",TM,UL,", OracleMonitorService.LockTypesBind(["tm", " UL ", "TM"]));
        Assert.Equal(",TX,", OracleMonitorService.LockTypesBind(["T'X"]));
    }

    [Fact]
    public void Locked_values_format_dates_binary_and_long_text()
    {
        Assert.Null(OracleMonitorService.FormatLockedValue(null));
        Assert.Null(OracleMonitorService.FormatLockedValue(DBNull.Value));
        Assert.Equal("2026-10-24 13:05:09", OracleMonitorService.FormatLockedValue(new DateTime(2026, 10, 24, 13, 5, 9)));
        Assert.Equal("2026-10-24 13:05:09.5", OracleMonitorService.FormatLockedValue(new DateTime(2026, 10, 24, 13, 5, 9, 500)));
        Assert.Equal("1.5", OracleMonitorService.FormatLockedValue(1.5m));
        Assert.Equal("0x0A0B", OracleMonitorService.FormatLockedValue(new byte[] { 10, 11 }));

        var bytes = new byte[100];
        Assert.EndsWith("… (100 bytes)", OracleMonitorService.FormatLockedValue(bytes), StringComparison.Ordinal);
        var text = OracleMonitorService.FormatLockedValue(new string('x', OracleMonitorService.LockedValueMaxLength + 10));
        Assert.Equal(OracleMonitorService.LockedValueMaxLength + 1, text!.Length);
    }

    [Fact]
    public void Lock_row_maps_object_transaction_and_blocker()
    {
        var start = new DateTime(2026, 10, 24, 9, 0, 0);
        object?[] r =
        [
            2, 30L, 300L, "APP", "pc1", "app.exe", "TX", 65537L, 99L, 0, 6, 12L, 0,
            null, null, start, 4L, "enq: TX - row lock contention", 12L, "sqlA", "sqlB", 1, 10L, 100L,
        ];

        var row = OracleMonitorService.ToLockRow(r);

        Assert.Equal(new SessionKey(2, 30, 300), row.Session);
        Assert.Equal("TX", row.LockType);
        Assert.Equal(0, row.HeldMode);
        Assert.Equal(6, row.RequestedMode);
        Assert.False(row.IsBlocking);
        Assert.Null(row.ObjectName);
        Assert.Equal(new DateTimeOffset(start, TimeSpan.Zero), row.TransactionStart);
        Assert.Equal(new SessionKey(1, 10, 100), row.BlockingSession);
        Assert.Null(row.Status); // 열 24 가 없는 옛 결과
        Assert.Equal("INACTIVE", OracleMonitorService.ToLockRow([.. r, "INACTIVE"]).Status);

        r[6] = "TM";
        r[12] = 2; // RAC 의 전역 막음
        r[13] = "APP";
        r[14] = "ORDERS";
        r[22] = null;
        var tm = OracleMonitorService.ToLockRow(r);
        Assert.True(tm.IsBlocking);
        Assert.Equal("APP.ORDERS", tm.ObjectName);
        Assert.Null(tm.BlockingSession);
    }

    [Fact]
    public void Deadlock_event_splits_message_lines_and_keeps_utc_time()
    {
        var at = new DateTime(2026, 10, 24, 1, 2, 3);
        var e = OracleMonitorService.ToDeadlockEvent([at, 2, "ORA-00060: Deadlock detected.\r\n\r\n  More info in file x.trc.\n"]);

        Assert.Equal(new DateTimeOffset(at, TimeSpan.Zero), e.At);
        Assert.Equal(2, e.InstanceId);
        Assert.Equal(["ORA-00060: Deadlock detected.", "More info in file x.trc."], e.MessageLines);
    }
}

/// <summary>26ai 컨테이너에서 TX 행 잠금·TM 잠금·잠긴 행·영향 순위·방치 트랜잭션·데드락·권한 부족을 확인한다(세션 모니터 시험과 같은 구성).</summary>
[Trait("Category", "Integration")]
[Collection(OracleFreeShared.Name)]
public sealed class LockMonitorTests : IAsyncLifetime
{
    private readonly OracleFreeFixture _db;
    private readonly ExecBus _bus = new();
    private readonly ExecInteraction _ui = new();
    private readonly List<OracleConnection> _raw = [];
    private OracleSessionManager _manager = null!;

    public LockMonitorTests(OracleFreeFixture db) => _db = db;

    public Task InitializeAsync()
    {
        _manager = new OracleSessionManager(_bus);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var c in _raw)
        {
            try
            {
                await c.DisposeAsync();
            }
            catch (OracleException)
            {
                // 이미 끊긴 연결.
            }
        }

        foreach (var s in _manager.Sessions)
        {
            await s.DisposeAsync();
        }
    }

    private OracleMonitorService Service() =>
        new(new OracleExecutionService(new ExecutionPolicy(), _ui, _bus, null, pageSize: 500));

    private async Task<ISession> OpenMonitorAsync(string user, string password)
    {
        var profile = new ConnectionProfile("free-" + user, user, _db.DataSource);
        using var cred = new SecureCredential(password);
        return await _manager.OpenAsync(profile, cred, SessionPurpose.Monitor, default);
    }

    private async Task<OracleConnection> RawAsync(string user = "system", string? password = null, bool sysdba = false)
    {
        var profile = new ConnectionProfile("free-" + user, user, _db.DataSource);
        var cs = new OracleConnectionStringBuilder(profile.BuildConnectionString(password ?? _db.SystemPassword));
        if (sysdba)
        {
            cs.DBAPrivilege = "SYSDBA";
        }

        var conn = new OracleConnection(cs.ConnectionString);
        await conn.OpenAsync();
        _raw.Add(conn);
        return conn;
    }

    internal static async Task ExecAsync(OracleConnection c, string sql, OracleTransaction? tx = null, params (string Name, object Value)[] binds)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        cmd.BindByName = true;
        foreach (var (name, value) in binds)
        {
            cmd.Parameters.Add(new OracleParameter(name, value));
        }

        await cmd.ExecuteNonQueryAsync();
    }

    internal static async Task<string> ScalarAsync(OracleConnection c, string sql)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToString(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture)!;
    }

    internal static async Task<SessionKey> KeyOfAsync(OracleConnection c)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT SYS_CONTEXT('USERENV','INSTANCE'), s.sid, s.serial# FROM v$session s WHERE s.sid = SYS_CONTEXT('USERENV','SID')";
        await using var r = await cmd.ExecuteReaderAsync();
        Assert.True(await r.ReadAsync());
        return new SessionKey(
            Convert.ToInt32(r.GetValue(0), CultureInfo.InvariantCulture),
            Convert.ToInt64(r.GetValue(1), CultureInfo.InvariantCulture),
            Convert.ToInt64(r.GetValue(2), CultureInfo.InvariantCulture));
    }

    /// <summary>대기가 서버에 보일 때까지(최대 20초) 잠금 목록을 다시 읽는다.</summary>
    private static async Task<LockSnapshot> WaitForAsync(Func<Task<LockSnapshot>> read, Func<LockSnapshot, bool> done)
    {
        var snap = await read();
        for (var i = 0; i < 40 && !done(snap); i++)
        {
            await Task.Delay(500);
            snap = await read();
        }

        Assert.True(done(snap), "대기가 잠금 목록에 나타나지 않았습니다: "
            + string.Join("; ", snap.Rows.Select(r => $"{r.Session} {r.LockType} {r.HeldMode}/{r.RequestedMode} {r.ObjectName} {r.Event}")));
        return snap;
    }

    /// <summary>서버 시각(DATE)을 서버 시간대로 UTC 로 바꾼 값이 지금과 가까운지(서버·PC 시간대가 달라도).</summary>
    internal static void AssertRecentUtc(DateTimeOffset? at, int minutes = 10)
    {
        Assert.NotNull(at);
        var now = DateTimeOffset.UtcNow;
        Assert.InRange(at.Value, now.AddMinutes(-minutes), now.AddMinutes(2));
    }

    private static string NewTable(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    [Fact]
    public async Task Row_lock_shows_tx_and_tm_rows_locked_row_binds_and_holder_impact()
    {
        var table = NewTable("LK_ROW_");
        var setup = await RawAsync();
        await ExecAsync(setup, $"CREATE TABLE {table} (id NUMBER PRIMARY KEY, v NUMBER, name VARCHAR2(30), note CLOB, d DATE)");
        await ExecAsync(setup, $"INSERT INTO {table} VALUES (1, 0, 'first', 'clob text', DATE '2026-10-24')");
        await ExecAsync(setup, $"INSERT INTO {table} VALUES (2, 0, 'second', NULL, NULL)");
        var rowId = await ScalarAsync(setup, $"SELECT ROWIDTOCHAR(ROWID) FROM {table} WHERE id = 1");
        var rowAddress = await ScalarAsync(setup,
            $"SELECT DBMS_ROWID.ROWID_RELATIVE_FNO(ROWID) || ',' || DBMS_ROWID.ROWID_BLOCK_NUMBER(ROWID) || ',' || DBMS_ROWID.ROWID_ROW_NUMBER(ROWID) FROM {table} WHERE id = 1");

        var holder = await RawAsync();
        var holderKey = await KeyOfAsync(holder);
        var holderTx = holder.BeginTransaction();
        await ExecAsync(holder, $"UPDATE {table} SET v = 1 WHERE id = 1", holderTx);

        var waiter = await RawAsync();
        var waiterKey = await KeyOfAsync(waiter);
        var waiterTx = waiter.BeginTransaction();
        var waiting = ExecAsync(waiter, $"UPDATE {table} SET v = :v WHERE id = :id", waiterTx, ("v", 2), ("id", 1));

        var waiter2 = await RawAsync();
        var waiter2Key = await KeyOfAsync(waiter2);
        var waiter2Tx = waiter2.BeginTransaction();
        var waiting2 = ExecAsync(waiter2, $"UPDATE {table} SET v = 3 WHERE id = 1", waiter2Tx);

        try
        {
            var monitor = await OpenMonitorAsync("system", _db.SystemPassword);
            var service = Service();
            var all = new LockFilter { Username = "system" };

            var snap = await WaitForAsync(() => service.GetLocksAsync(monitor, all, default),
                s => s.Rows.Count(r => r.LockType == "TX" && r.RequestedMode == 6 && (r.Session == waiterKey || r.Session == waiter2Key)) == 2);

            // 보유자: TX X 보유·막음, undo 사용, TM RX 에 표 이름.
            var holderTxRow = Assert.Single(snap.Rows, r => r.Session == holderKey && r.LockType == "TX");
            Assert.Equal(6, holderTxRow.HeldMode);
            Assert.Equal(0, holderTxRow.RequestedMode);
            Assert.True(holderTxRow.IsBlocking);
            Assert.NotNull(holderTxRow.TransactionStart);
            Assert.True(holderTxRow.UndoBlocks >= 1);
            Assert.Equal("SYSTEM", holderTxRow.Username);
            Assert.Equal("INACTIVE", holderTxRow.Status); // TSK-05-04: gv$session.status
            AssertRecentUtc(holderTxRow.TransactionStart);
            var holderTm = Assert.Single(snap.Rows, r => r.Session == holderKey && r.LockType == "TM");
            Assert.Equal(3, holderTm.HeldMode);
            Assert.Equal("SYSTEM." + table, holderTm.ObjectName);
            Assert.Null(holderTm.TransactionStart);

            // 대기자: 같은 TX 자원(id1·id2)을 X 로 요청, 보유자의 트랜잭션 시작을 xid 로 찾고, 막는 세션 = 보유자(serial# 까지).
            var waiterRow = Assert.Single(snap.Rows, r => r.Session == waiterKey && r.LockType == "TX" && r.RequestedMode == 6);
            Assert.Equal(0, waiterRow.HeldMode);
            Assert.Equal(holderTxRow.Id1, waiterRow.Id1);
            Assert.Equal(holderTxRow.Id2, waiterRow.Id2);
            Assert.Equal(holderTxRow.TransactionStart, waiterRow.TransactionStart);
            Assert.Equal(holderKey, waiterRow.BlockingSession);
            Assert.Equal("enq: TX - row lock contention", waiterRow.Event);
            Assert.NotNull(waiterRow.SqlId);
            Assert.True(waiterRow.SecondsInWait >= 0);
            Assert.Equal("ACTIVE", waiterRow.Status);

            // 대기 트리: 보유자 뿌리 아래 두 대기자.
            var root = Assert.Single(snap.WaitTree.Roots, n => n.Session == holderKey);
            Assert.Equal(new[] { waiterKey, waiter2Key }.OrderBy(k => k.Sid), root.Waiters.Select(w => w.Session).OrderBy(k => k.Sid));
            Assert.Equal("INACTIVE", root.Status);
            Assert.All(root.Waiters, w => Assert.Equal("ACTIVE", w.Status));
            Assert.False(waiting.IsCompleted);

            // 거르기.
            var blockingOnly = await service.GetLocksAsync(monitor, all with { WaitingOrBlockingOnly = true }, default);
            Assert.Contains(blockingOnly.Rows, r => r.Session == holderKey && r.LockType == "TX");
            Assert.DoesNotContain(blockingOnly.Rows, r => r.Session == holderKey && r.LockType == "TM"); // RX 끼리는 막지 않는다
            Assert.All(blockingOnly.Rows, r => Assert.True(r.RequestedMode > 0 || r.IsBlocking));

            var tmOnly = await service.GetLocksAsync(monitor, all with { Types = ["TM"] }, default);
            Assert.All(tmOnly.Rows, r => Assert.Equal("TM", r.LockType));
            Assert.Contains(tmOnly.Rows, r => r.Session == waiterKey && r.ObjectName == "SYSTEM." + table);

            var otherSchema = await service.GetLocksAsync(monitor, all with { Schema = "NO_SUCH_SCHEMA" }, default);
            Assert.DoesNotContain(otherSchema.Rows, r => r.LockType == "TM");
            Assert.Contains(otherSchema.Rows, r => r.Session == waiterKey && r.LockType == "TX");
            var sameSchema = await service.GetLocksAsync(monitor, all with { Schema = "system" }, default);
            Assert.Contains(sameSchema.Rows, r => r.Session == holderKey && r.LockType == "TM");

            var longWaits = await service.GetLocksAsync(monitor, all with { MinWaitSeconds = 3600 }, default);
            Assert.DoesNotContain(longWaits.Rows, r => r.RequestedMode > 0);
            Assert.Contains(longWaits.Rows, r => r.Session == holderKey && r.LockType == "TX");

            Assert.Empty((await service.GetLocksAsync(monitor, all with { InstanceId = 99 }, default)).Rows);
            Assert.Empty((await service.GetLocksAsync(monitor, new LockFilter { Username = "no_such_user" }, default)).Rows);
            Assert.Empty((await service.GetLocksAsync(monitor, all with { Types = ["MR"] }, default)).Rows); // 백그라운드 잠금은 항상 뺀다

            // 영향 순위: 보유자 하나에 대기자 둘.
            var impact = await service.GetBlockerImpactAsync(monitor, default);
            var mine = Assert.Single(impact, i => i.Holder == holderKey);
            Assert.Equal(2, mine.WaiterCount);
            Assert.Equal("INACTIVE", mine.Status);
            Assert.InRange(mine.MaxWaitSeconds, 0, mine.TotalWaitSeconds);
            Assert.DoesNotContain(impact, i => i.Holder == waiterKey);
            for (var i = 1; i < impact.Count; i++)
            {
                Assert.True(impact[i - 1].TotalWaitSeconds >= impact[i].TotalWaitSeconds);
            }

            // 잠긴 행: rowid 가 실제 행과 같고 값은 커밋된 값(보유자의 바뀐 값이 아님).
            var locked = await service.GetLockedRowAsync(monitor, waiterKey, default);
            Assert.NotNull(locked);
            Assert.Equal(rowId, locked.RowId);
            Assert.Equal("SYSTEM", locked.Owner);
            Assert.Equal(table, locked.Table);
            Assert.Equal(["ID", "V", "NAME", "NOTE", "D"], locked.ColumnNames);
            Assert.Equal(["1", "0", "first", "clob text", "2026-10-24 00:00:00"], locked.Values);
            // 블록·행은 DBMS_ROWID 와 같다. 파일은 v$datafile.rfile#(bigfile 표 공간이면 1024, DBMS_ROWID 는 0 을 돌려준다).
            Assert.EndsWith(string.Create(CultureInfo.InvariantCulture, $",{locked.BlockNumber},{locked.RowNumber}"), rowAddress, StringComparison.Ordinal);
            Assert.True(locked.FileNumber >= 0);
            Assert.Null(await service.GetLockedRowAsync(monitor, holderKey, default)); // 기다리지 않는 세션
            Assert.Null(await service.GetLockedRowAsync(monitor, new SessionKey(1, 999_999, 1), default));

            // 대기 바인드: 세션으로, 커서로.
            var bySession = await WaitForBindsAsync(() => service.GetWaitingBindsAsync(monitor, new WaitingBindTarget { Session = waiterKey }, default));
            Assert.Equal(["V", "ID"], bySession.Select(b => b.Name.TrimStart(':').ToUpperInvariant()));
            Assert.Equal("1", Assert.Single(bySession, b => b.Position == 2).Value);
            Assert.All(bySession.Where(b => b.Value is not null), b => AssertRecentUtc(b.CapturedAt, minutes: 60)); // 캡처된 값만 시각이 있다
            var byCursor = await service.GetWaitingBindsAsync(monitor,
                new WaitingBindTarget { Cursor = new CursorRef(waiterRow.SqlId!, null, waiterKey.InstanceId) }, default);
            Assert.Equal(bySession.Select(b => b.Name), byCursor.Select(b => b.Name));
            Assert.Empty(await service.GetWaitingBindsAsync(monitor, new WaitingBindTarget(), default));
            Assert.Empty(await service.GetWaitingBindsAsync(monitor, new WaitingBindTarget { Cursor = new CursorRef("nosuchsqlid00") }, default));

            // DDL 잠금은 뷰가 없어도(catblock 미실행) 오류 없이 목록.
            _ = await service.GetDdlLocksAsync(monitor, default);

            // 되돌림: 보유자가 롤백하면 차례로 풀린다.
            await holderTx.RollbackAsync();
            await waiting.WaitAsync(TimeSpan.FromSeconds(30));
            await waiterTx.RollbackAsync();
            await waiting2.WaitAsync(TimeSpan.FromSeconds(30));
            await waiter2Tx.RollbackAsync();

            var after = await service.GetLocksAsync(monitor, all, default);
            Assert.DoesNotContain(after.Rows, r => r.Session == holderKey || r.Session == waiterKey || r.Session == waiter2Key);
            Assert.DoesNotContain(after.WaitTree.Roots, n => n.Session == holderKey);
            Assert.DoesNotContain(await service.GetBlockerImpactAsync(monitor, default), i => i.Holder == holderKey);
            Assert.Null(await service.GetLockedRowAsync(monitor, waiterKey, default));
        }
        finally
        {
            foreach (var tx in new[] { holderTx, waiterTx, waiter2Tx })
            {
                try
                {
                    await tx.RollbackAsync();
                }
                catch (Exception ex) when (ex is InvalidOperationException or OracleException)
                {
                    // 이미 끝난 트랜잭션.
                }
            }

            await ExecAsync(setup, $"DROP TABLE {table} PURGE");
        }
    }

    private static async Task<IReadOnlyList<WaitingBind>> WaitForBindsAsync(Func<Task<IReadOnlyList<WaitingBind>>> read)
    {
        var binds = await read();
        for (var i = 0; i < 20 && binds.Count < 2; i++)
        {
            await Task.Delay(500);
            binds = await read();
        }

        return binds;
    }

    [Fact]
    public async Task Lock_table_shows_tm_exclusive_holder_and_share_waiter_without_locked_row()
    {
        var table = NewTable("LK_TM_");
        var setup = await RawAsync();
        await ExecAsync(setup, $"CREATE TABLE {table} (id NUMBER)");

        var holder = await RawAsync();
        var holderKey = await KeyOfAsync(holder);
        var holderTx = holder.BeginTransaction();
        await ExecAsync(holder, $"LOCK TABLE {table} IN EXCLUSIVE MODE", holderTx);

        var waiter = await RawAsync();
        var waiterKey = await KeyOfAsync(waiter);
        var waiterTx = waiter.BeginTransaction();
        var waiting = ExecAsync(waiter, $"LOCK TABLE {table} IN SHARE MODE", waiterTx);

        try
        {
            var monitor = await OpenMonitorAsync("system", _db.SystemPassword);
            var service = Service();
            var filter = new LockFilter { Types = ["TM"], WaitingOrBlockingOnly = true };

            var snap = await WaitForAsync(() => service.GetLocksAsync(monitor, filter, default),
                s => s.Rows.Any(r => r.Session == waiterKey && r.RequestedMode == 4));

            var held = Assert.Single(snap.Rows, r => r.Session == holderKey);
            Assert.Equal(6, held.HeldMode);
            Assert.True(held.IsBlocking);
            Assert.Equal("SYSTEM." + table, held.ObjectName);
            var wait = Assert.Single(snap.Rows, r => r.Session == waiterKey);
            Assert.Equal(0, wait.HeldMode);
            Assert.Equal(4, wait.RequestedMode);
            Assert.Equal(held.Id1, wait.Id1);
            Assert.Equal("enq: TM - contention", wait.Event);
            Assert.Equal(holderKey, wait.BlockingSession);

            Assert.Null(await service.GetLockedRowAsync(monitor, waiterKey, default)); // 행 잠금 대기가 아님
            Assert.Equal(1, Assert.Single(await service.GetBlockerImpactAsync(monitor, default), i => i.Holder == holderKey).WaiterCount);

            await holderTx.RollbackAsync();
            await waiting.WaitAsync(TimeSpan.FromSeconds(30));
            await waiterTx.RollbackAsync();
            Assert.DoesNotContain((await service.GetLocksAsync(monitor, filter, default)).Rows, r => r.Session == holderKey || r.Session == waiterKey);
        }
        finally
        {
            foreach (var tx in new[] { holderTx, waiterTx })
            {
                try
                {
                    await tx.RollbackAsync();
                }
                catch (Exception ex) when (ex is InvalidOperationException or OracleException)
                {
                    // 이미 끝난 트랜잭션.
                }
            }

            await ExecAsync(setup, $"DROP TABLE {table} PURGE");
        }
    }

    [Fact]
    public async Task Idle_transaction_is_listed_with_idle_time_undo_and_blocked_count()
    {
        var table = NewTable("LK_IDLE_");
        var setup = await RawAsync();
        await ExecAsync(setup, $"CREATE TABLE {table} (id NUMBER PRIMARY KEY, v NUMBER)");
        await ExecAsync(setup, $"INSERT INTO {table} VALUES (1, 0)");

        var idle = await RawAsync();
        var idleKey = await KeyOfAsync(idle);
        var idleTx = idle.BeginTransaction();
        await ExecAsync(idle, $"UPDATE {table} SET v = 1 WHERE id = 1", idleTx);

        var waiter = await RawAsync();
        var waiterTx = waiter.BeginTransaction();
        var waiting = ExecAsync(waiter, $"UPDATE {table} SET v = 2 WHERE id = 1", waiterTx);

        try
        {
            var monitor = await OpenMonitorAsync("system", _db.SystemPassword);
            var service = Service();
            await Task.Delay(TimeSpan.FromSeconds(2.5));

            IdleTransaction? row = null;
            for (var i = 0; i < 20 && row is not { BlockedCount: 1 }; i++)
            {
                row = (await service.GetIdleTransactionsAsync(monitor, 2, default)).SingleOrDefault(t => t.Session == idleKey);
                if (row is not { BlockedCount: 1 })
                {
                    await Task.Delay(500);
                }
            }

            Assert.NotNull(row);
            Assert.True(row.IdleSeconds >= 2, $"idle {row.IdleSeconds}");
            Assert.True(row.UndoBlocks >= 1);
            Assert.NotNull(row.TransactionStart);
            Assert.Equal("SYSTEM", row.Username);
            Assert.Equal(1, row.BlockedCount);
            Assert.False(string.IsNullOrEmpty(row.PrevSqlId));
            Assert.True(row.LockCount >= 2, $"locks {row.LockCount}"); // TX + TM
            AssertRecentUtc(row.TransactionStart);

            var list = await service.GetIdleTransactionsAsync(monitor, 0, default);
            for (var i = 1; i < list.Count; i++)
            {
                Assert.True(list[i - 1].IdleSeconds >= list[i].IdleSeconds);
            }

            Assert.DoesNotContain(await service.GetIdleTransactionsAsync(monitor, 86_400, default), t => t.Session == idleKey);

            await idleTx.RollbackAsync();
            await waiting.WaitAsync(TimeSpan.FromSeconds(30));
            await waiterTx.RollbackAsync();
            Assert.DoesNotContain(await service.GetIdleTransactionsAsync(monitor, 0, default), t => t.Session == idleKey);
        }
        finally
        {
            foreach (var tx in new[] { idleTx, waiterTx })
            {
                try
                {
                    await tx.RollbackAsync();
                }
                catch (Exception ex) when (ex is InvalidOperationException or OracleException)
                {
                    // 이미 끝난 트랜잭션.
                }
            }

            await ExecAsync(setup, $"DROP TABLE {table} PURGE");
        }
    }

    [Fact]
    public async Task Deadlock_is_read_from_alert_log_newest_first()
    {
        var table = NewTable("LK_DL_");
        var setup = await RawAsync();
        await ExecAsync(setup, $"CREATE TABLE {table} (id NUMBER PRIMARY KEY, v NUMBER)");
        await ExecAsync(setup, $"INSERT INTO {table} VALUES (1, 0)");
        await ExecAsync(setup, $"INSERT INTO {table} VALUES (2, 0)");

        var a = await RawAsync();
        var b = await RawAsync();
        var aTx = a.BeginTransaction();
        var bTx = b.BeginTransaction();
        try
        {
            var monitor = await OpenMonitorAsync("system", _db.SystemPassword);
            var service = Service();
            var before = await service.GetRecentDeadlocksAsync(monitor, 50, default);

            await ExecAsync(a, $"UPDATE {table} SET v = 1 WHERE id = 1", aTx);
            await ExecAsync(b, $"UPDATE {table} SET v = 1 WHERE id = 2", bTx);
            var aWaits = ExecAsync(a, $"UPDATE {table} SET v = 2 WHERE id = 2", aTx);
            await Task.Delay(500);
            var bWaits = ExecAsync(b, $"UPDATE {table} SET v = 2 WHERE id = 1", bTx);

            // 서버가 3초 안에 한쪽 문장을 ORA-00060 으로 되돌린다.
            var first = await Task.WhenAny(aWaits, bWaits).WaitAsync(TimeSpan.FromSeconds(30));
            var ex = await Assert.ThrowsAsync<OracleException>(() => first);
            Assert.Equal(60, ex.Number);
            await (first == aWaits ? aTx : bTx).RollbackAsync();
            await (first == aWaits ? bWaits : aWaits).WaitAsync(TimeSpan.FromSeconds(30));

            IReadOnlyList<DeadlockEvent> events = before;
            bool Arrived() => events.Count > 0 && (before.Count == 0 || events[0].At > before[0].At);
            for (var i = 0; i < 30 && !Arrived(); i++)
            {
                await Task.Delay(1000);
                events = await service.GetRecentDeadlocksAsync(monitor, 50, default);
            }

            Assert.NotEmpty(events);
            Assert.Contains("ORA-00060", events[0].MessageLines[0], StringComparison.Ordinal);
            Assert.True(events[0].At > DateTimeOffset.UtcNow.AddMinutes(-10), $"newest {events[0].At:O}");
            Assert.Equal(1, events[0].InstanceId); // 비 RAC
            for (var i = 1; i < events.Count; i++)
            {
                Assert.True(events[i - 1].At >= events[i].At);
            }

            Assert.Single(await service.GetRecentDeadlocksAsync(monitor, 1, default));
            Assert.Empty(await service.GetRecentDeadlocksAsync(monitor, 0, default));
        }
        finally
        {
            foreach (var tx in new[] { aTx, bTx })
            {
                try
                {
                    await tx.RollbackAsync();
                }
                catch (Exception ex) when (ex is InvalidOperationException or OracleException)
                {
                    // 이미 끝난 트랜잭션.
                }
            }

            await ExecAsync(setup, $"DROP TABLE {table} PURGE");
        }
    }

    [Fact]
    public async Task Account_without_performance_views_gets_empty_results_instead_of_errors()
    {
        var monitor = await OpenMonitorAsync(OracleFreeFixture.AppUser, _db.AppPassword);
        var service = Service();
        Assert.False(monitor.Server.Capabilities.CanQueryPerformanceViews);

        var snap = await service.GetLocksAsync(monitor, new LockFilter(), default);
        Assert.Empty(snap.Rows);
        Assert.Empty(snap.WaitTree.Roots);
        Assert.Empty(await service.GetBlockerImpactAsync(monitor, default));
        Assert.Null(await service.GetLockedRowAsync(monitor, new SessionKey(1, 1, 1), default));
        Assert.Empty(await service.GetDdlLocksAsync(monitor, default));
        Assert.Empty(await service.GetWaitingBindsAsync(monitor, new WaitingBindTarget { Session = new SessionKey(1, 1, 1) }, default));
        Assert.Empty(await service.GetIdleTransactionsAsync(monitor, 0, default));
        Assert.Empty(await service.GetRecentDeadlocksAsync(monitor, 10, default));
    }

    /// <summary>v$ 뷰만 받은 비 DBA 계정: all_objects 경로로 볼 수 있는 표만 이름·행 값이 나오고, 딕셔너리 전용 기능은 빈 목록.</summary>
    [Fact]
    public async Task Account_with_v_views_but_no_dictionary_uses_all_objects()
    {
        var user = "LKV_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var password = "Lk" + Guid.NewGuid().ToString("N")[..16] + "#1";
        var granted = NewTable("LK_ALLG_");
        var hidden = NewTable("LK_ALLH_");
        var setup = await RawAsync();
        await ExecAsync(setup, $"CREATE USER {user} IDENTIFIED BY \"{password}\"");
        await ExecAsync(setup, $"GRANT CREATE SESSION TO {user}");
        var sys = await RawAsync("sys", _db.SystemPassword, sysdba: true);
        await ExecAsync(sys, "ALTER SESSION SET CONTAINER = FREEPDB1");
        foreach (var view in new[] { "v_$session", "gv_$session", "gv_$lock", "gv_$transaction", "v_$datafile", "gv_$sql_bind_capture" })
        {
            await ExecAsync(sys, $"GRANT SELECT ON sys.{view} TO {user}");
        }

        await ExecAsync(setup, $"CREATE TABLE {granted} (id NUMBER PRIMARY KEY, v NUMBER)");
        await ExecAsync(setup, $"CREATE TABLE {hidden} (id NUMBER PRIMARY KEY, v NUMBER)");
        await ExecAsync(setup, $"INSERT INTO {granted} VALUES (1, 0)");
        await ExecAsync(setup, $"INSERT INTO {hidden} VALUES (1, 0)");
        await ExecAsync(setup, $"GRANT SELECT ON {granted} TO {user}");

        var holder = await RawAsync();
        var holderTx = holder.BeginTransaction();
        await ExecAsync(holder, $"UPDATE {granted} SET v = 1 WHERE id = 1", holderTx);
        await ExecAsync(holder, $"UPDATE {hidden} SET v = 1 WHERE id = 1", holderTx);
        var w1 = await RawAsync();
        var w1Key = await KeyOfAsync(w1);
        var w1Tx = w1.BeginTransaction();
        var w1Waits = ExecAsync(w1, $"UPDATE {granted} SET v = 2 WHERE id = 1", w1Tx);
        var w2 = await RawAsync();
        var w2Key = await KeyOfAsync(w2);
        var w2Tx = w2.BeginTransaction();
        var w2Waits = ExecAsync(w2, $"UPDATE {hidden} SET v = 2 WHERE id = 1", w2Tx);

        try
        {
            var monitor = await OpenMonitorAsync(user, password);
            Assert.True(monitor.Server.Capabilities.CanQueryPerformanceViews);
            Assert.False(monitor.Server.Capabilities.CanQueryDictionary);
            var service = Service();

            var snap = await WaitForAsync(() => service.GetLocksAsync(monitor, new LockFilter { Username = "system" }, default),
                s => s.Rows.Count(r => r.RequestedMode == 6 && (r.Session == w1Key || r.Session == w2Key)) == 2);
            var tmNames = snap.Rows.Where(r => r.LockType == "TM").Select(r => r.ObjectName).ToList();
            Assert.Contains("SYSTEM." + granted, tmNames);
            Assert.DoesNotContain("SYSTEM." + hidden, tmNames); // all_objects 에 보이지 않는 표는 이름 없음
            Assert.Contains(snap.Rows, r => r.LockType == "TM" && r.ObjectName is null);
            Assert.NotEmpty(snap.WaitTree.Roots); // blocking_tree 도 all_ 경로로 돈다

            var visible = await service.GetLockedRowAsync(monitor, w1Key, default);
            Assert.NotNull(visible);
            Assert.Equal(granted, visible.Table);
            Assert.Equal(["1", "0"], visible.Values);
            Assert.Null(await service.GetLockedRowAsync(monitor, w2Key, default));

            Assert.Equal(2, Assert.Single(await service.GetBlockerImpactAsync(monitor, default), i => i.WaiterCount == 2).WaiterCount);
            Assert.Empty(await service.GetDdlLocksAsync(monitor, default)); // dba_ddl_locks 는 딕셔너리 권한 필요
            Assert.Empty(await service.GetRecentDeadlocksAsync(monitor, 10, default)); // v$diag_alert_ext 권한 없음 → 빈 목록
            Assert.NotEmpty(await service.GetIdleTransactionsAsync(monitor, 0, default));
        }
        finally
        {
            await holderTx.RollbackAsync();
            await Task.WhenAll(w1Waits, w2Waits).WaitAsync(TimeSpan.FromSeconds(30));
            await w1Tx.RollbackAsync();
            await w2Tx.RollbackAsync();
            foreach (var s in _manager.Sessions.Where(s => s.Profile.User == user).ToList())
            {
                await s.DisposeAsync();
            }

            await ExecAsync(setup, $"DROP TABLE {granted} PURGE");
            await ExecAsync(setup, $"DROP TABLE {hidden} PURGE");
            await ExecAsync(setup, $"DROP USER {user} CASCADE");
        }
    }
}

/// <summary>11.2 서버(oranda-ora11g 또는 gvenzl/oracle-xe:11)에서 같은 카탈로그가 돌고 행 잠금·잠긴 행·영향 순위·방치 트랜잭션이 맞게 나온다. 데드락은 빈 목록.</summary>
[Trait("Category", "Integration")]
[Collection(Oracle11gShared.Name)]
public sealed class LockMonitor11gTests : IAsyncLifetime
{
    private readonly Oracle11gFixture _db;
    private readonly ExecBus _bus = new();
    private readonly ExecInteraction _ui = new();
    private readonly List<OracleConnection> _raw = [];
    private OracleSessionManager _manager = null!;

    public LockMonitor11gTests(Oracle11gFixture db) => _db = db;

    public Task InitializeAsync()
    {
        _manager = new OracleSessionManager(_bus);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var s in _manager.Sessions)
        {
            await s.DisposeAsync();
        }

        foreach (var c in _raw)
        {
            await c.DisposeAsync();
        }
    }

    private async Task<OracleConnection> RawAsync()
    {
        var c = await _db.SystemAsync();
        _raw.Add(c);
        return c;
    }

    [Fact]
    public async Task Row_lock_locked_row_impact_and_idle_transaction_on_11_2()
    {
        var table = "LK11_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var setup = await RawAsync();
        await LockMonitorTests.ExecAsync(setup, $"CREATE TABLE {table} (id NUMBER PRIMARY KEY, v NUMBER, name VARCHAR2(30))");
        await LockMonitorTests.ExecAsync(setup, $"INSERT INTO {table} VALUES (1, 0, 'first')");
        var rowId = await LockMonitorTests.ScalarAsync(setup, $"SELECT ROWIDTOCHAR(ROWID) FROM {table} WHERE id = 1");

        var holder = await RawAsync();
        var holderKey = await LockMonitorTests.KeyOfAsync(holder);
        var holderTx = holder.BeginTransaction();
        await LockMonitorTests.ExecAsync(holder, $"UPDATE {table} SET v = 1 WHERE id = 1", holderTx);
        var waiter = await RawAsync();
        var waiterKey = await LockMonitorTests.KeyOfAsync(waiter);
        var waiterTx = waiter.BeginTransaction();
        var waiting = LockMonitorTests.ExecAsync(waiter, $"UPDATE {table} SET v = :v WHERE id = :id", waiterTx, ("v", 2), ("id", 1));

        try
        {
            using var cred = new SecureCredential(_db.SystemPassword);
            var monitor = await _manager.OpenAsync(new ConnectionProfile("ora11-system", "system", _db.DataSource), cred, SessionPurpose.Monitor, default);
            Assert.Equal(11, monitor.Server.MajorVersion);
            var service = new OracleMonitorService(new OracleExecutionService(new ExecutionPolicy(), _ui, _bus, null, pageSize: 500));

            LockSnapshot snap = new([], new BlockingTree([]), default);
            for (var i = 0; i < 40 && !snap.Rows.Any(r => r.Session == waiterKey && r.RequestedMode == 6); i++)
            {
                await Task.Delay(500);
                snap = await service.GetLocksAsync(monitor, new LockFilter { Username = "system" }, default);
            }

            var held = Assert.Single(snap.Rows, r => r.Session == holderKey && r.LockType == "TX");
            Assert.True(held.IsBlocking);
            LockMonitorTests.AssertRecentUtc(held.TransactionStart);
            Assert.Equal("INACTIVE", held.Status);
            Assert.Contains(snap.Rows, r => r.Session == holderKey && r.LockType == "TM" && r.ObjectName == "SYSTEM." + table);
            var wait = Assert.Single(snap.Rows, r => r.Session == waiterKey && r.LockType == "TX" && r.RequestedMode == 6);
            Assert.Equal(holderKey, wait.BlockingSession);
            Assert.Equal("INACTIVE", Assert.Single(snap.WaitTree.Roots, n => n.Session == holderKey).Status);
            Assert.Equal("ACTIVE", wait.Status);

            var impact11 = Assert.Single(await service.GetBlockerImpactAsync(monitor, default), i => i.Holder == holderKey);
            Assert.Equal(1, impact11.WaiterCount);
            Assert.Equal("INACTIVE", impact11.Status);

            var locked = await service.GetLockedRowAsync(monitor, waiterKey, default);
            Assert.NotNull(locked);
            Assert.Equal(rowId, locked.RowId);
            Assert.Equal(["1", "0", "first"], locked.Values);
            Assert.True(locked.FileNumber > 0 && locked.BlockNumber > 0 && locked.RowNumber >= 0);

            _ = await service.GetWaitingBindsAsync(monitor, new WaitingBindTarget { Session = waiterKey }, default);
            _ = await service.GetDdlLocksAsync(monitor, default);
            Assert.Empty(await service.GetRecentDeadlocksAsync(monitor, 10, default)); // 11.2: v$diag_alert_ext 없음
            var idle11 = Assert.Single(await service.GetIdleTransactionsAsync(monitor, 0, default), t => t.Session == holderKey);
            Assert.Equal(1, idle11.BlockedCount);
            Assert.True(idle11.LockCount >= 2);
            LockMonitorTests.AssertRecentUtc(idle11.TransactionStart);

            await holderTx.RollbackAsync();
            await waiting.WaitAsync(TimeSpan.FromSeconds(30));
            await waiterTx.RollbackAsync();
            Assert.DoesNotContain((await service.GetLocksAsync(monitor, new LockFilter { Username = "system" }, default)).Rows,
                r => r.Session == holderKey || r.Session == waiterKey);
        }
        finally
        {
            foreach (var tx in new[] { holderTx, waiterTx })
            {
                try
                {
                    await tx.RollbackAsync();
                }
                catch (Exception ex) when (ex is InvalidOperationException or OracleException)
                {
                    // 이미 끝난 트랜잭션.
                }
            }

            await LockMonitorTests.ExecAsync(setup, $"DROP TABLE {table} PURGE");
        }
    }
}
