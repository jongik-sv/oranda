using System.Globalization;
using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Execution;
using Oranda.Core.Monitoring;
using Oranda.Core.Security;
using Oranda.Core.Sessions;
using Oranda.Oracle.Connection;
using Oranda.Oracle.Execution;
using Oranda.Oracle.IntegrationTests.Execution;
using Oranda.Oracle.IntegrationTests.Integration;
using Oranda.Oracle.Monitoring;
using Oranda.Oracle.Sessions;

namespace Oranda.Oracle.IntegrationTests.Monitoring;

// 소유: TSK-09-01. 세션 모니터.

/// <summary>DB 없이 도는 부분: Kill·트레이스 문장. (트리 조립·델타는 Core.Tests MonitorRowsTests, 라이선스 대조는 Core.Tests CatalogLicenseGateTests 로 옮겼다.)</summary>
public class SessionMonitorLogicTests
{
    [Theory]
    [InlineData(true, false, "ALTER SYSTEM KILL SESSION '12,345' IMMEDIATE", "12,345")]
    [InlineData(false, false, "ALTER SYSTEM KILL SESSION '12,345'", "12,345")]
    [InlineData(true, true, "ALTER SYSTEM KILL SESSION '12,345,@2' IMMEDIATE", "12,345,@2")]
    public void Kill_statement_is_a_dangerous_alter_system(bool immediate, bool rac, string text, string target)
    {
        var stmt = OracleMonitorService.KillStatement(new SessionKey(2, 12, 345), immediate, rac);

        Assert.Equal(text, stmt.Text);
        Assert.Equal("ALTER SYSTEM", stmt.Verb);
        Assert.Equal(target, stmt.DangerousTarget);
    }

    [Fact]
    public async Task Kill_asks_dangerous_command_confirmation_and_stops_when_declined()
    {
        var ui = new ExecInteraction { ConfirmAnswer = false };
        var exec = new OracleExecutionService(new ExecutionPolicy(), ui, new ExecBus(), null, pageSize: 500);
        var service = new OracleMonitorService(exec);
        var session = new DriverlessSession(new ConnectionProfile("p", "u", "h:1521/s"));

        await Assert.ThrowsAsync<OperationCanceledException>(() => service.KillAsync(session, new SessionKey(1, 12, 345), true, default));

        var decision = Assert.Single(ui.Confirms);
        Assert.Equal(PolicyRule.DangerousCommand, decision.Rule);
        Assert.Equal("12,345", decision.RetypeText);
        Assert.Equal(0, session.DriverTouches);
    }

    [Fact]
    public async Task Kill_on_read_only_profile_is_denied()
    {
        var ui = new ExecInteraction();
        var exec = new OracleExecutionService(new ExecutionPolicy(), ui, new ExecBus(), null, pageSize: 500);
        var service = new OracleMonitorService(exec);
        var session = new DriverlessSession(new ConnectionProfile("ro", "u", "h:1521/s", ReadOnly: true));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.KillAsync(session, new SessionKey(1, 1, 1), false, default));
        Assert.Empty(ui.Confirms);
    }

    [Fact]
    public void Trace_statement_passes_waits_and_binds()
    {
        var on = OracleMonitorService.TraceStatement(new SessionKey(1, 12, 345), true, new TraceOptions { Waits = false, Binds = true });
        var off = OracleMonitorService.TraceStatement(new SessionKey(1, 12, 345), false, new TraceOptions());

        Assert.Equal(StatementKind.PlSql, on.Kind);
        Assert.Contains("SESSION_TRACE_ENABLE(session_id => 12, serial_num => 345, waits => FALSE, binds => TRUE)", on.Text, StringComparison.Ordinal);
        Assert.Contains("SESSION_TRACE_DISABLE(session_id => 12, serial_num => 345)", off.Text, StringComparison.Ordinal);
    }
}

/// <summary>26ai 컨테이너에서 잠금 경합을 만들어 목록·블로킹 트리·상세·Kill·트레이스를 확인한다.</summary>
[Trait("Category", "Integration")]
[Collection(OracleFreeShared.Name)]
public sealed class SessionMonitorTests : IAsyncLifetime
{
    private readonly OracleFreeFixture _db;
    private readonly ExecBus _bus = new();
    private readonly ExecInteraction _ui = new();
    private readonly List<OracleConnection> _raw = [];
    private OracleSessionManager _manager = null!;

    public SessionMonitorTests(OracleFreeFixture db) => _db = db;

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
                // Kill 로 끊긴 연결.
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

    private async Task<OracleConnection> RawAsync()
    {
        var profile = new ConnectionProfile("free-system", "system", _db.DataSource);
        var conn = new OracleConnection(profile.BuildConnectionString(_db.SystemPassword));
        await conn.OpenAsync();
        _raw.Add(conn);
        return conn;
    }

    private static async Task ExecAsync(OracleConnection c, string sql, OracleTransaction? tx = null)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<SessionKey> KeyOfAsync(OracleConnection c)
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

    [Fact]
    public async Task Lock_contention_shows_holder_and_waiter_and_kill_releases_it()
    {
        var table = "MON_LOCK_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var setup = await RawAsync();
        await ExecAsync(setup, $"CREATE TABLE {table} (id NUMBER PRIMARY KEY, v NUMBER)");
        await ExecAsync(setup, $"INSERT INTO {table} VALUES (1, 0)");

        try
        {
            var holder = await RawAsync();
            var holderKey = await KeyOfAsync(holder);
            var holderTx = holder.BeginTransaction();
            await ExecAsync(holder, $"UPDATE {table} SET v = 1 WHERE id = 1", holderTx);

            var waiter = await RawAsync();
            var waiterKey = await KeyOfAsync(waiter);
            var waiterTx = waiter.BeginTransaction();
            var waiting = ExecAsync(waiter, $"UPDATE {table} SET v = 2 WHERE id = 1", waiterTx);

            var monitor = await OpenMonitorAsync("system", _db.SystemPassword);
            Assert.Equal(SessionPurpose.Monitor, monitor.Purpose);
            var service = Service();

            // 대기가 서버에 보일 때까지(최대 20초).
            BlockingTree tree = new([]);
            for (var i = 0; i < 40; i++)
            {
                tree = await service.GetBlockingTreeAsync(monitor, default);
                if (tree.Roots.Any(r => r.Session == holderKey && r.Waiters.Any(w => w.Session == waiterKey)))
                {
                    break;
                }

                await Task.Delay(500);
            }

            var root = Assert.Single(tree.Roots, r => r.Session == holderKey);
            var waiterNode = Assert.Single(root.Waiters, w => w.Session == waiterKey);
            Assert.Equal("SYSTEM", root.Username);
            Assert.Contains("enq: TX", waiterNode.Event, StringComparison.Ordinal);
            Assert.Equal("SYSTEM." + table, waiterNode.LockedObject);
            Assert.False(waiting.IsCompleted);

            // 목록: 두 세션이 보이고 대기자의 블로커가 보유자다.
            var list = await service.GetSessionsAsync(monitor, new SessionFilter { Username = "system" }, default);
            Assert.Contains(list, r => r.Key == holderKey);
            var waiterRow = Assert.Single(list, r => r.Key == waiterKey);
            Assert.Equal(holderKey.Sid, waiterRow.BlockingSession?.Sid);
            Assert.Equal("ACTIVE", waiterRow.Status);

            // 상세: 보유자의 트랜잭션·잠금 객체, 대기자의 현재 SQL 전문.
            var holderDetail = await service.GetSessionDetailAsync(monitor, holderKey, default);
            Assert.NotEmpty(Assert.Single(holderDetail.Sections, s => s.Title == "트랜잭션").Rows);
            Assert.Contains(Assert.Single(holderDetail.Sections, s => s.Title == "잠금 객체").Rows, r => Equals(r[1], table));
            Assert.NotEmpty(Assert.Single(holderDetail.Sections, s => s.Title == "통계").Rows);
            Assert.Contains("SET v = 1", holderDetail.PreviousSqlText ?? holderDetail.CurrentSqlText, StringComparison.Ordinal);

            var waiterDetail = await service.GetSessionDetailAsync(monitor, waiterKey, default);
            Assert.Contains("SET v = 2", waiterDetail.CurrentSqlText, StringComparison.Ordinal);
            Assert.NotNull(waiterDetail.CurrentCursor);
            Assert.Contains(waiterDetail.Sections, s => s.Title == "실제 계획");

            // Kill: 위험 명령 확인을 거쳐 보유자를 끊으면 대기자가 풀린다.
            await service.KillAsync(monitor, holderKey, immediate: true, default);
            Assert.Contains(_ui.Confirms, d => d.Rule == PolicyRule.DangerousCommand && d.RetypeText == $"{holderKey.Sid},{holderKey.Serial}");
            Assert.Contains(_bus.Events, e => e is StatementExecuted { Result.Statement.Verb: "ALTER SYSTEM" });

            await waiting.WaitAsync(TimeSpan.FromSeconds(30));
            await waiterTx.RollbackAsync();

            var after = await service.GetBlockingTreeAsync(monitor, default);
            Assert.DoesNotContain(after.Roots, r => r.Session == holderKey);
        }
        finally
        {
            await ExecAsync(setup, $"DROP TABLE {table} PURGE");
        }
    }

    [Fact]
    public async Task Second_refresh_reports_deltas()
    {
        var monitor = await OpenMonitorAsync("system", _db.SystemPassword);
        var busy = await RawAsync();
        var key = await KeyOfAsync(busy);
        var service = Service();

        await service.GetSessionsAsync(monitor, new SessionFilter(), default);
        for (var i = 0; i < 20; i++)
        {
            await ExecAsync(busy, "BEGIN NULL; END;");
        }

        var rows = await service.GetSessionsAsync(monitor, new SessionFilter(), default);
        var row = Assert.Single(rows, r => r.Key == key);
        Assert.True(row.Deltas.TryGetValue("execute count", out var execs));
        Assert.True(execs >= 20, $"execute count delta {execs}");

        var active = await service.GetSessionsAsync(monitor, new SessionFilter { ActiveOnly = true }, default);
        Assert.All(active, r => Assert.Equal("ACTIVE", r.Status));
        var text = await service.GetSessionsAsync(monitor, new SessionFilter { Text = "no-such-program-xyz" }, default);
        Assert.Empty(text);
    }

    [Fact]
    public async Task Session_detail_has_io_wait_history_long_ops_and_open_cursor_sections()
    {
        var monitor = await OpenMonitorAsync("system", _db.SystemPassword);
        var target = await RawAsync();
        var key = await KeyOfAsync(target);
        await ExecAsync(target, "BEGIN DBMS_SESSION.SLEEP(0.1); END;");
        var service = Service();

        var detail = await service.GetSessionDetailAsync(monitor, key, default);

        // 권한 문제면 구역이 ["오류"] 한 열이 된다. 여기서는 모두 제 열 머리여야 한다.
        var io = Assert.Single(detail.Sections, x => x.Title == "I/O");
        Assert.Equal("블록 읽기", io.Columns[0]);
        Assert.Single(io.Rows);

        var waits = Assert.Single(detail.Sections, x => x.Title == "대기 이력");
        Assert.Equal("순번", waits.Columns[0]);
        Assert.NotEmpty(waits.Rows);

        var cursors = Assert.Single(detail.Sections, x => x.Title == "열린 커서");
        Assert.Equal("SQL ID", cursors.Columns[0]);
        Assert.InRange(cursors.Rows.Count, 1, 200);

        // 진행 중인 긴 작업이 없으면 빈 구역(오류 아님).
        var longOps = Assert.Single(detail.Sections, x => x.Title == "긴 작업");
        Assert.Equal("작업", longOps.Columns[0]);
    }

    [Fact]
    public async Task Trace_on_returns_trace_file_path_and_off_returns_null()
    {
        var monitor = await OpenMonitorAsync("system", _db.SystemPassword);
        var target = await RawAsync();
        var key = await KeyOfAsync(target);
        var service = Service();

        var path = await service.SetTraceAsync(monitor, key, true, new TraceOptions { Waits = true, Binds = true }, default);
        Assert.False(string.IsNullOrWhiteSpace(path));
        Assert.EndsWith(".trc", path, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(_bus.Events, e => e is StatementExecuted { Result.Status: ExecutionStatus.Succeeded } x
            && x.Result.Statement.Text.Contains("SESSION_TRACE_ENABLE", StringComparison.Ordinal));

        Assert.Null(await service.SetTraceAsync(monitor, key, false, new TraceOptions(), default));
    }

    [Fact]
    public async Task Account_without_privileges_cannot_monitor()
    {
        var monitor = await OpenMonitorAsync(OracleFreeFixture.AppUser, _db.AppPassword);

        Assert.False(monitor.Server.Capabilities.CanQueryPerformanceViews);
        Assert.False(monitor.Server.Capabilities.CanKillSession);
        Assert.False(monitor.Server.Capabilities.CanTraceSessions);
        await Assert.ThrowsAsync<OracleException>(() => Service().GetSessionsAsync(monitor, new SessionFilter(), default));
    }
}
