using System.Diagnostics;
using System.Globalization;
using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Security;
using Oranda.Core.Sessions;
using Oranda.Core.Transfer.Copy;
using Oranda.Oracle.Catalog;
using Oranda.Oracle.Connection;
using Oranda.Oracle.Execution;
using Oranda.Oracle.IntegrationTests.Execution;
using Oranda.Oracle.IntegrationTests.Integration;
using Oranda.Oracle.Sessions;
using Oranda.Oracle.Transfer.Copy;

namespace Oranda.Oracle.IntegrationTests.Transfer.Copy;

// 소유: WH1. 복사 대기 원인 감시(CopyWaitProbe)를 실제 서버의 행 잠금 대기로 확인한다(26ai·11.2).

/// <summary>26ai: 행 잠금으로 기다리는 세션의 원인·막는 세션을 감시 세션이 읽고, 대기가 끝나면 감시 세션을 닫는다. 볼 권한이 없으면 조용히 끄고 안내 한 번.</summary>
[Trait("Category", "Integration")]
[Collection(OracleFreeShared.Name)]
public sealed class CopyWaitProbeTests
{
    private readonly OracleFreeFixture _db;

    public CopyWaitProbeTests(OracleFreeFixture db) => _db = db;

    [Fact]
    public Task Row_lock_wait_shows_the_enqueue_and_the_blocking_session_and_end_wait_closes_the_monitor()
        => CopyWaitProbeScenario.RowLockAsync(_db.DataSource, _db.SystemPassword);

    [Fact]
    public async Task Account_without_gv_session_access_turns_the_probe_off_with_one_notice()
    {
        await using var scope = await CopyDbScope.CreateAsync(_db, CopyDbScope.NewTag());
        await CopyWaitProbeScenario.NoAccessAsync(scope);
    }
}

/// <summary>11.2 에서 같은 확인(<c>copy.wait_cause</c> 의 GV$SESSION 열이 11.2 에도 있다).</summary>
[Trait("Category", "Integration")]
[Collection(Oracle11gShared.Name)]
public sealed class CopyWaitProbe11gTests
{
    private readonly Oracle11gFixture _db;

    public CopyWaitProbe11gTests(Oracle11gFixture db) => _db = db;

    [Fact]
    public Task Row_lock_wait_shows_the_enqueue_and_the_blocking_session_and_end_wait_closes_the_monitor()
        => CopyWaitProbeScenario.RowLockAsync(_db.DataSource, _db.SystemPassword);

    [Fact]
    public async Task Account_without_gv_session_access_turns_the_probe_off_with_one_notice()
    {
        await using var scope = await CopyDbScope.CreateAsync(_db, CopyDbScope.NewTag());
        await CopyWaitProbeScenario.NoAccessAsync(scope);
    }
}

/// <summary>두 서버 판이 함께 쓰는 시험 본문.</summary>
internal static class CopyWaitProbeScenario
{
    private static readonly TimeSpan Waited = TimeSpan.FromSeconds(31);

    /// <summary>
    /// 보유 세션 A(원시 연결, SYSTEM)가 행 하나를 고친 채 두고, 기다리는 세션 B(앱 세션, SYSTEM)가 같은 행을 고치려 기다린다.
    /// B 와 같은 접속(SYSTEM: GV$SESSION 을 읽는다)으로 감시 세션을 여는 감시가 「행 잠금(enq: TX) · 막는 세션 {A 의 SID} …」 을 돌려준다.
    /// </summary>
    public static async Task RowLockAsync(string dataSource, string systemPassword)
    {
        var bus = new ExecBus();
        var ui = new ExecInteraction();
        var manager = new OracleSessionManager(bus);
        var table = "WP_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

        await using var setup = await RawAsync(dataSource, systemPassword);
        await ExecAsync(setup, $"CREATE TABLE {table} (id NUMBER PRIMARY KEY, v NUMBER)");
        await ExecAsync(setup, $"INSERT INTO {table} VALUES (1, 0)");

        OracleConnection? holder = null;
        OracleTransaction? holderTx = null;
        OracleSession? waiter = null;
        OracleTransaction? waiterTx = null;
        Task? waiting = null;
        CopyWaitProbe? probe = null;
        try
        {
            holder = await RawAsync(dataSource, systemPassword);
            var holderSid = await ScalarAsync(holder, "SELECT SYS_CONTEXT('USERENV','SID') FROM dual");
            holderTx = holder.BeginTransaction();
            await ExecAsync(holder, $"UPDATE {table} SET v = 1 WHERE id = 1", holderTx);

            var profile = new ConnectionProfile("wp-system", "system", dataSource);
            waiter = (OracleSession)await OpenAsync(manager, profile, systemPassword, SessionPurpose.Dedicated, default);
            Assert.True(waiter.Server.SessionId > 0);
            await waiter.CommandGate.EnterAsync(default);
            waiterTx = waiter.Connection.BeginTransaction();
            var w = waiter;
            var wtx = waiterTx;
            waiting = Task.Run(async () =>
            {
                try
                {
                    await ExecAsync(w.Connection, $"UPDATE {table} SET v = 2 WHERE id = 1", wtx);
                }
                finally
                {
                    w.CommandGate.Release();
                }
            });

            // 감시 세션 = 복사 세션과 같은 접속 프로필, 감시 용도.
            probe = new CopyWaitProbe(
                async ct => await OpenAsync(manager, profile, systemPassword, SessionPurpose.Monitor, ct),
                new OracleBulkExecutionService(new ExecutionPolicy(), ui, bus), new OracleSqlCatalog(), OracleBulkValueCodec.Instance,
                new OracleErrorClassifier(), new SteppingClock());

            // B 의 UPDATE 가 서버에서 대기에 들어갈 때까지(최대 약 15초) 다시 묻는다. 시계가 부를 때마다 간격 넘게 가므로 매번 새로 읽는다.
            string? text = null;
            var started = Stopwatch.GetTimestamp();
            while (Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(15))
            {
                text = await probe.DescribeAsync(waiter, Waited, default);
                if (text?.Contains("막는 세션", StringComparison.Ordinal) == true || probe.IsOff)
                {
                    break;
                }

                await Task.Delay(500);
            }

            Assert.False(probe.IsOff, "감시가 꺼졌습니다(SYSTEM 이 GV$SESSION 을 읽지 못함?)");
            Assert.NotNull(text);
            Assert.StartsWith("행 잠금(enq: TX) · 막는 세션 " + holderSid, text, StringComparison.Ordinal);
            var rest = text["행 잠금(enq: TX) · 막는 세션 ".Length..];
            Assert.Equal(holderSid, rest.Split(' ')[0]); // 다른 SID 의 앞자리와 겹치지 않게 낱말로 비교
            Assert.Contains("SYSTEM@", text, StringComparison.Ordinal); // 막는 쪽 계정@기계
            Assert.True(probe.HasMonitorSession);
            Assert.Null(probe.TakeNotice());

            await probe.EndWaitAsync(waiter);
            Assert.False(probe.HasMonitorSession);
        }
        finally
        {
            if (holderTx is not null)
            {
                await holderTx.RollbackAsync(); // B 의 대기를 푼다
            }

            if (waiting is not null)
            {
                await waiting.WaitAsync(TimeSpan.FromSeconds(30));
            }

            if (waiterTx is not null)
            {
                await waiterTx.RollbackAsync();
            }

            if (probe is not null)
            {
                await probe.DisposeAsync();
            }

            if (waiter is not null)
            {
                await waiter.DisposeAsync();
            }

            if (holder is not null)
            {
                await holder.DisposeAsync();
            }

            foreach (var s in manager.Sessions.ToList())
            {
                await s.DisposeAsync();
            }

            await ExecAsync(setup, $"DROP TABLE {table} PURGE");
        }
    }

    /// <summary>
    /// 복사 시험 계정(<see cref="CopyDbScope.DstUser"/>: CREATE SESSION·CREATE TABLE 등만, 사전·성능 뷰 권한 없음)은 GV$SESSION 을 못 읽는다(ORA-00942).
    /// 감시는 null 로 끝나고 꺼지며, 감시 세션을 닫고, 안내(<see cref="CopyWaitProbe.NoAccessNotice"/>)는 한 번만 나온다.
    /// </summary>
    public static async Task NoAccessAsync(CopyDbScope scope)
    {
        var copy = await scope.OpenTargetAsync(asWriter: false, ConnectionEnvironment.Development);
        Assert.False(copy.Server.Capabilities.CanQueryPerformanceViews, "시험 계정이 V$SESSION 을 읽을 수 있어 「권한 없음」 경로를 볼 수 없습니다.");
        await using var probe = new CopyWaitProbe(
            async _ => await scope.OpenUserAsync(scope.DstUser, ConnectionEnvironment.Development),
            scope.Bulk(), new OracleSqlCatalog(), OracleBulkValueCodec.Instance, new OracleErrorClassifier());

        Assert.Null(await probe.DescribeAsync(copy, Waited, default));

        Assert.True(probe.IsOff);
        Assert.False(probe.HasMonitorSession);
        Assert.Equal(CopyWaitProbe.NoAccessNotice, probe.TakeNotice());
        Assert.Null(probe.TakeNotice());
        Assert.Null(await probe.DescribeAsync(copy, Waited, default));
    }

    private static async Task<ISession> OpenAsync(OracleSessionManager manager, ConnectionProfile profile, string password, SessionPurpose purpose,
                                                  CancellationToken ct)
    {
        using var cred = new SecureCredential(password);
        return await manager.OpenAsync(profile, cred, purpose, ct);
    }

    private static async Task<OracleConnection> RawAsync(string dataSource, string systemPassword)
    {
        var conn = new ConnectionProfile("wp-raw", "system", dataSource).CreateConnection(systemPassword);
        await conn.OpenAsync();
        return conn;
    }

    private static async Task ExecAsync(OracleConnection c, string sql, OracleTransaction? tx = null)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<string> ScalarAsync(OracleConnection c, string sql)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToString(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture)!;
    }

    /// <summary>부를 때마다 <see cref="CopyWaitProbe.MinInterval"/> 보다 1초 더 가는 시계: 되묻기마다 앞 값을 다시 쓰지 않고 새로 읽게 한다.</summary>
    private sealed class SteppingClock : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Add(ref _ticks, CopyWaitProbe.MinInterval.Ticks + TimeSpan.TicksPerSecond);
    }
}
