using Oranda.Core.Language;
using Oranda.Core.Metadata;
using Oranda.Language.Completion;
using Oranda.Language.Lint;

namespace Oranda.Language.Tests.Lint;

public class LintRulesTests
{
    private sealed class FakeCatalog(params TableInfo[] tables) : ILintCatalog
    {
        public TableInfo? Table(string? owner, string name) => tables.FirstOrDefault(t => t.Table.Name == name);
    }

    private static TableInfo Emp() => new(new DbObjectRef("HR", "EMP", "TABLE"),
    [
        new ColumnInfo("ID", 1, "NUMBER") { IsNullable = false },
        new ColumnInfo("MGR", 2, "NUMBER") { IsNullable = true },
        new ColumnInfo("CODE", 3, "VARCHAR2") { IsNullable = false },
        new ColumnInfo("HIRED", 4, "DATE") { IsNullable = true },
    ])
    {
        Indexes = [new IndexInfo("EMP_HIRED", false, ["HIRED"]), new IndexInfo("EMP_CODE", false, ["CODE"])],
    };

    private static List<Diagnostic> Lint(string sql, ILintCatalog? catalog = null) => LintRules.Run(LintModel.Parse(sql), catalog);

    private static void Hits(string rule, string sql, ILintCatalog? catalog = null, int count = 1)
        => Assert.Equal(count, Lint(sql, catalog).Count(d => d.Code == "LINT." + rule));

    private static void Clean(string sql, ILintCatalog? catalog = null) => Assert.Empty(Lint(sql, catalog));

    // ① 상관 UPDATE
    [Fact]
    public void Update_with_correlated_subquery_without_where_warns()
        => Hits(LintRules.UpdateSubqueryNoWhere, "UPDATE emp e SET mgr = (SELECT d.boss FROM dept d WHERE d.id = e.dept_id)");

    [Fact]
    public void Update_with_where_or_uncorrelated_subquery_is_fine()
    {
        Clean("UPDATE emp e SET mgr = (SELECT d.boss FROM dept d WHERE d.id = e.dept_id) WHERE EXISTS (SELECT 1 FROM dept d2 WHERE d2.id = e.dept_id)");
        Clean("UPDATE emp SET mgr = (SELECT MAX(boss) FROM dept)");
    }

    // ② ROWNUM + ORDER BY
    [Fact]
    public void Rownum_and_order_by_in_same_block_warns()
        => Hits(LintRules.RownumOrderBy, "SELECT * FROM emp WHERE ROWNUM <= 10 ORDER BY hired");

    [Fact]
    public void Rownum_over_ordered_inline_view_is_fine()
        => Clean("SELECT * FROM (SELECT * FROM emp ORDER BY hired) WHERE ROWNUM <= 10");

    // ③ NOT IN nullable
    [Fact]
    public void Not_in_nullable_column_warns_only_with_catalog()
    {
        var sql = "SELECT * FROM emp WHERE id NOT IN (SELECT mgr FROM emp)";
        Hits(LintRules.NotInNullable, sql, new FakeCatalog(Emp()));
        Hits(LintRules.NotInNullable, sql, null, 0);
    }

    [Fact]
    public void Not_in_with_not_null_column_or_filter_is_fine()
    {
        var cat = new FakeCatalog(Emp());
        Hits(LintRules.NotInNullable, "SELECT * FROM emp WHERE id NOT IN (SELECT id FROM emp)", cat, 0);
        Hits(LintRules.NotInNullable, "SELECT * FROM emp WHERE id NOT IN (SELECT mgr FROM emp WHERE mgr IS NOT NULL)", cat, 0);
    }

    // ④ 카테시안
    [Fact]
    public void Tables_without_join_condition_warn()
    {
        Hits(LintRules.CartesianJoin, "SELECT * FROM emp e, dept d");
        Hits(LintRules.CartesianJoin, "SELECT * FROM emp e, dept d WHERE e.id = 1");
    }

    [Fact]
    public void Joined_tables_are_fine()
    {
        Clean("SELECT * FROM emp e, dept d WHERE e.dept_id = d.id");
        Clean("SELECT * FROM emp e JOIN dept d ON e.dept_id = d.id");
        Clean("SELECT * FROM emp e, dept d WHERE e.dept_id = dept_id AND d.id = 1");
    }

    // ⑤ = NULL
    [Theory]
    [InlineData("SELECT * FROM emp WHERE mgr = NULL")]
    [InlineData("SELECT * FROM emp WHERE mgr != NULL")]
    [InlineData("SELECT * FROM emp WHERE mgr <> NULL")]
    [InlineData("SELECT * FROM emp WHERE NULL = mgr")]
    public void Comparing_to_null_warns(string sql) => Hits(LintRules.EqualsNull, sql);

    [Fact]
    public void Is_null_and_set_null_are_fine()
    {
        Clean("SELECT * FROM emp WHERE mgr IS NULL");
        Clean("UPDATE emp SET mgr = NULL WHERE id = 1");
    }

    // ⑥ 문자 열 vs 숫자
    [Fact]
    public void Char_column_against_number_warns_only_with_catalog()
    {
        var sql = "SELECT * FROM emp WHERE code = 10";
        Hits(LintRules.ImplicitConversion, sql, new FakeCatalog(Emp()));
        Hits(LintRules.ImplicitConversion, sql, null, 0);
    }

    [Fact]
    public void Number_column_or_string_literal_is_fine()
    {
        var cat = new FakeCatalog(Emp());
        Clean("SELECT * FROM emp WHERE id = 10", cat);
        Clean("SELECT * FROM emp WHERE code = '10'", cat);
    }

    // ⑦ 인덱스 열에 함수
    [Fact]
    public void Function_on_indexed_column_warns_only_with_catalog()
    {
        var sql = "SELECT * FROM emp WHERE TO_CHAR(hired, 'YYYYMMDD') = '20260101'";
        Hits(LintRules.FunctionOnIndexedColumn, sql, new FakeCatalog(Emp()));
        Hits(LintRules.FunctionOnIndexedColumn, sql, null, 0);
    }

    [Fact]
    public void Function_on_unindexed_column_or_value_side_is_fine()
    {
        var cat = new FakeCatalog(Emp());
        Clean("SELECT * FROM emp WHERE UPPER(mgr) = 'A'", cat);
        Clean("SELECT * FROM emp WHERE hired >= TO_DATE('20260101', 'YYYYMMDD')", cat);
    }

    // ⑧ (+) 일부만
    [Fact]
    public void Outer_join_plus_on_only_some_conditions_warns()
        => Hits(LintRules.PartialOuterJoin, "SELECT * FROM emp e, dept d WHERE e.dept_id = d.id(+) AND e.site = d.site");

    [Fact]
    public void Outer_join_plus_on_all_conditions_is_fine()
        => Clean("SELECT * FROM emp e, dept d WHERE e.dept_id = d.id(+) AND e.site = d.site(+)");

    // 리뷰 반영
    [Fact]
    public void Column_named_model_is_not_a_clause()
    {
        Clean("UPDATE car SET model = NULL WHERE id = 1");
        Clean("SELECT * FROM car c, maker m WHERE c.model = 'X' AND c.maker_id = m.id");
    }

    [Fact]
    public void Count_subquery_update_is_fine()
        => Clean("UPDATE dept d SET emp_cnt = (SELECT COUNT(*) FROM emp e WHERE e.dept_id = d.id)");

    [Fact]
    public void Keywords_before_parenthesis_are_not_functions()
        => Clean("SELECT * FROM emp e WHERE e.id > 0 AND (e.code, e.id) IN (SELECT code, id FROM emp)", new FakeCatalog(Emp()));

    [Fact]
    public void Insert_select_is_linted() => Hits(LintRules.EqualsNull, "INSERT INTO t SELECT * FROM a WHERE a.k = NULL");

    [Fact]
    public void Top_n_over_ordered_view_with_outer_order_is_fine()
        => Clean("SELECT * FROM (SELECT * FROM emp ORDER BY id DESC) WHERE ROWNUM <= 10 ORDER BY mgr");

    [Fact]
    public void Not_in_with_comparison_filter_is_fine()
        => Hits(LintRules.NotInNullable, "SELECT * FROM emp WHERE id NOT IN (SELECT mgr FROM emp WHERE mgr > 100)", new FakeCatalog(Emp()), 0);

    [Fact]
    public void Service_shifts_ranges_by_statement_offset()
    {
        var svc = new LintService(lookup: null);
        var text = "select 1 from dual;\nselect * from emp where mgr = null";
        var d = Assert.Single(svc.Lint(text, null, CancellationToken.None));
        Assert.Equal(text.IndexOf("mgr = null", StringComparison.Ordinal), d.Range.Start);
        Assert.Equal(DiagnosticSeverity.Warning, d.Severity);
        Assert.Equal("LINT.EQUALS_NULL", d.Code);
    }
}
