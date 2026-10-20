using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Security;
using Oranda.Core.Sessions;
using Oranda.Core.Catalog;
using Oranda.Oracle.Catalog;
using Oranda.Oracle.Connection;
using Oranda.Oracle.IntegrationTests.Integration;
using Oranda.Oracle.IntegrationTests.Metadata;
using Oranda.Oracle.IntegrationTests.Sessions;
using Oranda.Oracle.Metadata;
using Oranda.Oracle.Schema;
using Oranda.Oracle.Sessions;

namespace Oranda.Oracle.IntegrationTests.Schema;

// 소유: TSK-07-04. DB 정보·객체 검색·휴지통·INVALID 컴파일·DDL 일괄 추출.

public class SchemaStatementsTests
{
    [Theory]
    [InlineData("PROCEDURE", "ALTER PROCEDURE \"U\".\"N\" COMPILE")]
    [InlineData("PACKAGE", "ALTER PACKAGE \"U\".\"N\" COMPILE")]
    [InlineData("PACKAGE BODY", "ALTER PACKAGE \"U\".\"N\" COMPILE BODY")]
    [InlineData("TYPE BODY", "ALTER TYPE \"U\".\"N\" COMPILE BODY")]
    [InlineData("MATERIALIZED VIEW", "ALTER MATERIALIZED VIEW \"U\".\"N\" COMPILE")]
    public void Compile_statement_per_object_type(string type, string expected)
    {
        var stmt = SchemaStatements.Compile(new DbObjectRef("U", "N", type));

        Assert.NotNull(stmt);
        Assert.Equal(expected, stmt.Text);
        Assert.Equal(StatementKind.Ddl, stmt.Kind);
    }

    [Fact]
    public void Compile_is_null_for_types_that_cannot_be_compiled_and_public_synonym_has_no_owner()
    {
        Assert.Null(SchemaStatements.Compile(new DbObjectRef("U", "T", "TABLE")));
        Assert.Equal("ALTER PUBLIC SYNONYM \"S\" COMPILE", SchemaStatements.Compile(new DbObjectRef("PUBLIC", "S", "SYNONYM"))!.Text);
    }

    [Fact]
    public void Purge_asks_for_name_retype_and_flashback_does_not()
    {
        var item = new RecycleBinItem("BIN$abc==$0", "EMP", "TABLE", "USERS", null, true, true, 8);

        var purge = SchemaStatements.PurgeItem(item);
        Assert.Equal("PURGE TABLE \"BIN$abc==$0\"", purge.Text);
        Assert.Equal("EMP", purge.DangerousTarget);
        Assert.Equal("RECYCLEBIN", SchemaStatements.PurgeAll().DangerousTarget);

        var back = SchemaStatements.Flashback(item, "EMP_OLD");
        Assert.Equal("FLASHBACK TABLE \"BIN$abc==$0\" TO BEFORE DROP RENAME TO \"EMP_OLD\"", back.Text);
        Assert.Null(back.DangerousTarget);
    }

    [Theory]
    [InlineData("processes", "300", ParameterScope.Both, "ALTER SYSTEM SET processes = 300 SCOPE = BOTH;")]
    [InlineData("sga_target", "2G", ParameterScope.Spfile, "ALTER SYSTEM SET sga_target = 2G SCOPE = SPFILE;")]
    [InlineData("nls_date_format", "YYYY-MM-DD", ParameterScope.Memory, "ALTER SYSTEM SET nls_date_format = 'YYYY-MM-DD' SCOPE = MEMORY;")]
    [InlineData("db_name", "it's", ParameterScope.Both, "ALTER SYSTEM SET db_name = 'it''s' SCOPE = BOTH;")]
    public void Parameter_change_ddl_preview(string name, string value, ParameterScope scope, string expected)
        => Assert.Equal(expected, SchemaStatements.ParameterChangeDdl(name, value, scope));

    [Fact]
    public void Parameter_name_is_validated_against_injection()
        => Assert.Throws<ArgumentException>(() => SchemaStatements.ParameterChangeDdl("a; DROP USER x", "1", ParameterScope.Both));

    [Fact]
    public void Renames_replace_quoted_identifiers_once()
    {
        var ddl = "CREATE TABLE \"A\".\"T\" (X NUMBER); CREATE INDEX \"IX\" ON \"A\".\"T\"(X);";

        var result = DdlBulkExporter.ApplyRenames(ddl, [new DdlRename("A", "B"), new DdlRename("T", "A")]);

        Assert.Equal("CREATE TABLE \"B\".\"A\" (X NUMBER); CREATE INDEX \"IX\" ON \"B\".\"A\"(X);", result);
    }

    [Theory]
    [InlineData("VARCHAR2", 30L, null, null, "VARCHAR2(30)")]
    [InlineData("NUMBER", null, 10L, 2L, "NUMBER(10,2)")]
    [InlineData("NUMBER", null, 6L, 0L, "NUMBER(6)")]
    [InlineData("NUMBER", null, null, 0L, "NUMBER(*,0)")]
    [InlineData("NUMBER", null, null, null, "NUMBER")]
    [InlineData("FLOAT", null, 126L, null, "FLOAT(126)")]
    [InlineData("RAW", 16L, null, null, "RAW(16)")]
    [InlineData("TIMESTAMP(6)", null, null, 6L, "TIMESTAMP(6)")]
    public void Column_search_type_text(string type, long? length, long? precision, long? scale, string expected)
        => Assert.Equal(expected, OracleSchemaAdminProvider.TypeText(type, length, precision, scale));

    [Fact]
    public void Dbinfo_catalog_queries_are_collected()
    {
        var catalog = EmbeddedCatalog.Load();
        var server = new ServerInfo("19.0.0.0.0", "X", "h", "s", "X", null, false, 1, new ServerCapabilities(false, false, false, false, false));
        foreach (var id in new[]
        {
            "dbinfo.instance", "dbinfo.database", "dbinfo.version", "dbinfo.options", "dbinfo.parameters", "dbinfo.nls",
            "dbinfo.object_search", "dbinfo.references", "dbinfo.recyclebin",
            "dbinfo.source_search", "dbinfo.regex_check", "dbinfo.column_search",
        })
        {
            Assert.Equal(CatalogLicense.None, catalog.Get(id, server).Header.License);
        }
    }
}

[Trait("Category", "Integration")]
public sealed class DatabaseInfoIntegrationTests : IClassFixture<OracleFreeFixture>, IAsyncLifetime
{
    private readonly OracleFreeFixture _db;
    private readonly string _owner = "ADM_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
    private readonly string _recycleUser = "RCY_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
    private readonly string _recyclePassword = "Rc" + Guid.NewGuid().ToString("N")[..16] + "#1";
    private readonly List<ISession> _sessions = [];
    private readonly string _outDir = Path.Combine(Path.GetTempPath(), "oranda-ddl-" + Guid.NewGuid().ToString("N")[..8]);

    public DatabaseInfoIntegrationTests(OracleFreeFixture db) => _db = db;

    public async Task InitializeAsync()
    {
        var u = _owner;
        await using var conn = await OpenSystemAsync();
        string[] ddl =
        [
            $"CREATE USER {u} NO AUTHENTICATION QUOTA UNLIMITED ON USERS",
            $"CREATE TABLE {u}.dept (id NUMBER(6) CONSTRAINT {u}_dept_pk PRIMARY KEY, name VARCHAR2(30) NOT NULL)",
            $"CREATE TABLE {u}.emp (id NUMBER(6), dept_id NUMBER(6) REFERENCES {u}.dept(id))",
            $"ALTER TABLE {u}.dept ALLOCATE EXTENT",
            $"CREATE SEQUENCE {u}.seq1",
            $"CREATE VIEW {u}.dept_v AS SELECT id, name FROM {u}.dept",
            $"CREATE PROCEDURE {u}.sp AS BEGIN NULL; END;",
            // 없는 테이블을 쓰는 프로시저 → INVALID. 나중에 테이블을 만들면 컴파일로 VALID 가 된다.
            $"CREATE PROCEDURE {u}.late_p AS BEGIN INSERT INTO {u}.late_t VALUES (1); END;",
            $"CREATE USER {_recycleUser} IDENTIFIED BY \"{_recyclePassword}\" QUOTA UNLIMITED ON USERS",
            $"GRANT CREATE SESSION, CREATE TABLE TO {_recycleUser}",
        ];
        foreach (var sql in ddl)
        {
            await ExecAsync(conn, sql, ignoreErrors: sql.Contains("late_p", StringComparison.Ordinal));
        }
    }

    public async Task DisposeAsync()
    {
        foreach (var s in _sessions)
        {
            await s.DisposeAsync();
        }

        await using var conn = await OpenSystemAsync();
        await ExecAsync(conn, $"DROP USER {_owner} CASCADE", true);
        await ExecAsync(conn, $"DROP USER {_recycleUser} CASCADE", true);
        if (Directory.Exists(_outDir))
        {
            Directory.Delete(_outDir, recursive: true);
        }
    }

    [Fact]
    public async Task Db_info_has_instance_version_options_parameters_and_nls()
    {
        var s = await OpenAsync("system", _db.SystemPassword);

        var sections = await new OracleDatabaseInfoProvider().GetAsync(s, default);

        Assert.Equal(["인스턴스", "데이터베이스", "버전", "옵션", "파라미터", "NLS"], sections.Select(x => x.Title));
        var instance = sections[0];
        Assert.Equal(["항목", "값"], instance.Columns);
        Assert.Contains(instance.Rows, r => (string?)r[0] == "instance_name" && !string.IsNullOrEmpty((string?)r[1]));
        Assert.Contains(sections[2].Rows, r => ((string?)r[0] ?? string.Empty).Contains("Oracle", StringComparison.Ordinal));
        Assert.NotEmpty(sections[3].Rows);
        Assert.Contains(sections[4].Rows, r => (string?)r[0] == "db_block_size");
        Assert.Contains(sections[5].Rows, r => (string?)r[0] == "NLS_DATE_FORMAT" || (string?)r[1] == "NLS_DATE_FORMAT");
        Assert.DoesNotContain(sections, x => x.Columns.SequenceEqual(["오류"]));
    }

    [Fact]
    public async Task Db_info_without_privileges_degrades_per_section_instead_of_failing()
    {
        var s = await OpenAsync(OracleFreeFixture.AppUser, _db.AppPassword);

        var sections = await new OracleDatabaseInfoProvider().GetAsync(s, default);

        Assert.Equal(6, sections.Count);
        Assert.All(sections, x => Assert.NotEmpty(x.Rows));
    }

    [Fact]
    public async Task Object_search_filters_by_name_type_status_and_dates()
    {
        var s = await OpenAsync("system", _db.SystemPassword);
        var admin = new OracleSchemaAdminProvider();

        var byName = await admin.SearchObjectsAsync(s, new ObjectSearchQuery { Owner = _owner, NamePattern = "DEPT%" }, default);
        Assert.Equal(["DEPT", "DEPT_V"], byName.Select(r => r.Ref.Name).Order(StringComparer.Ordinal));

        var tables = await admin.SearchObjectsAsync(s, new ObjectSearchQuery { Owner = _owner, ObjectTypes = ["TABLE"] }, default);
        Assert.Equal(["DEPT", "EMP"], tables.Select(r => r.Ref.Name).Order(StringComparer.Ordinal));
        Assert.All(tables, r => Assert.NotNull(r.Created));

        var invalid = await admin.SearchObjectsAsync(s, new ObjectSearchQuery { Owner = _owner, InvalidOnly = true }, default);
        Assert.Contains(invalid, r => r.Ref.Name == "LATE_P" && r.Status == "INVALID");

        var future = await admin.SearchObjectsAsync(s,
            new ObjectSearchQuery { Owner = _owner, CreatedAfter = DateTime.UtcNow.AddDays(2), ModifiedAfter = DateTime.UtcNow.AddDays(2) }, default);
        Assert.Empty(future);

        var recent = await admin.SearchObjectsAsync(s, new ObjectSearchQuery { Owner = _owner, ModifiedAfter = DateTime.UtcNow.AddDays(-2) }, default);
        Assert.NotEmpty(recent);

        var limited = await admin.SearchObjectsAsync(s, new ObjectSearchQuery { Owner = _owner, Limit = 2 }, default);
        Assert.Equal(2, limited.Count);
    }

    [Fact]
    public async Task Object_search_filters_by_size()
    {
        var s = await OpenAsync("system", _db.SystemPassword);
        var admin = new OracleSchemaAdminProvider();

        var sized = await admin.SearchObjectsAsync(s, new ObjectSearchQuery { Owner = _owner, ObjectTypes = ["TABLE"], MinSizeBytes = 1 }, default);

        Assert.Contains(sized, r => r.Ref.Name == "DEPT" && r.SizeBytes > 0);
        Assert.DoesNotContain(sized, r => r.SizeBytes is null);
    }

    [Fact]
    public async Task Source_search_finds_lines_by_text_case_whole_word_and_regex()
    {
        await using (var conn = await OpenSystemAsync())
        {
            // all_source 줄 번호: 명세 1~3, 본문 1~9.
            await ExecAsync(conn, $"CREATE PACKAGE {_owner}.find_pkg AS\n  PROCEDURE run_it;\nEND find_pkg;");
            await ExecAsync(conn, $"CREATE PACKAGE BODY {_owner}.find_pkg AS\n  PROCEDURE run_it IS\n    v_total  NUMBER;\n    v_totalx NUMBER;\n  BEGIN\n"
                + "    v_total := 0; -- Needle_Mark\n    v_totalx := 1;\n  END run_it;\nEND find_pkg;");
        }

        var s = await OpenAsync("system", _db.SystemPassword);
        var admin = new OracleSchemaAdminProvider();
        async Task<SearchHits<SourceSearchHit>> Find(SourceSearchQuery q) => await admin.SearchSourceAsync(s, q with { Owner = _owner }, default);

        var mark = Assert.Single((await Find(new SourceSearchQuery { Text = "needle_mark" })).Rows);
        Assert.Equal(new DbObjectRef(_owner, "FIND_PKG", "PACKAGE BODY"), mark.Ref);
        Assert.Equal(6, mark.Line);
        Assert.EndsWith("-- Needle_Mark", mark.Text, StringComparison.Ordinal); // 줄 끝 줄바꿈은 뗀다

        Assert.Empty((await Find(new SourceSearchQuery { Text = "needle_mark", MatchCase = true })).Rows);
        Assert.Equal([3, 4, 6, 7], (await Find(new SourceSearchQuery { Text = "V_TOTAL" })).Rows.Select(h => h.Line));
        Assert.Equal([3, 6], (await Find(new SourceSearchQuery { Text = "v_total", WholeWord = true })).Rows.Select(h => h.Line));
        Assert.Equal([6, 7], (await Find(new SourceSearchQuery { Text = @"^\s*v_total(x)?\s*:=", UseRegex = true })).Rows.Select(h => h.Line));

        // 줄 끝 줄바꿈을 떼고 맞추므로 $ 가 줄 끝에 맞는다. 명세(3줄)와 본문(9줄) 모두.
        var ends = (await Find(new SourceSearchQuery { Text = "^end find_pkg;$", UseRegex = true })).Rows;
        Assert.Equal([("PACKAGE", 3), ("PACKAGE BODY", 9)], ends.Select(h => (h.Ref.ObjectType, h.Line)));

        var specOnly = await Find(new SourceSearchQuery { Text = "find_pkg", ObjectTypes = ["PACKAGE"] });
        Assert.NotEmpty(specOnly.Rows);
        Assert.All(specOnly.Rows, h => Assert.Equal("PACKAGE", h.Ref.ObjectType));

        var capped = await Find(new SourceSearchQuery { Text = "v_total", Limit = 1 });
        Assert.Single(capped.Rows);
        Assert.True(capped.Capped);
        Assert.False((await Find(new SourceSearchQuery { Text = "v_total", Limit = 4 })).Capped);
    }

    [Fact]
    public async Task Source_search_reports_a_bad_regex_even_when_no_line_would_be_read()
    {
        var s = await OpenAsync("system", _db.SystemPassword);
        var admin = new OracleSchemaAdminProvider();

        var bad = await Assert.ThrowsAsync<SearchPatternException>(() =>
            admin.SearchSourceAsync(s, new SourceSearchQuery { Owner = _owner, Text = "(abc", UseRegex = true }, default));
        Assert.Contains("ORA-12725", bad.Message, StringComparison.Ordinal);

        // 소스가 하나도 없는 스키마에서도 틀린 정규식을 알린다(dual 검사).
        var none = await Assert.ThrowsAsync<SearchPatternException>(() =>
            admin.SearchSourceAsync(s, new SourceSearchQuery { Owner = _recycleUser, Text = "[abc", UseRegex = true }, default));
        Assert.Contains("ORA-12726", none.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Column_search_finds_columns_case_insensitively_with_type_and_nullability()
    {
        var s = await OpenAsync("system", _db.SystemPassword);
        var admin = new OracleSchemaAdminProvider();

        var names = await admin.SearchColumnsAsync(s, new ColumnSearchQuery { Owner = _owner, NamePattern = "%name%" }, default);
        Assert.False(names.Capped);
        Assert.Equal([("DEPT", "TABLE"), ("DEPT_V", "VIEW")], names.Rows.Select(h => (h.Table.Name, h.Table.ObjectType)));
        var deptName = names.Rows[0];
        Assert.Equal("NAME", deptName.ColumnName);
        Assert.Equal("VARCHAR2(30)", deptName.DataType);
        Assert.False(deptName.IsNullable);

        var ids = await admin.SearchColumnsAsync(s, new ColumnSearchQuery { Owner = _owner, NamePattern = "%ID%" }, default);
        Assert.Contains(ids.Rows, h => h.Table.Name == "EMP" && h.ColumnName == "DEPT_ID" && h.DataType == "NUMBER(6)" && h.IsNullable);

        var capped = await admin.SearchColumnsAsync(s, new ColumnSearchQuery { Owner = _owner, NamePattern = "%", Limit = 1 }, default);
        Assert.Single(capped.Rows);
        Assert.True(capped.Capped);
    }

    [Fact]
    public async Task References_list_both_directions()
    {
        var s = await OpenAsync("system", _db.SystemPassword);
        var admin = new OracleSchemaAdminProvider();

        var view = await admin.GetReferencesAsync(s, new DbObjectRef(_owner, "DEPT_V", "VIEW"), default);
        var table = await admin.GetReferencesAsync(s, new DbObjectRef(_owner, "DEPT", "TABLE"), default);

        Assert.Contains(view, r => !r.IsUsedBy && r.Ref.Name == "DEPT");
        Assert.Contains(table, r => r.IsUsedBy && r.Ref.Name == "DEPT_V");
    }

    [Fact]
    public async Task Invalid_objects_become_valid_after_compile_statements()
    {
        var s = await OpenAsync("system", _db.SystemPassword);
        var admin = new OracleSchemaAdminProvider();
        await using (var conn = await OpenSystemAsync())
        {
            await ExecAsync(conn, $"CREATE TABLE {_owner}.late_t (id NUMBER)");
        }

        var before = await admin.SearchObjectsAsync(s, new ObjectSearchQuery { Owner = _owner, InvalidOnly = true }, default);
        Assert.Contains(before, r => r.Ref.Name == "LATE_P");

        await using (var conn = await OpenSystemAsync())
        {
            foreach (var row in before)
            {
                await ExecAsync(conn, SchemaStatements.Compile(row.Ref)!.Text);
            }
        }

        var after = await admin.SearchObjectsAsync(s, new ObjectSearchQuery { Owner = _owner, InvalidOnly = true }, default);
        Assert.Empty(after);
    }

    [Fact]
    public async Task Recyclebin_lists_dropped_table_and_flashback_and_purge_work()
    {
        var s = await OpenAsync(_recycleUser, _recyclePassword);
        var admin = new OracleSchemaAdminProvider();
        await using var conn = await OpenAsAsync(_recycleUser, _recyclePassword);
        await ExecAsync(conn, "CREATE TABLE keep_t (id NUMBER)");
        await ExecAsync(conn, "CREATE TABLE gone_t (id NUMBER)");
        await ExecAsync(conn, "DROP TABLE keep_t");
        await ExecAsync(conn, "DROP TABLE gone_t");

        var items = await admin.ListRecycleBinAsync(s, default);

        var keep = Assert.Single(items, i => i.OriginalName == "KEEP_T");
        var gone = Assert.Single(items, i => i.OriginalName == "GONE_T");
        Assert.StartsWith("BIN$", keep.ObjectName, StringComparison.Ordinal);
        Assert.True(keep.CanUndrop);
        Assert.NotNull(keep.DropTime);

        await ExecAsync(conn, SchemaStatements.Flashback(keep).Text);
        await ExecAsync(conn, SchemaStatements.PurgeItem(gone).Text);

        var after = await admin.ListRecycleBinAsync(s, default);
        Assert.DoesNotContain(after, i => i.OriginalName is "KEEP_T" or "GONE_T");
        await ExecAsync(conn, "DROP TABLE keep_t");
        await ExecAsync(conn, SchemaStatements.PurgeAll().Text);
        Assert.Empty(await admin.ListRecycleBinAsync(s, default));
    }

    [Fact]
    public async Task Bulk_extract_writes_single_file_in_dependency_order_with_renames()
    {
        var s = await OpenAsync("system", _db.SystemPassword);

        var result = await DdlBulkExporter.ExtractAsync(NewMeta(), s, new DdlExtractRequest
        {
            Owner = _owner,
            OutputDirectory = _outDir,
            Renames = [new DdlRename(_owner, "NEWSCHEMA")],
        }, null, default);

        var file = Assert.Single(result.Files);
        Assert.EndsWith("NEWSCHEMA.sql", file, StringComparison.Ordinal);
        var text = await File.ReadAllTextAsync(file);
        Assert.Contains("NEWSCHEMA", text);
        Assert.DoesNotContain($"\"{_owner}\"", text);
        Assert.Contains("CREATE TABLE", text);
        Assert.Contains("CREATE SEQUENCE", text);
        Assert.True(text.IndexOf("CREATE SEQUENCE", StringComparison.Ordinal) < text.IndexOf("CREATE TABLE", StringComparison.Ordinal));
        Assert.True(text.IndexOf("CREATE TABLE", StringComparison.Ordinal) < text.IndexOf("VIEW", StringComparison.Ordinal));
        Assert.Contains(result.Items, i => i.Ref.Name == "DEPT" && i.Succeeded);
        Assert.Equal(0, result.FailedCount);
    }

    [Fact]
    public async Task Bulk_extract_per_object_applies_filter_and_storage_options()
    {
        var s = await OpenAsync("system", _db.SystemPassword);
        var request = new DdlExtractRequest
        {
            Owner = _owner,
            ObjectTypes = ["TABLE"],
            NamePattern = "DEPT%",
            OutputDirectory = _outDir,
            Layout = DdlExtractLayout.FilePerObject,
            Ddl = new DdlOptions { IncludeTablespace = true, IncludeSegmentAttributes = true, IncludeStorage = true },
        };

        var result = await DdlBulkExporter.ExtractAsync(NewMeta(), s, request, null, default);

        var file = Assert.Single(result.Files);
        Assert.EndsWith(Path.Combine("table", "DEPT.sql"), file, StringComparison.Ordinal);
        Assert.Contains("TABLESPACE", await File.ReadAllTextAsync(file));
    }

    [Fact]
    public async Task Bulk_extract_keeps_going_when_one_object_fails()
    {
        var s = await OpenAsync("system", _db.SystemPassword);
        var meta = new FailingMeta(NewMeta(), "EMP");

        var result = await DdlBulkExporter.ExtractAsync(meta, s, new DdlExtractRequest
        {
            Owner = _owner,
            ObjectTypes = ["TABLE"],
            OutputDirectory = _outDir,
            Layout = DdlExtractLayout.FilePerObject,
        }, null, default);

        Assert.Equal(1, result.FailedCount);
        Assert.Equal("EMP", result.Items.Single(i => !i.Succeeded).Ref.Name);
        Assert.Single(result.Files);
    }

    private sealed class FailingMeta(IMetadataProvider inner, string failName) : IMetadataProvider
    {
        public Task<IReadOnlyList<string>> ListSchemasAsync(ISession s, CancellationToken ct) => inner.ListSchemasAsync(s, ct);

        public Task<IReadOnlyList<DbObjectRef>> ListObjectsAsync(ISession s, ObjectQuery q, CancellationToken ct) => inner.ListObjectsAsync(s, q, ct);

        public Task<TableInfo> GetTableAsync(ISession s, DbObjectRef table, CancellationToken ct) => inner.GetTableAsync(s, table, ct);

        public Task<IReadOnlyList<ProcedureInfo>> GetProceduresAsync(ISession s, DbObjectRef obj, CancellationToken ct) => inner.GetProceduresAsync(s, obj, ct);

        public Task<string> GetDdlAsync(ISession s, DbObjectRef obj, DdlOptions o, CancellationToken ct)
            => obj.Name == failName ? Task.FromException<string>(new InvalidOperationException("boom")) : inner.GetDdlAsync(s, obj, o, ct);

        public Task<ObjectDetail> GetObjectDetailAsync(ISession s, DbObjectRef obj, CancellationToken ct) => inner.GetObjectDetailAsync(s, obj, ct);

        public Task<string> GetSourceAsync(ISession s, DbObjectRef obj, CancellationToken ct) => inner.GetSourceAsync(s, obj, ct);

        public Task<IReadOnlyList<CompileError>> GetCompileErrorsAsync(ISession s, DbObjectRef obj, CancellationToken ct) => inner.GetCompileErrorsAsync(s, obj, ct);
    }

    private static OracleMetadataProvider NewMeta()
        => new(new MemoryMetadataCache(), new RecordingBus(), Timeout.InfiniteTimeSpan);

    private async Task<ISession> OpenAsync(string user, string password)
    {
        var manager = new OracleSessionManager(new RecordingBus());
        using var cred = new SecureCredential(password);
        var session = await manager.OpenAsync(new ConnectionProfile("t-" + user, user, _db.DataSource), cred, SessionPurpose.Work, default);
        _sessions.Add(session);
        return session;
    }

    private Task<OracleConnection> OpenSystemAsync() => OpenAsAsync("system", _db.SystemPassword);

    private async Task<OracleConnection> OpenAsAsync(string user, string password)
    {
        var profile = new ConnectionProfile("free-" + user, user, _db.DataSource);
        var conn = new OracleConnection(profile.BuildConnectionString(password));
        await conn.OpenAsync();
        return conn;
    }

    private static async Task ExecAsync(OracleConnection conn, string sql, bool ignoreErrors = false)
    {
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync();
        }
        catch (OracleException) when (ignoreErrors)
        {
        }
    }
}
