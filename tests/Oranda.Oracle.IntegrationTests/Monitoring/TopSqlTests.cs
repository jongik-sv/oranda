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

// 소유: TSK-09-02. Top SQL.

/// <summary>DB 없이 도는 부분: 바인드 행 정리, 모르는 세션 거부.</summary>
public class TopSqlLogicTests
{
    [Fact]
    public void Binds_are_ordered_by_position_and_keep_latest_capture_per_position()
    {
        var old = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
        var recent = old.AddHours(1);
        var rows = new List<object?[]>
        {
            new object?[] { ":B2", 2m, "NUMBER", "20", old, "YES" },
            new object?[] { ":B1", 1m, "VARCHAR2(32)", "old", old, "YES" },
            new object?[] { ":B1", 1m, "VARCHAR2(32)", "new", recent, "YES" },
            new object?[] { ":B3", 3m, "DATE", null, null, "NO" },
        };

        var binds = OracleMonitorService.TopSqlBinds(rows);

        Assert.Equal([":B1", ":B2", ":B3"], binds.Select(b => b.Name));
        Assert.Equal("new", binds[0].Value);
        Assert.Equal("20", binds[1].Value);
        Assert.Null(binds[2].Value);
        Assert.Null(binds[2].CapturedAt);
    }

    [Fact]
    public async Task Non_oracle_session_is_rejected_before_any_driver_call()
    {
        var service = new OracleMonitorService(new OracleExecutionService(new ExecutionPolicy(), new ExecInteraction(), new ExecBus(), null, pageSize: 500));
        var session = new DriverlessSession(new ConnectionProfile("p", "u", "h:1521/s"));

        await Assert.ThrowsAsync<ArgumentException>(() => service.GetTopSqlAsync(session, new TopSqlCriteria(), default));
        await Assert.ThrowsAsync<ArgumentException>(() => service.GetSqlDetailAsync(session, new CursorRef("abc"), default));
        Assert.Equal(0, session.DriverTouches);
    }
}

/// <summary>26ai 컨테이너에서 무거운 SQL 을 돌려 조건 검색·전문·바인드 캡처·실제 계획을 확인한다.</summary>
[Trait("Category", "Integration")]
[Collection(OracleFreeShared.Name)]
public sealed class TopSqlTests : IAsyncLifetime
{
    private readonly OracleFreeFixture _db;
    private readonly ExecBus _bus = new();
    private readonly ExecInteraction _ui = new();
    private OracleSessionManager _manager = null!;

    public TopSqlTests(OracleFreeFixture db) => _db = db;

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
    }

    private OracleMonitorService Service() =>
        new(new OracleExecutionService(new ExecutionPolicy(), _ui, _bus, null, pageSize: 500));

    private async Task<OracleConnection> RawAsync()
    {
        var profile = new ConnectionProfile("free-system", "system", _db.DataSource);
        var conn = new OracleConnection(profile.BuildConnectionString(_db.SystemPassword));
        await conn.OpenAsync();
        return conn;
    }

    private static async Task ExecAsync(OracleConnection c, string sql)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Condition_search_finds_heavy_sql_then_shows_full_text_binds_and_actual_plan()
    {
        var tag = "TOPSQL_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var table = tag + "_T";
        await using var raw = await RawAsync();
        await ExecAsync(raw, $"CREATE TABLE {table} AS SELECT level AS id, RPAD('x', 200, 'x') AS pad FROM dual CONNECT BY level <= 60000");

        try
        {
            var text = $"SELECT /* {tag} */ COUNT(*) FROM {table} t WHERE t.id > :b1 AND t.pad LIKE :b2";
            await using (var cmd = raw.CreateCommand())
            {
                cmd.BindByName = true;
                cmd.CommandText = text;
                cmd.Parameters.Add(new OracleParameter("b1", OracleDbType.Int32) { Value = 7 });
                cmd.Parameters.Add(new OracleParameter("b2", OracleDbType.Varchar2) { Value = "x%" });
                Assert.Equal(59993m, Convert.ToDecimal(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture));
            }

            var profile = new ConnectionProfile("free-system", "system", _db.DataSource);
            using var cred = new SecureCredential(_db.SystemPassword);
            var monitor = await _manager.OpenAsync(profile, cred, SessionPurpose.Monitor, default);
            var service = Service();

            // 조건: 읽은 블록 1천 이상 + 본문 일부(SQLM-01).
            var rows = await service.GetTopSqlAsync(monitor, new TopSqlCriteria
            {
                TextContains = tag.ToLowerInvariant(),
                MinBufferGets = 1000,
                MinExecutions = 1,
                MinElapsedSeconds = 0.000001,
                ParsingSchema = "system",
                OrderBy = TopSqlOrder.BufferGets,
                ActiveWithin = TimeSpan.FromMinutes(10),
            }, default);

            var row = Assert.Single(rows, r => r.SqlTextPreview.StartsWith("SELECT", StringComparison.Ordinal));
            Assert.True(row.BufferGets >= 1000);
            Assert.True(row.ElapsedSeconds > 0);
            Assert.Equal("SYSTEM", row.ParsingSchema);
            Assert.Contains(tag, row.SqlTextPreview, StringComparison.Ordinal);
            Assert.True(row.LastActiveTime is not null);

            // 조건에 못 미치면 빠진다.
            var none = await service.GetTopSqlAsync(monitor, new TopSqlCriteria { TextContains = tag, MinBufferGets = long.MaxValue / 2 }, default);
            Assert.Empty(none);
            var bySqlId = await service.GetTopSqlAsync(monitor, new TopSqlCriteria { TextContains = row.Cursor.SqlId, Top = 5 }, default);
            Assert.Contains(bySqlId, r => r.Cursor == row.Cursor);

            // 정렬·Top.
            var ordered = await service.GetTopSqlAsync(monitor, new TopSqlCriteria { OrderBy = TopSqlOrder.DiskReads, Top = 3 }, default);
            Assert.InRange(ordered.Count, 1, 3);
            Assert.True(ordered.Zip(ordered.Skip(1)).All(p => p.First.DiskReads >= p.Second.DiskReads));

            // 전문·바인드 캡처(SQLM-02).
            var detail = await service.GetSqlDetailAsync(monitor, row.Cursor, default);
            Assert.Contains(table, detail.FullText, StringComparison.Ordinal);
            Assert.Equal([":B1", ":B2"], detail.Binds.Select(b => b.Name.ToUpperInvariant()));
            Assert.Equal("7", detail.Binds[0].Value);
            Assert.Equal("x%", detail.Binds[1].Value);

            // 실제 계획: 찾은 커서로 DISPLAY_CURSOR 가 읽힌다(인수 조건).
            await using var planCmd = raw.CreateCommand();
            planCmd.CommandText = "SELECT plan_table_output FROM TABLE(DBMS_XPLAN.DISPLAY_CURSOR(:id, :child, 'TYPICAL'))";
            planCmd.Parameters.Add(new OracleParameter("id", OracleDbType.Varchar2) { Value = row.Cursor.SqlId });
            planCmd.Parameters.Add(new OracleParameter("child", OracleDbType.Int32) { Value = row.Cursor.ChildNumber });
            var plan = new List<string>();
            await using (var reader = await planCmd.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    plan.Add(reader.GetString(0));
                }
            }

            Assert.Contains(plan, l => l.Contains("TABLE ACCESS FULL", StringComparison.Ordinal));

            // 없는 SQL.
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetSqlDetailAsync(monitor, new CursorRef("0000000000000"), default));
        }
        finally
        {
            await ExecAsync(raw, $"DROP TABLE {table} PURGE");
        }
    }
}
