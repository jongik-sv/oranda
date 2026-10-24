using System.Globalization;
using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Monitoring;
using Oranda.Core.Plans;
using Oranda.Core.Security;
using Oranda.Core.Sessions;
using Oranda.Core.Tuning;
using Oranda.Language;
using Oranda.Oracle.Catalog;
using Oranda.Oracle.Connection;
using Oranda.Oracle.Execution;
using Oranda.Oracle.IntegrationTests.Execution;
using Oranda.Oracle.IntegrationTests.Integration;
using Oranda.Oracle.Plans;
using Oranda.Oracle.Sessions;
using Oranda.Oracle.Tuning;

namespace Oranda.Oracle.IntegrationTests.Tuning;

// 소유: TSK-06-02. 튜닝 조회 서비스.

/// <summary>DB 없이 도는 부분: 벤치마크 문장 거르기, 사유 펼치기, 버킷·비율 계산, 트레이스 가능 버전, 세션 거부.</summary>
public class TuningLogicTests
{
    private static readonly LanguageService Language = new();

    private static OracleTuningService Service()
    {
        var exec = new OracleExecutionService(new ExecutionPolicy(), new ExecInteraction(), new ExecBus(), null, pageSize: 500);
        return new OracleTuningService(exec, new OracleExecutionPlanService(exec), Language);
    }

    [Theory]
    [InlineData("SELECT * FROM emp")]
    [InlineData("  with x as (select 1 a from dual) select a from x ;")]
    [InlineData("select /* hint */ count(*) from dual")]
    public void Select_and_with_queries_are_accepted(string sql)
    {
        var stmt = OracleTuningService.BenchmarkStatement(Language, sql);

        Assert.Equal(StatementKind.Query, stmt.Kind);
    }

    [Theory]
    [InlineData("DELETE FROM emp")]
    [InlineData("UPDATE emp SET sal = 0")]
    [InlineData("INSERT INTO emp SELECT * FROM emp")]
    [InlineData("MERGE INTO t USING s ON (t.id = s.id) WHEN MATCHED THEN UPDATE SET t.v = s.v")]
    [InlineData("BEGIN NULL; END;")]
    [InlineData("CREATE TABLE t (a NUMBER)")]
    [InlineData("SELECT * FROM emp FOR UPDATE")]
    [InlineData("select * from emp for  update nowait")]
    [InlineData("SELECT 1 FROM dual; SELECT 2 FROM dual;")]
    [InlineData("SELECT * FROM emp WHERE id = :id")]
    [InlineData("SELECT * FROM emp WHERE id = &id")]
    [InlineData("   ")]
    [InlineData("EXPLAIN PLAN FOR SELECT 1 FROM dual")]
    public void Anything_but_one_plain_select_is_rejected(string sql)
    {
        Assert.Throws<ArgumentException>(() => OracleTuningService.BenchmarkStatement(Language, sql));
    }

    [Fact]
    public void Shared_cursor_rows_unfold_to_one_reason_per_y_column()
    {
        string[] names = ["SQL_ID", "ADDRESS", "CHILD_ADDRESS", "CHILD_NUMBER", "UNBOUND_CURSOR", "BIND_MISMATCH", "OPTIMIZER_MISMATCH", "REASON", "CON_ID"];
        var rows = new List<object?[]>
        {
            new object?[] { "abc", new byte[] { 1 }, new byte[] { 2 }, 1m, "N", "Y", "Y", "<ChildNode/>", 3m },
            new object?[] { "abc", new byte[] { 1 }, new byte[] { 3 }, 0m, "N", "N", "N", null, 3m },
            new object?[] { "Y", new byte[] { 1 }, new byte[] { 4 }, 2m, "Y", "N", "N", "  ", 3m },
        };

        var reasons = OracleTuningService.ToReasons(names, rows);

        Assert.Equal([(1, "BIND_MISMATCH"), (1, "OPTIMIZER_MISMATCH"), (2, "UNBOUND_CURSOR")], reasons.Select(r => (r.ChildNumber, r.Reason)));
        Assert.Equal("<ChildNode/>", reasons[0].Detail);
        Assert.Null(reasons[2].Detail); // 빈 reason 은 상세 없음. SQL_ID 값 "Y" 는 사유가 아니다.
    }

    [Fact]
    public void Shared_cursor_without_reason_column_on_11g_still_unfolds()
    {
        string[] names = ["INST_ID", "SQL_ID", "CHILD_NUMBER", "ROLL_INVALID_MISMATCH"];

        var reasons = OracleTuningService.ToReasons(names, [new object?[] { 1m, "a", 4m, "Y" }]);

        var r = Assert.Single(reasons);
        Assert.Equal(4, r.ChildNumber);
        Assert.Equal("ROLL_INVALID_MISMATCH", r.Reason);
        Assert.Null(r.Detail);
        Assert.Empty(OracleTuningService.ToReasons(names, []));
    }

    [Fact]
    public void Histogram_frequency_is_difference_from_previous_endpoint()
    {
        var buckets = OracleTuningService.ToBuckets([(9000, "1"), (9100, "2"), (9100, "3"), (10000, null)]);

        Assert.Equal([9000L, 100, 0, 900], buckets.Select(b => b.Frequency));
        Assert.Null(buckets[3].EndpointValue);
        Assert.Empty(OracleTuningService.ToBuckets([]));
        Assert.Equal(0, OracleTuningService.ToBuckets([(5, "a"), (3, "b")])[1].Frequency); // 뒤집힌 순서는 0
    }

    [Fact]
    public void Ash_share_is_ratio_of_samples()
    {
        var rows = OracleTuningService.ToAshRows([("ON CPU", null, 3), ("db file sequential read", "User I/O", 1)]);

        Assert.Equal(0.75, rows[0].Share, 3);
        Assert.Equal(0.25, rows[1].Share, 3);
        Assert.Equal(0, OracleTuningService.ToAshRows([("x", null, 0)])[0].Share);
    }

    [Theory]
    [InlineData("11.2.0.2.0", false)]
    [InlineData("12.1.0.2.0", false)]
    [InlineData("12.2.0.1.0", true)]
    [InlineData("19.3.0.0.0", true)]
    [InlineData("23.26.0.0.0", true)]
    public void Trace_contents_are_readable_from_12_2(string version, bool readable)
    {
        var server = new ServerInfo(version, "X", "h", "s", "X", "P", false, 1, new ServerCapabilities(true, true, true, true, true));

        var reason = OracleTuningService.TraceUnavailableReason(server);

        Assert.Equal(readable, reason is null);
        if (!readable)
        {
            Assert.Contains("12.2", reason, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("/opt/oracle/diag/rdbms/free/FREE/trace/FREE_ora_12.trc", "FREE_ora_12.trc")]
    [InlineData(@"C:\app\diag\rdbms\orcl\orcl\trace\orcl_ora_7.trc", "orcl_ora_7.trc")]
    [InlineData("plain.trc", "plain.trc")]
    public void Trace_file_name_drops_the_directory(string path, string name)
    {
        Assert.Equal(name, OracleTuningService.TraceFileName(path));
    }

    [Fact]
    public async Task Non_oracle_session_and_bad_arguments_are_rejected_before_any_driver_call()
    {
        var service = Service();
        var session = new DriverlessSession(new ConnectionProfile("p", "u", "h:1521/s"));

        await Assert.ThrowsAsync<ArgumentException>(() => service.GetTableStatsAsync(session, "S", "T", default));
        await Assert.ThrowsAsync<ArgumentException>(() => service.GetChildCursorsAsync(session, "abc", default));
        await Assert.ThrowsAsync<ArgumentException>(() => service.GetChildPlanAsync(session, new CursorRef("abc"), "TYPICAL", default));
        await Assert.ThrowsAsync<ArgumentException>(() => service.RunBenchmarkAsync(session, new BenchmarkCase("x", "DELETE FROM t"), 1, default));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.RunBenchmarkAsync(session, new BenchmarkCase("x", "SELECT 1 FROM dual"), 0, default));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.RunBenchmarkAsync(session, new BenchmarkCase("x", "SELECT 1 FROM dual"), OracleTuningService.MaxIterations + 1, default));
        await Assert.ThrowsAsync<NotSupportedException>(() => service.ReadTraceLinesAsync(session, new TraceFileInfo("/x.trc", false, "11g"), default));
        Assert.Equal(0, session.DriverTouches);
    }
}

/// <summary>26ai 컨테이너: 통계 수집된 표·히스토그램·stale, 자식 커서와 사유, 자식 계획, 벤치마크, 트레이스, baseline 보기, 비 DBA 경로, 팩 관문.</summary>
[Trait("Category", "Integration")]
[Collection(OracleFreeShared.Name)]
public sealed class TuningServiceTests : IAsyncLifetime
{
    private readonly OracleFreeFixture _db;
    private readonly ExecBus _bus = new();
    private readonly ExecInteraction _ui = new();
    private readonly List<OracleConnection> _raw = [];
    private OracleSessionManager _manager = null!;
    private string _table = null!;

    public TuningServiceTests(OracleFreeFixture db) => _db = db;

    public async Task InitializeAsync()
    {
        _manager = new OracleSessionManager(_bus);
        _table = "TUNE_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

        // 시험 준비(DDL·DML·DBMS_STATS)는 시험 안에서만 한다. skew 열: 1 이 9,000행, 2~11 이 100행씩.
        var raw = await RawAsync();
        await ExecAsync(raw, $"CREATE TABLE {_table} (id NUMBER PRIMARY KEY, skew NUMBER, pad VARCHAR2(100))");
        await ExecAsync(raw, $"INSERT INTO {_table} SELECT level, CASE WHEN level <= 9000 THEN 1 ELSE MOD(level, 10) + 2 END, RPAD('x', 50, 'x') FROM dual CONNECT BY level <= 10000");
        await ExecAsync(raw, "COMMIT");
        await ExecAsync(raw, $"CREATE INDEX {_table}_SK ON {_table} (skew, id)");
        await ExecAsync(raw, $"BEGIN DBMS_STATS.GATHER_TABLE_STATS(USER, '{_table}', estimate_percent => 100, method_opt => 'FOR ALL COLUMNS SIZE 1 FOR COLUMNS SIZE 254 SKEW', cascade => TRUE); END;");
        await ExecAsync(raw, $"GRANT SELECT ON {_table} TO {OracleFreeFixture.AppUser}");
    }

    public async Task DisposeAsync()
    {
        foreach (var s in _manager.Sessions)
        {
            await s.DisposeAsync();
        }

        try
        {
            await ExecAsync(_raw[0], $"DROP TABLE {_table} PURGE");
        }
        finally
        {
            foreach (var c in _raw)
            {
                await c.DisposeAsync();
            }
        }
    }

    private OracleTuningService Service()
    {
        var exec = new OracleExecutionService(new ExecutionPolicy(), _ui, _bus, null, pageSize: 500);
        return new OracleTuningService(exec, new OracleExecutionPlanService(exec), new LanguageService());
    }

    private async Task<ISession> OpenAsync(string user, string password, SessionPurpose purpose = SessionPurpose.Monitor, LicensePack pack = LicensePack.None)
    {
        var profile = new ConnectionProfile("free-" + user, user, _db.DataSource) { LicensePack = pack };
        using var cred = new SecureCredential(password);
        return await _manager.OpenAsync(profile, cred, purpose, default);
    }

    private Task<ISession> SystemAsync(SessionPurpose purpose = SessionPurpose.Monitor, LicensePack pack = LicensePack.None) =>
        OpenAsync("system", _db.SystemPassword, purpose, pack);

    private async Task<OracleConnection> RawAsync()
    {
        var profile = new ConnectionProfile("free-system", "system", _db.DataSource);
        var conn = new OracleConnection(profile.BuildConnectionString(_db.SystemPassword));
        await conn.OpenAsync();
        _raw.Add(conn);
        return conn;
    }

    private static async Task ExecAsync(OracleConnection c, string sql)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(OracleConnection c, string sql, params OracleParameter[] parameters)
    {
        await using var cmd = c.CreateCommand();
        cmd.BindByName = true;
        cmd.CommandText = sql;
        cmd.Parameters.AddRange(parameters);
        var v = await cmd.ExecuteScalarAsync();
        return v is DBNull ? null : v;
    }

    private static async Task<string> SqlIdAsync(OracleConnection c, string text) =>
        (string)(await ScalarAsync(c, "SELECT sql_id FROM v$sql WHERE sql_text = :t AND ROWNUM = 1",
            new OracleParameter("t", OracleDbType.Varchar2) { Value = text }))!;

    /// <summary>공유 풀에 이 본문 그대로의 커서가 몇 개 있는가(보내지 않았음을 확인).</summary>
    private static async Task<int> CursorCountAsync(OracleConnection c, string text) =>
        Convert.ToInt32(await ScalarAsync(c, "SELECT COUNT(*) FROM v$sql WHERE DBMS_LOB.COMPARE(sql_fulltext, :t) = 0",
            new OracleParameter("t", OracleDbType.Clob) { Value = text }), CultureInfo.InvariantCulture);

    [Fact]
    public async Task Table_column_histogram_and_index_statistics_are_read_and_stale_is_detected()
    {
        var s = await SystemAsync();
        var service = Service();

        var table = await service.GetTableStatsAsync(s, "SYSTEM", _table, default);
        Assert.NotNull(table);
        Assert.Equal(10000, table.NumRows);
        Assert.True(table.Blocks > 0);
        Assert.True(table.AvgRowLen > 0);
        Assert.NotNull(table.LastAnalyzed);
        Assert.False(table.Stale);

        var columns = await service.GetColumnStatsAsync(s, "SYSTEM", _table, default);
        Assert.Equal(["ID", "SKEW", "PAD"], columns.Select(c => c.ColumnName));
        var skew = columns[1];
        Assert.Equal("FREQUENCY", skew.Histogram);
        Assert.Equal(11, skew.NumDistinct);
        Assert.Equal(11, skew.NumBuckets);
        Assert.Equal(0, skew.NumNulls);
        Assert.Equal("NONE", columns[0].Histogram);
        Assert.Equal(10000, columns[0].NumDistinct);

        var buckets = await service.GetHistogramAsync(s, "SYSTEM", _table, "SKEW", default);
        Assert.Equal(11, buckets.Count);
        Assert.Equal(10000, buckets.Sum(b => b.Frequency));
        Assert.Equal(9000, buckets[0].Frequency);
        Assert.Equal("1", buckets[0].EndpointValue);

        var indexes = await service.GetIndexStatsAsync(s, "SYSTEM", _table, default);
        Assert.Equal(2, indexes.Count);
        var sk = Assert.Single(indexes, i => i.IndexName == _table + "_SK");
        Assert.Equal("NONUNIQUE", sk.Uniqueness);
        Assert.Equal(10000, sk.DistinctKeys);
        Assert.True(sk.LeafBlocks > 0);
        Assert.NotNull(sk.ClusteringFactor);
        Assert.Contains(indexes, i => i.Uniqueness == "UNIQUE");

        var indexColumns = await service.GetIndexColumnsAsync(s, "SYSTEM", _table, default);
        Assert.Equal(["SKEW", "ID"], Assert.Single(indexColumns, i => i.IndexName == _table + "_SK").Columns);
        Assert.Contains(indexColumns, i => i.Columns.SequenceEqual(["ID"]));

        // 없는 표.
        Assert.Null(await service.GetTableStatsAsync(s, "SYSTEM", _table + "_NONE", default));
        Assert.Empty(await service.GetColumnStatsAsync(s, "SYSTEM", _table + "_NONE", default));
        Assert.InRange((await service.GetHistogramAsync(s, "SYSTEM", _table, "PAD", default)).Count, 0, 2); // 히스토그램 없는 열은 최소·최대 두 끝점뿐.
        Assert.Empty(await service.GetHistogramAsync(s, "SYSTEM", _table, "NO_SUCH", default));

        // 행을 절반 더 넣고 DML 모니터링을 비우면 stale.
        var raw = _raw[0];
        await ExecAsync(raw, $"INSERT INTO {_table} SELECT 10000 + level, 99, 'y' FROM dual CONNECT BY level <= 5000");
        await ExecAsync(raw, "COMMIT");
        await ExecAsync(raw, "BEGIN DBMS_STATS.FLUSH_DATABASE_MONITORING_INFO; END;");
        var stale = await service.GetTableStatsAsync(s, "SYSTEM", _table, default);
        Assert.True(stale!.Stale);
        Assert.Equal(10000, stale.NumRows); // 통계는 다시 모으기 전 값.
    }

    [Fact]
    public async Task Non_dba_account_reads_statistics_through_all_views()
    {
        var app = await OpenAsync(OracleFreeFixture.AppUser, _db.AppPassword);
        Assert.False(app.Server.Capabilities.CanQueryDictionary);
        var service = Service();

        var table = await service.GetTableStatsAsync(app, "SYSTEM", _table, default);
        Assert.Equal(10000, table!.NumRows);
        Assert.Equal(11, (await service.GetHistogramAsync(app, "SYSTEM", _table, "SKEW", default)).Count);
        Assert.Equal(3, (await service.GetColumnStatsAsync(app, "SYSTEM", _table, default)).Count);
        // all_ind_statistics 는 남의 인덱스를 보이지 않아 값은 all_indexes 에서 온다.
        var indexes = await service.GetIndexStatsAsync(app, "SYSTEM", _table, default);
        Assert.Equal(2, indexes.Count);
        Assert.Equal(10000, Assert.Single(indexes, i => i.IndexName == _table + "_SK").DistinctKeys);
        Assert.Equal(2, (await service.GetIndexColumnsAsync(app, "SYSTEM", _table, default)).Count);

        // 계획 관리 뷰(dba_sql_*)는 권한이 없어 빈 목록.
        Assert.Empty(await service.GetSqlPlanControlsAsync(app, "0000000000000", default));
    }

    [Fact]
    public async Task Different_bind_types_make_two_child_cursors_with_a_mismatch_reason_and_their_own_plans()
    {
        var tag = "TCHILD_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var text = $"SELECT /* {tag} */ COUNT(*) FROM {_table} WHERE skew = :b";
        var raw = await RawAsync();
        foreach (var p in new[]
        {
            new OracleParameter("b", OracleDbType.Int32) { Value = 1 },
            new OracleParameter("b", OracleDbType.Varchar2) { Value = "3" },
        })
        {
            await using var cmd = raw.CreateCommand();
            cmd.BindByName = true;
            cmd.CommandText = text;
            cmd.Parameters.Add(p);
            await cmd.ExecuteScalarAsync();
        }

        var sqlId = await SqlIdAsync(raw, text);
        var s = await SystemAsync();
        var service = Service();

        var children = await service.GetChildCursorsAsync(s, sqlId, default);
        Assert.True(children.Count >= 2, $"자식 {children.Count}");
        Assert.All(children, c => Assert.Equal(1, c.Executions));
        Assert.All(children, c => Assert.NotNull(c.InstanceId));
        Assert.True(children[0].BufferGets > 0);
        Assert.NotNull(children[0].GetsPerExec);

        var reasons = await service.GetSharedCursorReasonsAsync(s, sqlId, default);
        Assert.Contains(reasons, r => r.ChildNumber >= 1 && r.Reason.Contains("BIND", StringComparison.Ordinal));
        Assert.DoesNotContain(reasons, r => r.Reason is "SQL_ID" or "CHILD_NUMBER" or "REASON");

        foreach (var child in children.Take(2))
        {
            var plan = await service.GetChildPlanAsync(s, new CursorRef(sqlId, child.ChildNumber, child.InstanceId), "ALLSTATS LAST", default);
            Assert.Equal(child.ChildNumber, plan.Cursor!.ChildNumber);
            Assert.NotEmpty(plan.Steps);
            Assert.Equal(child.PlanHashValue, plan.PlanHashValue);
        }

        await Assert.ThrowsAsync<ArgumentException>(() => service.GetChildPlanAsync(s, new CursorRef(sqlId), "TYPICAL", default));
        Assert.Empty(await service.GetChildCursorsAsync(s, "0000000000000", default));

        // baseline 을 하나 올리면 계획 관리 보기에 보인다(시험 준비용 DBMS_SPM, 끝나면 지운다).
        await using (var load = raw.CreateCommand())
        {
            load.BindByName = true;
            load.CommandText = "BEGIN :n := DBMS_SPM.LOAD_PLANS_FROM_CURSOR_CACHE(sql_id => :id); END;";
            var n = new OracleParameter("n", OracleDbType.Int32) { Direction = System.Data.ParameterDirection.Output };
            load.Parameters.Add(n);
            load.Parameters.Add(new OracleParameter("id", OracleDbType.Varchar2) { Value = sqlId });
            await load.ExecuteNonQueryAsync();
            Assert.True(Convert.ToInt32(n.Value.ToString(), CultureInfo.InvariantCulture) >= 1);
        }

        try
        {
            var controls = await service.GetSqlPlanControlsAsync(s, sqlId, default);
            var baseline = Assert.Single(controls.Where(c => c.Kind == SqlPlanControlKind.Baseline).Take(1));
            Assert.Equal("ENABLED", baseline.Status);
            Assert.True(baseline.Accepted);
            Assert.Contains(tag, baseline.SqlText, StringComparison.Ordinal);
            Assert.True(baseline.Created > DateTime.Now.AddDays(-1));
        }
        finally
        {
            await ExecAsync(raw, $"""
                DECLARE n PLS_INTEGER;
                BEGIN
                  FOR b IN (SELECT DISTINCT sql_handle FROM dba_sql_plan_baselines WHERE sql_text LIKE '%{tag}%') LOOP
                    n := DBMS_SPM.DROP_SQL_PLAN_BASELINE(sql_handle => b.sql_handle);
                  END LOOP;
                END;
                """);
        }
    }

    [Fact]
    public async Task Benchmark_runs_two_selects_n_times_and_rejects_dml_without_sending_it()
    {
        var tag = "TBENCH_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var full = new BenchmarkCase("원본", $"SELECT /* {tag} full */ /*+ FULL(x) */ COUNT(pad) FROM {_table} x WHERE skew = 3");
        var index = new BenchmarkCase("변형 1", $"SELECT /* {tag} index */ /*+ INDEX(x {_table}_SK) */ COUNT(*) FROM {_table} x WHERE skew = 3");
        var work = await SystemAsync(SessionPurpose.Work);
        var service = Service();

        var a = await service.RunBenchmarkAsync(work, full, 3, default);
        var b = await service.RunBenchmarkAsync(work, index, 3, default);

        var raw = _raw[0];
        foreach (var (result, bench) in new[] { (a, full), (b, index) })
        {
            Assert.Same(bench, result.Case);
            Assert.Equal(3, result.Iterations);
            Assert.Equal(1, result.Rows);
            Assert.True(result.LogicalReads > 0, bench.Label);
            Assert.True(result.Elapsed > TimeSpan.Zero);
            Assert.Equal(3, Convert.ToInt32(await ScalarAsync(raw, "SELECT SUM(executions) FROM v$sql WHERE sql_text = :t",
                new OracleParameter("t", OracleDbType.Varchar2) { Value = bench.Sql }), CultureInfo.InvariantCulture));
            var planHash = Convert.ToInt64(await ScalarAsync(raw, "SELECT plan_hash_value FROM v$sql WHERE sql_text = :t AND ROWNUM = 1",
                new OracleParameter("t", OracleDbType.Varchar2) { Value = bench.Sql }), CultureInfo.InvariantCulture);
            Assert.Equal(planHash, result.PlanHashValue);
        }

        Assert.NotEqual(a.PlanHashValue, b.PlanHashValue);
        Assert.True(a.LogicalReads > b.LogicalReads, $"FULL {a.LogicalReads} vs INDEX {b.LogicalReads}");

        // SELECT 가 아니면 거부하고 보내지 않는다.
        var delete = $"DELETE /* {tag} */ FROM {_table}";
        await Assert.ThrowsAsync<ArgumentException>(() => service.RunBenchmarkAsync(work, new BenchmarkCase("x", delete), 1, default));
        await Assert.ThrowsAsync<ArgumentException>(() => service.RunBenchmarkAsync(work, new BenchmarkCase("x", $"SELECT * FROM {_table} FOR UPDATE"), 1, default));
        Assert.Equal(0, await CursorCountAsync(raw, delete));
        Assert.Equal(10000m, await ScalarAsync(raw, $"SELECT COUNT(*) FROM {_table}"));
        Assert.False(work.HasPendingTransaction);
    }

    [Fact]
    public async Task Wait_sample_trace_file_and_trace_contents_are_read_for_a_traced_session()
    {
        var tag = "TTRACE_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var traced = await RawAsync();
        await ExecAsync(traced, $"ALTER SESSION SET tracefile_identifier = '{tag}'");
        await ExecAsync(traced, "ALTER SESSION SET EVENTS '10046 trace name context forever, level 8'");
        var text = $"SELECT /* {tag} */ COUNT(*) FROM {_table}";
        Assert.Equal(10000m, await ScalarAsync(traced, text));
        await ExecAsync(traced, "ALTER SESSION SET EVENTS '10046 trace name context off'");

        SessionKey key;
        await using (var cmd = traced.CreateCommand())
        {
            cmd.CommandText = "SELECT inst_id, sid, serial# FROM gv$session WHERE sid = TO_NUMBER(SYS_CONTEXT('USERENV', 'SID')) AND inst_id = TO_NUMBER(SYS_CONTEXT('USERENV', 'INSTANCE'))";
            await using var reader = await cmd.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            key = new SessionKey(reader.GetInt32(0), reader.GetInt64(1), reader.GetInt64(2));
        }

        var s = await SystemAsync();
        var service = Service();

        var mine = Assert.Single(await service.SampleWaitsAsync(s, null, default));
        Assert.True(mine.Sid > 0);
        Assert.True(mine.SampledAt > DateTime.Now.AddMinutes(-1));

        var file = await service.GetTraceFileAsync(s, key, default);
        Assert.True(file.Readable);
        Assert.Null(file.UnavailableReason);
        Assert.Contains(tag, file.Path, StringComparison.Ordinal);
        Assert.EndsWith(".trc", file.Path, StringComparison.Ordinal);

        var lines = await service.ReadTraceLinesAsync(s, file, default);
        Assert.Contains(lines, l => l.StartsWith("PARSING IN CURSOR #", StringComparison.Ordinal));
        var summary = TraceParser.Parse(lines);
        var st = Assert.Single(summary.Statements, x => x.SqlText.Contains(tag, StringComparison.Ordinal) && x.Depth == 0);
        Assert.Equal(1, st.ExecuteCount);
        Assert.True(st.FetchCount >= 1);
        Assert.Equal(1, st.Rows);
        Assert.True(st.Query > 0);
        Assert.NotNull(st.SqlId);
        Assert.Contains(summary.Waits, w => w.Event.StartsWith("SQL*Net", StringComparison.Ordinal));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetTraceFileAsync(s, new SessionKey(key.InstanceId, key.Sid, key.Serial + 99999), default));
    }

    [Fact]
    public async Task Awr_and_ash_are_not_sent_without_a_declared_pack()
    {
        var catalog = EmbeddedCatalog.Load();
        var none = await SystemAsync();
        var service = Service();
        var raw = _raw[0];
        var awrText = catalog.Get("tuning.awr_plan_history", none.Server).Sql;
        var ashText = catalog.Get("tuning.ash_waits", none.Server).Sql;
        var before = (await CursorCountAsync(raw, awrText), await CursorCountAsync(raw, ashText));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetAwrPlanHistoryAsync(none, "0000000000000", default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetAshWaitsAsync(none, "0000000000000", DateTime.Now.AddHours(-1), default));
        Assert.Equal(before, (await CursorCountAsync(raw, awrText), await CursorCountAsync(raw, ashText)));

        // 선언이 있고 서버 설정도 허용하면 보낸다(대조군: 위의 세기가 실제로 커서를 본다는 것).
        var declared = await SystemAsync(pack: LicensePack.Diagnostics);
        var serverAllows = ExecutionPolicy.ParseServerPackAccess(declared.Server.ManagementPackAccess) is not { } server || server >= LicensePack.Diagnostics;
        if (serverAllows)
        {
            Assert.Empty(await service.GetAwrPlanHistoryAsync(declared, "0000000000000", default));
            Assert.Empty(await service.GetAshWaitsAsync(declared, "0000000000000", DateTime.Now.AddHours(-1), default));
            Assert.True(await CursorCountAsync(raw, awrText) > before.Item1);
            Assert.True(await CursorCountAsync(raw, ashText) > before.Item2);
        }
        else
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetAwrPlanHistoryAsync(declared, "0000000000000", default));
        }
    }
}
