using Oranda.Core.Plans;
using Oranda.Language.Tuning;

namespace Oranda.Language.Tests.Tuning;

/// <summary>TSK-06-05 TN-10·11: 규칙 기반 재작성 후보.</summary>
public class RewriteAdvisorTests
{
    private static RewriteAdvice One(string sql, RewriteRule rule, ExecutionPlan? plan = null) =>
        Assert.Single(RewriteAdvisor.Analyze(sql, plan), a => a.Rule == rule);

    private static string Flat(string s) => string.Join(' ', s.Split((char[])[' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries));

    // ---- NOT IN → NOT EXISTS ----

    [Fact]
    public void Not_in_subquery_becomes_correlated_not_exists_and_is_flagged_as_null_sensitive()
    {
        var a = One("SELECT lh.lot_id FROM mes.lot_hist lh WHERE lh.eqp_id = :B1 AND lh.evt_cd NOT IN (SELECT s.evt_cd FROM mes.evt_skip s WHERE s.site = :B4)", RewriteRule.NotInToNotExists);
        Assert.True(a.HasRewrite);
        Assert.True(a.SemanticsMayChange);
        Assert.Contains("NULL", a.Caution, StringComparison.Ordinal);
        Assert.Equal("NOT IN → NOT EXISTS", a.Title);
        Assert.Equal("NOT EXISTS", a.ShortLabel);
        Assert.Equal(1, a.Line);
        Assert.Equal(
            "SELECT lh.lot_id FROM mes.lot_hist lh WHERE lh.eqp_id = :B1 AND NOT EXISTS (SELECT 1 FROM mes.evt_skip s WHERE s.site = :B4 AND s.evt_cd = lh.evt_cd)",
            Flat(a.Candidate.Sql));
    }

    [Fact]
    public void Not_in_without_inner_where_and_with_bare_columns_qualifies_both_sides()
    {
        var a = One("SELECT * FROM emp WHERE deptno NOT IN (SELECT deptno FROM dept d)", RewriteRule.NotInToNotExists);
        Assert.Equal("SELECT * FROM emp WHERE NOT EXISTS (SELECT 1 FROM dept d WHERE d.deptno = emp.deptno)", Flat(a.Candidate.Sql));
    }

    [Fact]
    public void Not_in_inner_or_condition_is_parenthesised_before_the_correlation()
    {
        var a = One("SELECT e.x FROM emp e WHERE e.k NOT IN (SELECT s.k FROM s WHERE s.a = 1 OR s.b = 2)", RewriteRule.NotInToNotExists);
        Assert.Contains("WHERE (s.a = 1 OR s.b = 2) AND s.k = e.k", Flat(a.Candidate.Sql), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("SELECT * FROM emp WHERE (a, b) NOT IN (SELECT x, y FROM t)")]                         // 튜플 왼쪽
    [InlineData("SELECT * FROM emp e WHERE e.k NOT IN (SELECT x, y FROM t)")]                          // 열 여럿
    [InlineData("SELECT * FROM emp e WHERE e.k NOT IN (SELECT t.x FROM t GROUP BY t.x)")]              // GROUP BY
    [InlineData("SELECT * FROM emp, dept WHERE deptno NOT IN (SELECT x FROM t)")]                       // 바깥 표 둘 + 한정 없음
    [InlineData("SELECT * FROM t WHERE k NOT IN (SELECT k FROM t)")]                                    // 안팎 같은 표 이름
    [InlineData("SELECT * FROM emp e WHERE e.a || e.k NOT IN (SELECT t.x FROM t)")]                    // 식 왼쪽
    public void Not_in_shapes_that_cannot_be_rewritten_safely_give_a_warning_only(string sql)
    {
        var a = One(sql, RewriteRule.NotInToNotExists);
        Assert.False(a.HasRewrite);
        Assert.Contains("자동으로 바꾸지 못했습니다", a.Candidate.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Not_in_value_list_is_not_a_candidate()
    {
        Assert.DoesNotContain(RewriteAdvisor.Analyze("SELECT e.x FROM emp e WHERE e.k NOT IN (1, 2, 3)"), a => a.Rule == RewriteRule.NotInToNotExists);
    }

    // ---- OR → UNION ALL ----

    [Fact]
    public void Or_over_different_columns_becomes_union_all_with_lnnvl()
    {
        var a = One("SELECT o.id FROM orders o WHERE o.site = :s AND (o.cust_id = 10 OR o.order_no = 'A1')", RewriteRule.OrToUnionAll);
        Assert.True(a.HasRewrite);
        Assert.False(a.SemanticsMayChange);
        Assert.Equal(
            "SELECT o.id FROM orders o WHERE o.site = :s AND (o.cust_id = 10) UNION ALL SELECT o.id FROM orders o WHERE o.site = :s AND (o.order_no = 'A1') AND LNNVL(o.cust_id = 10)",
            Flat(a.Candidate.Sql));
    }

    [Fact]
    public void Top_level_or_keeps_the_with_clause_once()
    {
        var a = One("WITH c AS (SELECT 1 x FROM dual) SELECT t.a FROM t, c WHERE t.a = 1 OR t.b = 2", RewriteRule.OrToUnionAll);
        var sql = Flat(a.Candidate.Sql);
        Assert.StartsWith("WITH c AS (SELECT 1 x FROM dual) SELECT t.a FROM t, c WHERE (t.a = 1) UNION ALL SELECT t.a", sql, StringComparison.Ordinal);
        Assert.Equal(1, sql.Split("WITH").Length - 1);
        Assert.EndsWith("WHERE (t.b = 2) AND LNNVL(t.a = 1)", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Or_on_one_column_with_equalities_is_an_in_list_and_not_reported()
    {
        Assert.DoesNotContain(RewriteAdvisor.Analyze("SELECT * FROM t WHERE t.a = 1 OR t.a = 2"), a => a.Rule == RewriteRule.OrToUnionAll);
        Assert.DoesNotContain(RewriteAdvisor.Analyze("SELECT * FROM t WHERE t.a = 1"), a => a.Rule == RewriteRule.OrToUnionAll);
    }

    [Theory]
    [InlineData("SELECT t.a FROM t WHERE t.a = 1 OR t.b = 2 ORDER BY t.a")]
    [InlineData("SELECT DISTINCT t.a FROM t WHERE t.a = 1 OR t.b = 2")]
    [InlineData("SELECT COUNT(*) FROM t WHERE t.a = 1 OR t.b = 2")]
    [InlineData("SELECT t.a FROM t WHERE (t.a = 1 AND t.c = 3) OR t.b = 2")]
    [InlineData("SELECT t.a FROM t WHERE ROWNUM < 10 AND (t.a = 1 OR t.b = 2)")]
    [InlineData("SELECT t.a FROM t WHERE t.a = 1 OR t.b IN (SELECT x FROM u)")]
    public void Or_shapes_that_change_meaning_give_a_warning_only(string sql)
    {
        var a = One(sql, RewriteRule.OrToUnionAll);
        Assert.False(a.HasRewrite);
    }

    // ---- 열에 씌운 함수 ----

    [Fact]
    public void Trunc_equality_on_a_column_becomes_a_one_day_range()
    {
        var a = One("SELECT lh.lot_id FROM lot_hist lh\n WHERE TRUNC(lh.evt_time) = :d AND lh.eqp_id = :e", RewriteRule.FunctionOnColumn);
        Assert.True(a.HasRewrite);
        Assert.Equal(2, a.Line);
        Assert.Equal("TRUNC 제거", a.ShortLabel);
        Assert.Equal("열에 씌운 함수 · TRUNC(lh.evt_time)", a.Title);
        Assert.Equal("SELECT lh.lot_id FROM lot_hist lh WHERE (lh.evt_time >= TRUNC(:d) AND lh.evt_time < TRUNC(:d) + 1) AND lh.eqp_id = :e", Flat(a.Candidate.Sql));
        Assert.NotNull(a.Caution);
    }

    [Fact]
    public void Other_functions_on_columns_warn_without_rewrite_and_select_list_is_ignored()
    {
        var list = RewriteAdvisor.Analyze("SELECT UPPER(e.ename), TRUNC(e.hired) FROM emp e JOIN dept d ON UPPER(d.dname) = 'X' WHERE NVL(e.comm, 0) > 0 ORDER BY TRUNC(e.hired)")
            .Where(a => a.Rule == RewriteRule.FunctionOnColumn).ToList();
        Assert.Equal(["열에 씌운 함수 · UPPER(d.dname)", "열에 씌운 함수 · NVL(e.comm, 0)"], list.Select(a => a.Title));
        Assert.All(list, a => Assert.False(a.HasRewrite));
    }

    [Fact]
    public void Function_on_a_value_or_pseudo_column_is_not_a_warning()
    {
        Assert.DoesNotContain(RewriteAdvisor.Analyze("SELECT * FROM t WHERE t.d >= TRUNC(SYSDATE) AND t.n = TO_NUMBER(:x) AND t.s = UPPER('a')"),
            a => a.Rule == RewriteRule.FunctionOnColumn);
    }

    // ---- 암시적 형 변환(계획) ----

    [Fact]
    public void Conversion_function_in_plan_predicates_is_a_suspected_implicit_conversion()
    {
        var a = One(TuningFixtures.Sql, RewriteRule.ImplicitConversion, TuningFixtures.JoinPlan());
        Assert.Equal("암시적 형 변환 의심 · LOT_NO", a.Title);
        Assert.Equal(3, a.StepId);
        Assert.Equal("Id 3", a.Location());
        Assert.Null(a.Line);
        Assert.False(a.HasRewrite);
    }

    [Fact]
    public void Conversion_the_user_wrote_explicitly_is_left_to_the_function_rule_and_no_plan_means_no_check()
    {
        var plan = TuningFixtures.JoinPlan();
        Assert.DoesNotContain(RewriteAdvisor.Analyze("SELECT * FROM lot_hist lh WHERE TO_NUMBER(lh.lot_no) = 123", plan), a => a.Rule == RewriteRule.ImplicitConversion);
        Assert.DoesNotContain(RewriteAdvisor.Analyze(TuningFixtures.Sql), a => a.Rule == RewriteRule.ImplicitConversion);
    }

    [Fact]
    public void Internal_function_is_reported_once_per_column_with_a_note()
    {
        var plan = new ExecutionPlan(
            [
                new PlanStep(0, null, 0, "SELECT STATEMENT"),
                new PlanStep(1, 0, 1, "TABLE ACCESS") { Options = "FULL", ObjectName = "T", FilterPredicates = "INTERNAL_FUNCTION(\"T\".\"ID\")=1" },
                new PlanStep(2, 0, 1, "TABLE ACCESS") { Options = "FULL", ObjectName = "T", FilterPredicates = "INTERNAL_FUNCTION(\"T\".\"ID\")=2" },
            ], string.Empty);
        var a = One("SELECT * FROM t WHERE t.id = '1'", RewriteRule.ImplicitConversion, plan);
        Assert.Contains("IN 목록", a.Candidate.Description, StringComparison.Ordinal);
    }

    // ---- 스칼라 하위 질의 → 조인 ----

    [Fact]
    public void Scalar_subquery_in_select_list_becomes_a_left_join()
    {
        var a = One("SELECT lh.lot_id, (SELECT c.code_nm FROM com_code c WHERE c.code = lh.evt_cd AND c.grp = 'EVT') evt_nm FROM lot_hist lh WHERE lh.eqp_id = :e",
            RewriteRule.ScalarSubqueryToJoin);
        Assert.True(a.HasRewrite);
        Assert.True(a.SemanticsMayChange);
        Assert.Equal("스칼라 하위 질의 → 조인 · com_code", a.Title);
        Assert.Equal(
            "SELECT lh.lot_id, c.code_nm evt_nm FROM lot_hist lh LEFT JOIN com_code c ON c.code = lh.evt_cd AND c.grp = 'EVT' WHERE lh.eqp_id = :e",
            Flat(a.Candidate.Sql));
    }

    [Fact]
    public void Scalar_subquery_with_comma_joins_uses_plus_marks()
    {
        var a = One("SELECT e.ename, (SELECT d.dname FROM dept d WHERE d.deptno = e.deptno) dn FROM emp e, bonus b WHERE b.ename = e.ename",
            RewriteRule.ScalarSubqueryToJoin);
        Assert.Equal(
            "SELECT e.ename, d.dname dn FROM emp e, bonus b, dept d WHERE b.ename = e.ename AND d.deptno(+) = e.deptno",
            Flat(a.Candidate.Sql));
    }

    [Theory]
    [InlineData("SELECT e.x, (SELECT MAX(d.y) FROM d WHERE d.k = e.k) FROM e")]                 // 집계
    [InlineData("SELECT e.x, (SELECT y FROM d WHERE d.k = e.k) FROM e")]                         // 별칭 없음
    [InlineData("SELECT e.x, (SELECT d.y FROM d WHERE k = e.k) FROM e")]                         // 한정 없음
    [InlineData("SELECT e.x, (SELECT e.y FROM d e WHERE e.k = 1) FROM e")]                       // 별칭 겹침
    [InlineData("SELECT e.x, (SELECT d.y FROM d) FROM e")]                                       // 상관 없음
    [InlineData("SELECT e.x, (SELECT d.y FROM d, f WHERE d.k = e.k) FROM e")]                    // 표 둘
    public void Scalar_subquery_shapes_that_cannot_be_joined_give_a_warning_only(string sql)
    {
        var a = One(sql, RewriteRule.ScalarSubqueryToJoin);
        Assert.False(a.HasRewrite);
    }

    // ---- SELECT * ----

    [Fact]
    public void Select_star_is_warned_once_per_block_but_not_inside_exists_or_count()
    {
        var list = RewriteAdvisor.Analyze("SELECT * FROM (SELECT t.* FROM t) v WHERE EXISTS (SELECT * FROM u WHERE u.k = v.k) AND (SELECT COUNT(*) FROM w) > 0")
            .Where(a => a.Rule == RewriteRule.SelectStar).ToList();
        Assert.Equal(2, list.Count);
        Assert.All(list, a => Assert.False(a.HasRewrite));
        Assert.DoesNotContain(RewriteAdvisor.Analyze("SELECT a * b FROM t"), a => a.Rule == RewriteRule.SelectStar);
    }

    // ---- 경계 ----

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-- only a comment")]
    public void Empty_text_gives_no_advice(string sql) => Assert.Empty(RewriteAdvisor.Analyze(sql));

    [Fact]
    public void Trailing_semicolon_is_ignored_and_english_titles_are_available()
    {
        var a = Assert.Single(RewriteAdvisor.Analyze("SELECT t.a FROM t WHERE t.a = 1 OR t.b = 2;", english: true), x => x.Rule == RewriteRule.OrToUnionAll);
        Assert.True(a.HasRewrite);
        Assert.DoesNotContain(";", a.Candidate.Sql, StringComparison.Ordinal);
        Assert.Equal("line 1", a.Location(english: true));
        var star = Assert.Single(RewriteAdvisor.Analyze("SELECT * FROM t", english: true));
        Assert.Equal("SELECT * warning", star.Title);
    }

    [Fact]
    public void Unbalanced_text_never_produces_a_rewrite()
    {
        var list = RewriteAdvisor.Analyze("SELECT t.a FROM t WHERE (t.a = 1 OR t.b = 2");
        Assert.All(list, a => Assert.False(a.HasRewrite));
    }
}
