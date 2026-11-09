using Oranda.Core.Catalog;
using Oranda.Core.Metadata;
using Oranda.Tibero.Schema;
using Oranda.Tibero.Sessions;
using static Oranda.Tibero.Tests.Schema.SchemaFakes;

namespace Oranda.Tibero.Tests.Schema;

/// <summary>메타데이터 제공자 본체(스키마·객체·테이블·주석·프로시저)를 가짜 카탈로그 실행기로 본다.</summary>
public class TiberoMetadataProviderTests
{
    private static readonly DbObjectRef Emp = new("SCOTT", "EMP", "TABLE");

    private static readonly string[] ColumnHeader =
    [
        "COLUMN_NAME", "POSITION", "DATA_TYPE", "DATA_LENGTH", "DATA_PRECISION", "DATA_SCALE", "NULLABLE", "DATA_DEFAULT",
        "VIRTUAL_COLUMN", "IDENTITY_FLAG", "HIDDEN_COLUMN", "DOMAIN_NAME", "COMMENTS",
    ];

    private static readonly string[] ArgumentHeader =
        ["PROCEDURE_NAME", "OVERLOAD", "POSITION", "ARGUMENT_NAME", "IN_OUT", "DATA_TYPE", "DEFAULTED", "TYPE_OWNER", "TYPE_NAME", "TYPE_SUBNAME"];

    private static readonly string[] MemberHeader = ["PROCEDURE_NAME", "OVERLOAD", "SUBPROGRAM_ID", "OBJECT_TYPE"];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Every_catalog_id_the_provider_uses_resolves_in_the_tibero_catalog(bool dictionary)
    {
        await using var session = Session(dictionary);

        var unresolved = TiberoMetadataProvider.CatalogIds.Where(id => string.IsNullOrWhiteSpace(TiberoCatalogQuery.Sql(session, id))).ToList();

        Assert.Empty(unresolved);
        Assert.Equal(28, TiberoMetadataProvider.CatalogIds.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task Schemas_are_the_first_column_without_binds()
    {
        var calls = new List<(string Id, IReadOnlyDictionary<string, object?>? Binds)>();
        var provider = new TiberoMetadataProvider(Recording(calls, _ => Table(["USERNAME"], ["HR"], ["SCOTT"])));
        await using var session = Session();

        var schemas = await provider.ListSchemasAsync(session, CancellationToken.None);

        Assert.Equal(["HR", "SCOTT"], schemas);
        Assert.Equal(("meta.schemas", (IReadOnlyDictionary<string, object?>?)null), Assert.Single(calls));
    }

    [Fact]
    public async Task Objects_bind_every_filter_by_the_oracle_names_and_map_owner_name_type()
    {
        var calls = new List<(string Id, IReadOnlyDictionary<string, object?>? Binds)>();
        var provider = new TiberoMetadataProvider(Recording(calls, _ => Table(
            ["OWNER", "OBJECT_NAME", "OBJECT_TYPE", "STATUS", "LAST_DDL_TIME", "CREATED"],
            ["SCOTT", "EMP", "TABLE", "VALID", null, null],
            ["SCOTT", "EMP_V", "VIEW", "INVALID", null, null])));
        await using var session = Session();
        var created = new DateTimeOffset(2026, 1, 2, 12, 0, 0, TimeSpan.FromHours(9));
        var modified = new DateTimeOffset(2026, 3, 4, 0, 0, 0, TimeSpan.Zero);

        var objects = await provider.ListObjectsAsync(session, new ObjectQuery
        {
            Owner = "SCOTT",
            ObjectTypes = ["TABLE", "VIEW"],
            NamePattern = "EMP%",
            InvalidOnly = true,
            CreatedAfter = created,
            ModifiedAfter = modified,
            Limit = 50,
        }, CancellationToken.None);

        Assert.Equal([new DbObjectRef("SCOTT", "EMP", "TABLE"), new DbObjectRef("SCOTT", "EMP_V", "VIEW")], objects);
        var (id, binds) = Assert.Single(calls);
        Assert.Equal("meta.objects", id);
        Assert.Equal(new Dictionary<string, object?>
        {
            ["owner"] = "SCOTT",
            ["types"] = "|TABLE|VIEW|",
            ["name_pattern"] = "EMP%",
            ["invalid_only"] = 1,
            ["created_after"] = created.UtcDateTime,
            ["modified_after"] = modified.UtcDateTime,
            ["max_rows"] = 50,
        }, binds);
    }

    [Fact]
    public void An_empty_object_query_binds_nulls_zero_and_no_row_limit()
    {
        Assert.Equal(new Dictionary<string, object?>
        {
            ["owner"] = null,
            ["types"] = null,
            ["name_pattern"] = null,
            ["invalid_only"] = 0,
            ["created_after"] = null,
            ["modified_after"] = null,
            ["max_rows"] = int.MaxValue,
        }, TiberoMetadataProvider.ObjectBinds(new ObjectQuery()));
    }

    private static TiberoMetadataProvider CachedProvider(List<(string Id, IReadOnlyDictionary<string, object?>? Binds)> calls, Func<string, TiberoSessionQuery.Table> answer)
        => new(new MemoryMetadataCache(), new RecordingEventBus(), Timeout.InfiniteTimeSpan, Recording(calls, answer));

    [Fact]
    public async Task The_full_object_list_is_read_once_with_empty_binds_and_rows_map_to_meta_objects()
    {
        var calls = new List<(string Id, IReadOnlyDictionary<string, object?>? Binds)>();
        var provider = CachedProvider(calls, _ => Table(
            ["OWNER", "OBJECT_NAME", "OBJECT_TYPE", "STATUS", "LAST_DDL_TIME", "CREATED"],
            ["SCOTT", "EMP", "TABLE", "VALID", new DateTime(2026, 2, 3, 4, 5, 6, DateTimeKind.Unspecified), null],
            ["SCOTT", "EMP_V", "VIEW", "INVALID", null, null]));
        await using var session = Session();

        var objects = await provider.RefreshAsync(session, CancellationToken.None);
        var listed = await provider.ListObjectsAsync(session, new ObjectQuery { ObjectTypes = ["VIEW"] }, CancellationToken.None);

        var (id, binds) = Assert.Single(calls);
        Assert.Equal("meta.objects", id);
        Assert.Equal(TiberoMetadataProvider.ObjectBinds(new ObjectQuery()), binds);
        var emp = objects.Single(o => o.Name == "EMP");
        Assert.Equal((session.DbKey, "SCOTT", "TABLE", "VALID"), (emp.DbKey, emp.Owner, emp.Type, emp.Status));
        Assert.Equal(new DateTimeOffset(2026, 2, 3, 4, 5, 6, TimeSpan.Zero), emp.LastDdlTime);
        Assert.Null(objects.Single(o => o.Name == "EMP_V").LastDdlTime);
        Assert.Equal([new DbObjectRef("SCOTT", "EMP_V", "VIEW")], listed);
    }

    [Fact]
    public async Task Prefix_search_answers_from_the_cache_without_asking_the_server_and_fails_before_the_list_is_cached()
    {
        var calls = new List<(string Id, IReadOnlyDictionary<string, object?>? Binds)>();
        var provider = CachedProvider(calls, _ => Table(
            ["OWNER", "OBJECT_NAME", "OBJECT_TYPE", "STATUS", "LAST_DDL_TIME", "CREATED"],
            ["HR", "T_Z", "TABLE", "VALID", null, null],
            ["APP", "T_A", "TABLE", "VALID", null, null],
            ["APP", "OTHER", "TABLE", "VALID", null, null]));
        await using var session = Session();

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.FindObjectsByPrefixAsync(session, "T_", 5, CancellationToken.None));
        Assert.Empty(calls);

        await provider.ListObjectsAsync(session, new ObjectQuery(), CancellationToken.None);
        var found = await provider.FindObjectsByPrefixAsync(session, "T_", 5, CancellationToken.None);
        var first = await provider.FindObjectsByPrefixAsync(session, "T_", 1, CancellationToken.None);

        Assert.Equal([new DbObjectRef("APP", "T_A", "TABLE"), new DbObjectRef("HR", "T_Z", "TABLE")], found);
        Assert.Equal([new DbObjectRef("APP", "T_A", "TABLE")], first);
        Assert.Single(calls);
    }

    [Fact]
    public async Task A_cached_table_and_procedure_list_answer_without_asking_the_server_again()
    {
        var calls = new List<(string Id, IReadOnlyDictionary<string, object?>? Binds)>();
        var provider = CachedProvider(calls, ById(
            ("meta.objects", Table(
                ["OWNER", "OBJECT_NAME", "OBJECT_TYPE", "STATUS", "LAST_DDL_TIME", "CREATED"],
                ["SCOTT", "EMP", "TABLE", "VALID", null, null],
                ["HR", "PKG", "PACKAGE", "VALID", null, null])),
            ("meta.columns", Table(ColumnHeader, ["EMPNO", 1m, "NUMBER", null, 4m, 0m, "N", null, "NO", null, "NO", null, null])),
            ("meta.procedures", Table(MemberHeader, ["PING", null, 1m, "PACKAGE"])),
            ("meta.arguments", Table(ArgumentHeader))));
        await using var session = Session();
        await provider.ListObjectsAsync(session, new ObjectQuery(), CancellationToken.None);
        var pkg = new DbObjectRef("HR", "PKG", "PACKAGE");
        var first = await provider.GetTableAsync(session, Emp, CancellationToken.None);
        var firstProcs = await provider.GetProceduresAsync(session, pkg, CancellationToken.None);
        calls.Clear();

        var second = await provider.GetTableAsync(session, Emp, CancellationToken.None);
        var secondProcs = await provider.GetProceduresAsync(session, pkg, CancellationToken.None);

        Assert.Empty(calls);
        Assert.Equal(first.Columns, second.Columns);
        Assert.Equal(["PING"], secondProcs.Select(p => p.Name));
        Assert.Equal(firstProcs.Select(p => p.Name), secondProcs.Select(p => p.Name));
    }

    [Fact]
    public void Only_driver_server_errors_and_closed_or_busy_sessions_are_ignored_in_the_background_refresh()
    {
        var provider = new TiberoMetadataProvider(Throwing(new InvalidOperationException()));

        Assert.True(provider.IsIgnorable(ServerError(-10007, "TBR-10007: non-access server error")));
        Assert.True(provider.IsIgnorable(ServerError(-12001, "TBR-12001: session killed")));
        Assert.True(provider.IsIgnorable(new InvalidOperationException()));
        Assert.True(provider.IsIgnorable(new ObjectDisposedException("session")));
        Assert.False(provider.IsIgnorable(new CatalogQueryNotFoundException("meta.objects", 6)));
        Assert.False(provider.IsIgnorable(new IOException()));
    }

    [Fact]
    public async Task Table_reads_columns_constraints_indexes_and_comment_with_table_binds()
    {
        var calls = new List<(string Id, IReadOnlyDictionary<string, object?>? Binds)>();
        var provider = new TiberoMetadataProvider(Recording(calls, ById(
            ("meta.columns", Table(ColumnHeader,
                ["EMPNO", 1m, "NUMBER", null, 4m, 0m, "N", null, "NO", null, "NO", null, "사번"],
                ["ENAME", 2m, "VARCHAR", 10m, null, null, "Y", "'X'  \n", "NO", null, "NO", null, null])),
            ("meta.constraints", Table(
                ["CONSTRAINT_NAME", "CONSTRAINT_TYPE", "STATUS", "SEARCH_CONDITION", "R_OWNER", "R_TABLE_NAME", "COLUMN_NAME", "POSITION", "R_CONSTRAINT_NAME"],
                ["EMP_PK", "P", "ENABLED", null, null, null, "EMPNO", 1m, null],
                ["EMP_DEPT_FK", "R", "DISABLED", null, "SCOTT", "DEPT", "DEPTNO", 1m, "DEPT_PK"],
                ["SYS_C1", "C", "ENABLED", "\"ENAME\" IS NOT NULL", null, null, "ENAME", null, null],
                ["EMP_CK", "C", "ENABLED", "EMPNO > 0", null, null, "EMPNO", null, null])),
            ("meta.indexes", Table(["INDEX_NAME", "UNIQUENESS", "INDEX_TYPE", "COLUMN_NAME", "COLUMN_POSITION"],
                ["EMP_IX", "NONUNIQUE", "NORMAL", "ENAME", 1m],
                ["EMP_IX", "NONUNIQUE", "NORMAL", "EMPNO", 2m])),
            ("meta.table_comment", Table(["COMMENTS"], ["사원"])))));
        await using var session = Session();

        var table = await provider.GetTableAsync(session, Emp, CancellationToken.None);

        Assert.Equal(["meta.columns", "meta.constraints", "meta.indexes", "meta.table_comment"], calls.Select(c => c.Id));
        Assert.All(calls, c => Assert.Equal(new Dictionary<string, object?> { ["owner"] = "SCOTT", ["name"] = "EMP" }, c.Binds));
        Assert.Equal(new ColumnInfo("EMPNO", 1, "NUMBER") { Precision = 4, Scale = 0, IsNullable = false, Comment = "사번" }, table.Columns[0]);
        Assert.Equal(new ColumnInfo("ENAME", 2, "VARCHAR") { Length = 10, DefaultValue = "'X'" }, table.Columns[1]);
        Assert.Equal(["EMP_PK", "EMP_DEPT_FK", "EMP_CK"], table.Constraints.Select(c => c.Name));
        var fk = table.Constraints[1];
        Assert.Equal((ConstraintKind.ForeignKey, false, new DbObjectRef("SCOTT", "DEPT", "TABLE"), "DEPT_PK"), (fk.Kind, fk.IsEnabled, fk.ReferencedTable, fk.ReferencedConstraint));
        Assert.Equal("EMPNO > 0", table.Constraints[2].Condition);
        var index = Assert.Single(table.Indexes);
        Assert.Equal(("EMP_IX", false, "NORMAL"), (index.Name, index.IsUnique, index.IndexType));
        Assert.Equal(["ENAME", "EMPNO"], index.Columns);
        Assert.Equal("사원", table.Comment);
    }

    [Fact]
    public async Task A_table_without_columns_is_not_found()
    {
        var provider = new TiberoMetadataProvider(Recording([], _ => Table(ColumnHeader)));
        await using var session = Session();

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetTableAsync(session, Emp, CancellationToken.None));
    }

    [Fact]
    public async Task Table_comments_keep_only_non_empty_comments_and_bind_the_owner()
    {
        var calls = new List<(string Id, IReadOnlyDictionary<string, object?>? Binds)>();
        var provider = new TiberoMetadataProvider(Recording(calls, _ => Table(["TABLE_NAME", "COMMENTS"], ["EMP", "사원"], ["DEPT", ""], ["BONUS", null])));
        await using var session = Session();

        var comments = await provider.ListTableCommentsAsync(session, "SCOTT", CancellationToken.None);

        Assert.Equal(new Dictionary<string, string> { ["EMP"] = "사원" }, comments);
        Assert.Equal(("meta.table_comments", "SCOTT"), (calls[0].Id, calls[0].Binds!["owner"]));
    }

    [Fact]
    public async Task Package_members_take_their_arguments_and_a_body_reference_reads_the_specification()
    {
        var calls = new List<(string Id, IReadOnlyDictionary<string, object?>? Binds)>();
        var provider = new TiberoMetadataProvider(Recording(calls, ById(
            ("meta.procedures", Table(MemberHeader, ["GET_SAL", null, 1m, "PACKAGE"], ["PING", null, 2m, "PACKAGE"])),
            ("meta.arguments", Table(ArgumentHeader,
                ["GET_SAL", null, 0m, null, "OUT", "NUMBER", null, null, null, null],
                ["GET_SAL", null, 1m, "P_ID", "IN", "NUMBER", null, null, null, null],
                ["GET_SAL", null, 2m, "P_ROW", "IN/OUT", "OBJECT", null, "HR", "EMP_T", null],
                ["GET_SAL", null, 3m, "P_MSG", "OUT", "VARCHAR2", null, null, null, null])))));
        await using var session = Session();

        var procs = await provider.GetProceduresAsync(session, new DbObjectRef("HR", "PKG", "PACKAGE BODY"), CancellationToken.None);

        Assert.All(calls, c => Assert.Equal(new Dictionary<string, object?> { ["owner"] = "HR", ["name"] = "PKG" }, c.Binds));
        Assert.Equal(["meta.procedures", "meta.arguments"], calls.Select(c => c.Id));
        Assert.Equal(2, procs.Count);
        var getSal = procs[0];
        Assert.Equal(("GET_SAL", true, "NUMBER", (int?)null), (getSal.Name, getSal.IsFunction, getSal.ReturnType, getSal.Overload));
        Assert.Equal(
            [("P_ID", 1, ArgumentDirection.In, (string?)null), ("P_ROW", 2, ArgumentDirection.InOut, "HR.EMP_T"), ("P_MSG", 3, ArgumentDirection.Out, null)],
            getSal.Arguments.Select(a => (a.Name, a.Position, a.Direction, a.TypeName)));
        Assert.DoesNotContain(getSal.Arguments, a => a.HasDefault);
        Assert.Equal(("PING", false, 0), (procs[1].Name, procs[1].IsFunction, procs[1].Arguments.Count));
    }

    [Fact]
    public void Overloaded_members_take_the_argument_group_with_the_same_name_and_overload_number()
    {
        // 결정 64: meta.procedures·meta.arguments 가 같은 규칙으로 오버로드 번호를 낸다(같은 이름 둘 이상 = 1..n, 하나 = NULL). 끝 열 = subprogram_id.
        var procs = TiberoMetadataProvider.BuildProcedures(new DbObjectRef("HR", "PKG", "PACKAGE"),
            [["F", "1", 1m, "PACKAGE"], ["F", "2", 2m, "PACKAGE"], ["G", null, 3m, "PACKAGE"]],
            [
                ["F", "1", 1m, "A", "IN", "NUMBER", null, null, null, null, 1m],
                ["F", "2", 1m, "A", "IN", "VARCHAR2", null, null, null, null, 2m],
                ["F", "2", 2m, "B", "IN OUT", "DATE", null, null, null, null, 2m],
                ["G", null, 1m, "X", "IN", "NUMBER", null, null, null, null, 3m],
            ]);

        Assert.Equal([("F", (int?)1, 1), ("F", 2, 2), ("G", null, 1)], procs.Select(p => (p.Name, p.Overload, p.Arguments.Count)));
        Assert.Equal(["NUMBER"], procs[0].Arguments.Select(a => a.DataType));
        Assert.Equal([ArgumentDirection.In, ArgumentDirection.InOut], procs[1].Arguments.Select(a => a.Direction));
    }

    [Fact]
    public void A_member_without_argument_rows_is_kept_as_an_entry_without_arguments()
    {
        var procs = TiberoMetadataProvider.BuildProcedures(new DbObjectRef("HR", "PKG", "PACKAGE"),
            [["F", "1", 1m, "PACKAGE"], ["F", "2", 2m, "PACKAGE"]],
            [["F", "1", 1m, "A", "IN", "NUMBER", null, null, null, null, 1m]]);

        Assert.Equal([("F", (int?)1, 1), ("F", 2, 0)], procs.Select(p => (p.Name, p.Overload, p.Arguments.Count)));
    }

    [Fact]
    public void A_standalone_procedure_without_member_rows_is_built_from_its_arguments()
    {
        var procs = TiberoMetadataProvider.BuildProcedures(new DbObjectRef("HR", "RAISE", "PROCEDURE"), [],
            [["RAISE", null, 1m, "P_PCT", "IN", "NUMBER", null, null, null, null]]);

        var proc = Assert.Single(procs);
        Assert.Equal(("RAISE", false, "P_PCT"), (proc.Name, proc.IsFunction, Assert.Single(proc.Arguments).Name));
        Assert.Empty(TiberoMetadataProvider.BuildProcedures(new DbObjectRef("HR", "PKG", "PACKAGE"), [], []));
    }
}
