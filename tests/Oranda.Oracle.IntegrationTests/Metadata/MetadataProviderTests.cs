using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Metadata;
using Oranda.Core.Security;
using Oranda.Core.Sessions;
using Oranda.Core.Settings;
using Oranda.Core.Storage;
using Oranda.Oracle.Connection;
using Oranda.Oracle.IntegrationTests.Integration;
using Oranda.Oracle.IntegrationTests.Sessions;
using Oranda.Oracle.Metadata;
using Oranda.Oracle.Sessions;

namespace Oranda.Oracle.IntegrationTests.Metadata;

// 소유: TSK-04-04. 객체·테이블·인자.

public class MetadataProviderLogicTests
{
    [Theory]
    [InlineData("EMP%", "EMP", true)]
    [InlineData("EMP%", "EMPLOYEES", true)]
    [InlineData("EMP%", "DEMP", false)]
    [InlineData("E_P", "EMP", true)]
    [InlineData("E_P", "EMMP", false)]
    [InlineData("%", "ANY", true)]
    [InlineData("A\\_B%", "A_BC", true)]
    [InlineData("A\\_B%", "AXBC", false)]
    [InlineData("a%", "ABC", false)]
    public void Like_pattern_translates_like_the_server(string pattern, string name, bool expected)
        => Assert.Equal(expected, OracleMetadataProvider.LikeToRegex(pattern).IsMatch(name));
}

/// <summary>시험용 메모리 캐시. <see cref="IMetadataCache"/> 의 뜻(Invalidate 는 목록을 낡게 만든다)을 그대로 따른다.</summary>
internal sealed class MemoryMetadataCache : IMetadataCache
{
    private readonly Dictionary<string, MetaSource> _sources = [];
    private readonly Dictionary<(string, string, string, string), MetaObject> _objects = [];

    public int UpsertCalls { get; private set; }

    public Task<MetaSource?> GetSourceAsync(string dbKey, CancellationToken ct)
        => Task.FromResult(_sources.GetValueOrDefault(dbKey));

    public Task<IReadOnlyList<MetaObject>> GetObjectsAsync(string dbKey, string? owner, string? type, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<MetaObject>>([.. _objects.Values.Where(o => o.DbKey == dbKey && (owner is null || o.Owner == owner) && (type is null || o.Type == type))]);

    public Task<IReadOnlyList<MetaObject>> FindByPrefixAsync(string dbKey, string namePrefix, int limit, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<MetaObject>>([.. _objects.Values.Where(o => o.DbKey == dbKey && o.Name.StartsWith(namePrefix, StringComparison.Ordinal)).Take(limit)]);

    public Task UpsertAsync(MetaSource source, IReadOnlyList<MetaObject> objects, CancellationToken ct)
    {
        UpsertCalls++;
        var old = _sources.GetValueOrDefault(source.DbKey);
        _sources[source.DbKey] = source with { RefreshedAt = source.RefreshedAt ?? old?.RefreshedAt };
        foreach (var o in objects)
        {
            _objects[(source.DbKey, o.Owner, o.Name, o.Type)] = o with { DbKey = source.DbKey };
        }

        return Task.CompletedTask;
    }

    public Task InvalidateAsync(string dbKey, IReadOnlyList<DbObjectRef> objects, CancellationToken ct)
    {
        foreach (var key in _objects.Keys.Where(k => k.Item1 == dbKey && (objects.Count == 0 || objects.Any(r => r.Owner == k.Item2 && r.Name == k.Item3 && (r.ObjectType.Length == 0 || r.ObjectType == k.Item4)))).ToList())
        {
            _objects.Remove(key);
        }

        if (_sources.TryGetValue(dbKey, out var s))
        {
            _sources[dbKey] = s with { RefreshedAt = null };
        }

        return Task.CompletedTask;
    }

    public Task PruneAsync(MetadataCacheSettings settings, CancellationToken ct) => Task.CompletedTask;
}

[Trait("Category", "Integration")]
[Collection(OracleFreeShared.Name)]
public sealed class MetadataProviderIntegrationTests : IAsyncLifetime
{
    private readonly OracleFreeFixture _db;
    private readonly string _owner = "META_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
    private readonly List<ISession> _sessions = [];

    public MetadataProviderIntegrationTests(OracleFreeFixture db) => _db = db;

    public async Task InitializeAsync()
    {
        var u = _owner;
        await using var conn = await OpenSystemAsync();
        string[] ddl =
        [
            $"CREATE USER {u} NO AUTHENTICATION QUOTA UNLIMITED ON USERS",
            $"CREATE TABLE {u}.dept (id NUMBER(6) CONSTRAINT dept_pk PRIMARY KEY, name VARCHAR2(30 CHAR) NOT NULL, loc VARCHAR2(20) DEFAULT 'SEOUL')",
            $"COMMENT ON TABLE {u}.dept IS 'department'",
            $"COMMENT ON COLUMN {u}.dept.name IS 'department name'",
            $"CREATE TABLE {u}.emp (id NUMBER GENERATED ALWAYS AS IDENTITY, dept_id NUMBER(6) CONSTRAINT emp_dept_fk REFERENCES {u}.dept(id), "
                + "email VARCHAR2(100) CONSTRAINT emp_email_uq UNIQUE, salary NUMBER(8,2) CONSTRAINT emp_sal_ck CHECK (salary > 0), "
                + "bonus NUMBER GENERATED ALWAYS AS (salary * 0.1) VIRTUAL, secret VARCHAR2(10) INVISIBLE, CONSTRAINT emp_pk PRIMARY KEY (id))",
            $"CREATE INDEX {u}.emp_dept_ix ON {u}.emp(dept_id, salary)",
            $"CREATE VIEW {u}.emp_v AS SELECT id, email FROM {u}.emp",
            $"CREATE FORCE VIEW {u}.bad_v AS SELECT * FROM {u}.no_such_table",
            $"CREATE PROCEDURE {u}.sp(p IN OUT VARCHAR2, q NUMBER DEFAULT 1) AS BEGIN NULL; END;",
            $"CREATE FUNCTION {u}.f(a NUMBER) RETURN VARCHAR2 AS BEGIN RETURN 'x'; END;",
            $"CREATE PACKAGE {u}.pk AS PROCEDURE hire(p_name IN VARCHAR2, p_dept IN NUMBER DEFAULT 1, p_id OUT NUMBER); "
                + "FUNCTION cnt RETURN NUMBER; FUNCTION cnt(p_dept NUMBER) RETURN NUMBER; PROCEDURE noargs; END;",
            $"CREATE PACKAGE BODY {u}.pk AS PROCEDURE hire(p_name IN VARCHAR2, p_dept IN NUMBER DEFAULT 1, p_id OUT NUMBER) IS BEGIN p_id := 1; END; "
                + "FUNCTION cnt RETURN NUMBER IS BEGIN RETURN 0; END; FUNCTION cnt(p_dept NUMBER) RETURN NUMBER IS BEGIN RETURN 1; END; PROCEDURE noargs IS BEGIN NULL; END; END;",
            $"CREATE DOMAIN {u}.d_email AS VARCHAR2(100)",
            $"CREATE TABLE {u}.dom_t (mail VARCHAR2(100) DOMAIN {u}.d_email)",
            $"GRANT SELECT ON {u}.emp TO {OracleFreeFixture.AppUser}",
            $"GRANT SELECT ON {u}.emp_v TO {OracleFreeFixture.AppUser}",
            $"GRANT EXECUTE ON {u}.pk TO {OracleFreeFixture.AppUser}",
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
    public async Task Schemas_are_listed_for_dba_and_non_dba()
    {
        var provider = NewProvider(out _, out _);
        var dba = await provider.ListSchemasAsync(await OpenAsync("system", _db.SystemPassword), default);
        var app = await provider.ListSchemasAsync(await OpenAsync(OracleFreeFixture.AppUser, _db.AppPassword), default);

        Assert.Contains(_owner, dba);
        Assert.Contains(_owner, app);
        Assert.Contains("SYSTEM", app);
    }

    [Fact]
    public async Task Non_dba_account_sees_only_granted_objects_through_all_views()
    {
        var provider = NewProvider(out _, out _);
        var app = await OpenAsync(OracleFreeFixture.AppUser, _db.AppPassword);
        Assert.False(app.Server.Capabilities.CanQueryDictionary);

        var objects = await provider.ListObjectsAsync(app, new ObjectQuery { Owner = _owner }, default);

        Assert.Contains(new DbObjectRef(_owner, "EMP", "TABLE"), objects);
        Assert.Contains(new DbObjectRef(_owner, "EMP_V", "VIEW"), objects);
        Assert.Contains(new DbObjectRef(_owner, "PK", "PACKAGE"), objects);
        Assert.DoesNotContain(objects, o => o.Name == "DEPT");

        var table = await provider.GetTableAsync(app, new DbObjectRef(_owner, "EMP", "TABLE"), default);
        Assert.Contains(table.Columns, c => c.Name == "SALARY");
        var procs = await provider.GetProceduresAsync(app, new DbObjectRef(_owner, "PK", "PACKAGE"), default);
        Assert.Equal(4, procs.Count);
    }

    [Fact]
    public async Task Dba_account_sees_all_objects_and_filters_apply()
    {
        var provider = NewProvider(out _, out _);
        var dba = await OpenAsync("system", _db.SystemPassword);
        Assert.True(dba.Server.Capabilities.CanQueryDictionary);

        var all = await provider.ListObjectsAsync(dba, new ObjectQuery { Owner = _owner }, default);
        Assert.Contains(all, o => o.Name == "DEPT" && o.ObjectType == "TABLE");
        Assert.Contains(all, o => o.Name == "D_EMAIL" && o.ObjectType == "DOMAIN");
        Assert.DoesNotContain(all, o => o.ObjectType == "INDEX" && o.Name.StartsWith("BIN$", StringComparison.Ordinal));

        var tables = await provider.ListObjectsAsync(dba, new ObjectQuery { Owner = _owner, ObjectTypes = ["TABLE"], NamePattern = "E%" }, default);
        Assert.Equal([new DbObjectRef(_owner, "EMP", "TABLE")], tables);

        var invalid = await provider.ListObjectsAsync(dba, new ObjectQuery { Owner = _owner, InvalidOnly = true }, default);
        Assert.Contains(invalid, o => o.Name == "BAD_V");
        Assert.DoesNotContain(invalid, o => o.Name == "EMP");

        var limited = await provider.ListObjectsAsync(dba, new ObjectQuery { Owner = _owner, Limit = 2 }, default);
        Assert.Equal(2, limited.Count);

        var recent = await provider.ListObjectsAsync(dba, new ObjectQuery { Owner = _owner, CreatedAfter = DateTimeOffset.UtcNow.AddYears(10) }, default);
        Assert.Empty(recent);
    }

    [Fact]
    public async Task Cached_list_is_served_without_reading_server_after_reconnect()
    {
        var provider = NewProvider(out var cache, out var bus);
        var first = await OpenAsync("system", _db.SystemPassword);
        var query = new ObjectQuery { Owner = _owner, ObjectTypes = ["TABLE"] };
        var before = await provider.ListObjectsAsync(first, query, default);
        Assert.Contains(before, o => o.Name == "DEPT");

        await using (var conn = await OpenSystemAsync())
        {
            await ExecAsync(conn, $"DROP TABLE {_owner}.dom_t PURGE");
        }

        // 다시 접속한 세션 + 새 제공자(같은 캐시): 서버에는 dom_t 가 이미 없지만 캐시는 아직 안다 = 서버를 읽지 않았다.
        var provider2 = new OracleMetadataProvider(cache, bus, Timeout.InfiniteTimeSpan);
        var second = await OpenAsync("system", _db.SystemPassword);
        var cached = await provider2.ListObjectsAsync(second, query, default);
        Assert.Contains(cached, o => o.Name == "DOM_T");
        Assert.Equal(before.Count, cached.Count);

        // 갱신하면 사라진 것을 알고 이벤트를 낸다.
        await provider2.RefreshAsync(second, default);
        var after = await provider2.ListObjectsAsync(second, query, default);
        Assert.DoesNotContain(after, o => o.Name == "DOM_T");
        var evt = Assert.Single(bus.Events.OfType<MetadataInvalidated>());
        Assert.Equal(second.DbKey, evt.DbKey);
        Assert.Contains(new DbObjectRef(_owner, "DOM_T", "TABLE"), evt.Objects);
    }

    [Fact]
    public async Task Refresh_detects_changed_new_and_dropped_objects_by_last_ddl_time()
    {
        var provider = NewProvider(out var cache, out var bus);
        var s = await OpenAsync("system", _db.SystemPassword);
        await provider.ListObjectsAsync(s, new ObjectQuery { Owner = _owner }, default);
        Assert.Empty(bus.Events.OfType<MetadataInvalidated>()); // 첫 적재는 변경이 아니다.

        // 상세를 캐시해 두고, DDL 이 나면 비워지는지 본다.
        await provider.GetTableAsync(s, new DbObjectRef(_owner, "DEPT", "TABLE"), default);
        Assert.NotNull((await cache.GetObjectsAsync(s.DbKey, _owner, "TABLE", default)).Single(o => o.Name == "DEPT").PayloadJson);

        await Task.Delay(1500); // DATE 는 초 단위
        await using (var conn = await OpenSystemAsync())
        {
            await ExecAsync(conn, $"ALTER TABLE {_owner}.dept ADD (note VARCHAR2(10))");
            await ExecAsync(conn, $"CREATE TABLE {_owner}.fresh_t (a NUMBER)");
            await ExecAsync(conn, $"DROP VIEW {_owner}.bad_v");
        }

        await provider.RefreshAsync(s, default);

        var evt = Assert.Single(bus.Events.OfType<MetadataInvalidated>());
        Assert.Contains(new DbObjectRef(_owner, "DEPT", "TABLE"), evt.Objects);
        Assert.Contains(new DbObjectRef(_owner, "FRESH_T", "TABLE"), evt.Objects);
        Assert.Contains(new DbObjectRef(_owner, "BAD_V", "VIEW"), evt.Objects);
        Assert.DoesNotContain(new DbObjectRef(_owner, "EMP", "TABLE"), evt.Objects);

        var dept = (await cache.GetObjectsAsync(s.DbKey, _owner, "TABLE", default)).Single(o => o.Name == "DEPT");
        Assert.Null(dept.PayloadJson);
        var table = await provider.GetTableAsync(s, new DbObjectRef(_owner, "DEPT", "TABLE"), default);
        Assert.Contains(table.Columns, c => c.Name == "NOTE");
    }

    [Fact]
    public async Task Table_info_has_columns_constraints_indexes_and_comments()
    {
        var provider = NewProvider(out _, out _);
        var s = await OpenAsync("system", _db.SystemPassword);

        var dept = await provider.GetTableAsync(s, new DbObjectRef(_owner, "DEPT", "TABLE"), default);
        Assert.Equal("department", dept.Comment);
        var name = dept.Columns.Single(c => c.Name == "NAME");
        Assert.False(name.IsNullable);
        Assert.Equal(30, name.Length);
        Assert.Equal("department name", name.Comment);
        Assert.Equal("'SEOUL'", dept.Columns.Single(c => c.Name == "LOC").DefaultValue);
        Assert.Equal(6, dept.Columns.Single(c => c.Name == "ID").Precision);

        var emp = await provider.GetTableAsync(s, new DbObjectRef(_owner, "EMP", "TABLE"), default);
        Assert.Equal(["ID", "DEPT_ID", "EMAIL", "SALARY", "BONUS", "SECRET"], emp.Columns.Select(c => c.Name));
        Assert.True(emp.Columns.Single(c => c.Name == "ID").IsIdentity);
        Assert.True(emp.Columns.Single(c => c.Name == "BONUS").IsVirtual);
        Assert.True(emp.Columns.Single(c => c.Name == "SECRET").IsInvisible);
        Assert.Equal(8, emp.Columns.Single(c => c.Name == "SALARY").Precision);
        Assert.Equal(2, emp.Columns.Single(c => c.Name == "SALARY").Scale);

        var pk = emp.Constraints.Single(c => c.Kind == ConstraintKind.PrimaryKey);
        Assert.Equal(["ID"], pk.Columns);
        var fk = emp.Constraints.Single(c => c.Kind == ConstraintKind.ForeignKey);
        Assert.Equal(new DbObjectRef(_owner, "DEPT", "TABLE"), fk.ReferencedTable);
        Assert.Equal(["DEPT_ID"], fk.Columns);
        Assert.Equal(["EMAIL"], emp.Constraints.Single(c => c.Kind == ConstraintKind.Unique).Columns);
        var check = emp.Constraints.Single(c => c.Kind == ConstraintKind.Check);
        Assert.Contains("salary > 0", check.Condition, StringComparison.Ordinal);
        Assert.DoesNotContain(emp.Constraints, c => c.Condition?.Contains("IS NOT NULL", StringComparison.Ordinal) == true);

        var ix = emp.Indexes.Single(i => i.Name == "EMP_DEPT_IX");
        Assert.False(ix.IsUnique);
        Assert.Equal(["DEPT_ID", "SALARY"], ix.Columns);
        Assert.Contains(emp.Indexes, i => i.IsUnique && i.Columns.SequenceEqual(["ID"]));

        var view = await provider.GetTableAsync(s, new DbObjectRef(_owner, "EMP_V", "VIEW"), default);
        Assert.Equal(["ID", "EMAIL"], view.Columns.Select(c => c.Name));
    }

    [Fact]
    public async Task Domain_name_is_reported_on_23_and_later()
    {
        var provider = NewProvider(out _, out _);
        var s = await OpenAsync("system", _db.SystemPassword);

        var table = await provider.GetTableAsync(s, new DbObjectRef(_owner, "DOM_T", "TABLE"), default);

        Assert.Equal("D_EMAIL", table.Columns.Single().Domain);
    }

    [Fact]
    public async Task Missing_table_throws()
    {
        var provider = NewProvider(out _, out _);
        var s = await OpenAsync("system", _db.SystemPassword);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.GetTableAsync(s, new DbObjectRef(_owner, "NOPE", "TABLE"), default));
    }

    [Fact]
    public async Task Table_detail_comes_from_cache_once_stored()
    {
        var provider = NewProvider(out _, out _);
        var s = await OpenAsync("system", _db.SystemPassword);
        await provider.ListObjectsAsync(s, new ObjectQuery { Owner = _owner }, default);
        var first = await provider.GetTableAsync(s, new DbObjectRef(_owner, "DEPT", "TABLE"), default);

        await using (var conn = await OpenSystemAsync())
        {
            await ExecAsync(conn, $"ALTER TABLE {_owner}.dept DROP COLUMN loc");
        }

        var second = await provider.GetTableAsync(s, new DbObjectRef(_owner, "DEPT", "TABLE"), default);

        Assert.Equal(first.Columns.Select(c => c.Name), second.Columns.Select(c => c.Name));
        Assert.Contains(second.Columns, c => c.Name == "LOC"); // 아직 낡은 캐시. 갱신 전까지는 의도된 동작.
        Assert.Equal(first.Constraints.Select(c => c.Name), second.Constraints.Select(c => c.Name));
        Assert.Equal(first.Comment, second.Comment);
    }

    [Fact]
    public async Task Procedures_of_package_include_overloads_functions_and_defaults()
    {
        var provider = NewProvider(out _, out _);
        var s = await OpenAsync("system", _db.SystemPassword);

        var members = await provider.GetProceduresAsync(s, new DbObjectRef(_owner, "PK", "PACKAGE BODY"), default);

        Assert.Equal(["CNT", "CNT", "HIRE", "NOARGS"], members.Select(m => m.Name).Order());
        var hire = members.Single(m => m.Name == "HIRE");
        Assert.False(hire.IsFunction);
        Assert.Equal(["P_NAME", "P_DEPT", "P_ID"], hire.Arguments.Select(a => a.Name));
        Assert.Equal([ArgumentDirection.In, ArgumentDirection.In, ArgumentDirection.Out], hire.Arguments.Select(a => a.Direction));
        Assert.Equal([false, true, false], hire.Arguments.Select(a => a.HasDefault));
        Assert.Equal("VARCHAR2", hire.Arguments[0].DataType);

        var cnts = members.Where(m => m.Name == "CNT").OrderBy(m => m.Arguments.Count).ToList();
        Assert.All(cnts, c => Assert.True(c.IsFunction));
        Assert.All(cnts, c => Assert.Equal("NUMBER", c.ReturnType));
        Assert.Empty(cnts[0].Arguments);
        Assert.Single(cnts[1].Arguments);
        Assert.NotEqual(cnts[0].Overload, cnts[1].Overload);

        Assert.Empty(members.Single(m => m.Name == "NOARGS").Arguments);
    }

    [Fact]
    public async Task Procedures_of_standalone_procedure_and_function()
    {
        var provider = NewProvider(out _, out _);
        var s = await OpenAsync("system", _db.SystemPassword);

        var sp = Assert.Single(await provider.GetProceduresAsync(s, new DbObjectRef(_owner, "SP", "PROCEDURE"), default));
        Assert.Equal("SP", sp.Name);
        Assert.False(sp.IsFunction);
        Assert.Equal([ArgumentDirection.InOut, ArgumentDirection.In], sp.Arguments.Select(a => a.Direction));
        Assert.True(sp.Arguments[1].HasDefault);

        var f = Assert.Single(await provider.GetProceduresAsync(s, new DbObjectRef(_owner, "F", "FUNCTION"), default));
        Assert.True(f.IsFunction);
        Assert.Equal("VARCHAR2", f.ReturnType);
        Assert.Single(f.Arguments);
    }

    private static OracleMetadataProvider NewProvider(out MemoryMetadataCache cache, out RecordingBus bus)
    {
        cache = new MemoryMetadataCache();
        bus = new RecordingBus();
        return new OracleMetadataProvider(cache, bus, Timeout.InfiniteTimeSpan);
    }

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
