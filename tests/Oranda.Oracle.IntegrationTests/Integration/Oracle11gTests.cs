using System.Globalization;
using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Catalog;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Grid;
using Oranda.Core.Metadata;
using Oranda.Core.Monitoring;
using Oranda.Core.Plans;
using Oranda.Core.Security;
using Oranda.Core.Sessions;
using Oranda.Oracle.Catalog;
using Oranda.Oracle.Connection;
using Oranda.Oracle.Execution;
using Oranda.Oracle.Grid;
using Oranda.Oracle.IntegrationTests.Execution;
using Oranda.Oracle.IntegrationTests.Metadata;
using Oranda.Oracle.IntegrationTests.Sessions;
using Oranda.Oracle.Metadata;
using Oranda.Oracle.Monitoring;
using Oranda.Oracle.Plans;
using Oranda.Oracle.Schema;
using Oranda.Oracle.Sessions;

namespace Oranda.Oracle.IntegrationTests.Integration;

// 소유: TSK-03-07. Oracle 11.2 서버(gvenzl/oracle-xe:11 = 11.2.0.2 XE)에서 카탈로그 전 항목과 핵심 흐름.

/// <summary>
/// 11g 서버 하나를 <see cref="Oracle11gShared"/> 컬렉션이 공유한다(26ai 의 <see cref="OracleFreeFixture"/> 와 같은 방식).
/// <para>공용 DB: <c>ORANDA_IT_11G_DATASOURCE</c>(예 <c>127.0.0.1:1522/XE</c>)·<c>ORANDA_IT_11G_SYSTEM_PASSWORD</c> 가 모두 있으면
/// 그 DB 에 붙고 끝나도 그대로 둔다. 없으면 Testcontainers 로 <c>gvenzl/oracle-xe:11</c>(풀 이미지. slim 은 DBMS_METADATA 스타일시트가 없어 DDL 이 안 된다)을 띄우고 끝나면 지운다.</para>
/// 시험 계정은 실행마다 고유 이름(<c>O11_xxxxxxxx</c>)으로 만들고 끝날 때 지운다. 비밀번호는 출력·저장하지 않는다.
/// </summary>
public sealed class Oracle11gFixture : IAsyncLifetime
{
    private IContainer? _container;

    public string DataSource { get; private set; } = string.Empty;

    public string SystemPassword { get; private set; } = string.Empty;

    /// <summary>객체를 가진 비 DBA 계정(all_ 뷰 경로).</summary>
    public string Owner { get; } = "O11_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    /// <summary>서버에 DBMS_METADATA 스타일시트가 없다(gvenzl 11g slim 이미지). 그때 GET_DDL 은 ORA-39212.</summary>
    public bool MetadataStylesheetsMissing { get; private set; }

    public string OwnerPassword { get; } = "Ow" + Guid.NewGuid().ToString("N")[..16] + "x1";

    public async Task InitializeAsync()
    {
        // 개발 PC 의 podman 포트 중계(gvproxy)는 긴급(OOB) 데이터를 버리고, 11g 서버는 OOB 를 쓰는 접속에서 in-band 브레이크를 읽지 않는다.
        // 그 경로에서 실행 중 취소를 보내면 드라이버가 서버의 브레이크 응답을 끝없이 기다린다. 이 시험 프로세스만 in-band 로 바꾼다.
        if (Environment.GetEnvironmentVariable("ORANDA_IT_11G_DISABLE_OOB") == "1")
        {
            OracleConfiguration.DisableOOB = true;
        }

        if (Environment.GetEnvironmentVariable("ORANDA_IT_11G_DATASOURCE") is { Length: > 0 } ds
            && Environment.GetEnvironmentVariable("ORANDA_IT_11G_SYSTEM_PASSWORD") is { Length: > 0 } pw)
        {
            DataSource = ds;
            SystemPassword = pw;
        }
        else
        {
            SystemPassword = "Pw" + Guid.NewGuid().ToString("N")[..16] + "x1";
            _container = new ContainerBuilder("gvenzl/oracle-xe:11")
                .WithEnvironment("ORACLE_PASSWORD", SystemPassword)
                .WithPortBinding(1521, true)
                .WithCreateParameterModifier(p => (p.HostConfig ??= new()).ShmSize = 1L << 30) // XE 11g 는 공유 메모리 1 GB 가 필요하다
                .WithWaitStrategy(Wait.ForUnixContainer())
                .Build();
            await _container.StartAsync();
            DataSource = $"{_container.Hostname}:{_container.GetMappedPublicPort(1521)}/XE";
        }

        await using var conn = new ConnectionProfile("ora11-system", "system", DataSource).CreateConnection(SystemPassword);
        await OpenWhenReadyAsync(conn, TimeSpan.FromMinutes(5));
        await EnsureMetadataStylesheetsAsync(conn);
        foreach (var sql in new[]
        {
            $"CREATE USER {Owner} IDENTIFIED BY \"{OwnerPassword}\" DEFAULT TABLESPACE users QUOTA UNLIMITED ON users",
            $"GRANT CREATE SESSION, CREATE TABLE, CREATE VIEW, CREATE PROCEDURE, CREATE SEQUENCE, CREATE TRIGGER TO {Owner}",
        })
        {
            await ExecAsync(conn, sql);
        }
    }

    public async Task DisposeAsync()
    {
        try
        {
            await using var conn = await SystemAsync();
            await ExecAsync(conn, $"DROP USER {Owner} CASCADE");
        }
        catch (OracleException)
        {
            // 공용 DB 에 계정이 남으면 다음 실행 전에 SYSTEM 으로 지워도 된다(O11_*).
        }

        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    public async Task<OracleConnection> SystemAsync()
    {
        var conn = new ConnectionProfile("ora11-system", "system", DataSource).CreateConnection(SystemPassword);
        await conn.OpenAsync();
        return conn;
    }

    public async Task<OracleConnection> OwnerAsync()
    {
        var conn = new ConnectionProfile("ora11-owner", Owner, DataSource).CreateConnection(OwnerPassword);
        await conn.OpenAsync();
        return conn;
    }

    public static async Task ExecAsync(OracleConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// gvenzl 11g slim 이미지는 DBMS_METADATA 의 XSL 스타일시트가 비어 있어 GET_DDL 이 ORA-39212 를 낸다(이미지 문제, 실제 11.2 DB 는 정상).
    /// 그때만 SYS 로 스타일시트를 다시 싣는다. SYS 비밀번호는 컨테이너 비밀번호와 같다(gvenzl).
    /// </summary>
    private async Task EnsureMetadataStylesheetsAsync(OracleConnection system)
    {
        try
        {
            await using var cmd = system.CreateCommand();
            cmd.CommandText = "SELECT DBMS_METADATA.GET_DDL('TABLE', 'HELP', 'SYSTEM') FROM dual";
            await cmd.ExecuteScalarAsync();
        }
        catch (OracleException ex) when (ex.Number == 39212)
        {
            var sys = new OracleConnectionStringBuilder(new ConnectionProfile("ora11-sys", "sys", DataSource).BuildConnectionString(SystemPassword))
            {
                DBAPrivilege = "SYSDBA",
            };
            await using var conn = ConnectionProfileExtensions.CreateConnection(sys.ConnectionString);
            await conn.OpenAsync();
            try
            {
                await ExecAsync(conn, "BEGIN SYS.DBMS_METADATA_UTIL.LOAD_STYLESHEETS; END;");
            }
            catch (OracleException load) when (load.Number == 31609)
            {
                // slim 이미지는 xsl 파일까지 지웠다. DDL 시험은 이 사실을 단정으로 바꾼다(풀 이미지 gvenzl/oracle-xe:11 로 돌리면 정상 단정).
                MetadataStylesheetsMissing = true;
            }
        }
    }

    private static async Task OpenWhenReadyAsync(OracleConnection conn, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            try
            {
                await conn.OpenAsync();
                return;
            }
            catch (OracleException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(3));
            }
        }
    }
}

[CollectionDefinition(Name)]
public sealed class Oracle11gShared : ICollectionFixture<Oracle11gFixture>
{
    public const string Name = "Oracle11g";
}

[Trait("Category", "Integration")]
[Collection(Oracle11gShared.Name)]
public sealed class Oracle11gTests : IAsyncLifetime
{
    private readonly Oracle11gFixture _db;
    private readonly ExecBus _bus = new();
    private readonly ExecInteraction _ui = new();
    private readonly List<OracleConnection> _raw = [];
    private OracleSessionManager _manager = null!;

    public Oracle11gTests(Oracle11gFixture db) => _db = db;

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

    private async Task<OracleSession> OpenSystemAsync(SessionPurpose purpose = SessionPurpose.Work)
    {
        using var cred = new SecureCredential(_db.SystemPassword);
        return (OracleSession)await _manager.OpenAsync(new ConnectionProfile("ora11-system", "system", _db.DataSource), cred, purpose, default);
    }

    private async Task<OracleSession> OpenOwnerAsync()
    {
        using var cred = new SecureCredential(_db.OwnerPassword);
        return (OracleSession)await _manager.OpenAsync(new ConnectionProfile("ora11-owner", _db.Owner, _db.DataSource), cred, SessionPurpose.Work, default);
    }

    private async Task<OracleConnection> RawSystemAsync()
    {
        var c = await _db.SystemAsync();
        _raw.Add(c);
        return c;
    }

    private async Task<OracleConnection> RawOwnerAsync()
    {
        var c = await _db.OwnerAsync();
        _raw.Add(c);
        return c;
    }

    private OracleExecutionService Exec(int pageSize = 500) => new(new ExecutionPolicy(), _ui, _bus, null, pageSize);

    private static SqlStatement Q(string text) => new(text, default, StatementKind.Query) { Verb = "SELECT" };

    private static readonly Dictionary<string, BindValue> NoBinds = [];

    private static string Unique(string prefix) => prefix + "_" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    // ---- 접속·관문 ----

    [Fact]
    public async Task Server_11_2_is_allowed_and_probed_without_12c_features()
    {
        await using var raw = await RawSystemAsync();
        Assert.StartsWith("11.2.", raw.ServerVersion, StringComparison.Ordinal);
        Assert.True(SupportedServerPolicy.IsSupported(raw.ServerVersion, out var reason), reason);

        var info = await ServerProbe.ProbeAsync(raw);
        Assert.Equal(11, info.MajorVersion);
        Assert.False(info.IsAtLeast12c);
        Assert.False(info.IsMultitenant);
        Assert.Null(info.ContainerName);
        Assert.False(info.IsAutonomous);
        Assert.Equal("Express", info.Edition);
        Assert.True(info.Capabilities.IsDba);
        Assert.True(info.Capabilities.CanQueryPerformanceViews);
        Assert.False(info.Features.Release12Columns);
        Assert.False(info.Features.Multitenant);

        // 세션 관리자 관문을 지나 세션이 열린다. 캐시 키의 컨테이너 자리는 「-」.
        await using var s = await OpenSystemAsync();
        Assert.Equal(11, s.Server.MajorVersion);
        Assert.Contains("/-/", s.DbKey, StringComparison.Ordinal);
        Assert.Equal(SessionState.Idle, s.State);
    }

    // ---- 카탈로그 전 항목 ----

    [Fact]
    public async Task Every_catalog_query_parses_on_11_2_for_dba_and_all_views()
    {
        var catalog = EmbeddedCatalog.Load();
        await using var raw = await RawSystemAsync();
        var info = await ServerProbe.ProbeAsync(raw);
        var failures = new List<string>();
        var checkedCount = 0;

        // 일부러 19 이상 전용인 id(예: session.my_blocking)는 11 변형이 없고 기능을 숨긴다. 단위 시험 Oracle11CatalogTests 가 목록을 단정한다.
        foreach (var id in catalog.Entries.Where(e => e.MinVersion < 12).Select(e => e.Id).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var query = catalog.Get(id, info);
            Assert.Equal(11, query.Header.MinVersion);
            if (query.Header.License != CatalogLicense.None)
            {
                continue; // 팩이 필요한 문장은 보유 선언 없이 서버로 보내지 않는다(SAFE-07).
            }

            foreach (var prefix in new[] { "dba", "all" })
            {
                var sql = query.Sql.Replace("@PFX@", prefix, StringComparison.Ordinal)
                    .Replace("@SEGMENTS@", "dba_segments", StringComparison.Ordinal);
                try
                {
                    await ParseOnlyAsync(raw, sql);
                    checkedCount++;
                }
                catch (OracleException ex)
                {
                    failures.Add($"{query.Header.Path} ({prefix}): {ex.Message}");
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        Assert.True(checkedCount >= 100, $"검사한 문장 {checkedCount}");
    }

    /// <summary>DBMS_SQL.PARSE 로 구문·열 이름만 확인한다(실행하지 않으므로 바인드 값이 없어도 된다).</summary>
    private static async Task ParseOnlyAsync(OracleConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.BindByName = true;
        cmd.CommandText = """
            DECLARE
              c INTEGER := DBMS_SQL.OPEN_CURSOR;
            BEGIN
              DBMS_SQL.PARSE(c, :q, DBMS_SQL.NATIVE);
              DBMS_SQL.CLOSE_CURSOR(c);
            EXCEPTION
              WHEN OTHERS THEN
                IF DBMS_SQL.IS_OPEN(c) THEN
                  DBMS_SQL.CLOSE_CURSOR(c);
                END IF;
                RAISE;
            END;
            """;
        cmd.Parameters.Add(new OracleParameter("q", OracleDbType.Clob) { Value = sql });
        await cmd.ExecuteNonQueryAsync();
    }

    // ---- SQL 실행·결과 그리드·취소 ----

    [Fact]
    public async Task Query_pages_in_grid_and_server_error_is_shown()
    {
        await using var s = await OpenSystemAsync();
        var r = await Exec(100).ExecuteAsync(s, Q("SELECT level AS n, '한글' || level AS name FROM dual CONNECT BY level <= 250"), NoBinds, null, default);
        Assert.Equal(ExecutionStatus.Succeeded, r.Status);
        await using (var rs = Assert.IsType<OracleResultSet>(r.ResultSet))
        {
            Assert.Equal(["N", "NAME"], rs.Columns.Select(c => c.Name));
            var p0 = await rs.GetPageAsync(0, default);
            Assert.Equal(100, p0.RowCount);
            Assert.Equal("한글100", p0.Columns[1][99]);
            var p2 = await rs.GetPageAsync(2, default);
            Assert.Equal(50, p2.RowCount);
            Assert.True(p2.IsLast);
            Assert.Equal(250, rs.KnownRowCount);
        }

        // 서버 오류는 그대로 보인다.
        var bad = await Exec().ExecuteAsync(s, Q("SELECT * FROM no_such_table_11g"), NoBinds, null, default);
        Assert.Equal(ExecutionStatus.Failed, bad.Status);
        Assert.Equal(942, bad.Error?.Number);
        Assert.Equal(SessionState.Idle, s.State);
    }

    /// <summary>
    /// 실행 중 취소·가져오기 중 취소. OOB 가 통하지 않는 경로(개발 PC 의 podman 포트 중계)에서 11g 는 브레이크 응답이 오지 않아 끝나지 않으므로,
    /// <c>ORANDA_IT_11G_DISABLE_OOB=1</c>(이 프로세스만 in-band 브레이크) 이거나 OOB 가 통하는 경로(<c>ORANDA_IT_CANCEL_STRICT=1</c>)일 때만 돈다.
    /// 둘 다 없으면 아무것도 하지 않고 끝난다(결과 보고에 「미실행」).
    /// </summary>
    [Fact]
    public async Task Cancel_running_query_and_fetch_keep_session_usable()
    {
        if (!OracleConfiguration.DisableOOB && Environment.GetEnvironmentVariable("ORANDA_IT_CANCEL_STRICT") != "1")
        {
            return;
        }

        await using var s = await OpenSystemAsync();
        // 오래 도는 질의(이 서버에서 혼자 끝나면 약 8 s)를 1.5 s 뒤 취소한다.
        using var cts = new CancellationTokenSource();
        var task = Exec().ExecuteAsync(s,
            Q("SELECT COUNT(*) FROM (SELECT 1 FROM dual CONNECT BY level <= 3000) a, (SELECT 1 FROM dual CONNECT BY level <= 3000) b, " +
              "(SELECT 1 FROM dual CONNECT BY level <= 20) c"), NoBinds, null, cts.Token);
        await Task.Delay(1500);
        Assert.Equal(SessionState.Busy, s.State);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await cts.CancelAsync();
        var cancelled = await task.WaitAsync(TimeSpan.FromSeconds(60));
        sw.Stop();
        Assert.Equal(ExecutionStatus.Cancelled, cancelled.Status);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"취소 반영 {sw.ElapsedMilliseconds} ms (혼자 끝나면 약 8 s)");
        Assert.Equal(SessionState.Idle, s.State);
        await AssertRunsNextStatementAsync(s);

        // 큰 결과를 가져오는 중 취소: 끊긴 뒤에도 같은 결과를 행 빠짐 없이 이어 가져온다.
        var r = await Exec().ExecuteAsync(s, Q("""
            SELECT ROWNUM AS n, RPAD('x', 200, 'x') AS pad
              FROM (SELECT 1 FROM dual CONNECT BY level <= 3000), (SELECT 1 FROM dual CONNECT BY level <= 3000)
            """), NoBinds, null, default);
        var rs = Assert.IsType<OracleResultSet>(r.ResultSet);
        using var fetchCts = new CancellationTokenSource();
        var fetch = rs.GetPageAsync(17000, fetchCts.Token);
        await Task.Delay(1000);
        Assert.False(fetch.IsCompleted);
        await fetchCts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fetch.WaitAsync(TimeSpan.FromSeconds(60)));
        var next = await rs.GetPageAsync(rs.NextPageIndex, default);
        Assert.Equal(500, next.RowCount);
        Assert.Equal(next.FirstRowIndex + 1, Convert.ToInt64(next.Columns[0][0], CultureInfo.InvariantCulture));
        await rs.DisposeAsync();
        await AssertRunsNextStatementAsync(s);
    }

    private async Task AssertRunsNextStatementAsync(OracleSession s)
    {
        var after = await Exec().ExecuteAsync(s, Q("SELECT 1 FROM dual"), NoBinds, null, default);
        Assert.Equal(ExecutionStatus.Succeeded, after.Status);
        Assert.Equal(1m, (await after.ResultSet!.GetPageAsync(0, default)).Columns[0][0]);
        await after.ResultSet.DisposeAsync();
    }

    [Fact]
    public async Task Dml_rolls_back_and_dbms_output_is_collected()
    {
        var table = Unique("DML");
        await using var s = await OpenOwnerAsync();
        var exec = Exec();
        await exec.ExecuteAsync(s, new SqlStatement($"CREATE TABLE {table} (id NUMBER)", default, StatementKind.Ddl) { Verb = "CREATE" }, NoBinds, null, default);
        try
        {
            var ins = await exec.ExecuteAsync(s, new SqlStatement($"INSERT INTO {table} VALUES (1)", default, StatementKind.Dml) { Verb = "INSERT" }, NoBinds, null, default);
            Assert.Equal(1, ins.RowsAffected);
            await exec.ExecuteAsync(s, new SqlStatement("ROLLBACK", default, StatementKind.TransactionControl) { Verb = "ROLLBACK" }, NoBinds, null, default);
            var count = await exec.ExecuteAsync(s, Q($"SELECT COUNT(*) FROM {table}"), NoBinds, null, default);
            Assert.Equal(0m, (await count.ResultSet!.GetPageAsync(0, default)).Columns[0][0]);
            await count.ResultSet.DisposeAsync();

            var output = await exec.ExecuteAsync(s,
                new SqlStatement("BEGIN DBMS_OUTPUT.PUT_LINE('안녕 11g'); END;", default, StatementKind.PlSql) { Verb = "BEGIN" }, NoBinds, null, default);
            Assert.Equal(ExecutionStatus.Succeeded, output.Status);
            Assert.Contains("안녕 11g", output.DbmsOutput ?? [], StringComparer.Ordinal);
        }
        finally
        {
            await exec.ExecuteAsync(s, new SqlStatement($"DROP TABLE {table} PURGE", default, StatementKind.Ddl) { Verb = "DROP" }, NoBinds, null, default);
        }
    }

    // ---- LOB ----

    [Fact]
    public async Task Clob_and_blob_stream_back_unchanged()
    {
        var table = Unique("LOB");
        var raw = await RawOwnerAsync();
        await Oracle11gFixture.ExecAsync(raw, $"CREATE TABLE {table} (id NUMBER PRIMARY KEY, c CLOB, b BLOB)");
        try
        {
            var text = string.Concat(Enumerable.Repeat("가나다 abc 0123456789\n", 20_000));
            var bytes = Enumerable.Range(0, 300_000).Select(i => (byte)(i % 251)).ToArray();
            await using (var cmd = raw.CreateCommand())
            {
                cmd.BindByName = true;
                cmd.CommandText = $"INSERT INTO {table} (id, c, b) VALUES (1, :c, :b)";
                cmd.Parameters.Add("c", OracleDbType.Clob).Value = text;
                cmd.Parameters.Add("b", OracleDbType.Blob).Value = bytes;
                await cmd.ExecuteNonQueryAsync();
            }

            await using var s = await OpenOwnerAsync();
            var exec = Exec(100);
            var r = await exec.ExecuteAsync(s, Q($"SELECT c, b FROM {table} WHERE id = 1"), NoBinds, null, default);
            Assert.Equal(ExecutionStatus.Succeeded, r.Status);
            var page = await r.ResultSet!.GetPageAsync(0, default);
            var clob = Assert.IsType<LobValue>(page.Columns[0][0]);
            var blob = Assert.IsType<LobValue>(page.Columns[1][0]);
            await r.ResultSet.DisposeAsync();
            Assert.Equal(LobKind.Clob, clob.Kind);
            Assert.True(clob.IsTruncated);

            var accessor = new OracleLobAccessor();
            await using (var cs = await accessor.OpenReadAsync(s, clob, default))
            {
                using var ms = new MemoryStream();
                await cs.CopyToAsync(ms);
                Assert.Equal(text, Encoding.UTF8.GetString(ms.ToArray()));
            }

            await using (var bs = await accessor.OpenReadAsync(s, blob, default))
            {
                using var ms = new MemoryStream();
                await bs.CopyToAsync(ms);
                Assert.Equal(bytes, ms.ToArray());
            }
        }
        finally
        {
            await Oracle11gFixture.ExecAsync(raw, $"DROP TABLE {table} PURGE");
        }
    }

    // ---- 스키마 브라우저(트리·상세·DDL)·자동완성 메타 ----

    private static async Task CreateSchemaObjectsAsync(OracleConnection owner, string dept, string emp)
    {
        foreach (var sql in new[]
        {
            $"CREATE TABLE {dept} (dept_id NUMBER(6) CONSTRAINT {dept}_PK PRIMARY KEY, name VARCHAR2(30) NOT NULL, budget NUMBER(10,2) DEFAULT 0, " +
                $"CONSTRAINT {dept}_CK CHECK (budget >= 0))",
            $"COMMENT ON TABLE {dept} IS '부서 표'",
            $"COMMENT ON COLUMN {dept}.name IS '부서 이름'",
            $"CREATE TABLE {emp} (emp_id NUMBER PRIMARY KEY, dept_id NUMBER(6) REFERENCES {dept}(dept_id), sal NUMBER, " +
                "bonus AS (sal * 0.1) VIRTUAL, note CLOB)",
            $"CREATE INDEX {emp}_IX ON {emp} (UPPER(TO_CHAR(dept_id)))", // 함수 기반 색인 = 시스템 숨은 열
            $"CREATE OR REPLACE VIEW {dept}_V AS SELECT dept_id, name FROM {dept}",
            $"CREATE OR REPLACE TRIGGER {dept}_T BEFORE INSERT ON {dept} FOR EACH ROW BEGIN NULL; END;",
            $"CREATE OR REPLACE PROCEDURE {dept}_P (p_id IN NUMBER, p_name OUT VARCHAR2) AS BEGIN SELECT name INTO p_name FROM {dept} WHERE dept_id = p_id; END;",
        })
        {
            await Oracle11gFixture.ExecAsync(owner, sql);
        }
    }

    [Fact]
    public async Task Schema_browser_tree_detail_ddl_and_completion_metadata_work_for_dba_and_owner()
    {
        var dept = Unique("DEPT");
        var emp = Unique("EMP");
        var raw = await RawOwnerAsync();
        await CreateSchemaObjectsAsync(raw, dept, emp);

        foreach (var s in new ISession[] { await OpenSystemAsync(), await OpenOwnerAsync() })
        {
            var provider = new OracleMetadataProvider(new MemoryMetadataCache(), new RecordingBus(), Timeout.InfiniteTimeSpan);

            // 트리: 스키마·객체 목록(meta.schemas·meta.objects).
            var schemas = await provider.ListSchemasAsync(s, default);
            Assert.Contains(_db.Owner, schemas);
            var objects = await provider.ListObjectsAsync(s, new ObjectQuery { Owner = _db.Owner }, default);
            Assert.Contains(objects, o => o.Name == dept && o.ObjectType == "TABLE");
            Assert.Contains(objects, o => o.Name == dept + "_V" && o.ObjectType == "VIEW");
            Assert.Contains(objects, o => o.Name == dept + "_P" && o.ObjectType == "PROCEDURE");
            var limited = await provider.ListObjectsAsync(s, new ObjectQuery { Owner = _db.Owner, CreatedAfter = DateTimeOffset.UtcNow.AddDays(-1), Limit = 2 }, default);
            Assert.Equal(2, limited.Count);

            // 열·제약(자동완성 메타): NOT NULL 검사 제약은 빠지고, 시스템 숨은 열(함수 기반 색인)은 안 보인다.
            var empInfo = await provider.GetTableAsync(s, new DbObjectRef(_db.Owner, emp, "TABLE"), default);
            Assert.Equal(["EMP_ID", "DEPT_ID", "SAL", "BONUS", "NOTE"], empInfo.Columns.Select(c => c.Name));
            Assert.True(empInfo.Columns.Single(c => c.Name == "BONUS").IsVirtual);
            Assert.All(empInfo.Columns, c => Assert.False(c.IsIdentity));
            Assert.All(empInfo.Columns, c => Assert.False(c.IsInvisible));
            Assert.Contains(empInfo.Constraints, c => c.Kind == ConstraintKind.ForeignKey && c.ReferencedTable!.Name == dept);

            var deptInfo = await provider.GetTableAsync(s, new DbObjectRef(_db.Owner, dept, "TABLE"), default);
            Assert.Equal("부서 표", deptInfo.Comment);
            Assert.Equal("부서 이름", deptInfo.Columns.Single(c => c.Name == "NAME").Comment);
            Assert.False(deptInfo.Columns.Single(c => c.Name == "NAME").IsNullable);
            Assert.Equal("0", deptInfo.Columns.Single(c => c.Name == "BUDGET").DefaultValue?.Trim());
            Assert.Equal([dept + "_CK", dept + "_PK"], deptInfo.Constraints.Select(c => c.Name).Order(StringComparer.Ordinal));
            Assert.Equal("budget >= 0", deptInfo.Constraints.Single(c => c.Kind == ConstraintKind.Check).Condition);

            // 프로시저 인자.
            var procs = await provider.GetProceduresAsync(s, new DbObjectRef(_db.Owner, dept + "_P", "PROCEDURE"), default);
            Assert.Equal(["P_ID", "P_NAME"], Assert.Single(procs).Arguments.Select(a => a.Name));

            // 상세: 11g 에 없는 항목은 숨기거나 안내 문구.
            var detail = await provider.GetObjectDetailAsync(s, new DbObjectRef(_db.Owner, emp, "TABLE"), default);
            Assert.DoesNotContain(detail.Sections, x => x.Columns.SequenceEqual(["오류"]));
            var general = Assert.Single(detail.Sections, x => x.Title == "일반");
            Assert.Equal(ServerFeatures.UnavailableText, general.Rows.Single(r => (string?)r[0] == "편집 가능(editionable)")[1]);
            var columns = Assert.Single(detail.Sections, x => x.Title == "열");
            Assert.DoesNotContain("Identity", columns.Columns);
            Assert.DoesNotContain("도메인", columns.Columns);
            Assert.Equal(5, columns.Rows.Count);

            var viewDetail = await provider.GetObjectDetailAsync(s, new DbObjectRef(_db.Owner, dept + "_V", "VIEW"), default);
            Assert.NotEmpty(viewDetail.Sections);

            // DDL(DBMS_METADATA).
            if (_db.MetadataStylesheetsMissing)
            {
                var noXsl = await Assert.ThrowsAsync<OracleException>(() =>
                    provider.GetDdlAsync(s, new DbObjectRef(_db.Owner, dept, "TABLE"), new DdlOptions(), default));
                Assert.Equal(39212, noXsl.Number);
            }
            else
            {
                var ddl = await provider.GetDdlAsync(s, new DbObjectRef(_db.Owner, dept, "TABLE"), new DdlOptions { IncludeDependentObjects = true }, default);
                Assert.Contains("CREATE TABLE", ddl, StringComparison.Ordinal);
                Assert.Contains(dept + "_CK", ddl, StringComparison.Ordinal);
                Assert.Contains("COMMENT ON", ddl, StringComparison.Ordinal);
            }

            // 표 하위 폴더(TSK-13-08): 트리거·Snapshot Log.
            var tree = new OracleSchemaTreeProvider();
            var triggers = await tree.ListTriggersAsync(s, new DbObjectRef(_db.Owner, dept, "TABLE"), default);
            Assert.Contains(triggers.Items, t => t.Name == dept + "_T");
            var logs = await tree.ListSnapshotLogsAsync(s, new DbObjectRef(_db.Owner, dept, "TABLE"), default);
            Assert.False(logs.Denied);
            Assert.Empty(logs.Items); // XE 11g 는 MView log 를 만들 수 없다(ORA-00439). 쿼리가 11g 에서 도는지만 본다

            var source = await provider.GetSourceAsync(s, new DbObjectRef(_db.Owner, dept + "_P", "PROCEDURE"), default);
            Assert.Contains("SELECT name INTO p_name", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Database_info_and_object_source_column_search_work()
    {
        var dept = Unique("SRCH");
        var emp = Unique("SRCE");
        var raw = await RawOwnerAsync();
        await CreateSchemaObjectsAsync(raw, dept, emp);

        await using var s = await OpenSystemAsync();
        var sections = await new OracleDatabaseInfoProvider().GetAsync(s, default);
        Assert.DoesNotContain(sections, x => x.Columns.SequenceEqual(["오류"]));
        var version = Assert.Single(sections, x => x.Title == "버전");
        Assert.Contains(version.Rows, r => Convert.ToString(r[0], CultureInfo.InvariantCulture)!.Contains("11.2", StringComparison.Ordinal));
        var instance = Assert.Single(sections, x => x.Title == "인스턴스");
        Assert.Contains(instance.Rows, r => (string?)r[0] == "version" && Convert.ToString(r[1], CultureInfo.InvariantCulture)!.StartsWith("11.2", StringComparison.Ordinal));
        Assert.Contains(instance.Rows, r => (string?)r[0] == "edition" && (string?)r[1] == "XE");

        var admin = new OracleSchemaAdminProvider();
        var objects = await admin.SearchObjectsAsync(s, new ObjectSearchQuery { Owner = _db.Owner, NamePattern = dept + "%" }, default);
        Assert.Equal(["INDEX", "PROCEDURE", "TABLE", "TRIGGER", "VIEW"], objects.Select(o => o.Ref.ObjectType).Order(StringComparer.Ordinal));
        var sized = await admin.SearchObjectsAsync(s, new ObjectSearchQuery { Owner = _db.Owner, ObjectTypes = ["TABLE"], Limit = 1 }, default);
        Assert.Single(sized);

        var source = await admin.SearchSourceAsync(s, new SourceSearchQuery { Owner = _db.Owner, Text = "into p_name" }, default);
        Assert.Contains(source.Rows, h => h.Ref.Name == dept + "_P");
        var regex = await admin.SearchSourceAsync(s, new SourceSearchQuery { Owner = _db.Owner, Text = "p_[a-z]+ OUT", UseRegex = true, Limit = 1 }, default);
        Assert.Single(regex.Rows);

        var cols = await admin.SearchColumnsAsync(s, new ColumnSearchQuery { Owner = _db.Owner, NamePattern = "%dept%" }, default);
        Assert.Contains(cols.Rows, h => h.Table.Name == emp && h.ColumnName == "DEPT_ID");
        var capped = await admin.SearchColumnsAsync(s, new ColumnSearchQuery { Owner = _db.Owner, NamePattern = "%", Limit = 1 }, default);
        Assert.Single(capped.Rows);
        Assert.True(capped.Capped);

        var refs = await admin.GetReferencesAsync(s, new DbObjectRef(_db.Owner, dept + "_V", "VIEW"), default);
        Assert.Contains(refs, x => !x.IsUsedBy && x.Ref.Name == dept);
        _ = await admin.ListRecycleBinAsync(s, default);
    }

    // ---- 실행 계획(예상·실제) ----

    private OracleExecutionPlanService Plans() => new(Exec());

    [Fact]
    public async Task Explain_and_actual_plan_work()
    {
        var table = Unique("PLN");
        var raw = await RawSystemAsync();
        await Oracle11gFixture.ExecAsync(raw, $"CREATE TABLE {table} AS SELECT level AS id, mod(level, 7) AS g FROM dual CONNECT BY level <= 3000");
        try
        {
            await using var s = await OpenSystemAsync();
            var plan = await Plans().ExplainAsync(s,
                new SqlStatement($"SELECT g, COUNT(*) FROM {table} WHERE id > :b1 GROUP BY g", default, StatementKind.Query) { Verb = "SELECT", BindNames = ["b1"] }, default);
            var scan = Assert.Single(plan.Steps, x => x.Operation == "TABLE ACCESS");
            Assert.Equal("FULL", scan.Options);
            Assert.True(scan.Risk.HasFlag(PlanRisk.FullScan));
            Assert.Contains("Plan hash value", plan.Text, StringComparison.Ordinal);

            var tag = Unique("ACT");
            await using (var cmd = raw.CreateCommand())
            {
                cmd.CommandText = $"SELECT /*+ gather_plan_statistics {tag} */ COUNT(*) FROM {table} WHERE g = 1";
                await cmd.ExecuteScalarAsync();
            }

            string sqlId;
            await using (var find = raw.CreateCommand())
            {
                find.CommandText = $"SELECT sql_id FROM v$sql WHERE sql_text LIKE 'SELECT /*+ gather_plan_statistics {tag} */%' AND ROWNUM = 1";
                sqlId = (string)(await find.ExecuteScalarAsync())!;
            }

            var actual = await Plans().GetCursorPlanAsync(s, new CursorRef(sqlId), "ALLSTATS LAST", default);
            Assert.Contains("A-Rows", actual.Text, StringComparison.Ordinal);
            Assert.True(Assert.Single(actual.Steps, x => x.Operation == "TABLE ACCESS").ActualRows > 0);
        }
        finally
        {
            await Oracle11gFixture.ExecAsync(raw, $"DROP TABLE {table} PURGE");
        }
    }

    [Fact]
    public async Task Explain_works_with_an_old_private_plan_table_without_plan_id()
    {
        // 9i 형식 PLAN_TABLE(plan_id·projection·time·qblock_name·other_xml 없음)을 사용자 스키마에 둔다. 공용 동의어보다 먼저 잡힌다.
        var raw = await RawOwnerAsync();
        await Oracle11gFixture.ExecAsync(raw, """
            CREATE TABLE plan_table (
              statement_id VARCHAR2(30), timestamp DATE, remarks VARCHAR2(80), operation VARCHAR2(30), options VARCHAR2(255),
              object_node VARCHAR2(128), object_owner VARCHAR2(30), object_name VARCHAR2(30), object_instance NUMERIC,
              object_type VARCHAR2(30), optimizer VARCHAR2(255), search_columns NUMBER, id NUMERIC, parent_id NUMERIC,
              position NUMERIC, cost NUMERIC, cardinality NUMERIC, bytes NUMERIC, other_tag VARCHAR2(255),
              partition_start VARCHAR2(255), partition_stop VARCHAR2(255), partition_id NUMERIC, other LONG,
              distribution VARCHAR2(30), cpu_cost NUMERIC, io_cost NUMERIC, temp_space NUMERIC,
              access_predicates VARCHAR2(4000), filter_predicates VARCHAR2(4000))
            """);
        try
        {
            await using var s = await OpenOwnerAsync();
            var plan = await Plans().ExplainAsync(s, Q("SELECT * FROM dual WHERE dummy = 'X'"), default);

            Assert.Contains(plan.Steps, x => x.Operation == "TABLE ACCESS" && x.ObjectName == "DUAL");
            Assert.Contains(plan.Steps, x => x.FilterPredicates is not null);
            Assert.NotNull(plan.Text);

            // 정리(statement_id 로 지움)도 옛 표에서 된다.
            await using var left = raw.CreateCommand();
            left.CommandText = "SELECT COUNT(*) FROM plan_table WHERE statement_id LIKE 'ORANDA_%'";
            Assert.Equal(0m, Convert.ToDecimal(await left.ExecuteScalarAsync(), CultureInfo.InvariantCulture));
        }
        finally
        {
            await Oracle11gFixture.ExecAsync(raw, "DROP TABLE plan_table PURGE");
        }
    }

    // ---- 세션 모니터·Top SQL ----

    private OracleMonitorService Monitor() => new(Exec());

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
    public async Task Session_monitor_lists_sessions_detail_and_blocking_tree()
    {
        var monitor = await OpenSystemAsync(SessionPurpose.Monitor);
        var target = await RawSystemAsync();
        var key = await KeyOfAsync(target);
        await Oracle11gFixture.ExecAsync(target, "BEGIN FOR r IN (SELECT * FROM all_objects WHERE ROWNUM <= 50) LOOP NULL; END LOOP; END;");
        var service = Monitor();

        var rows = await service.GetSessionsAsync(monitor, new SessionFilter(), default);
        Assert.Contains(rows, r => r.Key == key);
        var again = await service.GetSessionsAsync(monitor, new SessionFilter { Username = "system" }, default);
        Assert.Contains(again, r => r.Key == key);

        var detail = await service.GetSessionDetailAsync(monitor, key, default);
        Assert.DoesNotContain(detail.Sections, x => x.Columns.SequenceEqual(["오류"]));
        var cursors = Assert.Single(detail.Sections, x => x.Title == "열린 커서");
        Assert.InRange(cursors.Rows.Count, 1, 200);
        Assert.False(string.IsNullOrEmpty(detail.PreviousSqlText ?? detail.CurrentSqlText));

        _ = await service.GetBlockingTreeAsync(monitor, default);
    }

    [Fact]
    public async Task Top_sql_finds_statement_and_reads_full_text()
    {
        var tag = Unique("TOP");
        var raw = await RawSystemAsync();
        await using (var cmd = raw.CreateCommand())
        {
            cmd.CommandText = $"SELECT /* {tag} */ COUNT(*) FROM all_objects";
            await cmd.ExecuteScalarAsync();
        }

        var monitor = await OpenSystemAsync(SessionPurpose.Monitor);
        var service = Monitor();
        var rows = await service.GetTopSqlAsync(monitor, new TopSqlCriteria { TextContains = tag, ParsingSchema = "system", ActiveWithin = TimeSpan.FromMinutes(10) }, default);
        var row = Assert.Single(rows, r => r.SqlTextPreview.StartsWith("SELECT", StringComparison.Ordinal));
        Assert.True(row.Executions >= 1);

        var top3 = await service.GetTopSqlAsync(monitor, new TopSqlCriteria { OrderBy = TopSqlOrder.BufferGets, Top = 3 }, default);
        Assert.InRange(top3.Count, 1, 3);

        var detail = await service.GetSqlDetailAsync(monitor, row.Cursor, default);
        Assert.Contains(tag, detail.FullText, StringComparison.Ordinal);
    }
}
