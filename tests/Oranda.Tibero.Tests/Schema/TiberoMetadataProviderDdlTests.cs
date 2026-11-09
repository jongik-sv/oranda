using Oranda.Core.Metadata;
using Oranda.Tibero.Schema;
using Oranda.Tibero.Sessions;
using static Oranda.Tibero.Tests.Schema.SchemaFakes;

namespace Oranda.Tibero.Tests.Schema;

/// <summary>DDL 추출(표 = <c>meta.ddl</c>, PSM 객체 = <c>meta.source</c> 글)을 가짜 실행기로 본다.</summary>
public class TiberoMetadataProviderDdlTests
{
    private static readonly DbObjectRef Emp = new("SCOTT", "EMP", "TABLE");

    private static TiberoMetadataProvider Provider(
        List<(string Id, IReadOnlyDictionary<string, object?>? Binds)> calls,
        Func<string, TiberoSessionQuery.Table> answer,
        Func<IReadOnlyDictionary<string, object?>, string?>? table = null) =>
        new(Recording(calls, answer))
        {
            ReadTableDdl = (_, binds, _) => Task.FromResult(table?.Invoke(binds)),
        };

    [Fact]
    public async Task Table_ddl_sends_exactly_the_binds_meta_ddl_expects_and_trims_the_result()
    {
        IReadOnlyDictionary<string, object?>? sent = null;
        var calls = new List<(string Id, IReadOnlyDictionary<string, object?>? Binds)>();
        var provider = Provider(calls, ById(), binds =>
        {
            sent = binds;
            return "\r\n  CREATE TABLE \"SCOTT\".\"EMP\" (A NUMBER);\n\n";
        });
        await using var session = Session();

        var ddl = await provider.GetDdlAsync(session, Emp,
            new DdlOptions { SqlTerminator = true, IncludeStorage = true, IncludeDependentObjects = true }, CancellationToken.None);

        Assert.Equal("CREATE TABLE \"SCOTT\".\"EMP\" (A NUMBER);", ddl);
        Assert.Equal(new Dictionary<string, object?>
        {
            ["p_type"] = "TABLE",
            ["name"] = "EMP",
            ["owner"] = "SCOTT",
            ["terminator"] = 1,
            ["storage"] = 1,
            ["segment_attributes"] = 0,
            ["tablespace"] = 0,
            ["dependents"] = 1,
        }, sent);
        // 종속 객체는 사전 뷰로 읽는다(여기서는 모두 빈 결과라 표 DDL 만 남는다). 제약을 먼저 읽는다(제약 인덱스를 가리려고). 문장은 Oracle 순서.
        Assert.Equal(
            ["meta.ddl_constraints", "meta.ddl_table_indexes", "meta.table_comment", "meta.columns", "meta.detail_privileges_user", "meta.detail_triggers"],
            calls.Select(c => c.Id));
    }

    [Fact]
    public async Task Table_ddl_without_dependents_asks_nothing_else()
    {
        var calls = new List<(string Id, IReadOnlyDictionary<string, object?>? Binds)>();
        var provider = Provider(calls, ById(), _ => "CREATE TABLE T (A NUMBER);");
        await using var session = Session();

        await provider.GetDdlAsync(session, Emp, new DdlOptions(), CancellationToken.None);

        Assert.Empty(calls);
    }

    [Fact]
    public async Task Table_dependents_are_complete_on_their_own_whatever_the_table_ddl_contains_and_split_off_like_oracle()
    {
        // 표 DDL 이 기본키·주석을 이미 담았어도 종속 부분은 빼지 않는다(CTAS 복사본에 종속 부분만 다시 실행한다).
        const string table = "CREATE TABLE \"SCOTT\".\"EMP\" (\"ID\" NUMBER, \"NAME\" VARCHAR2(10) NOT NULL, CONSTRAINT \"EMP_PK\" PRIMARY KEY (\"ID\"));\n" +
                             "COMMENT ON TABLE \"SCOTT\".\"EMP\" IS '사원';";
        var calls = new List<(string Id, IReadOnlyDictionary<string, object?>? Binds)>();
        var provider = Provider(calls, ById(
            ("meta.ddl_constraints", Table(["CONSTRAINT_NAME"],
                ["EMP_CK", "C", "ENABLED", "ID > 0", 1, "ID", null, null, null, null],
                ["EMP_PK", "P", "ENABLED", null, 1, "ID", null, null, null, null],
                ["SYS_C0009", "C", "ENABLED", "\"NAME\" IS NOT NULL", 1, "NAME", null, null, null, null])),
            ("meta.ddl_table_indexes", Table(["OWNER"],
                ["SCOTT", "EMP_NAME_IX", "SCOTT", "EMP", "NONUNIQUE", "NORMAL", "NO", 1, "NAME", "DESC", null],
                ["SCOTT", "EMP_PK", "SCOTT", "EMP", "UNIQUE", "NORMAL", "NO", 1, "ID", "ASC", null],
                ["SCOTT", "EMP_PART_IX", "SCOTT", "EMP", "NONUNIQUE", "NORMAL", "YES", 1, "NAME", "ASC", null],
                ["SCOTT", "SYS_IL0001", "SCOTT", "EMP", "UNIQUE", "LOB", "NO", 1, "C", "ASC", null],
                ["APP", "EMP_APP_IX", "SCOTT", "EMP", "NONUNIQUE", "NORMAL", "NO", 1, "ID", "ASC", null])),
            ("meta.table_comment", Table(["COMMENTS"], ["사원"])),
            ("meta.columns", Table(["COLUMN_NAME"],
                ["ID", 1, "NUMBER", null, null, null, "N", null, "NO", null, "NO", null, "번호"],
                ["NAME", 2, "VARCHAR2", 10, null, null, "Y", null, "NO", null, "NO", null, null])),
            ("meta.detail_privileges_user", Table(["GRANTEE"], ["HR", "SELECT", "NO", "SCOTT"])),
            ("meta.detail_triggers", Table(["TRIGGER_NAME"], ["EMP_BI", "BEFORE EACH ROW", "INSERT", "DISABLED", null, "SCOTT"])),
            ("meta.source", Table(["TEXT"], ["TRIGGER emp_bi BEFORE INSERT ON emp FOR EACH ROW\n"], ["BEGIN NULL; END;"]))),
            _ => table);
        await using var session = Session();

        var plain = await provider.GetDdlAsync(session, Emp, new DdlOptions(), CancellationToken.None);
        var full = await provider.GetDdlAsync(session, Emp, new DdlOptions { IncludeDependentObjects = true }, CancellationToken.None);

        string[] expected =
        [
            "CREATE INDEX \"SCOTT\".\"EMP_NAME_IX\" ON \"SCOTT\".\"EMP\" (\"NAME\" DESC);",
            "-- 인덱스 SCOTT.EMP_PART_IX 는 파티션 인덱스다: LOCAL·파티션 정의를 사전에서 읽지 않아 DDL 을 만들지 않는다(전역 인덱스로 잘못 만들지 않게).",
            "CREATE INDEX \"APP\".\"EMP_APP_IX\" ON \"SCOTT\".\"EMP\" (\"ID\");",
            "ALTER TABLE \"SCOTT\".\"EMP\" ADD CONSTRAINT \"EMP_CK\" CHECK (ID > 0) ENABLE;",
            "ALTER TABLE \"SCOTT\".\"EMP\" ADD CONSTRAINT \"EMP_PK\" PRIMARY KEY (\"ID\") ENABLE;",
            "COMMENT ON TABLE \"SCOTT\".\"EMP\" IS '사원';",
            "COMMENT ON COLUMN \"SCOTT\".\"EMP\".\"ID\" IS '번호';",
            "GRANT SELECT ON \"SCOTT\".\"EMP\" TO \"HR\";",
            "CREATE OR REPLACE TRIGGER \"SCOTT\".\"EMP_BI\" BEFORE INSERT ON emp FOR EACH ROW\nBEGIN NULL; END;\n/\nALTER TRIGGER \"SCOTT\".\"EMP_BI\" DISABLE;",
        ];
        Assert.Equal(table, plain);
        Assert.Equal(table + "\n" + string.Join("\n", expected), full);
        Assert.Equal(string.Join("\n", expected), Oranda.Oracle.Schema.ColumnReorderScripts.DependentPart(plain, full));
        Assert.Equal(new Dictionary<string, object?> { ["owner"] = "SCOTT", ["name"] = "EMP" }, calls.Single(c => c.Id == "meta.ddl_constraints").Binds);
        Assert.Equal(new Dictionary<string, object?> { ["owner"] = "SCOTT", ["name"] = "EMP_BI", ["type"] = "TRIGGER" }, calls.Single(c => c.Id == "meta.source").Binds);
    }

    [Fact]
    public async Task A_view_takes_its_query_from_meta_ddl_view_and_its_column_list_from_meta_columns()
    {
        var calls = new List<(string Id, IReadOnlyDictionary<string, object?>? Binds)>();
        var provider = Provider(calls, ById(
            ("meta.ddl_view", Table(["TEXT"], ["select id from emp"])),
            ("meta.columns", Table(["COLUMN_NAME"], ["ID", 1, "NUMBER", null, null, null, "Y", null, "NO", null, "NO", null, null]))));
        await using var session = Session();

        var ddl = await provider.GetDdlAsync(session, new DbObjectRef("HR", "EMP_V", "VIEW"), new DdlOptions(), CancellationToken.None);

        Assert.Equal("CREATE OR REPLACE FORCE VIEW \"HR\".\"EMP_V\" (\"ID\") AS \n  select id from emp;", ddl);
        Assert.Equal(["meta.ddl_view", "meta.columns"], calls.Select(c => c.Id));
        Assert.Equal(new Dictionary<string, object?> { ["owner"] = "HR", ["name"] = "EMP_V" }, calls[0].Binds);
    }

    [Fact]
    public async Task Sequence_synonym_and_index_each_read_their_dictionary_rows()
    {
        var calls = new List<(string Id, IReadOnlyDictionary<string, object?>? Binds)>();
        var provider = Provider(calls, ById(
            ("meta.ddl_sequence", Table(["MIN_VALUE"], ["1", "999", "1", "N", "N", "20", "7"])),
            ("meta.name_step", Table(["KIND"], ["SYNONYM", "SCOTT", "EMP", null])),
            ("meta.ddl_index", Table(["OWNER"], ["HR", "EMP_UX", "SCOTT", "EMP", "UNIQUE", "NORMAL", "NO", 1, "ID", "ASC", null]))));
        await using var session = Session();
        var bare = new DdlOptions { SqlTerminator = false };

        var sequence = await provider.GetDdlAsync(session, new DbObjectRef("HR", "SQ", "SEQUENCE"), bare, CancellationToken.None);
        var synonym = await provider.GetDdlAsync(session, new DbObjectRef("PUBLIC", "EMP", "SYNONYM"), bare, CancellationToken.None);
        var index = await provider.GetDdlAsync(session, new DbObjectRef("HR", "EMP_UX", "INDEX"), bare, CancellationToken.None);

        Assert.Equal("CREATE SEQUENCE \"HR\".\"SQ\" MINVALUE 1 MAXVALUE 999 INCREMENT BY 1 START WITH 7 CACHE 20 NOORDER NOCYCLE", sequence);
        Assert.Equal("CREATE OR REPLACE PUBLIC SYNONYM \"EMP\" FOR \"SCOTT\".\"EMP\"", synonym);
        Assert.Equal("CREATE UNIQUE INDEX \"HR\".\"EMP_UX\" ON \"SCOTT\".\"EMP\" (\"ID\")", index);
        Assert.Equal(new Dictionary<string, object?> { ["owner"] = "HR", ["name"] = "EMP_UX" }, calls.Single(c => c.Id == "meta.ddl_index").Binds);
    }

    [Fact]
    public async Task An_index_that_cannot_be_written_and_missing_rows_are_reported()
    {
        var domain = Provider([], ById(
            ("meta.ddl_index", Table(["OWNER"], ["HR", "I", "SCOTT", "EMP", "NONUNIQUE", "DOMAIN", "NO", 1, "NAME", "ASC", null]))));
        var partitioned = Provider([], ById(
            ("meta.ddl_index", Table(["OWNER"], ["HR", "I", "SCOTT", "EMP", "NONUNIQUE", "NORMAL", "YES", 1, "NAME", "ASC", null]))));
        var empty = Provider([], ById());
        await using var session = Session();

        var refused = await Assert.ThrowsAsync<NotSupportedException>(() => domain.GetDdlAsync(session, new DbObjectRef("HR", "I", "INDEX"), new DdlOptions(), CancellationToken.None));
        Assert.Contains("DOMAIN", refused.Message, StringComparison.Ordinal);
        var global = await Assert.ThrowsAsync<NotSupportedException>(() => partitioned.GetDdlAsync(session, new DbObjectRef("HR", "I", "INDEX"), new DdlOptions(), CancellationToken.None));
        Assert.Contains("파티션", global.Message, StringComparison.Ordinal);
        foreach (var type in new[] { "VIEW", "SEQUENCE", "SYNONYM", "INDEX", "TRIGGER" })
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => empty.GetDdlAsync(session, new DbObjectRef("HR", "X", type), new DdlOptions(), CancellationToken.None));
        }
    }

    [Fact]
    public async Task Table_ddl_without_the_terminator_binds_zero()
    {
        IReadOnlyDictionary<string, object?>? sent = null;
        var provider = Provider([], ById(), binds =>
        {
            sent = binds;
            return "CREATE TABLE T (A NUMBER)";
        });
        await using var session = Session();

        var ddl = await provider.GetDdlAsync(session, Emp, new DdlOptions { SqlTerminator = false }, CancellationToken.None);

        Assert.Equal("CREATE TABLE T (A NUMBER)", ddl);
        Assert.Equal(0, sent!["terminator"]);
    }

    [Fact]
    public async Task A_procedure_becomes_create_or_replace_with_the_qualified_name_and_a_slash()
    {
        var calls = new List<(string Id, IReadOnlyDictionary<string, object?>? Binds)>();
        var provider = Provider(calls, _ => Table(["TEXT"], ["PROCEDURE p_hello (a NUMBER)\n"], ["IS\n"], ["BEGIN NULL; END;\n"]));
        await using var session = Session();

        var withSlash = await provider.GetDdlAsync(session, new DbObjectRef("HR", "P_HELLO", "PROCEDURE"), new DdlOptions(), CancellationToken.None);
        var bare = await provider.GetDdlAsync(session, new DbObjectRef("HR", "P_HELLO", "PROCEDURE"), new DdlOptions { SqlTerminator = false }, CancellationToken.None);

        Assert.Equal("CREATE OR REPLACE PROCEDURE \"HR\".\"P_HELLO\" (a NUMBER)\nIS\nBEGIN NULL; END;\n/", withSlash);
        Assert.Equal("CREATE OR REPLACE PROCEDURE \"HR\".\"P_HELLO\" (a NUMBER)\nIS\nBEGIN NULL; END;", bare);
        Assert.Equal(["meta.source", "meta.source"], calls.Select(c => c.Id));
        Assert.Equal(new Dictionary<string, object?> { ["owner"] = "HR", ["name"] = "P_HELLO", ["type"] = "PROCEDURE" }, calls[0].Binds);
    }

    [Theory]
    [InlineData("PACKAGE", "PACKAGE pkg AS\nEND;")]
    [InlineData("PACKAGE BODY", "PACKAGE BODY pkg AS\nEND;")]
    [InlineData("FUNCTION", "FUNCTION pkg RETURN NUMBER IS BEGIN RETURN 1; END;")]
    [InlineData("TYPE", "TYPE pkg AS OBJECT (a NUMBER)")]
    [InlineData("TYPE BODY", "TYPE BODY pkg AS\nEND;")]
    public async Task Each_psm_type_asks_its_own_source_type_and_keeps_spec_and_body_apart(string type, string source)
    {
        var calls = new List<(string Id, IReadOnlyDictionary<string, object?>? Binds)>();
        var provider = Provider(calls, _ => Table(["TEXT"], [source]));
        await using var session = Session();

        var ddl = await provider.GetDdlAsync(session, new DbObjectRef("HR", "PKG", type), new DdlOptions { SqlTerminator = false }, CancellationToken.None);

        Assert.StartsWith("CREATE OR REPLACE " + type + " \"HR\".\"PKG\"", ddl, StringComparison.Ordinal);
        Assert.Equal(type, calls.Single().Binds!["type"]);
    }

    [Fact]
    public async Task A_trigger_is_built_from_its_source_lines_and_ends_with_its_state_like_oracle()
    {
        var calls = new List<(string Id, IReadOnlyDictionary<string, object?>? Binds)>();
        var source = Table(["TEXT"], ["TRIGGER trg_emp_bi\n"], ["BEFORE INSERT ON emp\n"], ["BEGIN NULL; END;"]);
        var disabled = Provider(calls, ById(("meta.source", source), ("meta.ddl_trigger", Table(["STATUS"], ["DISABLED"]))));
        var unknown = Provider([], ById(("meta.source", source)));
        await using var session = Session();
        var trigger = new DbObjectRef("SCOTT", "TRG_EMP_BI", "TRIGGER");

        var ddl = await disabled.GetDdlAsync(session, trigger, new DdlOptions(), CancellationToken.None);
        var noState = await unknown.GetDdlAsync(session, trigger, new DdlOptions(), CancellationToken.None);

        const string create = "CREATE OR REPLACE TRIGGER \"SCOTT\".\"TRG_EMP_BI\"\nBEFORE INSERT ON emp\nBEGIN NULL; END;";
        Assert.Equal(create + "\n/\nALTER TRIGGER \"SCOTT\".\"TRG_EMP_BI\" DISABLE;", ddl);
        Assert.Equal(create + "\n/", noState);
        Assert.Equal(new Dictionary<string, object?> { ["owner"] = "SCOTT", ["name"] = "TRG_EMP_BI" }, calls.Single(c => c.Id == "meta.ddl_trigger").Binds);
    }

    [Theory]
    [InlineData("MATERIALIZED VIEW")]
    [InlineData("DATABASE LINK")]
    [InlineData("JAVA SOURCE")]
    public async Task Types_built_later_say_which_type_is_not_supported(string type)
    {
        var provider = Provider([], ById());
        await using var session = Session();

        var ex = await Assert.ThrowsAsync<NotSupportedException>(() => provider.GetDdlAsync(session, new DbObjectRef("S", "X", type), new DdlOptions(), CancellationToken.None));

        Assert.Contains(type, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_objects_throw_invalid_operation_like_the_source_reader()
    {
        var noSource = Provider([], ById());
        var emptyDdl = Provider([], ById(), _ => " \r\n");
        var nullDdl = Provider([], ById(), _ => null);
        var notPsm = Provider([], _ => Table(["TEXT"], ["FUNCTION f RETURN NUMBER IS BEGIN RETURN 1; END;"]));
        await using var session = Session();

        await Assert.ThrowsAsync<InvalidOperationException>(() => noSource.GetDdlAsync(session, new DbObjectRef("S", "P", "PROCEDURE"), new DdlOptions(), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => emptyDdl.GetDdlAsync(session, Emp, new DdlOptions(), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => nullDdl.GetDdlAsync(session, Emp, new DdlOptions(), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => notPsm.GetDdlAsync(session, new DbObjectRef("S", "P", "PROCEDURE"), new DdlOptions(), CancellationToken.None));
    }
}
