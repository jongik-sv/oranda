using Oranda.Core.Formatting;
using Oranda.Language.Lexing;

namespace Oranda.Language.Tests.Formatting;

// 소유: TSK-13-38. Orange SQL Formatter 대응 옵션(키워드 오른쪽 맞춤·GROUP/ORDER BY 여러 줄).
public class FormatterOrangeOptionsTests
{
    static ISqlFormatter Formatter()
    {
        var type = typeof(LanguageAssembly).Assembly.GetType("Oranda.Language.Formatting.SqlFormatter")!;
        return (ISqlFormatter)Activator.CreateInstance(type, nonPublic: true)!;
    }

    static string Fmt(string sql, FormatterOptions o)
    {
        var r = Formatter().Format(sql, o);
        Assert.True(r.Succeeded, $"{r.Failure}: {r.Message}");
        Assert.Equal(r.Text, Formatter().Format(r.Text, o).Text); // 멱등
        return r.Text;
    }

    static readonly FormatterOptions Right = new() { KeywordAlign = KeywordAlign.Right };

    static readonly FormatterOptions Multi = new() { LineBreaks = new LineBreakOptions { GroupOrderBy = ByListLayout.MultiLine } };

    const string Query = "select a, b as bb, count(*) from t1 left join t2 on t1.id = t2.id where x = 1 and y = 2 or z in (select q from r where k = 1) group by a, b, c order by a desc, b";

    static readonly string[] Samples =
    [
        Query,
        "update t set a = 1, b = 2 where c = 3",
        "insert into t (a, b) values (1, 2)",
        "delete from t where a = 1",
        "begin select a into v from t where x = 1; v := 2; end;",
        "select a from t -- c\n group by a, -- cm\n b order by f(a, b), c",
        "create table t (id number, name varchar2(10))",
        "select a from t union all select b from u",
    ];

    [Fact]
    public void New_options_default_to_the_existing_output()
    {
        var d = new FormatterOptions();
        Assert.Equal(KeywordAlign.Left, d.KeywordAlign);
        Assert.Equal(ByListLayout.SingleLine, d.LineBreaks.GroupOrderBy);
        var explicitDefaults = new FormatterOptions { KeywordAlign = KeywordAlign.Left, LineBreaks = new LineBreakOptions { GroupOrderBy = ByListLayout.SingleLine } };
        foreach (var s in Samples)
        {
            Assert.Equal(Fmt(s, d), Fmt(s, explicitDefaults));
        }

        Assert.Equal("SELECT a\nFROM t\nGROUP BY a, b\nORDER BY a, b", Fmt("select a from t group by a, b order by a, b", d));
    }

    // ---- Keyword Align = Right ----

    [Fact]
    public void Right_align_puts_clause_keywords_on_a_river()
    {
        Assert.Equal(
            "SELECT a,\n       b AS bb,\n       COUNT(*)\n  FROM t1\n  LEFT JOIN t2 ON t1.id = t2.id\n WHERE x = 1\n   AND y = 2\n    OR z IN (\n" +
            "           SELECT q\n             FROM r\n            WHERE k = 1\n    )\n GROUP BY a, b, c\n ORDER BY a DESC, b",
            Fmt(Query, Right));
    }

    [Fact]
    public void Right_align_closes_parens_and_case_on_the_opening_line_column()
    {
        Assert.Equal(
            "SELECT a\n  FROM t\n WHERE EXISTS (\n       SELECT 1\n         FROM u\n        WHERE u.a = t.a\n          AND u.b = 2\n )\n ORDER BY 1",
            Fmt("select a from t where exists (select 1 from u where u.a = t.a and u.b = 2) order by 1", Right));
        Assert.Equal(
            "SELECT *\n  FROM (\n       SELECT a\n         FROM t\n  ) x\n WHERE x.a = 1",
            Fmt("select * from (select a from t) x where x.a = 1", Right));
        Assert.Equal(
            "SELECT CASE\n       WHEN a = 1 THEN 'x'\n       ELSE 'y'\nEND k,\n       b\n  FROM t",
            Fmt("select case when a = 1 then 'x' else 'y' end k, b from t", Right));
    }

    [Fact]
    public void Right_align_leaves_with_at_the_left_and_aligns_each_cte_body()
    {
        Assert.Equal(
            "WITH x AS (\n    SELECT a\n      FROM t\n), y AS (\n    SELECT c\n      FROM u\n)\nSELECT *\n  FROM x\n  JOIN y ON x.a = y.c",
            Fmt("with x as (select a from t), y as (select c from u) select * from x join y on x.a = y.c", Right));
    }

    [Fact]
    public void Right_align_dml_statements()
    {
        Assert.Equal("UPDATE t\n   SET a = 1, b = 2\n WHERE c = 3", Fmt("update t set a = 1, b = 2 where c = 3", Right));
        Assert.Equal("DELETE FROM t\n WHERE a = 1", Fmt("delete from t where a = 1", Right));
        Assert.Equal("INSERT INTO t (a, b)\nVALUES (1, 2)", Fmt("insert into t (a, b) values (1, 2)", Right));
        Assert.Equal("SELECT a\n  FROM t\n UNION ALL\nSELECT b\n  FROM u", Fmt("select a from t union all select b from u", Right));
    }

    [Fact]
    public void Right_align_inside_plsql_stops_at_the_statement_end()
    {
        Assert.Equal("BEGIN\n    SELECT a\n      INTO v\n      FROM t\n     WHERE x = 1;\n    v := 2;\nEND;", Fmt("begin select a into v from t where x = 1; v := 2; end;", Right));
    }

    [Fact]
    public void Right_align_with_leading_commas_hangs_the_comma_left_of_the_river()
    {
        var o = Right with { CommaPosition = CommaPosition.Leading };
        Assert.Equal("SELECT a\n     , b\n     , c\n  FROM t", Fmt("select a, b, c from t", o));
    }

    [Fact]
    public void Right_align_keeps_own_line_comments_with_the_next_line()
    {
        Assert.Equal("SELECT a\n  -- 표\n  FROM t", Fmt("select a\n-- 표\nfrom t", Right));
    }

    [Fact]
    public void Right_align_and_or_and_join_after_a_line_comment_stay_on_the_river()
    {
        Assert.Equal("SELECT a\n  FROM t\n WHERE x = 1 -- c\n   AND y = 2", Fmt("select a from t where x = 1 -- c\nand y = 2", Right));
        Assert.Equal("SELECT a\n  FROM t -- c\n  JOIN u ON t.a = u.a", Fmt("select a from t -- c\njoin u on t.a = u.a", Right));
    }

    [Fact]
    public void Right_align_with_tabs_counts_columns()
    {
        var o = Right with { Indent = new IndentOptions { UseTabs = true } };
        Assert.Equal("SELECT a,\n\t   b\n  FROM t", Fmt("select a, b from t", o));
    }

    [Fact]
    public void Right_align_leaves_other_statements_and_frozen_regions_alone()
    {
        foreach (var s in new[] { "create table t (id number, name varchar2(10))", "grant select on t to u", "-- @formatter:off\nselect a\nfrom t\n-- @formatter:on" })
        {
            Assert.Equal(Fmt(s, new FormatterOptions()), Fmt(s, Right));
        }
    }

    [Fact]
    public void Right_align_single_keyword_and_empty_input()
    {
        Assert.Equal("SELECT 1\n  FROM dual", Fmt("select 1 from dual", Right));
        Assert.True(Formatter().Format(string.Empty, Right).Succeeded);
        Assert.Equal("SELECT", Fmt("select", Right));
    }

    // ---- Group/Order By = In Multiple Line ----

    [Fact]
    public void Multi_line_group_and_order_by_put_one_column_per_line()
    {
        Assert.Equal("SELECT a\nFROM t\nGROUP BY a,\n    b,\n    c\nORDER BY a DESC,\n    b", Fmt("select a from t group by a, b, c order by a desc, b", Multi));
        var leading = Multi with { CommaPosition = CommaPosition.Leading };
        Assert.Equal("SELECT a\nFROM t\nGROUP BY a\n    , b\nORDER BY a\n    , b", Fmt("select a from t group by a, b order by a, b", leading));
    }

    [Fact]
    public void Multi_line_single_column_and_function_arguments_stay_on_one_line()
    {
        Assert.Equal("SELECT a\nFROM t\nORDER BY a", Fmt("select a from t order by a", Multi));
        Assert.Equal("SELECT a\nFROM t\nGROUP BY ROLLUP (a, b),\n    NVL(c, 0)", Fmt("select a from t group by rollup(a, b), nvl(c, 0)", Multi));
        Assert.Equal("SELECT ROW_NUMBER() OVER (ORDER BY a, b) rn\nFROM t", Fmt("select row_number() over (order by a, b) rn from t", Multi));
    }

    [Fact]
    public void Multi_line_keeps_a_comment_inside_the_list()
    {
        Assert.Equal("SELECT a\nFROM t\nGROUP BY a, -- 첫째\n    b,\n    c", Fmt("select a from t group by a, -- 첫째\nb, c", Multi));
    }

    [Fact]
    public void Multi_line_in_a_subquery_and_an_empty_list()
    {
        Assert.Equal(
            "SELECT *\nFROM (\n    SELECT a, COUNT(*) n\n    FROM t\n    GROUP BY a,\n        b\n) x",
            Fmt("select * from (select a, count(*) n from t group by a, b) x", Multi with { LineBreaks = Multi.LineBreaks with { SelectList = SelectListLayout.FillToWidth } }));
        var empty = Formatter().Format("select a from t group by", Multi);
        Assert.True(empty.Succeeded);
        Assert.Equal(Formatter().Format("select a from t group by", new FormatterOptions()).Text, empty.Text);
    }

    [Fact]
    public void Multi_line_with_right_align()
    {
        var o = Multi with { KeywordAlign = KeywordAlign.Right };
        Assert.Equal("SELECT a\n  FROM t\n GROUP BY a,\n       b\n ORDER BY a", Fmt("select a from t group by a, b order by a", o));
    }
}
