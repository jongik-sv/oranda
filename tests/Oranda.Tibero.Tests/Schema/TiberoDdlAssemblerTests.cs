using Oranda.Tibero.Schema;
using Column = Oranda.Tibero.Schema.TiberoDdlAssembler.IndexColumn;
using DdlConstraint = Oranda.Tibero.Schema.TiberoDdlAssembler.DdlConstraint;
using DdlIndex = Oranda.Tibero.Schema.TiberoDdlAssembler.DdlIndex;

namespace Oranda.Tibero.Tests.Schema;

/// <summary>사전 뷰 행 → Oracle 모양 DDL 글(<see cref="TiberoDdlAssembler"/>, 세션 없음).</summary>
public class TiberoDdlAssemblerTests
{
    [Fact]
    public void A_view_is_create_or_replace_force_with_quoted_columns_and_the_query_on_the_next_line()
    {
        var ddl = TiberoDdlAssembler.View("HR", "EMP_V", ["ID", "NAME"], "  select id, name from emp\n", terminator: true);

        Assert.Equal("CREATE OR REPLACE FORCE VIEW \"HR\".\"EMP_V\" (\"ID\", \"NAME\") AS \n  select id, name from emp;", ddl);
        Assert.Equal("CREATE OR REPLACE FORCE VIEW \"HR\".\"V\" AS \n  select 1 from dual", TiberoDdlAssembler.View("HR", "V", [], "select 1 from dual", false));
    }

    [Fact]
    public void A_sequence_starts_with_its_last_number_and_spells_cache_order_cycle_like_oracle()
    {
        object?[] cached = ["1", "9999999999999999999999999999", "1", "N", "N", "20", "41"];
        object?[] plain = ["-100", "-1", "-1", "Y", "Y", "0", "-5"];

        Assert.Equal(
            "CREATE SEQUENCE \"HR\".\"SQ\" MINVALUE 1 MAXVALUE 9999999999999999999999999999 INCREMENT BY 1 START WITH 41 CACHE 20 NOORDER NOCYCLE;",
            TiberoDdlAssembler.Sequence("HR", "SQ", cached, terminator: true));
        Assert.Equal(
            "CREATE SEQUENCE \"HR\".\"SQ\" MINVALUE -100 MAXVALUE -1 INCREMENT BY -1 START WITH -5 NOCACHE ORDER CYCLE",
            TiberoDdlAssembler.Sequence("HR", "SQ", plain, terminator: false));
    }

    [Fact]
    public void Private_and_public_synonyms_point_at_the_qualified_target()
    {
        Assert.Equal("CREATE OR REPLACE SYNONYM \"HR\".\"E\" FOR \"SCOTT\".\"EMP\";", TiberoDdlAssembler.Synonym("HR", "E", "SCOTT", "EMP", true));
        Assert.Equal("CREATE OR REPLACE PUBLIC SYNONYM \"DUAL\" FOR \"SYS\".\"DUAL\"", TiberoDdlAssembler.Synonym("PUBLIC", "DUAL", "SYS", "DUAL", false));
        Assert.Equal("CREATE OR REPLACE SYNONYM \"HR\".\"X\" FOR \"T\"", TiberoDdlAssembler.Synonym("HR", "X", null, "T", false));
    }

    private static DdlIndex Ix(string name, string? uniqueness, string? type, bool partitioned, params Column[] columns) =>
        new("HR", name, "HR", "T", uniqueness, type, partitioned, columns);

    private static DdlConstraint Con(string name, string type, params string[] columns) =>
        new(name, type, true, null, columns, null, null, [], null);

    [Fact]
    public void Index_rows_group_by_owner_and_name_so_same_names_in_two_schemas_stay_apart()
    {
        object?[][] rows =
        [
            ["HR", "I", "HR", "T", "UNIQUE", "FUNCTION-BASED NORMAL", "NO", 1, "A", "ASC", null],
            ["HR", "I", "HR", "T", "UNIQUE", "FUNCTION-BASED NORMAL", "NO", 2, "SYS_NC00004$", "DESC", "UPPER(\"B\") "],
            ["APP", "I", "HR", "T", "NONUNIQUE", "NORMAL", "YES", 1, "C", "ASC", null],
        ];

        var indexes = TiberoDdlAssembler.Indexes(rows);

        Assert.Equal(2, indexes.Count);
        Assert.Equal("CREATE UNIQUE INDEX \"HR\".\"I\" ON \"HR\".\"T\" (\"A\", UPPER(\"B\") DESC);", TiberoDdlAssembler.Index(indexes[0], true));
        Assert.Equal("APP", indexes[1].Owner);
        Assert.True(indexes[1].Partitioned);
        Assert.Equal(["C"], indexes[1].Columns.Select(c => c.Name));
    }

    [Fact]
    public void An_index_keeps_unique_bitmap_and_reverse()
    {
        Assert.Equal("CREATE BITMAP INDEX \"HR\".\"B\" ON \"HR\".\"T\" (\"A\")",
            TiberoDdlAssembler.Index(Ix("B", "NONUNIQUE", "BITMAP", false, new Column("A", false, null)), false));
        Assert.Equal("CREATE INDEX \"HR\".\"R\" ON \"HR\".\"T\" (\"A\" DESC) REVERSE",
            TiberoDdlAssembler.Index(Ix("R", "NONUNIQUE", "NORMAL/REV", false, new Column("A", true, null)), false));
    }

    [Fact]
    public void Indexes_that_cannot_be_written_say_why()
    {
        var a = new Column("A", false, null);
        Assert.Null(TiberoDdlAssembler.IndexRefusal(Ix("I", null, "NORMAL", false, a)));
        Assert.Null(TiberoDdlAssembler.IndexRefusal(Ix("I", null, "GLOBAL", false, a))); // 낱말 단위: GLOBAL 안의 LOB 는 LOB 인덱스가 아니다
        Assert.True(TiberoDdlAssembler.HasTypeWord("IOT - TOP", "IOT"));
        Assert.Contains("DOMAIN", TiberoDdlAssembler.IndexRefusal(Ix("I", null, "DOMAIN", false, a)), StringComparison.Ordinal);
        Assert.Contains("파티션", TiberoDdlAssembler.IndexRefusal(Ix("I", null, "NORMAL", true, a)), StringComparison.Ordinal);
        Assert.Contains("*_IND_COLUMNS", TiberoDdlAssembler.IndexRefusal(Ix("I", null, "NORMAL", false)), StringComparison.Ordinal);
        Assert.Contains("*_IND_EXPRESSIONS", TiberoDdlAssembler.IndexRefusal(Ix("I", null, "FUNCTION-BASED NORMAL", false, new Column("SYS_NC00003$", false, null))),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Constraint_indexes_are_found_from_dictionary_names_and_unique_columns_only()
    {
        DdlConstraint[] constraints = [Con("T_PK", "P", "ID"), Con("T_FK", "R", "D")];
        var id = new Column("ID", false, null);

        Assert.True(TiberoDdlAssembler.IsConstraintIndex(Ix("T_PK", "UNIQUE", "NORMAL", false, id), constraints));
        Assert.True(TiberoDdlAssembler.IsConstraintIndex(Ix("SYS_IDX_1", "UNIQUE", "NORMAL", false, id), constraints));
        Assert.False(TiberoDdlAssembler.IsConstraintIndex(Ix("T_ID_IX", "NONUNIQUE", "NORMAL", false, id), constraints));
        Assert.False(TiberoDdlAssembler.IsConstraintIndex(Ix("T_FK", "NONUNIQUE", "NORMAL", false, new Column("D", false, null)), constraints));
    }

    [Theory]
    [InlineData("\"A\" IS NOT NULL", true)]
    [InlineData(" a is not null ", true)]
    [InlineData("\"B\" IS NOT NULL", false)]
    [InlineData("A IS NOT NULL OR B IS NOT NULL", false)]
    [InlineData("A > 0 AND A IS NOT NULL", false)]
    public void Only_a_whole_single_column_is_not_null_condition_is_a_not_null_check(string condition, bool expected) =>
        Assert.Equal(expected, TiberoDdlAssembler.IsNotNullCheck(new DdlConstraint("SYS_C1", "C", true, condition, ["A"], null, null, [], null)));

    [Fact]
    public void Constraint_rows_become_complete_alter_table_statements_keys_and_checks_first_then_foreign_keys()
    {
        object?[][] rows =
        [
            ["T_FK", "R", "ENABLED", null, 1, "D1", "HR", "T", "ID", "CASCADE"],
            ["T_FK", "R", "ENABLED", null, 2, "D2", "HR", "T", "ID2", "CASCADE"],
            ["T_FK2", "R", "DISABLED", null, 1, "E", null, null, null, "NO ACTION"],
            ["T_PK", "P", "ENABLED", null, 1, "ID", null, null, null, null],
            ["T_PK", "P", "ENABLED", null, 2, "ID2", null, null, null, null],
            ["T_UK", "U", "DISABLED", null, 1, "A", null, null, null, null],
            ["SYS_C0001", "C", "ENABLED", " A > 0 ", 1, "A", null, null, null, null],
            ["SYS_C0002", "C", "ENABLED", "\"NAME\" IS NOT NULL", 1, "NAME", null, null, null, null],
        ];

        var statements = TiberoDdlAssembler.ConstraintStatements("HR", "T", TiberoDdlAssembler.Constraints(rows), true);

        Assert.Equal(
        [
            "ALTER TABLE \"HR\".\"T\" ADD CONSTRAINT \"T_PK\" PRIMARY KEY (\"ID\", \"ID2\") ENABLE;",
            "ALTER TABLE \"HR\".\"T\" ADD CONSTRAINT \"T_UK\" UNIQUE (\"A\") DISABLE;",
            "ALTER TABLE \"HR\".\"T\" ADD CONSTRAINT \"SYS_C0001\" CHECK (A > 0) ENABLE;",
            "ALTER TABLE \"HR\".\"T\" ADD CONSTRAINT \"T_FK\" FOREIGN KEY (\"D1\", \"D2\") REFERENCES \"HR\".\"T\" (\"ID\", \"ID2\") ON DELETE CASCADE ENABLE;",
            "-- 외래키 T_FK2 의 참조 표·열을 사전에서 읽지 못해 DDL 을 만들지 못했다.",
        ], statements);
    }

    [Fact]
    public void Comments_come_table_first_then_columns_with_quotes_doubled()
    {
        var statements = TiberoDdlAssembler.Comments("HR", "T", "it's a table", [("A", "열 A"), ("B", null), ("C", "")], true);

        Assert.Equal(
        [
            "COMMENT ON TABLE \"HR\".\"T\" IS 'it''s a table';",
            "COMMENT ON COLUMN \"HR\".\"T\".\"A\" IS '열 A';",
        ], statements);
    }

    [Fact]
    public void Grants_are_one_per_grantee_and_privilege_keeping_the_grant_option_and_public_unquoted()
    {
        object?[][] rows =
        [
            ["SCOTT", "SELECT", "NO", "ADMIN"],
            ["SCOTT", "SELECT", "YES", "HR"],
            ["PUBLIC", "SELECT", "NO", "HR"],
        ];

        Assert.Equal(
        [
            "GRANT SELECT ON \"HR\".\"T\" TO \"SCOTT\" WITH GRANT OPTION;",
            "GRANT SELECT ON \"HR\".\"T\" TO PUBLIC;",
        ], TiberoDdlAssembler.Grants("HR", "T", rows, true));
    }

    [Fact]
    public void A_trigger_ends_with_its_state_statement_like_oracle()
    {
        const string create = "CREATE OR REPLACE TRIGGER \"HR\".\"TRG\" BEFORE INSERT ON T FOR EACH ROW BEGIN NULL; END;";

        Assert.Equal(create + "\n/\nALTER TRIGGER \"HR\".\"TRG\" DISABLE;", TiberoDdlAssembler.Trigger(create, "HR", "TRG", "DISABLED", true));
        Assert.Equal(create + "\nALTER TRIGGER \"HR\".\"TRG\" ENABLE", TiberoDdlAssembler.Trigger(create, "HR", "TRG", "ENABLED", false));
        Assert.Equal(create + "\n/", TiberoDdlAssembler.Trigger(create, "HR", "TRG", null, true));
    }

    [Fact]
    public void Dependents_follow_the_table_ddl_so_the_dependent_part_splits_off_like_oracle()
    {
        const string table = "CREATE TABLE \"HR\".\"T\" (A NUMBER);";
        string[] dependents = ["CREATE INDEX \"HR\".\"I\" ON \"HR\".\"T\" (\"A\");", "GRANT SELECT ON \"HR\".\"T\" TO PUBLIC;"];

        var full = TiberoDdlAssembler.WithDependents(table, dependents);

        Assert.Equal(table, TiberoDdlAssembler.WithDependents(table, []));
        Assert.Equal(string.Join("\n", dependents), Oranda.Oracle.Schema.ColumnReorderScripts.DependentPart(table, full));
    }
}
