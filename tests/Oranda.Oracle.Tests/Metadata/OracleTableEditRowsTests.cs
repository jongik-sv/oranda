using Oranda.Core.Metadata;
using Oranda.Oracle.Metadata;

namespace Oranda.Oracle.Tests.Metadata;

// 테이블 편집기 TE-29: Oracle meta.* 뒤에 붙인 열 → 모델 init 멤버. 행 모양은 docs/design/oracle-table-edit-probe.md 실측 값.
public class OracleTableEditRowsTests
{
    // meta.columns: column_name, position, data_type, data_length, data_precision, data_scale, nullable, data_default, virtual_column,
    // identity, hidden_column, domain_name, comments, byte_length, char_used, identity_generation, identity_options, default_on_null
    private static object?[] Col(string name, string type, string? charUsed = null, string identity = "NO",
        string? generation = null, string? options = null, string? onNull = "NO", string? dflt = null) =>
        [name, 1m, type, 10m, null, null, "Y", dflt, "NO", identity, "NO", null, null, 10m, charUsed, generation, options, onNull];

    // meta.constraints: constraint_name, constraint_type, status, search_condition, r_owner, r_table_name, column_name, position,
    // r_constraint_name, delete_rule, r_column_name, index_name
    private static object?[] Cons(string name, string type, string? column, int position, string? rule = null, string? refColumn = null,
        string? index = null) =>
        [name, type, "ENABLED", null, type == "R" ? "SAMPLE" : null, type == "R" ? "ORDERS" : null, column, (decimal)position,
         type == "R" ? "PK_ORDERS" : null, rule, refColumn, index];

    // meta.indexes: index_name, uniqueness, index_type, column_name, column_position, descend, column_expression, tablespace_name
    private static object?[] Idx(string name, string column, int position, string descend = "ASC", string? expression = null,
        string type = "NORMAL", string unique = "NONUNIQUE", string? tablespace = "USERS") =>
        [name, unique, type, column, (decimal)position, descend, expression, tablespace];

    [Fact]
    public void Char_semantics_are_read_only_for_varchar2_and_char()
    {
        Assert.Equal(CharSemantics.Characters, OracleTableEditRows.ToColumn(Col("C2", "VARCHAR2", "C")).CharSemantics);
        Assert.Equal(CharSemantics.Bytes, OracleTableEditRows.ToColumn(Col("C1", "CHAR", "B")).CharSemantics);
        Assert.Null(OracleTableEditRows.ToColumn(Col("C5", "NVARCHAR2", "C")).CharSemantics); // n CHAR 를 쓰면 문법 오류
        Assert.Null(OracleTableEditRows.ToColumn(Col("N1", "NUMBER")).CharSemantics);
    }

    [Fact]
    public void Identity_options_are_split_into_start_and_increment()
    {
        var c = OracleTableEditRows.ToColumn(Col("ID", "NUMBER", identity: "YES", generation: "BY DEFAULT",
            options: "START WITH: 100, INCREMENT BY: 5, MAX_VALUE: 9999999999999999999999999999, MIN_VALUE: 1, CYCLE_FLAG: N",
            dflt: "\"SAMPLE\".\"ISEQ$$_124062\".nextval"));

        Assert.True(c.IsIdentity);
        Assert.Equal(new IdentityInfo(IdentityGeneration.ByDefault) { StartWith = 100, IncrementBy = 5 }, c.Identity);
    }

    [Theory]
    [InlineData("ALWAYS", "NO", IdentityGeneration.Always)]
    [InlineData("BY DEFAULT", "YES", IdentityGeneration.ByDefaultOnNull)]
    public void Identity_generation_uses_default_on_null(string generation, string onNull, IdentityGeneration expected)
    {
        var c = OracleTableEditRows.ToColumn(Col("ID", "NUMBER", identity: "YES", generation: generation, onNull: onNull));

        Assert.Equal(expected, c.Identity!.Generation);
        Assert.Null(c.Identity.StartWith); // 옵션 글이 없으면 모름
    }

    [Fact]
    public void Old_row_shape_without_the_extra_columns_reads_as_before()
    {
        object?[] old = ["C2", 1m, "VARCHAR2", 10m, null, null, "Y", null, "NO", "NO", "NO", null, null, 40m];

        var c = OracleTableEditRows.ToColumn(old);

        Assert.Equal("C2", c.Name);
        Assert.Null(c.CharSemantics);
        Assert.Null(c.Identity);
    }

    [Fact]
    public void Foreign_keys_get_the_delete_rule_and_referenced_columns_in_position_order()
    {
        object?[][] rows =
        [
            Cons("FK_ITEMS_01", "R", "ORDER_NO", 2, "SET NULL", "NO"),
            Cons("FK_ITEMS_01", "R", "ORDER_ID", 1, "SET NULL", "ID"),
            Cons("FK_ITEMS_02", "R", "P2", 1, "CASCADE", "ID"),
            Cons("FK_ITEMS_03", "R", "P3", 1, "NO ACTION", "ID"),
            Cons("PK_ITEMS", "P", "ID", 1, index: "PK_ITEMS"),
        ];

        var list = OracleTableEditRows.WithConstraintExtras(MetadataRows.BuildConstraints(rows), rows);

        var fk1 = list.Single(c => c.Name == "FK_ITEMS_01");
        Assert.Equal(DeleteRule.SetNull, fk1.DeleteRule);
        Assert.Equal(["ID", "NO"], fk1.ReferencedColumns);
        Assert.Equal(DeleteRule.Cascade, list.Single(c => c.Name == "FK_ITEMS_02").DeleteRule);
        Assert.Equal(DeleteRule.NoAction, list.Single(c => c.Name == "FK_ITEMS_03").DeleteRule);
        Assert.Empty(list.Single(c => c.Name == "PK_ITEMS").ReferencedColumns);
    }

    [Fact]
    public void Descending_plain_column_is_read_back_under_its_own_name()
    {
        // 실측 10: ix(p1 DESC, p2) → FUNCTION-BASED NORMAL, 첫 열 SYS_NC00006$ 와 식 "P1"
        object?[][] rows =
        [
            Idx("IX_C_01", "SYS_NC00006$", 1, "DESC", "\"P1\"", "FUNCTION-BASED NORMAL"),
            Idx("IX_C_01", "P2", 2, type: "FUNCTION-BASED NORMAL"),
        ];

        var index = Assert.Single(OracleTableEditRows.BuildIndexes(rows, []));

        Assert.Equal(["P1", "P2"], index.Columns);
        Assert.Equal([true, false], index.Descending);
        Assert.Empty(index.Expressions);
        Assert.Equal("FUNCTION-BASED NORMAL", index.IndexType);
        Assert.Equal("USERS", index.Tablespace);
    }

    [Fact]
    public void Function_expressions_stay_with_the_hidden_column_name()
    {
        object?[][] rows =
        [
            Idx("IX_C_03", "P3", 1, type: "FUNCTION-BASED NORMAL"),
            Idx("IX_C_03", "SYS_NC00008$", 2, "DESC", "UPPER(\"PC\")", "FUNCTION-BASED NORMAL"),
        ];

        var index = Assert.Single(OracleTableEditRows.BuildIndexes(rows, []));

        Assert.Equal(["P3", "SYS_NC00008$"], index.Columns); // 데이터 복사의 함수 기반 판정이 숨은 열 이름을 쓴다
        Assert.Equal([null, "UPPER(\"PC\")"], index.Expressions);
        Assert.Equal([false, true], index.Descending);
    }

    [Fact]
    public void Plain_index_has_empty_extra_lists_and_constraint_indexes_name_their_constraint()
    {
        object?[][] idx = [Idx("PK_ITEMS", "ID", 1, unique: "UNIQUE"), Idx("IX_ITEMS_01", "P1", 1)];
        object?[][] cons = [Cons("PK_ITEMS", "P", "ID", 1, index: "PK_ITEMS"), Cons("FK_ITEMS_01", "R", "P1", 1, "NO ACTION", "ID")];

        var list = OracleTableEditRows.BuildIndexes(idx, cons);

        Assert.Equal("PK_ITEMS", list.Single(i => i.Name == "PK_ITEMS").ConstraintName);
        var plain = list.Single(i => i.Name == "IX_ITEMS_01");
        Assert.Null(plain.ConstraintName);
        Assert.Empty(plain.Descending);
        Assert.Empty(plain.Expressions);
    }

    [Theory]
    [InlineData("USERS", "N", null, "NO", null, "NO", TableKind.Heap, false)]
    [InlineData(null, "Y", null, "NO", null, "NO", TableKind.Temporary, false)]
    [InlineData(null, "N", "IOT", "NO", null, "NO", TableKind.Iot, false)]
    [InlineData(null, "N", null, "YES", null, "NO", TableKind.Heap, true)]
    [InlineData("USERS", "N", null, "NO", "C_EMP", "NO", TableKind.Cluster, false)]
    [InlineData(null, "N", null, "NO", null, "YES", TableKind.External, false)]
    public void Table_props_give_kind_tablespace_and_partitioned(
        string? tablespace, string temporary, string? iot, string partitioned, string? cluster, string external, TableKind kind, bool isPartitioned)
    {
        var table = new TableInfo(new DbObjectRef("SAMPLE", "ORDERS", "TABLE"), []);
        object?[][] rows = [[tablespace, temporary, temporary == "Y" ? "SYS$SESSION" : null, iot, partitioned, cluster, external]];

        var result = OracleTableEditRows.WithTableProps(table, rows);

        Assert.Equal(kind, result.Kind);
        Assert.Equal(tablespace, result.Tablespace);
        Assert.Equal(isPartitioned, result.IsPartitioned);
    }

    [Fact]
    public void View_without_table_props_stays_a_heap_table()
    {
        var view = new TableInfo(new DbObjectRef("SAMPLE", "V_ORDERS", "VIEW"), []);

        Assert.Same(view, OracleTableEditRows.WithTableProps(view, []));
    }
}
