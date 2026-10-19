using System.Globalization;
using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Plans;
using Oranda.Core.Security;
using Oranda.Core.Sessions;
using Oranda.Oracle.Connection;
using Oranda.Oracle.Execution;
using Oranda.Oracle.IntegrationTests.Execution;
using Oranda.Oracle.IntegrationTests.Integration;
using Oranda.Oracle.Plans;
using Oranda.Oracle.Sessions;

namespace Oranda.Oracle.IntegrationTests.Plans;

// 소유: TSK-07-01. 실행 계획.

/// <summary>DB 없이 도는 부분: 위험 단계 판정, 깊이 계산, 드라이버 없는 세션 거부.</summary>
public class ExecutionPlanLogicTests
{
    private static OracleExecutionPlanService.RawStep Step(string op, string? options = null, long? est = null, long? actual = null, long? starts = null) =>
        new(1, null, op, options, null, null, null, est, actual, starts, null, null, null);

    [Fact]
    public void Full_table_scan_and_cartesian_join_are_flagged()
    {
        Assert.Equal(PlanRisk.FullScan, OracleExecutionPlanService.ClassifyRisk(Step("TABLE ACCESS", "FULL")));
        Assert.Equal(PlanRisk.None, OracleExecutionPlanService.ClassifyRisk(Step("INDEX", "FULL SCAN")));
        Assert.Equal(PlanRisk.None, OracleExecutionPlanService.ClassifyRisk(Step("TABLE ACCESS", "BY INDEX ROWID")));
        Assert.Equal(PlanRisk.Cartesian, OracleExecutionPlanService.ClassifyRisk(Step("MERGE JOIN", "CARTESIAN")));
    }

    [Fact]
    public void Cardinality_gap_is_flagged_both_ways_per_start_and_ignores_small_numbers()
    {
        Assert.Equal(PlanRisk.CardinalityMisestimate, OracleExecutionPlanService.ClassifyRisk(Step("FILTER", null, est: 10, actual: 10_000, starts: 1)));
        Assert.Equal(PlanRisk.CardinalityMisestimate, OracleExecutionPlanService.ClassifyRisk(Step("FILTER", null, est: 100_000, actual: 5, starts: 1)));
        Assert.Equal(PlanRisk.None, OracleExecutionPlanService.ClassifyRisk(Step("FILTER", null, est: 1000, actual: 1500, starts: 1)));

        // 시작 횟수당으로 보면 맞는 추정(예: 1행 × 5000회).
        Assert.Equal(PlanRisk.None, OracleExecutionPlanService.ClassifyRisk(Step("INDEX", "UNIQUE SCAN", est: 1, actual: 5000, starts: 5000)));
        Assert.Equal(PlanRisk.None, OracleExecutionPlanService.ClassifyRisk(Step("FILTER", null, est: 1, actual: 8, starts: 1)));
        Assert.Equal(PlanRisk.None, OracleExecutionPlanService.ClassifyRisk(Step("FILTER", null, est: 1000, actual: null)));
    }

    [Fact]
    public void Steps_are_ordered_by_id_with_depth_from_parent_chain()
    {
        var steps = OracleExecutionPlanService.BuildSteps(
        [
            new(2, 1, "TABLE ACCESS", "FULL", "S", "T", 5, 100, 90, 1, 2000, "a", "f"),
            new(0, null, "SELECT STATEMENT", null, null, null, 7, null, null, null, null, null, null),
            new(1, 0, "SORT", "AGGREGATE", null, null, null, 1, 1, 1, null, null, null),
        ]);

        Assert.Equal([0, 1, 2], steps.Select(x => x.Id));
        Assert.Equal([0, 1, 2], steps.Select(x => x.Depth));
        Assert.Equal(PlanRisk.FullScan, steps[2].Risk);
        Assert.Equal(TimeSpan.FromMilliseconds(2), steps[2].ActualTime);
        Assert.Equal("a", steps[2].AccessPredicates);
        Assert.Equal("f", steps[2].FilterPredicates);
    }

    [Fact]
    public async Task Non_oracle_session_is_rejected_before_any_driver_call()
    {
        var service = new OracleExecutionPlanService(new OracleExecutionService(new ExecutionPolicy(), new ExecInteraction(), new ExecBus(), null, pageSize: 500));
        var session = new DriverlessSession(new ConnectionProfile("p", "u", "h:1521/s"));

        await Assert.ThrowsAsync<ArgumentException>(() => service.GetCursorPlanAsync(session, new CursorRef("abc"), "TYPICAL", default));
        await Assert.ThrowsAsync<ArgumentException>(() => OracleExecutionPlanService.GetSqlMonitorReportAsync(session, new CursorRef("abc"), default));
        Assert.Equal(0, session.DriverTouches);
    }

    [Fact]
    public async Task Plsql_statement_cannot_be_explained()
    {
        var service = new OracleExecutionPlanService(new OracleExecutionService(new ExecutionPolicy(), new ExecInteraction(), new ExecBus(), null, pageSize: 500));
        var session = new DriverlessSession(new ConnectionProfile("p", "u", "h:1521/s"));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.ExplainAsync(session, new SqlStatement("BEGIN NULL; END;", default, StatementKind.PlSql), default));
    }
}

/// <summary>26ai 컨테이너에서 예상·실제 계획을 읽는다.</summary>
[Trait("Category", "Integration")]
public sealed class ExecutionPlanTests : IClassFixture<OracleFreeFixture>, IAsyncLifetime
{
    private readonly OracleFreeFixture _db;
    private readonly ExecBus _bus = new();
    private readonly ExecInteraction _ui = new();
    private OracleSessionManager _manager = null!;

    public ExecutionPlanTests(OracleFreeFixture db) => _db = db;

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

    private OracleExecutionPlanService Service() =>
        new(new OracleExecutionService(new ExecutionPolicy(), _ui, _bus, null, pageSize: 500));

    private async Task<OracleSession> OpenAsync(bool readOnly = false, LicensePack pack = LicensePack.None)
    {
        var profile = new ConnectionProfile("free-system", "system", _db.DataSource, ReadOnly: readOnly, LicensePack: pack);
        using var cred = new SecureCredential(_db.SystemPassword);
        return (OracleSession)await _manager.OpenAsync(profile, cred, SessionPurpose.Work, default);
    }

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
    public async Task Explain_select_and_update_show_tree_text_predicates_and_risks()
    {
        var table = "PLAN_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        await using var raw = await RawAsync();
        await ExecAsync(raw, $"CREATE TABLE {table} AS SELECT level AS id, mod(level, 7) AS g FROM dual CONNECT BY level <= 500");
        try
        {
            await using var session = await OpenAsync();
            var service = Service();

            var select = await service.ExplainAsync(session,
                new SqlStatement($"SELECT g, COUNT(*) FROM {table} WHERE id > :b1 GROUP BY g", default, StatementKind.Query) { Verb = "SELECT", BindNames = ["b1"] }, default);
            Assert.True(select.Steps.Count >= 3);
            Assert.Equal(0, select.Steps[0].Id);
            Assert.Contains("SELECT STATEMENT", select.Steps[0].Operation, StringComparison.Ordinal);
            var scan = Assert.Single(select.Steps, x => x.Operation == "TABLE ACCESS");
            Assert.Equal("FULL", scan.Options);
            Assert.Equal(table, scan.ObjectName);
            Assert.True(scan.Depth > 0);
            Assert.True(scan.Risk.HasFlag(PlanRisk.FullScan));
            Assert.Contains("ID", scan.FilterPredicates ?? scan.AccessPredicates ?? string.Empty, StringComparison.Ordinal);
            Assert.Null(scan.ActualRows);
            Assert.Contains("Plan hash value", select.Text, StringComparison.Ordinal);
            Assert.Contains("TABLE ACCESS FULL", select.Text, StringComparison.Ordinal);
            Assert.True(select.PlanHashValue > 0);

            // DML: 계획만 만들고 데이터는 바뀌지 않는다.
            var update = await service.ExplainAsync(session,
                new SqlStatement($"UPDATE {table} SET g = 0 WHERE id = 3", default, StatementKind.Dml) { Verb = "UPDATE" }, default);
            Assert.Contains(update.Steps, x => x.Operation.StartsWith("UPDATE", StringComparison.Ordinal));
            Assert.Contains("UPDATE", update.Text, StringComparison.Ordinal);

            await using var check = raw.CreateCommand();
            check.CommandText = $"SELECT COUNT(*) FROM {table} WHERE g = 0 AND id = 3";
            Assert.Equal(0m, Convert.ToDecimal(await check.ExecuteScalarAsync(), CultureInfo.InvariantCulture));

            // 카테시안 조인.
            var cartesian = await service.ExplainAsync(session,
                new SqlStatement($"SELECT /*+ ORDERED USE_MERGE_CARTESIAN(b) */ a.id FROM {table} a, {table} b", default, StatementKind.Query) { Verb = "SELECT" }, default);
            Assert.Contains(cartesian.Steps, x => x.Risk.HasFlag(PlanRisk.Cartesian));

            // 계획 행은 정리된다.
            await using var left = raw.CreateCommand();
            left.CommandText = "SELECT COUNT(*) FROM plan_table WHERE statement_id LIKE 'ORANDA_%'";
            Assert.Equal(0m, Convert.ToDecimal(await left.ExecuteScalarAsync(), CultureInfo.InvariantCulture));

            // 문법 오류는 서버 오류 그대로.
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExplainAsync(session,
                new SqlStatement("SELECT * FROM no_such_table_zz", default, StatementKind.Query) { Verb = "SELECT" }, default));
            Assert.Contains("ORA-00942", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            await ExecAsync(raw, $"DROP TABLE {table} PURGE");
        }
    }

    [Fact]
    public async Task Explain_on_read_only_profile_explains_the_server_limit()
    {
        await using var session = await OpenAsync(readOnly: true);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Service().ExplainAsync(session,
            new SqlStatement("SELECT 1 FROM dual", default, StatementKind.Query) { Verb = "SELECT" }, default));
        Assert.Contains("ORA-01456", ex.Message, StringComparison.Ordinal);
        Assert.Contains("DISPLAY_CURSOR", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Actual_plan_shows_a_rows_flags_misestimate_and_options_change_the_text()
    {
        var tag = "PLANACT_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var table = tag + "_T";
        await using var raw = await RawAsync();
        await ExecAsync(raw, $"CREATE TABLE {table} AS SELECT level AS id, 1 AS g FROM dual CONNECT BY level <= 3000");
        try
        {
            // 통계가 없는 채로 읽혀 예상(동적 표본 없이)과 실제가 어긋나도록 통계를 일부러 비운다.
            await ExecAsync(raw, $"BEGIN DBMS_STATS.SET_TABLE_STATS(USER, '{table}', numrows => 5); END;");

            await using var session = await OpenAsync();
            await using (var cmd = raw.CreateCommand())
            {
                cmd.CommandText = $"SELECT /*+ gather_plan_statistics {tag} */ COUNT(*) FROM {table} WHERE g = 1";
                Assert.Equal(3000m, Convert.ToDecimal(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture));
            }

            string sqlId;
            int child;
            await using (var find = raw.CreateCommand())
            {
                find.CommandText = $"SELECT sql_id, child_number FROM v$sql WHERE sql_text LIKE 'SELECT /*+ gather_plan_statistics {tag} */%' ORDER BY last_active_time DESC FETCH FIRST 1 ROWS ONLY";
                await using var r = await find.ExecuteReaderAsync();
                Assert.True(await r.ReadAsync());
                sqlId = r.GetString(0);
                child = r.GetInt32(1);
            }

            var service = Service();
            var plan = await service.GetCursorPlanAsync(session, new CursorRef(sqlId, child), "ALLSTATS LAST", default);

            Assert.Equal(sqlId, plan.Cursor?.SqlId);
            Assert.Contains("A-Rows", plan.Text, StringComparison.Ordinal);
            var scan = Assert.Single(plan.Steps, x => x.Operation == "TABLE ACCESS");
            Assert.Equal(3000, scan.ActualRows);
            Assert.Equal(1, scan.Starts);
            Assert.NotNull(scan.ActualTime);
            Assert.True(scan.Risk.HasFlag(PlanRisk.FullScan));
            Assert.True(scan.Risk.HasFlag(PlanRisk.CardinalityMisestimate));
            Assert.Contains("G", scan.FilterPredicates ?? string.Empty, StringComparison.Ordinal);

            // 형식 옵션: 기본 TYPICAL 에는 A-Rows 열이 없다. 자식 번호를 생략해도 된다.
            var typical = await service.GetCursorPlanAsync(session, new CursorRef(sqlId), "TYPICAL", default);
            Assert.DoesNotContain("A-Rows", typical.Text, StringComparison.Ordinal);
            Assert.Contains("TABLE ACCESS FULL", typical.Text, StringComparison.Ordinal);
            Assert.Equal(3000, Assert.Single(typical.Steps, x => x.Operation == "TABLE ACCESS").ActualRows);

            // 없는 커서.
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetCursorPlanAsync(session, new CursorRef("0000000000000"), "TYPICAL", default));
        }
        finally
        {
            await ExecAsync(raw, $"DROP TABLE {table} PURGE");
        }
    }

    [Fact]
    public async Task Pack_requiring_monitor_report_is_blocked_before_the_driver_without_declaration()
    {
        await using var undeclared = await OpenAsync();
        var service = Service();
        var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => OracleExecutionPlanService.GetSqlMonitorReportAsync(undeclared, new CursorRef("0000000000000"), default));
        Assert.Contains("DiagnosticsAndTuning", ex.Message, StringComparison.Ordinal);

        await using var diagnosticsOnly = await OpenAsync(pack: LicensePack.Diagnostics);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => OracleExecutionPlanService.GetSqlMonitorReportAsync(diagnosticsOnly, new CursorRef("0000000000000"), default));
    }
}
