using Oranda.Core.Metadata;
using Oranda.Oracle.Schema;

namespace Oranda.Oracle.Tests.Schema;

// 소유: TSK-13-01. 열 순서 바꾸기 스크립트 만들기·막는 경우·소스 후보 판정.
public class ColumnReorderScriptsTests
{
    private static readonly DbObjectRef Table = new("SCOTT", "EMP", "TABLE");
    private static readonly ColumnReorderTraits Plain = new(false, false, null);

    private static ColumnInfo Col(string name, int pos) => new(name, pos, "NUMBER");

    private static string[] Texts(IEnumerable<Oranda.Core.Execution.SqlStatement> statements) => [.. statements.Select(s => s.Text)];

    [Fact]
    public void InvisibleVisible_starts_at_first_difference_and_follows_new_order()
    {
        var statements = ColumnReorderScripts.InvisibleVisible(Table, ["A", "B", "C", "D"], ["A", "C", "D", "B"]);

        Assert.Equal(
        [
            "ALTER TABLE \"SCOTT\".\"EMP\" MODIFY (\"C\" INVISIBLE)",
            "ALTER TABLE \"SCOTT\".\"EMP\" MODIFY (\"C\" VISIBLE)",
            "ALTER TABLE \"SCOTT\".\"EMP\" MODIFY (\"D\" INVISIBLE)",
            "ALTER TABLE \"SCOTT\".\"EMP\" MODIFY (\"D\" VISIBLE)",
            "ALTER TABLE \"SCOTT\".\"EMP\" MODIFY (\"B\" INVISIBLE)",
            "ALTER TABLE \"SCOTT\".\"EMP\" MODIFY (\"B\" VISIBLE)",
        ], Texts(statements));
    }

    [Fact]
    public void InvisibleVisible_hides_each_column_right_before_showing_it()
    {
        // 모든 열이 동시에 숨는 순간이 없어야 한다: INVISIBLE 바로 다음이 같은 열의 VISIBLE.
        var texts = Texts(ColumnReorderScripts.InvisibleVisible(Table, ["A", "B", "C"], ["C", "B", "A"]));

        Assert.Equal(6, texts.Length);
        for (var i = 0; i < texts.Length; i += 2)
        {
            Assert.EndsWith("INVISIBLE)", texts[i], StringComparison.Ordinal);
            Assert.EndsWith("VISIBLE)", texts[i + 1], StringComparison.Ordinal);
            Assert.Equal(texts[i].Replace("INVISIBLE", "VISIBLE", StringComparison.Ordinal), texts[i + 1]);
        }
    }

    [Fact]
    public void InvisibleVisible_is_empty_when_nothing_changed()
        => Assert.Empty(ColumnReorderScripts.InvisibleVisible(Table, ["A", "B"], ["A", "B"]));

    [Fact]
    public void InvisibleVisible_rejects_order_that_is_not_a_permutation()
        => Assert.Throws<ArgumentException>(() => ColumnReorderScripts.InvisibleVisible(Table, ["A", "B"], ["A", "X"]));

    [Fact]
    public void InvisibleVisible_quotes_identifiers()
    {
        var text = Texts(ColumnReorderScripts.InvisibleVisible(new DbObjectRef("U", "T\"1", "TABLE"), ["a b", "C"], ["C", "a b"]))[0];

        Assert.Equal("ALTER TABLE \"U\".\"T\"\"1\" MODIFY (\"C\" INVISIBLE)", text);
    }

    [Fact]
    public void BlockReasons_is_empty_for_plain_table_on_12c()
        => Assert.Empty(ColumnReorderScripts.BlockReasons(ColumnReorderMethod.InvisibleVisible, [Col("A", 1)], Plain, atLeast12c: true));

    [Theory]
    [InlineData(true, false, null, "외부 표")]
    [InlineData(false, true, null, "임시 표")]
    [InlineData(false, false, "CL1", "클러스터 표(CL1)")]
    public void BlockReasons_names_the_table_kind(bool external, bool temporary, string? cluster, string expected)
    {
        var reasons = ColumnReorderScripts.BlockReasons(
            ColumnReorderMethod.InvisibleVisible, [Col("A", 1)], new ColumnReorderTraits(external, temporary, cluster), atLeast12c: true);

        Assert.Contains(reasons, r => r.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void BlockReasons_blocks_originally_invisible_columns_and_11g()
    {
        var columns = new[] { Col("A", 1), new ColumnInfo("H", 2, "NUMBER") { IsInvisible = true } };

        var reasons = ColumnReorderScripts.BlockReasons(ColumnReorderMethod.InvisibleVisible, columns, Plain, atLeast12c: false);

        Assert.Contains(reasons, r => r.Contains("12c", StringComparison.Ordinal));
        Assert.Contains(reasons, r => r.Contains("INVISIBLE 인 열", StringComparison.Ordinal) && r.Contains('H', StringComparison.Ordinal));
    }

    [Fact]
    public void BlockReasons_for_rebuild_ignore_version_and_invisible_columns_but_not_table_kind()
    {
        var columns = new[] { new ColumnInfo("H", 1, "NUMBER") { IsInvisible = true } };

        Assert.Empty(ColumnReorderScripts.BlockReasons(ColumnReorderMethod.Rebuild, columns, Plain, atLeast12c: false));
        Assert.Single(ColumnReorderScripts.BlockReasons(ColumnReorderMethod.Rebuild, columns, new ColumnReorderTraits(true, false, null), atLeast12c: false));
    }

    private static readonly ColumnInfo[] Columns =
    [
        new("ID", 1, "NUMBER") { IsNullable = false },
        new("NAME", 2, "VARCHAR2") { DefaultValue = "'x'" },
        new("TOTAL", 3, "NUMBER") { IsVirtual = true, DefaultValue = "\"ID\"*2" },
    ];

    [Fact]
    public void Rebuild_without_enterprise_uses_ctas_rename_and_carries_dependent_ddl()
    {
        var script = ColumnReorderScripts.Rebuild(Table, Columns, ["NAME", "ID", "TOTAL"], false, true,
            "CREATE INDEX \"SCOTT\".\"EMP_I\" ON \"SCOTT\".\"EMP\" (\"NAME\");");

        Assert.StartsWith("-- 열 순서 바꾸기: SCOTT.EMP", script, StringComparison.Ordinal);
        Assert.Contains("검토 뒤 실행", script, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE \"SCOTT\".\"EMP$NEW\" AS SELECT \"NAME\", \"ID\" FROM \"SCOTT\".\"EMP\";", script, StringComparison.Ordinal);
        Assert.Contains("MODIFY (\"NAME\" DEFAULT 'x');", script, StringComparison.Ordinal);
        Assert.Contains("ADD (\"TOTAL\" AS (\"ID\"*2));", script, StringComparison.Ordinal);
        Assert.Contains("ALTER TABLE \"SCOTT\".\"EMP\" RENAME TO \"EMP$OLD\";", script, StringComparison.Ordinal);
        Assert.Contains("ALTER TABLE \"SCOTT\".\"EMP$NEW\" RENAME TO \"EMP\";", script, StringComparison.Ordinal);
        Assert.Contains("CREATE INDEX \"SCOTT\".\"EMP_I\"", script, StringComparison.Ordinal);
        Assert.Contains("DBMS_STATS.GATHER_TABLE_STATS", script, StringComparison.Ordinal);
        Assert.DoesNotContain("DBMS_REDEFINITION", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Rebuild_with_enterprise_uses_dbms_redefinition_and_comments_dependent_ddl()
    {
        var script = ColumnReorderScripts.Rebuild(Table, Columns, ["NAME", "ID", "TOTAL"], true, false, "COMMENT ON TABLE \"SCOTT\".\"EMP\" IS 'x';");

        Assert.Contains("CAN_REDEF_TABLE('SCOTT', 'EMP', DBMS_REDEFINITION.CONS_USE_ROWID)", script, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE \"SCOTT\".\"EMP$INT\" AS SELECT \"NAME\", \"ID\" FROM \"SCOTT\".\"EMP\" WHERE 1 = 0;", script, StringComparison.Ordinal);
        Assert.Contains("START_REDEF_TABLE", script, StringComparison.Ordinal);
        Assert.Contains("COPY_TABLE_DEPENDENTS", script, StringComparison.Ordinal);
        Assert.Contains("FINISH_REDEF_TABLE", script, StringComparison.Ordinal);
        Assert.Contains("-- COMMENT ON TABLE \"SCOTT\".\"EMP\" IS 'x';", script, StringComparison.Ordinal);
        Assert.DoesNotContain("RENAME TO", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Rebuild_puts_originally_invisible_columns_last_and_hides_them_again()
    {
        ColumnInfo[] columns = [new("A", 1, "NUMBER"), new("H", 2, "NUMBER") { IsInvisible = true }, new("B", 3, "NUMBER")];

        var script = ColumnReorderScripts.Rebuild(Table, columns, ["B", "A"], false, true, string.Empty);

        Assert.Contains("AS SELECT \"B\", \"A\", \"H\" FROM", script, StringComparison.Ordinal);
        Assert.Contains("MODIFY (\"H\" INVISIBLE);", script, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("CREATE TABLE T (A NUMBER);", "CREATE TABLE T (A NUMBER);\nCREATE INDEX I ON T(A);", "CREATE INDEX I ON T(A);")]
    [InlineData("CREATE TABLE T (A NUMBER);", "something else", "something else")]
    public void DependentPart_strips_the_table_ddl_prefix(string tableDdl, string full, string expected)
        => Assert.Equal(expected, ColumnReorderScripts.DependentPart(tableDdl, full));

    [Fact]
    public void AsSingleBlock_wraps_statements_in_one_plsql_block_with_a_summary_comment()
    {
        var statements = ColumnReorderScripts.InvisibleVisible(new DbObjectRef("U", "T'1", "TABLE"), ["A", "B"], ["B", "A"]);

        var block = ColumnReorderScripts.AsSingleBlock(new DbObjectRef("U", "T'1", "TABLE"), statements);

        Assert.Equal(Oranda.Core.Execution.StatementKind.PlSql, block.Kind);
        Assert.StartsWith("-- 열 순서 바꾸기 U.T'1: ALTER TABLE 4문장", block.Text, StringComparison.Ordinal);
        Assert.Contains("EXECUTE IMMEDIATE 'ALTER TABLE \"U\".\"T''1\" MODIFY (\"B\" INVISIBLE)';", block.Text, StringComparison.Ordinal); // 작은따옴표는 두 번
        Assert.EndsWith("END;", block.Text, StringComparison.Ordinal);
    }
}
