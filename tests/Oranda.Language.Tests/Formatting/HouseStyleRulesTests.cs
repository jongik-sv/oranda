using Oranda.Core.Formatting;
using Oranda.Core.Text;
using Oranda.Language.Lexing;

namespace Oranda.Language.Tests.Formatting;

// 소유: TSK-13-43. 사내 서식 배치(clauseLayout Column)·비교 맞춤(align.comparison)·ORDER BY 따로(lineBreaks.orderBy) 규칙과 경계.
public class HouseStyleRulesTests
{
    static readonly FormatterOptions House = FormatterOptions.OrandaDefault;

    static ISqlFormatter Formatter()
    {
        var type = typeof(LanguageAssembly).Assembly.GetType("Oranda.Language.Formatting.SqlFormatter")!;
        return (ISqlFormatter)Activator.CreateInstance(type, nonPublic: true)!;
    }

    static string Fmt(string sql, FormatterOptions? o = null)
    {
        var r = Formatter().Format(sql, o ?? House);
        Assert.True(r.Succeeded, $"{r.Failure}: {r.Message}");
        return r.Text.Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    static string L(params string[] lines) => string.Join("\n", lines);

    // ---- 기본 제공 프로필 ----

    [Fact]
    public void Oranda_default_options_are_the_house_style()
    {
        var o = FormatterOptions.OrandaDefault;
        Assert.Equal(ClauseLayout.Column, o.ClauseLayout);
        Assert.Equal(CommaPosition.Leading, o.CommaPosition);
        Assert.Equal(LetterCase.Upper, o.Case.Identifier);
        Assert.Equal(ByListLayout.MultiLine, o.LineBreaks.GroupOrderBy);
        Assert.Equal(OrderByLayout.SingleLine, o.LineBreaks.OrderBy);
        Assert.Equal(0, o.LineBreaks.InListThreshold);
        Assert.Equal(120, o.LineBreaks.MaxLineWidth);
        Assert.False(o.Align.Alias);
        Assert.True(o.Align.Comparison);

        // 새 속성의 기본값은 옛 출력(저장된 사용자 프로필은 그대로 읽힌다).
        var old = new FormatterOptions();
        Assert.Equal(ClauseLayout.Indent, old.ClauseLayout);
        Assert.Equal(OrderByLayout.FollowGroupBy, old.LineBreaks.OrderBy);
        Assert.False(old.Align.Comparison);
    }

    [Fact]
    public void Old_default_options_keep_the_indented_layout()
    {
        Assert.Equal(L("SELECT a,", "    b", "FROM t", "WHERE x = 1", "    AND y = 2"), Fmt("select a, b from t where x = 1 and y = 2", new FormatterOptions()));
    }

    // ---- 절·목록 ----

    [Fact]
    public void Clause_keywords_start_the_line_and_bodies_start_at_column_7()
    {
        Assert.Equal(
            L("SELECT A.X", "     , A.Y", "FROM   T A", "WHERE  A.X = 1", "AND    A.Y = 2", "OR     A.Z = 3", "HAVING COUNT(*) > 0"),
            Fmt("select a.x, a.y from t a where a.x = 1 and a.y = 2 or a.z = 3 having count(*) > 0"));
    }

    [Fact]
    public void Group_by_breaks_and_order_by_stays_on_one_line_until_it_overflows()
    {
        Assert.Equal(L("SELECT A", "FROM   T", "GROUP BY A", "     , B", "ORDER BY A, B DESC"), Fmt("select a from t group by a, b order by a, b desc"));
        string longCols = string.Join(", ", Enumerable.Range(1, 12).Select(i => $"COLUMN_NAME_{i:00}"));
        var text = Fmt($"select a from t order by {longCols}");
        Assert.Contains("ORDER BY COLUMN_NAME_01\n     , COLUMN_NAME_02\n", text, StringComparison.Ordinal);
        Assert.EndsWith("     , COLUMN_NAME_12", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Order_by_option_follow_uses_group_order_by()
    {
        var o = FormatterOptions.OrandaDefault with { LineBreaks = FormatterOptions.OrandaDefault.LineBreaks with { OrderBy = OrderByLayout.FollowGroupBy } };
        Assert.Equal(L("SELECT A", "FROM   T", "ORDER BY A", "     , B"), Fmt("select a from t order by a, b", o));
        var old = new FormatterOptions { LineBreaks = new LineBreakOptions { GroupOrderBy = ByListLayout.MultiLine, OrderBy = OrderByLayout.SingleLine } };
        Assert.Equal(L("SELECT a", "FROM t", "GROUP BY a,", "    b", "ORDER BY a, b"), Fmt("select a from t group by a, b order by a, b", old));
    }

    [Fact]
    public void Single_item_lists_and_empty_bodies_are_fine()
    {
        Assert.Equal("SELECT 1\nFROM   DUAL", Fmt("select 1 from dual"));
        Assert.Equal("SELECT *\nFROM   T\nGROUP BY A\nORDER BY A", Fmt("select * from t group by a order by a"));
        Assert.Equal("SELECT", Fmt("select"));
    }

    [Fact]
    public void Trailing_comma_option_puts_items_at_column_7()
    {
        var o = House with { CommaPosition = CommaPosition.Trailing };
        Assert.Equal(L("SELECT A,", "       B", "FROM   T,", "       U"), Fmt("select a, b from t, u", o));
    }

    [Fact]
    public void Fill_to_width_select_list_wraps_only_when_full()
    {
        var o = House with { LineBreaks = House.LineBreaks with { SelectList = SelectListLayout.FillToWidth, MaxLineWidth = 40 } };
        Assert.Equal(L("SELECT AAAAAAAAAA, BBBBBBBBBB", "     , CCCCCCCCCC, DDDDDDDDDD", "FROM   T"), Fmt("select aaaaaaaaaa, bbbbbbbbbb, cccccccccc, dddddddddd from t", o));
    }

    [Fact]
    public void And_or_after_and_none_options()
    {
        var after = House with { LineBreaks = House.LineBreaks with { AndOr = AndOrBreak.After } };
        Assert.Equal(L("SELECT 1", "FROM   T", "WHERE  A = 1 AND", "       B = 2"), Fmt("select 1 from t where a = 1 and b = 2", after));
        var none = House with { LineBreaks = House.LineBreaks with { AndOr = AndOrBreak.None } };
        Assert.Equal(L("SELECT 1", "FROM   T", "WHERE  A = 1 AND B = 2"), Fmt("select 1 from t where a = 1 and b = 2", none));
    }

    [Fact]
    public void Between_and_is_not_a_condition_break()
    {
        Assert.Equal(L("SELECT 1", "FROM   T", "WHERE  A BETWEEN 1 AND 2", "AND    B NOT BETWEEN :f AND :t"), Fmt("select 1 from t where a between 1 and 2 and b not between :f and :t"));
    }

    [Fact]
    public void Ansi_join_puts_join_and_on_at_the_clause_column()
    {
        Assert.Equal(
            L("SELECT A.X", "FROM   T A", "JOIN   U B", "ON     B.ID = A.ID", "AND    B.K = 1", "LEFT OUTER JOIN V C", "ON     C.ID = A.ID"),
            Fmt("select a.x from t a join u b on b.id = a.id and b.k = 1 left outer join v c on c.id = a.id"));
    }

    [Fact]
    public void Set_operators_fetch_and_for_update()
    {
        Assert.Equal(
            L("SELECT A", "FROM   T", "UNION ALL", "SELECT B", "FROM   U", "ORDER BY 1", "OFFSET 5 ROWS", "FETCH NEXT 10 ROWS ONLY"),
            Fmt("select a from t union all select b from u order by 1 offset 5 rows fetch next 10 rows only"));
        Assert.Equal(L("SELECT A", "FROM   T", "FOR UPDATE NOWAIT"), Fmt("select a from t for update nowait"));
    }

    // ---- 괄호 묶음 ----

    [Fact]
    public void Short_simple_subquery_stays_on_one_line_and_long_one_becomes_a_block()
    {
        Assert.Equal(L("SELECT A", "FROM   T", "WHERE  A IN (SELECT B FROM U WHERE C = 1)"), Fmt("select a from t where a in (select b from u where c = 1)"));
        Assert.Equal(
            L("SELECT A", "FROM   T", "WHERE  A IN", "       (", "           SELECT B", "           FROM   U", "           WHERE  C = 1", "           AND    D = 2", "       )"),
            Fmt("select a from t where a in (select b from u where c = 1 and d = 2)"));
        string longName = new('X', 110);
        var text = Fmt($"select a from t where exists (select 1 from {longName} where c = 1)");
        Assert.Contains("WHERE  EXISTS\n       (\n           SELECT 1\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Block_subquery_written_on_many_lines_stays_a_block()
    {
        const string sql = "SELECT A\nFROM   T\nWHERE  EXISTS\n       (\n           SELECT 1\n           FROM   U\n       )";
        Assert.Equal(sql, Fmt(sql));
        Assert.Equal(L("SELECT A", "FROM   T", "WHERE  EXISTS (SELECT 1 FROM U)"), Fmt("select a from t where exists (select 1 from u)"));
    }

    [Fact]
    public void Nested_blocks_move_four_columns_right_each_level()
    {
        var text = Fmt("select a from (select b from (select c, d from w group by c, d) x group by b) v");
        Assert.Equal(
            L(
                "SELECT A",
                "FROM   (",
                "           SELECT B",
                "           FROM   (",
                "                      SELECT C",
                "                           , D",
                "                      FROM   W",
                "                      GROUP BY C",
                "                           , D",
                "                  ) X",
                "           GROUP BY B",
                "       ) V"),
            text);
    }

    [Fact]
    public void Scalar_subquery_after_an_operator_opens_on_its_own_line()
    {
        Assert.Equal(
            L("SELECT A", "FROM   T", "WHERE  A =", "       (", "           SELECT MAX(B)", "           FROM   U", "           GROUP BY C", "       )"),
            Fmt("select a from t where a = (select max(b) from u group by c)"));
    }

    [Fact]
    public void Long_condition_group_becomes_a_block_with_right_aligned_or()
    {
        string v = new('V', 50);
        var text = Fmt($"select 1 from t where a = 1 and (b = '{v}' or c = '{v}' and d = 1)");
        Assert.Equal(
            L("SELECT 1", "FROM   T", "WHERE  A = 1", "AND    (", $"           B = '{v}'", $"        OR C = '{v}'", "       AND D = 1", "       )"),
            text);
        Assert.Equal(L("SELECT 1", "FROM   T", "WHERE  (A = 1 OR B = 2)"), Fmt("select 1 from t where (a = 1 or b = 2)"));
    }

    [Fact]
    public void Case_rules()
    {
        Assert.Equal("SELECT CASE WHEN A = 1 THEN 'Y' ELSE 'N' END F\nFROM   T", Fmt("select case when a = 1 then 'Y' else 'N' end f from t"));
        Assert.Equal(
            L("SELECT CASE A WHEN 1 THEN 'x'", "            WHEN 2 THEN 'y'", "       END F", "FROM   T"),
            Fmt("select case a when 1 then 'x' when 2 then 'y' end f from t"));
        Assert.Equal(
            L("SELECT X", "     , CASE WHEN A = 1 THEN 'x'", "            ELSE 'y'", "       END F", "FROM   T"),
            Fmt("select x, case when a = 1 then 'x'\nelse 'y' end f from t"));
    }

    [Fact]
    public void With_clause_rules()
    {
        Assert.Equal(
            L("WITH A (X) AS", "(", "    SELECT 1", "    FROM   DUAL", ")", ", B AS", "(", "    SELECT X", "    FROM   A", ")", "SELECT *", "FROM   B"),
            Fmt("with a (x) as (select 1 from dual), b as (select x from a) select * from b"));
        var trailing = House with { CommaPosition = CommaPosition.Trailing };
        Assert.Contains(")\n,", Fmt("with a as (select 1 from dual), b as (select 2 from dual) select 1 from a", House), StringComparison.Ordinal);
        Assert.Contains("),\nB AS", Fmt("with a as (select 1 from dual), b as (select 2 from dual) select 1 from a", trailing), StringComparison.Ordinal);
    }

    // ---- 비교 맞춤 ----

    [Fact]
    public void Outer_join_group_and_same_column_range_are_aligned()
    {
        Assert.Equal(
            L("SELECT 1", "FROM   A, B", "WHERE  B.K = A.K", "AND    B.LONG_COL(+) = A.X", "AND    B.C(+)        = 'Y'", "AND    A.D >= :f", "AND    A.D <  :t", "AND    A.E = 1").Replace("FROM   A, B", "FROM   A\n     , B", StringComparison.Ordinal),
            Fmt("select 1 from a, b where b.k = a.k and b.long_col(+) = a.x and b.c(+) = 'Y' and a.d >= :f and a.d < :t and a.e = 1"));
    }

    [Fact]
    public void Comparison_alignment_can_be_switched_off_and_different_aliases_do_not_align()
    {
        var off = House with { Align = House.Align with { Comparison = false } };
        Assert.Equal(L("SELECT 1", "FROM   T", "WHERE  B.LONG(+) = 1", "AND    B.C(+) = 2"), Fmt("select 1 from t where b.long(+) = 1 and b.c(+) = 2", off));
        Assert.Equal(L("SELECT 1", "FROM   T", "WHERE  B.LONG(+) = 1", "AND    C.C(+) = 2"), Fmt("select 1 from t where b.long(+) = 1 and c.c(+) = 2"));
    }

    [Fact]
    public void Multi_line_left_side_is_not_aligned()
    {
        var text = Fmt("select 1 from t where 'a\nb' = x and 'a\nb' = yy");
        Assert.DoesNotContain("   =", text, StringComparison.Ordinal);
        Assert.Equal(text, Fmt(text));
    }

    [Fact]
    public void Paren_inner_space_option_applies_to_block_and_flat_parens()
    {
        var o = House with { ParenInnerSpace = true };
        var text = Fmt("select a from t where a in (select b from u)", o);
        Assert.Contains("( SELECT B FROM U )", text, StringComparison.Ordinal);
        Assert.Equal(text, Fmt(text, o));
    }

    // ---- 주석·빈 줄 ----

    [Fact]
    public void Comments_keep_their_place()
    {
        Assert.Equal(
            L("-- head", "SELECT A    -- keep three", "     , B", "     -- own line", "     , C /* inline */", "FROM   T"),
            Fmt("-- head\nselect a    -- keep three\n, b\n-- own line\n, c /* inline */ from t"));
    }

    [Fact]
    public void Line_comment_inside_an_item_forces_the_next_token_down()
    {
        var text = Fmt("select a, -- c1\n b from t");
        Assert.Equal(L("SELECT A", "     , -- c1", "       B", "FROM   T"), text);
        Assert.Equal(text, Fmt(text));
    }

    // ---- DML·기타 문장 ----

    [Fact]
    public void Insert_update_delete_layouts()
    {
        Assert.Equal(L("INSERT INTO T (A, B)", "VALUES (1, 'x')"), Fmt("insert into t (a, b) values (1, 'x')"));
        Assert.Equal(L("INSERT INTO T (A)", "SELECT B", "FROM   U"), Fmt("insert into t (a) select b from u"));
        Assert.Equal(L("UPDATE T", "SET    A = 1", "     , B = :b", "WHERE  C = 1"), Fmt("update t set a = 1, b = :b where c = 1"));
        Assert.Equal(L("DELETE FROM T", "WHERE  X = 1"), Fmt("delete from t where x = 1"));
        Assert.Equal(L("CREATE OR REPLACE VIEW V AS", "SELECT A", "FROM   T"), Fmt("create or replace view v as select a from t"));
        Assert.Equal(L("EXPLAIN PLAN FOR", "SELECT A", "FROM   T"), Fmt("explain plan for select a from t"));
    }

    [Fact]
    public void Merge_plsql_and_broken_statements_fall_back_safely()
    {
        foreach (var sql in new[]
        {
            "merge into t using s on (t.id = s.id) when matched then update set t.a = s.a",
            "begin select 1 into x from dual; end;",
            "select a from t where (a = 1",
            "insert all into t1 (a) values (1) select * from dual",
            "select a from t -- @formatter:off\n where   x=1",
        })
        {
            var r = Formatter().Format(sql, House);
            Assert.True(r.Succeeded, sql);
            Assert.Equal(r.Text, Formatter().Format(r.Text, House).Text);
        }
    }

    [Fact]
    public void Korean_quoted_and_bind_names_keep_their_text()
    {
        Assert.Equal(L("SELECT A.X 품명", "     , A.Y \"중량(T)\"", "FROM   T A", "WHERE  A.Z = :fromDd"), Fmt("select a.x 품명, a.y \"중량(T)\" from t a where a.z = :fromDd"));
    }

    [Fact]
    public void Range_format_counts_the_base_indent_in_the_line_width()
    {
        string v = new('V', 40);
        string sql = $"        select a from t where a in (select b from u where c = '{v}' )";
        var r = Formatter().Format(sql, House, new TextRange(0, sql.Length));
        Assert.True(r.Succeeded);
        Assert.StartsWith("        SELECT A\n        FROM   T\n", r.Text.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("(SELECT B FROM U WHERE C = ", r.Text, StringComparison.Ordinal);
        var narrow = House with { LineBreaks = House.LineBreaks with { MaxLineWidth = 80 } };
        var r2 = Formatter().Format(sql, narrow, new TextRange(0, sql.Length));
        Assert.Contains("        WHERE  A IN\n               (\n", r2.Text.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public void Keyword_right_align_does_not_touch_column_layout_statements()
    {
        var o = House with { KeywordAlign = KeywordAlign.Right };
        Assert.Equal(L("SELECT A", "     , B", "FROM   T"), Fmt("select a, b from t", o));
    }

    // ---- 말뭉치·퍼징 ----

    static readonly string[] GoldenFiles = ["query-format-basic.sql", "query-format-with.sql", "query-format-analysis.sql"];

    static IEnumerable<FormatterOptions> HouseVariants()
    {
        yield return House;
        yield return House with { CommaPosition = CommaPosition.Trailing };
        yield return House with { LineBreaks = House.LineBreaks with { AndOr = AndOrBreak.After } };
        yield return House with { LineBreaks = House.LineBreaks with { AndOr = AndOrBreak.None, SelectList = SelectListLayout.FillToWidth } };
        yield return House with { LineBreaks = House.LineBreaks with { MaxLineWidth = 40, JoinClause = false } };
        yield return House with { Indent = new IndentOptions { Size = 2 } };
        yield return House with { Indent = new IndentOptions { UseTabs = true } };
        yield return House with { KeywordAlign = KeywordAlign.Right, Align = House.Align with { Alias = true, EqualsSign = true } };
        yield return House with { Case = new CaseOptions { Keyword = LetterCase.Lower, Function = LetterCase.AsIs, Identifier = LetterCase.AsIs } };
        yield return House with { Comments = new CommentOptions { MaxBlankLines = 0, Preserve = false } };
        yield return House with { StatementTerminator = StatementTerminator.Add };
    }

    [Fact]
    public void Regression_corpus_keeps_tokens_and_is_idempotent_with_house_variants()
    {
        var f = Formatter();
        var failures = new List<string>();
        var golden = GoldenFiles.Select(HouseStyleGoldenTests.Resource);
        foreach (var o in HouseVariants())
        {
            foreach (var raw in FormatterBasicTests.Samples.Concat(golden))
            {
                foreach (var sql in new[] { raw, raw.Replace("\n", "\r\n", StringComparison.Ordinal), HouseStyleGoldenTests.Collapse(raw) })
                {
                    var r = f.Format(sql, o);
                    if (!r.Succeeded)
                    {
                        failures.Add($"{r.Failure} {r.Message} :: {sql}");
                        continue;
                    }

                    var again = f.Format(r.Text, o);
                    if (again.Text != r.Text) failures.Add($"idempotent :: {sql}\n--- 1\n{r.Text}\n--- 2\n{again.Text}");
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n\n", failures.Take(5)));
    }

    [Fact]
    public void Fuzzed_fragments_never_break_tokens_or_idempotency_with_house_style()
    {
        var f = Formatter();
        var rnd = new Random(43);
        string all = string.Join(";\n", FormatterBasicTests.Samples);
        var toks = SqlLexer.Tokenize(all).Select(t => all.Substring(t.Start, t.Length)).ToList();
        string[] seps = ["\n", "", "\n\n", " ", " "];
        var failures = new List<string>();
        for (int n = 0; n < 3000; n++)
        {
            string s;
            if (n % 2 == 0)
            {
                int a = rnd.Next(all.Length);
                s = all[a..Math.Min(all.Length, a + rnd.Next(400))];
            }
            else
            {
                var sb = new System.Text.StringBuilder("select ");
                for (int i = rnd.Next(1, 40); i > 0; i--) sb.Append(toks[rnd.Next(toks.Count)]).Append(seps[rnd.Next(seps.Length)]);
                s = sb.ToString();
            }

            var r = f.Format(s, House);
            if (!r.Succeeded) failures.Add($"{r.Failure} {r.Message} :: {s}");
            else if (f.Format(r.Text, House).Text != r.Text) failures.Add($"idempotent :: {s}");
        }

        Assert.True(failures.Count == 0, string.Join("\n\n", failures.Take(5)));
    }
}
