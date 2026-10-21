using System.Diagnostics;
using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Execution;
using Oranda.Core.Grid;
using Oranda.Core.Security;
using Oranda.Core.Sessions;
using Oranda.Oracle.Connection;
using Oranda.Oracle.Execution;
using Oranda.Oracle.IntegrationTests.Integration;
using Oranda.Oracle.Sessions;

namespace Oranda.Oracle.IntegrationTests.Execution;

// 소유: TSK-06-01. 실행·취소·트랜잭션·DBMS_OUTPUT.

/// <summary>DB 없이 도는 실행 엔진 시험(정책 관문 앞 단계, 치환, 바인드, 스크립트 오류 처리).</summary>
public class ExecutionServiceLogicTests
{
    [Fact]
    public async Task License_tagged_query_without_pack_never_reaches_driver()
    {
        // 가짜 세션: 드라이버(OracleSession)가 아니므로 정책 관문을 지나면 ArgumentException 이 난다.
        var bus = new ExecBus();
        var ui = new ExecInteraction();
        var service = new OracleExecutionService(new ExecutionPolicy(), ui, bus, null, pageSize: 500);
        var session = new DriverlessSession(new ConnectionProfile("p", "u", "h:1521/s"));
        var stmt = new SqlStatement("SELECT * FROM v$active_session_history", default, StatementKind.Query) { Verb = "SELECT" };

        var r = await service.ExecuteAsync(session, stmt, new Dictionary<string, BindValue>(),
            new ExecutionOptions { RequiredLicense = LicensePack.Diagnostics }, default);

        Assert.Equal(ExecutionStatus.Denied, r.Status);
        Assert.Equal(PolicyRule.License, r.Policy?.Rule);
        Assert.Equal(0, session.DriverTouches);
        Assert.Empty(ui.Confirms);
        Assert.Contains(bus.Events, e => e is PolicyDecisionMade { Decision.Kind: PolicyDecisionKind.Deny });
        Assert.Contains(bus.Events, e => e is StatementExecuted x && x.Result.Status == ExecutionStatus.Denied);
    }

    [Fact]
    public async Task Read_only_profile_update_is_denied()
    {
        var service = new OracleExecutionService(new ExecutionPolicy(), new ExecInteraction(), new ExecBus(), null, pageSize: 500);
        var session = new DriverlessSession(new ConnectionProfile("ro", "u", "h:1521/s", ReadOnly: true));
        var stmt = new SqlStatement("UPDATE emp SET sal = 0 WHERE id = 1", default, StatementKind.Dml) { Verb = "UPDATE" };

        var r = await service.ExecuteAsync(session, stmt, new Dictionary<string, BindValue>(), null, default);

        Assert.Equal(ExecutionStatus.Denied, r.Status);
        Assert.Equal(PolicyRule.ReadOnlyProfile, r.Policy?.Rule);
    }

    [Fact]
    public async Task Declined_confirmation_is_cancelled_without_driver()
    {
        var ui = new ExecInteraction { ConfirmAnswer = false };
        var service = new OracleExecutionService(new ExecutionPolicy(), ui, new ExecBus(), null, pageSize: 500);
        var session = new DriverlessSession(new ConnectionProfile("p", "u", "h:1521/s"));
        var stmt = new SqlStatement("DELETE FROM emp", default, StatementKind.Dml) { Verb = "DELETE", IsUnfilteredDml = true };

        var r = await service.ExecuteAsync(session, stmt, new Dictionary<string, BindValue>(), null, default);

        Assert.Equal(ExecutionStatus.Cancelled, r.Status);
        Assert.Equal(PolicyRule.UnfilteredDml, Assert.Single(ui.Confirms).Rule);
    }

    [Fact]
    public async Task Cancelled_substitution_prompt_cancels()
    {
        var ui = new ExecInteraction { Substitutions = null };
        var service = new OracleExecutionService(new ExecutionPolicy(), ui, new ExecBus(), null, pageSize: 500);
        var stmt = new SqlStatement("SELECT &x FROM dual", default, StatementKind.Query) { SubstitutionNames = ["x"] };

        var r = await service.ExecuteAsync(new DriverlessSession(new ConnectionProfile("p", "u", "h")), stmt,
            new Dictionary<string, BindValue>(), null, default);

        Assert.Equal(ExecutionStatus.Cancelled, r.Status);
    }

    [Theory]
    [InlineData(ScriptErrorMode.Stop, ScriptErrorAction.Stop, 1)]
    [InlineData(ScriptErrorMode.Continue, ScriptErrorAction.Stop, 3)]
    [InlineData(ScriptErrorMode.Ask, ScriptErrorAction.Stop, 1)]
    [InlineData(ScriptErrorMode.Ask, ScriptErrorAction.Continue, 3)]
    [InlineData(ScriptErrorMode.Ask, ScriptErrorAction.ContinueAll, 3)]
    public async Task Script_error_mode_controls_continuation(ScriptErrorMode mode, ScriptErrorAction answer, int expected)
    {
        var ui = new ExecInteraction { ScriptAnswer = answer };
        var service = new OracleExecutionService(new ExecutionPolicy(), ui, new ExecBus(), null, pageSize: 500);
        var session = new DriverlessSession(new ConnectionProfile("ro", "u", "h", ReadOnly: true));
        var stmts = Enumerable.Range(1, 3)
            .Select(i => new SqlStatement($"DELETE FROM t{i} WHERE 1=0", default, StatementKind.Dml) { Verb = "DELETE" })
            .ToList();

        var results = new List<ExecutionResult>();
        await foreach (var r in service.ExecuteScriptAsync(session, stmts, mode, null, default))
        {
            results.Add(r);
        }

        Assert.Equal(expected, results.Count);
        Assert.All(results, r => Assert.Equal(ExecutionStatus.Denied, r.Status));
        Assert.Equal(answer == ScriptErrorAction.ContinueAll ? 1 : mode == ScriptErrorMode.Ask ? expected : 0, ui.ScriptAsks);
    }

    [Theory]
    [InlineData("SELECT &x FROM dual", "SELECT 42 FROM dual")]
    [InlineData("SELECT '&x.abc' FROM dual", "SELECT '42abc' FROM dual")]
    [InlineData("SELECT &&x + &X FROM dual", "SELECT 42 + 42 FROM dual")]
    [InlineData("SELECT &y, & x FROM dual", "SELECT &y, & x FROM dual")]
    [InlineData("SELECT 'a&' FROM dual", "SELECT 'a&' FROM dual")]
    public void Substitution_replaces_names(string text, string expected)
    {
        var defined = new List<string>();
        var result = SubstitutionText.Apply(text,
            n => string.Equals(n, "x", StringComparison.OrdinalIgnoreCase) ? "42" : null,
            (n, _) => defined.Add(n));

        Assert.Equal(expected, result);
        Assert.Equal(text.Contains("&&", StringComparison.Ordinal) ? 1 : 0, defined.Count);
    }

    [Fact]
    public void Missing_bind_value_is_reported()
    {
        using var cmd = new OracleCommand();
        var stmt = new SqlStatement("SELECT :a, :b FROM dual", default, StatementKind.Query) { BindNames = ["a", "b"] };
        var binds = new Dictionary<string, BindValue> { ["A"] = new(BindDataType.Number, 1m) };

        var ex = Assert.Throws<InvalidOperationException>(() => BindBuilder.Build(cmd, stmt, binds));
        Assert.Contains(":b", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Out_binds_get_default_size()
    {
        var p = BindBuilder.Create("o", new BindValue(BindDataType.Varchar2, null, BindDirection.Out));

        Assert.Equal(System.Data.ParameterDirection.Output, p.Direction);
        Assert.Equal(BindBuilder.DefaultOutSize, p.Size);
        Assert.Equal(OracleDbType.Varchar2, p.OracleDbType);
    }

    [Theory]
    [InlineData("12.5", typeof(decimal))]
    [InlineData("-0.000001", typeof(decimal))]
    [InlineData("123456789012345678901234567890123456789", typeof(string))]
    public void Number_keeps_precision(string text, Type expected)
    {
        var v = OracleValues.FromDecimal(global::Oracle.ManagedDataAccess.Types.OracleDecimal.Parse(text));

        Assert.IsType(expected, v);
        if (v is string s)
        {
            Assert.Equal(text, s);
        }
    }
}

[Trait("Category", "Integration")]
[Collection(OracleFreeShared.Name)]
public sealed class ExecutionServiceTests : IAsyncLifetime
{
    private readonly OracleFreeFixture _db;
    private readonly ExecBus _bus = new();
    private readonly ExecInteraction _ui = new();
    private OracleSessionManager _manager = null!;

    public ExecutionServiceTests(OracleFreeFixture db) => _db = db;

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

    private OracleExecutionService Service(int pageSize = 500) =>
        new(new ExecutionPolicy(), _ui, _bus, null, pageSize);

    private async Task<OracleSession> OpenAsync(bool readOnly = false)
    {
        var profile = new ConnectionProfile("free-system", "system", _db.DataSource, ReadOnly: readOnly);
        using var cred = new SecureCredential(_db.SystemPassword);
        return (OracleSession)await _manager.OpenAsync(profile, cred, SessionPurpose.Work, default);
    }

    private static SqlStatement Q(string text) => new(text, default, StatementKind.Query) { Verb = "SELECT" };

    private static SqlStatement Dml(string text) => new(text, default, StatementKind.Dml) { Verb = text.Split(' ')[0] };

    private static SqlStatement Ddl(string text) => new(text, default, StatementKind.Ddl) { Verb = text.Split(' ')[0] };

    private static SqlStatement Block(string text, params string[] binds) =>
        new(text, default, StatementKind.PlSql) { Verb = "BEGIN", BindNames = binds };

    private static readonly Dictionary<string, BindValue> NoBinds = [];

    [Fact]
    public async Task Query_pages_forward_with_fetch_size_from_row_size()
    {
        await using var s = await OpenAsync();
        var r = await Service(100).ExecuteAsync(s, Q("SELECT level AS n, 'row' || level AS name FROM dual CONNECT BY level <= 250"), NoBinds, null, default);

        Assert.Equal(ExecutionStatus.Succeeded, r.Status);
        await using var rs = Assert.IsType<OracleResultSet>(r.ResultSet);
        Assert.Equal(["N", "NAME"], rs.Columns.Select(c => c.Name));
        Assert.Equal(ColumnDataKind.Number, rs.Columns[0].Kind);
        Assert.Equal(ColumnDataKind.Text, rs.Columns[1].Kind);
        Assert.True(rs.FetchSize > 0);
        Assert.Equal(0, rs.FetchSize % 100);

        var p0 = await rs.GetPageAsync(0, default);
        Assert.Equal(100, p0.RowCount);
        Assert.Equal(1m, p0.Columns[0][0]);
        Assert.Equal("row100", p0.Columns[1][99]);
        Assert.Same(p0, await rs.GetPageAsync(0, default));
        Assert.Null(rs.KnownRowCount);

        var p2 = await rs.GetPageAsync(2, default);
        Assert.Equal(50, p2.RowCount);
        Assert.Equal(200, p2.FirstRowIndex);
        Assert.True(p2.IsLast);
        Assert.True(rs.IsComplete);
        Assert.Equal(250, rs.KnownRowCount);
        await Assert.ThrowsAsync<InvalidOperationException>(() => rs.GetPageAsync(1, default));
        Assert.Equal(0, (await rs.GetPageAsync(5, default)).RowCount);
        Assert.Equal(SessionState.Idle, s.State);
    }

    [Fact]
    public async Task Read_only_profile_blocks_update_at_policy_and_server()
    {
        var table = "oranda_ro_" + Guid.NewGuid().ToString("N")[..8];
        await AdminAsync($"CREATE TABLE {table} (id NUMBER)");
        try
        {
            await using var s = await OpenAsync(readOnly: true);
            var service = Service();

            var denied = await service.ExecuteAsync(s, Dml($"UPDATE {table} SET id = 1"), NoBinds, null, default);
            Assert.Equal(ExecutionStatus.Denied, denied.Status);
            Assert.Equal(PolicyRule.ReadOnlyProfile, denied.Policy?.Rule);

            // 이중 방어: 분류가 틀려 관문을 지나도 서버의 읽기 전용 트랜잭션이 막는다(ORA-01456).
            var misclassified = new SqlStatement($"INSERT INTO {table} VALUES (1)", default, StatementKind.Query);
            var failed = await service.ExecuteAsync(s, misclassified, NoBinds, null, default);
            Assert.Equal(ExecutionStatus.Failed, failed.Status);
            Assert.Equal(1456, failed.Error?.Number);
            Assert.False(s.HasPendingTransaction);

            var ok = await service.ExecuteAsync(s, Q("SELECT 1 FROM dual"), NoBinds, null, default);
            Assert.Equal(ExecutionStatus.Succeeded, ok.Status);
            await ok.ResultSet!.DisposeAsync();
        }
        finally
        {
            await AdminAsync($"DROP TABLE {table} PURGE");
        }
    }

    [Fact]
    public async Task License_tagged_query_text_never_reaches_server()
    {
        await using var s = await OpenAsync();
        var marker = "oranda_lic_" + Guid.NewGuid().ToString("N")[..12];
        var stmt = Q($"SELECT /* {marker} */ COUNT(*) FROM v$active_session_history");

        var r = await Service().ExecuteAsync(s, stmt, NoBinds, new ExecutionOptions { RequiredLicense = LicensePack.Diagnostics }, default);

        Assert.Equal(ExecutionStatus.Denied, r.Status);
        await using var other = new OracleConnection(s.Profile.BuildConnectionString(_db.SystemPassword));
        await other.OpenAsync();
        await using var cmd = other.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM v$sql WHERE sql_text LIKE '%' || :m || '%' AND sql_text NOT LIKE '%v$sql WHERE%'";
        cmd.Parameters.Add("m", marker);
        Assert.Equal(0m, Convert.ToDecimal(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Cancel_releases_running_statement_within_one_second()
    {
        await using var s = await OpenAsync();
        using var cts = new CancellationTokenSource();
        // CPU 를 오래 쓰는 질의(DBMS_SESSION.SLEEP 은 컨테이너 환경에서 서버가 중단 요청을 잠이 끝난 뒤에야 처리한다).
        var task = Service().ExecuteAsync(s,
            Q("SELECT COUNT(*) FROM all_objects a, all_objects b, (SELECT 1 FROM dual CONNECT BY level <= 100) c"), NoBinds, null, cts.Token);

        await Task.Delay(1500);
        Assert.Equal(SessionState.Busy, s.State);
        var sw = Stopwatch.StartNew();
        await cts.CancelAsync();
        var r = await task;
        sw.Stop();

        Assert.Equal(ExecutionStatus.Cancelled, r.Status);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1), $"취소 반영 {sw.ElapsedMilliseconds} ms");
        Assert.Equal(SessionState.Idle, s.State);

        // 취소 뒤에도 세션을 계속 쓸 수 있다.
        var after = await Service().ExecuteAsync(s, Q("SELECT 1 FROM dual"), NoBinds, null, default);
        Assert.Equal(ExecutionStatus.Succeeded, after.Status);
        await after.ResultSet!.DisposeAsync();
    }

    // TSK-06-11: 엔진은 취소 즉시 브레이크를 보낸다. 서버가 그것을 언제 읽는지는 서버 sqlnet.ora 에 달렸다.
    // 시험 이미지(gvenzl)는 DISABLE_OOB=ON·BREAK_POLL_SKIP=1000 이라 in-band 브레이크만 받고, PL/SQL 대기 중에는 읽지 않는다.
    // 1 s 단정은 OOB 가 통하는 경로에서만 켠다(ORANDA_IT_CANCEL_STRICT=1). 그 밖에서는 취소가 먹었는지(스스로 끝나기 전에 끝남)만 본다.
    private static bool StrictCancel => Environment.GetEnvironmentVariable("ORANDA_IT_CANCEL_STRICT") == "1";

    private static readonly TimeSpan CancelBudget = TimeSpan.FromSeconds(1);

    [Fact]
    public async Task Cancel_during_plsql_sleep_ends_cancelled_and_session_runs_next_statement()
    {
        await using var s = await OpenAsync();
        var elapsed = await CancelWhileRunningAsync(s, Block("BEGIN DBMS_SESSION.SLEEP(5); END;"));

        // in-band 브레이크면 서버가 남은 잠(약 4 s)을 다 잔 뒤 ORA-01013 을 돌려준다.
        var limit = StrictCancel ? CancelBudget : TimeSpan.FromSeconds(10);
        Assert.True(elapsed < limit, $"취소 반영 {elapsed.TotalMilliseconds:F0} ms (한도 {limit.TotalMilliseconds:F0} ms)");
        await AssertRunsNextStatementAsync(s);
    }

    [Fact]
    public async Task Cancel_during_plsql_cpu_loop_ends_cancelled_and_session_runs_next_statement()
    {
        await using var s = await OpenAsync();
        // 서버 CPU 를 30 s 동안 쓰는 루프(취소가 안 먹으면 30 s 뒤 스스로 끝난다). t 는 시간대 있는 형식이어야 한다: TIMESTAMP 면 세션 시간대(KST)와 서버(UTC) 차이로 즉시 끝난다.
        const string loop = """
            DECLARE
              x NUMBER := 0;
              t TIMESTAMP WITH TIME ZONE := SYSTIMESTAMP + INTERVAL '30' SECOND;
            BEGIN
              WHILE SYSTIMESTAMP < t LOOP
                FOR i IN 1 .. 10000 LOOP x := x + SQRT(i); END LOOP;
              END LOOP;
            END;
            """;
        var elapsed = await CancelWhileRunningAsync(s, Block(loop));

        // BREAK_POLL_SKIP=1000 이면 서버가 브레이크를 몇 초 뒤에 읽는다(측정 1.7~11 s). 루프가 스스로 끝나기 전이면 취소가 먹은 것이다.
        var limit = StrictCancel ? CancelBudget : TimeSpan.FromSeconds(20);
        Assert.True(elapsed < limit, $"취소 반영 {elapsed.TotalMilliseconds:F0} ms (한도 {limit.TotalMilliseconds:F0} ms)");
        await AssertRunsNextStatementAsync(s);
    }

    [Fact]
    public async Task Cancel_during_large_fetch_keeps_rows_in_order_and_session_runs_next_statement()
    {
        await using var s = await OpenAsync();
        // 900만 행(깊은 CONNECT BY 는 ORA-30009 위험이 있어 얕은 둘을 곱한다). n 은 1부터 빈틈없이 는다.
        var r = await Service().ExecuteAsync(s, Q("""
            SELECT ROWNUM AS n, RPAD('x', 200, 'x') AS pad
              FROM (SELECT 1 FROM dual CONNECT BY level <= 3000), (SELECT 1 FROM dual CONNECT BY level <= 3000)
            """), NoBinds, null, default);
        Assert.Equal(ExecutionStatus.Succeeded, r.Status);
        var rs = Assert.IsType<OracleResultSet>(r.ResultSet);

        using var cts = new CancellationTokenSource();
        // 페이지 17,000(약 850만 행)까지 가져오는 중에 취소한다.
        var fetch = rs.GetPageAsync(17000, cts.Token);
        await Task.Delay(1000);
        Assert.False(fetch.IsCompleted);
        Assert.Equal(SessionState.Busy, s.State);
        var sw = Stopwatch.StartNew();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fetch);
        sw.Stop();
        // BREAK_POLL_SKIP=1000 이면 서버가 행을 보내는 중에도 브레이크를 드물게 확인한다(측정 0~1,036 ms, 기본값이면 0~38 ms).
        var limit = StrictCancel ? CancelBudget : TimeSpan.FromSeconds(3);
        Assert.True(sw.Elapsed < limit, $"취소 반영 {sw.ElapsedMilliseconds} ms (한도 {limit.TotalMilliseconds:F0} ms)");

        // 취소 뒤에도 같은 결과를 이어 가져온다: 페이지 중간에서 끊겼어도 행이 빠지거나 밀리지 않는다.
        var next = await rs.GetPageAsync(rs.NextPageIndex, default);
        Assert.Equal(500, next.RowCount);
        Assert.Equal(next.FirstRowIndex + 1, Convert.ToInt64(next.Columns[0][0], System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(next.FirstRowIndex + 500, Convert.ToInt64(next.Columns[0][499], System.Globalization.CultureInfo.InvariantCulture));

        await rs.DisposeAsync();
        await AssertRunsNextStatementAsync(s);
    }

    /// <summary>문장을 실행하고 1 s 뒤(서버가 일하는 중) 취소한다. 취소 명령 → 「취소됨」 결과까지 걸린 시간.</summary>
    private async Task<TimeSpan> CancelWhileRunningAsync(OracleSession s, SqlStatement stmt)
    {
        using var cts = new CancellationTokenSource();
        var task = Service().ExecuteAsync(s, stmt, NoBinds, null, cts.Token);
        await Task.Delay(1000);
        Assert.False(task.IsCompleted);
        Assert.Equal(SessionState.Busy, s.State);

        var sw = Stopwatch.StartNew();
        await cts.CancelAsync();
        var r = await task;
        sw.Stop();

        Assert.Equal(ExecutionStatus.Cancelled, r.Status);
        Assert.Null(r.Error); // ORA-01013 은 실패가 아니라 취소다
        return sw.Elapsed;
    }

    /// <summary>취소 뒤 세션이 유휴로 돌아와 다음 문장을 실행한다.</summary>
    private async Task AssertRunsNextStatementAsync(OracleSession s)
    {
        Assert.Equal(SessionState.Idle, s.State);
        var next = await Service().ExecuteAsync(s, Q("SELECT 1 FROM dual"), NoBinds, null, default);
        Assert.Equal(ExecutionStatus.Succeeded, next.Status);
        var page = await next.ResultSet!.GetPageAsync(0, default);
        Assert.Equal(1m, page.Columns[0][0]);
        await next.ResultSet.DisposeAsync();
    }

    [Fact]
    public async Task Dml_is_not_autocommitted_and_pending_is_tracked()
    {
        await using var s = await OpenAsync();
        var service = Service();
        var table = "oranda_tx_" + Guid.NewGuid().ToString("N")[..8];
        await service.ExecuteAsync(s, Ddl($"CREATE TABLE {table} (id NUMBER)"), NoBinds, null, default);
        Assert.Contains(_bus.Events, e => e is MetadataInvalidated m && m.DbKey == s.DbKey);
        try
        {
            var ins = await service.ExecuteAsync(s, Dml($"INSERT INTO {table} VALUES (1)"), NoBinds, null, default);
            Assert.Equal(ExecutionStatus.Succeeded, ins.Status);
            Assert.Equal(1, ins.RowsAffected);
            Assert.True(ins.TransactionPending);
            Assert.True(s.HasPendingTransaction);
            Assert.Contains(_bus.Events, e => e is TransactionStateChanged { Pending: true } t && t.SessionId == s.Id);
            Assert.Equal(0, await CountFromOtherSessionAsync(table));

            await s.CommitAsync(default);
            Assert.False(s.HasPendingTransaction);
            Assert.Equal(1, await CountFromOtherSessionAsync(table));

            // 커밋 뒤에도 자동 커밋으로 돌아가지 않는다.
            await service.ExecuteAsync(s, Dml($"INSERT INTO {table} VALUES (2)"), NoBinds, null, default);
            Assert.True(s.HasPendingTransaction);
            Assert.Equal(1, await CountFromOtherSessionAsync(table));
            var rb = await service.ExecuteAsync(s, new SqlStatement("ROLLBACK", default, StatementKind.TransactionControl) { Verb = "ROLLBACK" },
                NoBinds, null, default);
            Assert.False(rb.TransactionPending);
            Assert.False(s.HasPendingTransaction);
        }
        finally
        {
            await service.ExecuteAsync(s, Ddl($"DROP TABLE {table} PURGE"), NoBinds, null, default);
        }
    }

    [Fact]
    public async Task Dbms_output_is_collected_after_execution()
    {
        await using var s = await OpenAsync();
        var r = await Service().ExecuteAsync(s,
            Block("BEGIN DBMS_OUTPUT.PUT_LINE('hello'); DBMS_OUTPUT.PUT_LINE('세계'); END;"), NoBinds, null, default);

        Assert.Equal(ExecutionStatus.Succeeded, r.Status);
        Assert.Equal(["hello", "세계"], r.DbmsOutput);
        Assert.Contains(_bus.Events, e => e is DbmsOutputReceived d && d.Lines.Count == 2);

        var next = await Service().ExecuteAsync(s, Block("BEGIN NULL; END;"), NoBinds, null, default);
        Assert.Empty(next.DbmsOutput);
    }

    [Fact]
    public async Task Display_cursor_with_null_sql_id_shows_the_previous_user_query()
    {
        // TSK-06-14: 질의 뒤에 숨은 GET_LINES 를 보내면 display_cursor(null) 가 그 문장을 보였다.
        await using var s = await OpenAsync();
        var service = Service();
        var marker = "oranda_06_14_" + Guid.NewGuid().ToString("N")[..8];
        var q = await service.ExecuteAsync(s, Q($"SELECT /* {marker} */ level AS n FROM dual CONNECT BY level < 10"), NoBinds, null, default);
        await using (var rs = Assert.IsType<OracleResultSet>(q.ResultSet))
        {
            Assert.Equal(9, (await rs.GetPageAsync(0, default)).RowCount);
        }

        var plan = await service.ExecuteAsync(s, Q("SELECT * FROM table(dbms_xplan.display_cursor(sql_id => null, format => 'ALLSTATS LAST'))"), NoBinds, null, default);
        await using var planRows = Assert.IsType<OracleResultSet>(plan.ResultSet);
        var page = await planRows.GetPageAsync(0, default);
        var text = string.Join("\n", Enumerable.Range(0, page.RowCount).Select(i => page.Columns[0][i] as string));
        Assert.Contains(marker, text, StringComparison.Ordinal);
        Assert.DoesNotContain("GET_LINES", text, StringComparison.Ordinal);

        // 질의 안에서 낸 출력은 버리지 않고 다음 PL/SQL 뒤에 나온다.
        var put = await service.ExecuteAsync(s, Block("BEGIN DBMS_OUTPUT.PUT_LINE('later'); END;"), NoBinds, null, default);
        Assert.Equal(["later"], put.DbmsOutput);
    }

    [Fact]
    public async Task Binds_in_out_and_ref_cursor()
    {
        await using var s = await OpenAsync();
        var binds = new Dictionary<string, BindValue>
        {
            ["i"] = new(BindDataType.Number, 21m),
            ["o"] = new(BindDataType.Number, null, BindDirection.Out),
            ["t"] = new(BindDataType.Varchar2, null, BindDirection.Out),
            ["c"] = new(BindDataType.RefCursor, null, BindDirection.Out),
        };

        var r = await Service().ExecuteAsync(s,
            Block("BEGIN :o := :i * 2; :t := 'v' || :i; OPEN :c FOR SELECT level n FROM dual CONNECT BY level <= 3; END;", "o", "i", "t", "c"),
            binds, null, default);

        Assert.Equal(ExecutionStatus.Succeeded, r.Status);
        Assert.Equal(42m, r.OutValues["o"]);
        Assert.Equal("v21", r.OutValues["t"]);
        await using var rc = Assert.IsAssignableFrom<IResultSet>(r.OutValues["c"]);
        var page = await rc.GetPageAsync(0, default);
        Assert.Equal(3, page.RowCount);
        Assert.True(page.IsLast);
    }

    [Fact]
    public async Task Substitution_values_are_asked_and_double_ampersand_is_remembered()
    {
        await using var s = await OpenAsync();
        _ui.Substitutions = new Dictionary<string, string> { ["x"] = "40", ["y"] = "2" };
        var service = Service();
        var stmt = new SqlStatement("SELECT &&x + &y AS v FROM dual", default, StatementKind.Query) { SubstitutionNames = ["x", "y"] };

        var r = await service.ExecuteAsync(s, stmt, NoBinds, null, default);
        await using (var rs = r.ResultSet!)
        {
            Assert.Equal(42m, (await rs.GetPageAsync(0, default)).Columns[0][0]);
        }

        Assert.Equal("SELECT 40 + 2 AS v FROM dual", r.Statement.Text);

        var r2 = await service.ExecuteAsync(s, stmt, NoBinds, null, default);
        await r2.ResultSet!.DisposeAsync();
        Assert.Equal(["x", "y"], _ui.AskedNames[0]);
        Assert.Equal(["y"], _ui.AskedNames[1]);
    }

    [Fact]
    public async Task Server_error_has_code_and_position()
    {
        await using var s = await OpenAsync();
        var r = await Service().ExecuteAsync(s, Q("SELECT *\n  FROM no_such_table_x"), NoBinds, null, default);

        Assert.Equal(ExecutionStatus.Failed, r.Status);
        Assert.Equal("ORA-00942", r.Error?.Code);
        Assert.Equal(942, r.Error?.Number);
        Assert.Equal(2, r.Error?.Line);
        Assert.Equal(SessionState.Idle, s.State);
    }

    [Fact]
    public async Task Column_types_are_mapped_to_display_kinds()
    {
        await using var s = await OpenAsync();
        var sql = """
            SELECT 123456789012345678901234567890.123 AS big, DATE '2024-02-29' AS d,
                   TIMESTAMP '2024-02-29 10:11:12.5 +09:00' AS tz, TO_CLOB('long text') AS c,
                   HEXTORAW('CAFE') AS r, TO_BLOB(HEXTORAW('0102')) AS b, TRUE AS flag,
                   INTERVAL '2-3' YEAR TO MONTH AS ym, ROWID AS rid, CAST(NULL AS VARCHAR2(10)) AS nothing
              FROM dual
            """;

        var r = await Service().ExecuteAsync(s, Q(sql), NoBinds, null, default);

        Assert.Equal(ExecutionStatus.Succeeded, r.Status);
        await using var rs = r.ResultSet!;
        var kinds = rs.Columns.Select(c => c.Kind).ToList();
        Assert.Equal(
            [ColumnDataKind.Number, ColumnDataKind.Date, ColumnDataKind.TimestampTz, ColumnDataKind.Clob, ColumnDataKind.Raw,
             ColumnDataKind.Blob, ColumnDataKind.Boolean, ColumnDataKind.IntervalYearMonth, ColumnDataKind.RowId, ColumnDataKind.Text],
            kinds);
        var page = await rs.GetPageAsync(0, default);
        Assert.Equal("123456789012345678901234567890.123", page.Columns[0][0]);
        Assert.Equal(new DateTime(2024, 2, 29), page.Columns[1][0]);
        Assert.Equal(TimeSpan.FromHours(9), Assert.IsType<DateTimeOffset>(page.Columns[2][0]).Offset);
        var clob = Assert.IsType<LobValue>(page.Columns[3][0]);
        Assert.Equal("long text", clob.TextPreview);
        Assert.False(clob.IsTruncated);
        Assert.Equal(new byte[] { 0xCA, 0xFE }, page.Columns[4][0]);
        Assert.Equal(new byte[] { 1, 2 }, Assert.IsType<LobValue>(page.Columns[5][0]).BinaryPreview.ToArray());
        Assert.Equal(true, page.Columns[6][0]);
        Assert.IsType<string>(page.Columns[8][0]);
        Assert.Null(page.Columns[9][0]);
    }

    private async Task AdminAsync(string sql)
    {
        await using var other = new OracleConnection(
            new ConnectionProfile("o", "system", _db.DataSource).BuildConnectionString(_db.SystemPassword));
        await other.OpenAsync();
        await using var cmd = other.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<int> CountFromOtherSessionAsync(string table)
    {
        await using var other = new OracleConnection(
            new ConnectionProfile("o", "system", _db.DataSource).BuildConnectionString(_db.SystemPassword));
        await other.OpenAsync();
        await using var cmd = other.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }
}

internal sealed class ExecBus : IEventBus
{
    public List<OrandaEvent> Events { get; } = [];

    public IDisposable Subscribe<TEvent>(object owner, Action<TEvent> handler)
        where TEvent : OrandaEvent
        => throw new NotSupportedException();

    public void Publish<TEvent>(TEvent e)
        where TEvent : OrandaEvent
    {
        lock (Events)
        {
            Events.Add(e);
        }
    }
}

internal sealed class ExecInteraction : IExecutionInteraction
{
    public bool ConfirmAnswer { get; set; } = true;

    public IReadOnlyDictionary<string, string>? Substitutions { get; set; } = new Dictionary<string, string>();

    public ScriptErrorAction ScriptAnswer { get; set; } = ScriptErrorAction.Stop;

    public List<PolicyDecision> Confirms { get; } = [];

    public List<IReadOnlyList<string>> AskedNames { get; } = [];

    public int ScriptAsks { get; private set; }

    public Task<bool> ConfirmAsync(SqlStatement stmt, PolicyDecision decision, CancellationToken ct)
    {
        Confirms.Add(decision);
        return Task.FromResult(ConfirmAnswer);
    }

    public Task<IReadOnlyDictionary<string, string>?> AskSubstitutionsAsync(IReadOnlyList<string> names, CancellationToken ct)
    {
        AskedNames.Add(names);
        return Task.FromResult(Substitutions);
    }

    public Task<ScriptErrorAction> AskScriptErrorAsync(SqlStatement stmt, DbError failure, CancellationToken ct)
    {
        ScriptAsks++;
        return Task.FromResult(ScriptAnswer);
    }
}

/// <summary>드라이버가 없는 세션. 실행 엔진이 이 세션으로 드라이버 단계에 들어가면 ArgumentException 이 난다.</summary>
internal sealed class DriverlessSession(ConnectionProfile profile) : ISession
{
    public Guid Id { get; } = Guid.NewGuid();

    public ConnectionProfile Profile { get; } = profile;

    public ServerInfo Server { get; } = new("23.26.0.0.0", "FREE", "h", "s", "FREE", "FREEPDB1", true, 1,
        new ServerCapabilities(false, false, false, false, false));

    public SessionPurpose Purpose => SessionPurpose.Work;

    public string DbKey => "FREE/FREEPDB1/U";

    public SessionState State => SessionState.Idle;

    public bool HasPendingTransaction => false;

    /// <summary>Commit/Rollback 호출 수(드라이버 접촉).</summary>
    public int DriverTouches { get; private set; }

    public Task CommitAsync(CancellationToken ct)
    {
        DriverTouches++;
        return Task.CompletedTask;
    }

    public Task RollbackAsync(CancellationToken ct)
    {
        DriverTouches++;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
