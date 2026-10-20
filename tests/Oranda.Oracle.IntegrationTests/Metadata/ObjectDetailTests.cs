using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Connection;
using Oranda.Core.Metadata;
using Oranda.Core.Security;
using Oranda.Core.Sessions;
using Oranda.Oracle.Connection;
using Oranda.Oracle.IntegrationTests.Integration;
using Oranda.Oracle.IntegrationTests.Sessions;
using Oranda.Oracle.Metadata;
using Oranda.Oracle.Sessions;
using Xunit.Abstractions;

namespace Oranda.Oracle.IntegrationTests.Metadata;

// 소유: TSK-07-03. 상세·DDL.

public class ObjectDetailLogicTests
{
    [Theory]
    [InlineData("TABLE", "TABLE")]
    [InlineData("PACKAGE", "PACKAGE_SPEC")]
    [InlineData("PACKAGE BODY", "PACKAGE_BODY")]
    [InlineData("MATERIALIZED VIEW", "MATERIALIZED_VIEW")]
    [InlineData("TYPE", "TYPE_SPEC")]
    [InlineData("TYPE BODY", "TYPE_BODY")]
    [InlineData("DATABASE LINK", "DB_LINK")]
    public void Object_type_maps_to_dbms_metadata_name(string objectType, string expected)
        => Assert.Equal(expected, OracleMetadataProvider.ToMetadataType(objectType));
}

[Trait("Category", "Integration")]
public sealed class ObjectDetailIntegrationTests : IClassFixture<OracleFreeFixture>, IAsyncLifetime
{
    private readonly OracleFreeFixture _db;
    private readonly ITestOutputHelper _output;
    private readonly string _owner = "DTL_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
    private readonly List<ISession> _sessions = [];

    public ObjectDetailIntegrationTests(OracleFreeFixture db, ITestOutputHelper output)
    {
        _db = db;
        _output = output;
    }

    public async Task InitializeAsync()
    {
        var u = _owner;
        await using var conn = await OpenSystemAsync();
        string[] ddl =
        [
            $"CREATE USER {u} NO AUTHENTICATION QUOTA UNLIMITED ON USERS",
            $"CREATE TABLE {u}.dept (id NUMBER(6) CONSTRAINT dept_pk PRIMARY KEY, name VARCHAR2(30) NOT NULL)",
            $"CREATE TABLE {u}.modern (id NUMBER GENERATED ALWAYS AS IDENTITY PRIMARY KEY, flag BOOLEAN DEFAULT TRUE, emb VECTOR(3, FLOAT32), "
                + $"dept_id NUMBER(6) CONSTRAINT modern_dept_fk REFERENCES {u}.dept(id), secret VARCHAR2(10) INVISIBLE, "
                + "tax NUMBER GENERATED ALWAYS AS (id * 2) VIRTUAL)",
            $"COMMENT ON TABLE {u}.modern IS 'modern table'",
            $"CREATE INDEX {u}.modern_dept_ix ON {u}.modern(dept_id)",
            $"ALTER TABLE {u}.modern ALLOCATE EXTENT", // 세그먼트 지연 생성을 피해 크기 절을 채운다
            $"CREATE TABLE {u}.part_t (id NUMBER, d DATE) PARTITION BY RANGE (id) (PARTITION p1 VALUES LESS THAN (100), PARTITION p2 VALUES LESS THAN (MAXVALUE))",
            $"CREATE VIEW {u}.dept_v AS SELECT id, name FROM {u}.dept",
            $"CREATE TRIGGER {u}.dept_bi BEFORE INSERT ON {u}.dept FOR EACH ROW WHEN (new.id > 0) BEGIN NULL; END;",
            $"CREATE TRIGGER {u}.dept_v_io INSTEAD OF INSERT ON {u}.dept_v FOR EACH ROW BEGIN NULL; END;",
            $"CREATE PROCEDURE {u}.sp AS BEGIN NULL; END;",
            $"CREATE PACKAGE {u}.pk AS PROCEDURE a; END;",
            $"CREATE PACKAGE BODY {u}.pk AS PROCEDURE a IS BEGIN NULL; END; END;",
            $"GRANT SELECT ON {u}.dept TO {OracleFreeFixture.AppUser}",
            $"GRANT SELECT ON {u}.modern TO {OracleFreeFixture.AppUser}",
        ];
        foreach (var sql in ddl)
        {
            await ExecAsync(conn, sql);
        }
    }

    public async Task DisposeAsync()
    {
        foreach (var s in _sessions)
        {
            await s.DisposeAsync();
        }

        await using var conn = await OpenSystemAsync();
        await ExecAsync(conn, $"DROP USER {_owner} CASCADE");
    }

    [Fact]
    public async Task Table_ddl_contains_boolean_and_vector_columns()
    {
        var provider = NewProvider();
        var s = await OpenAsync("system", _db.SystemPassword);

        var ddl = await provider.GetDdlAsync(s, new DbObjectRef(_owner, "MODERN", "TABLE"), new DdlOptions(), default);

        Assert.Contains("CREATE TABLE", ddl);
        Assert.Contains($"\"{_owner}\".\"MODERN\"", ddl);
        Assert.Contains("BOOLEAN", ddl);
        Assert.Contains("VECTOR(3, FLOAT32)", ddl);
        Assert.Contains("INVISIBLE", ddl);
        Assert.Contains("GENERATED ALWAYS AS IDENTITY", ddl);
        Assert.EndsWith(";", ddl);
    }

    [Fact]
    public async Task Ddl_options_control_terminator_storage_and_tablespace()
    {
        var provider = NewProvider();
        var s = await OpenAsync("system", _db.SystemPassword);
        var table = new DbObjectRef(_owner, "DEPT", "TABLE");

        var bare = await provider.GetDdlAsync(s, table, new DdlOptions { SqlTerminator = false }, default);
        var full = await provider.GetDdlAsync(s, table,
            new DdlOptions { IncludeStorage = true, IncludeSegmentAttributes = true, IncludeTablespace = true }, default);
        var noSegment = await provider.GetDdlAsync(s, table,
            new DdlOptions { IncludeStorage = false, IncludeSegmentAttributes = false, IncludeTablespace = false }, default);

        Assert.False(bare.EndsWith(';'));
        Assert.Contains("TABLESPACE", full);
        Assert.DoesNotContain("TABLESPACE", noSegment);
        Assert.DoesNotContain("SEGMENT CREATION", noSegment);
        Assert.True(full.Length > noSegment.Length);
    }

    [Fact]
    public async Task Dependent_objects_are_appended_on_request()
    {
        var provider = NewProvider();
        var s = await OpenAsync("system", _db.SystemPassword);
        var table = new DbObjectRef(_owner, "MODERN", "TABLE");

        var plain = await provider.GetDdlAsync(s, table, new DdlOptions(), default);
        var withDeps = await provider.GetDdlAsync(s, table, new DdlOptions { IncludeDependentObjects = true }, default);

        Assert.DoesNotContain("CREATE INDEX", plain);
        Assert.Contains("CREATE INDEX", withDeps);
        Assert.Contains("MODERN_DEPT_IX", withDeps);
        Assert.Contains("COMMENT ON TABLE", withDeps);
        Assert.Contains("GRANT SELECT", withDeps);
    }

    [Theory]
    [InlineData("VIEW", "DEPT_V", "CREATE")]
    [InlineData("PROCEDURE", "SP", "PROCEDURE")]
    [InlineData("PACKAGE", "PK", "PACKAGE")]
    [InlineData("PACKAGE BODY", "PK", "PACKAGE BODY")]
    [InlineData("INDEX", "MODERN_DEPT_IX", "CREATE INDEX")]
    public async Task Other_object_types_have_ddl(string type, string name, string expected)
    {
        var provider = NewProvider();
        var s = await OpenAsync("system", _db.SystemPassword);

        var ddl = await provider.GetDdlAsync(s, new DbObjectRef(_owner, name, type), new DdlOptions(), default);

        Assert.Contains(expected, ddl);
    }

    [Fact]
    public async Task Missing_object_raises_error_not_empty_ddl()
    {
        var provider = NewProvider();
        var s = await OpenAsync("system", _db.SystemPassword);

        await Assert.ThrowsAsync<OracleException>(
            () => provider.GetDdlAsync(s, new DbObjectRef(_owner, "NO_SUCH", "TABLE"), new DdlOptions(), default));
    }

    [Fact]
    public async Task Non_dba_account_cannot_read_ddl_of_other_schema_and_gets_server_error()
    {
        var provider = NewProvider();
        var app = await OpenAsync(OracleFreeFixture.AppUser, _db.AppPassword);

        // SELECT_CATALOG_ROLE 이 없으면 남의 스키마 DDL 은 DBMS_METADATA 가 거부한다(ORA-31603). 빈 문자열로 삼키지 않고 서버 오류를 그대로 올린다.
        var ex = await Assert.ThrowsAsync<OracleException>(
            () => provider.GetDdlAsync(app, new DbObjectRef(_owner, "DEPT", "TABLE"), new DdlOptions(), default));
        Assert.Equal(31603, ex.Number);
    }

    [Fact]
    public async Task Table_detail_fills_columns_constraints_indexes_privileges_and_size()
    {
        var provider = NewProvider();
        var s = await OpenAsync("system", _db.SystemPassword);

        var detail = await provider.GetObjectDetailAsync(s, new DbObjectRef(_owner, "MODERN", "TABLE"), default);

        var titles = detail.Sections.Select(x => x.Title).ToList();
        Assert.Contains("일반", titles);
        Assert.Contains("열", titles);
        Assert.Contains("제약", titles);
        Assert.Contains("인덱스", titles);
        Assert.Contains("권한", titles);
        Assert.Contains("통계", titles);
        Assert.Contains("크기", titles);

        var columns = Section(detail, "열");
        var names = columns.Rows.Select(r => (string?)r[1]).ToList();
        Assert.Equal(["ID", "FLAG", "EMB", "DEPT_ID", "SECRET", "TAX"], names);
        Assert.Equal("BOOLEAN", columns.Rows[1][2]);
        Assert.Equal("VECTOR", columns.Rows[2][2]);
        Assert.Equal("Y", columns.Rows[0][9]);   // identity
        Assert.Equal("Y", columns.Rows[4][10]);  // invisible
        Assert.Equal("Y", columns.Rows[5][8]);   // virtual

        Assert.Contains(Section(detail, "제약").Rows, r => (string?)r[1] == "FOREIGN KEY" && (string?)r[3] == $"{_owner}.DEPT");
        Assert.Contains(Section(detail, "인덱스").Rows, r => (string?)r[0] == "MODERN_DEPT_IX");
        Assert.Contains(Section(detail, "권한").Rows, r => string.Equals((string?)r[0], OracleFreeFixture.AppUser, StringComparison.OrdinalIgnoreCase) && (string?)r[1] == "SELECT");
        Assert.Contains(Section(detail, "크기").Rows, r => (string?)r[0] == "합계");
    }

    [Fact]
    public async Task Partitioned_table_detail_lists_partitions()
    {
        var provider = NewProvider();
        var s = await OpenAsync("system", _db.SystemPassword);

        var detail = await provider.GetObjectDetailAsync(s, new DbObjectRef(_owner, "PART_T", "TABLE"), default);

        var partitions = Section(detail, "파티션");
        Assert.Equal(["P1", "P2"], partitions.Rows.Select(r => (string?)r[1]));
        Assert.Equal("RANGE", partitions.Rows[0][2]);
        Assert.Equal("100", partitions.Rows[0][4]);
    }

    [Fact]
    public async Task Dependencies_are_listed_in_both_directions()
    {
        var provider = NewProvider();
        var s = await OpenAsync("system", _db.SystemPassword);

        var view = await provider.GetObjectDetailAsync(s, new DbObjectRef(_owner, "DEPT_V", "VIEW"), default);
        var table = await provider.GetObjectDetailAsync(s, new DbObjectRef(_owner, "DEPT", "TABLE"), default);

        Assert.Contains(Section(view, "의존").Rows, r => (string?)r[0] == "참조함" && (string?)r[2] == "DEPT");
        Assert.Contains(Section(table, "의존").Rows, r => (string?)r[0] == "참조됨" && (string?)r[2] == "DEPT_V");
    }

    [Fact]
    public async Task Table_detail_lists_its_triggers_and_the_foreign_keys_that_reference_it()
    {
        var provider = NewProvider();
        var s = await OpenAsync("system", _db.SystemPassword);

        var dept = await provider.GetObjectDetailAsync(s, new DbObjectRef(_owner, "DEPT", "TABLE"), default);

        var trigger = Assert.Single(Section(dept, "트리거").Rows);
        Assert.Equal("DEPT_BI", trigger[0]);
        Assert.Equal("BEFORE EACH ROW", trigger[1]);
        Assert.Equal("INSERT", trigger[2]);
        Assert.Equal("ENABLED", trigger[3]);
        Assert.Contains("id > 0", (string?)trigger[4], StringComparison.OrdinalIgnoreCase);
        Assert.Equal(_owner, trigger[5]);

        var fk = Assert.Single(Section(dept, "참조됨").Rows);
        Assert.Equal([_owner, "MODERN", "MODERN_DEPT_FK", "DEPT_ID", "DEPT_PK", "NO ACTION", "ENABLED"], fk.Select(v => (string?)v));

        // MODERN 은 트리거도, 자기를 참조하는 외래키도 없다: 빈 구역은 넣지 않는다.
        var modern = await provider.GetObjectDetailAsync(s, new DbObjectRef(_owner, "MODERN", "TABLE"), default);
        Assert.DoesNotContain(modern.Sections, x => x.Title is "트리거" or "참조됨");
    }

    [Fact]
    public async Task View_detail_lists_instead_of_triggers()
    {
        var provider = NewProvider();
        var s = await OpenAsync("system", _db.SystemPassword);

        var view = await provider.GetObjectDetailAsync(s, new DbObjectRef(_owner, "DEPT_V", "VIEW"), default);

        var trigger = Assert.Single(Section(view, "트리거").Rows);
        Assert.Equal("DEPT_V_IO", trigger[0]);
        Assert.Equal("INSTEAD OF", trigger[1]);
        Assert.DoesNotContain(view.Sections, x => x.Title == "참조됨");
    }

    [Fact]
    public async Task Non_table_object_detail_has_general_section_without_columns()
    {
        var provider = NewProvider();
        var s = await OpenAsync("system", _db.SystemPassword);

        var detail = await provider.GetObjectDetailAsync(s, new DbObjectRef(_owner, "SP", "PROCEDURE"), default);

        Assert.Contains(detail.Sections, x => x.Title == "일반");
        Assert.DoesNotContain(detail.Sections, x => x.Title == "열");
    }

    [Fact]
    public async Task Non_dba_account_gets_detail_without_dba_views()
    {
        var provider = NewProvider();
        var app = await OpenAsync(OracleFreeFixture.AppUser, _db.AppPassword);
        Assert.False(app.Server.Capabilities.CanQueryDictionary);

        var detail = await provider.GetObjectDetailAsync(app, new DbObjectRef(_owner, "MODERN", "TABLE"), default);

        Assert.Contains(detail.Sections, x => x.Title == "열");
        Assert.DoesNotContain(detail.Sections, x => x.Title == "크기"); // 남의 스키마: user_segments 로는 못 본다
    }

    /// <summary>DBMS_DEVELOPER.GET_METADATA(26ai 신규) 를 쓸 수 있는지·같은 DDL 을 주는지 잰다. 결과는 시험 출력에 남긴다(구현은 DBMS_METADATA).</summary>
    [Fact]
    public async Task Dbms_developer_availability_is_measured()
    {
        await using var conn = await OpenSystemAsync();
        await using var probe = conn.CreateCommand();
        probe.CommandText = "SELECT COUNT(*) FROM all_objects WHERE object_name = 'DBMS_DEVELOPER' AND object_type = 'PACKAGE'";
        var present = Convert.ToInt32(await probe.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture) > 0;
        _output.WriteLine($"DBMS_DEVELOPER present={present}");
        if (!present)
        {
            return;
        }

        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT DBMS_DEVELOPER.GET_METADATA(:n, :o) FROM dual";
            cmd.BindByName = true;
            cmd.Parameters.Add("n", "DEPT");
            cmd.Parameters.Add("o", _owner);
            var text = Convert.ToString(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
            _output.WriteLine("DBMS_DEVELOPER.GET_METADATA(name, schema) ok, length=" + text?.Length);
        }
        catch (OracleException ex)
        {
            _output.WriteLine("DBMS_DEVELOPER.GET_METADATA call failed: " + ex.Message);
        }
    }

    private static DetailSection Section(ObjectDetail detail, string title) => detail.Sections.Single(x => x.Title == title);

    private static OracleMetadataProvider NewProvider()
        => new(new MemoryMetadataCache(), new RecordingBus(), Timeout.InfiniteTimeSpan);

    private async Task<ISession> OpenAsync(string user, string password)
    {
        var manager = new OracleSessionManager(new RecordingBus());
        using var cred = new SecureCredential(password);
        var session = await manager.OpenAsync(new ConnectionProfile("t-" + user, user, _db.DataSource), cred, SessionPurpose.Work, default);
        _sessions.Add(session);
        return session;
    }

    private async Task<OracleConnection> OpenSystemAsync()
    {
        var profile = new ConnectionProfile("free-system", "system", _db.DataSource);
        var conn = new OracleConnection(profile.BuildConnectionString(_db.SystemPassword));
        await conn.OpenAsync();
        return conn;
    }

    private static async Task ExecAsync(OracleConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }
}
