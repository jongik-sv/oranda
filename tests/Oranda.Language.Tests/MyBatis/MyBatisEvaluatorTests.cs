using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Oranda.Core.Text;
using Oranda.Language.MyBatis;
using Oranda.Testing.MyBatis;
using Xunit.Abstractions;

namespace Oranda.Language.Tests.MyBatis;

/// <summary>
/// TSK-02-02 동적 SQL 계산기: 시안 시나리오, 태그별 동작, 블록 범위·덮어쓰기, 바인드 이름, 파라미터 찾기, 조합, 모두 켬, 50 ms.
/// MyBatis 3.5.19 와 같은 출력인지는 <see cref="GoldenEvaluatorTests"/> 가 본다. 여기는 Oranda 쪽 결과 모양을 본다.
/// </summary>
public partial class MyBatisEvaluatorTests(ITestOutputHelper output)
{
    private static readonly MyBatisEvaluator Evaluator = new();
    private static readonly GoldenMapper Emp = GoldenMapper.Of(MapperSamples.EmpMapper);

    private static Dictionary<string, object?> V(params (string Key, object? Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

    private static List<object?> L(params object?[] items) => [.. items];

    private static MyBatisElement Stmt(string text, string name = "select") =>
        new() { Kind = MyBatisElementKind.Select, Name = name, Id = "s", Text = text };

    private static EvalResult Eval(string text, Dictionary<string, object?> values, IReadOnlyDictionary<TextRange, bool>? overrides = null, GoldenMapper? fragments = null) =>
        Evaluator.Evaluate(Stmt(text), fragments ?? GoldenMapper.Of("<mapper namespace=\"t\"></mapper>"), values, overrides);

    // 시안과 비교: 공백 덩어리를 하나로, 괄호·쉼표 앞뒤 공백은 없앤다(MyBatis 는 foreach 조각을 공백으로 잇는다).
    private static string Shape(string sql) => PunctuationRegex().Replace(Regex.Replace(sql, @"\s+", " ").Trim(), "$1");

    [GeneratedRegex(@"\s*([(),])\s*")]
    private static partial Regex PunctuationRegex();

    // ---- 시안 selectEmpList 시나리오 4개(Main.dc.html) ----

    public static TheoryData<string, Dictionary<string, object?>, string, string[], bool[]> MockupScenarios() => new()
    {
        {
            "이름 검색", V(("empNo", null), ("ename", "KIM"), ("deptIds", L()), ("orderBy", "E.ENAME")),
            "SELECT E.EMPNO, E.ENAME, E.JOB, E.SAL, D.DNAME FROM EMP E JOIN DEPT D ON D.DEPTNO = E.DEPTNO WHERE E.ENAME LIKE '%' || :ename || '%' ORDER BY E.ENAME",
            ["ename=KIM"], [true, false, true, false]
        },
        {
            "부서 목록", V(("empNo", null), ("ename", ""), ("deptIds", L(10, 20)), ("orderBy", "E.DEPTNO, E.ENAME")),
            "SELECT E.EMPNO, E.ENAME, E.JOB, E.SAL, D.DNAME FROM EMP E JOIN DEPT D ON D.DEPTNO = E.DEPTNO WHERE E.DEPTNO IN (:id_1, :id_2) ORDER BY E.DEPTNO, E.ENAME",
            ["id_1=10", "id_2=20"], [true, false, false, true]
        },
        {
            "전체 조건", V(("empNo", 7369), ("ename", "SM"), ("deptIds", L(20)), ("orderBy", "E.EMPNO")),
            "SELECT E.EMPNO, E.ENAME, E.JOB, E.SAL, D.DNAME FROM EMP E JOIN DEPT D ON D.DEPTNO = E.DEPTNO WHERE E.EMPNO = :empNo AND E.ENAME LIKE '%' || :ename || '%' AND E.DEPTNO IN (:id_1) ORDER BY E.EMPNO",
            ["empNo=7369", "ename=SM", "id_1=20"], [true, true, true, true]
        },
        {
            "조건 없음", V(("empNo", null), ("ename", null), ("deptIds", null), ("orderBy", "E.EMPNO")),
            "SELECT E.EMPNO, E.ENAME, E.JOB, E.SAL, D.DNAME FROM EMP E JOIN DEPT D ON D.DEPTNO = E.DEPTNO ORDER BY E.EMPNO",
            [], [false, false, false, false]
        },
    };

    [Theory]
    [MemberData(nameof(MockupScenarios))]
    public void Mockup_selectEmpList_scenarios_match_the_design(string scenario, Dictionary<string, object?> values, string sql, string[] binds, bool[] whereAndIfs)
    {
        var r = Evaluator.Evaluate(Emp["selectEmpList"], Emp, values, null);

        Assert.True(Shape(sql) == Shape(r.Sql), $"{scenario}: {r.Sql}");
        Assert.Equal(binds, r.Binds.Select(b => $"{b.Name}={b.Value}"));
        var orderBy = Assert.Single(r.Substitutions);
        Assert.Equal(("orderBy", values["orderBy"]), (orderBy.Name, (object?)orderBy.Value));

        // 시안의 켬·끔 표시: where, if 세 개(foreach 는 if 안이라 마지막 if 와 같이 움직인다).
        var tags = r.Blocks.Where(b => b.Tag is "where" or "if").Select(b => b.IsOn).ToArray();
        Assert.Equal(whereAndIfs, tags);
        Assert.Empty(r.Issues);
    }

    [Fact]
    public void Mockup_updateEmpSal_without_set_keeps_the_dangling_comma_like_MyBatis()
    {
        // 시안 Combos: <set> 없이 SET 뒤 if 두 개. sal 만 있으면 MyBatis 도 「SET SAL = ?, WHERE」 를 만든다(조합 검사가 잡을 오류).
        var r = Evaluator.Evaluate(Emp["updateEmpSal"], Emp, V(("sal", 1000), ("comm", null), ("empNo", 7369)), null);
        Assert.Equal("UPDATE EMP SET SAL = :sal, WHERE EMPNO = :empNo", GoldenEvaluatorTests.Normalize(r.Sql));
    }

    // ---- 태그별 ----

    [Fact]
    public void Where_drops_the_leading_and_or_and_disappears_when_empty()
    {
        const string text = """<select id="s">SELECT * FROM T <where><if test="a != null">AND A = #{a}</if><if test="b != null"> or B = #{b}</if></where></select>""";
        Assert.Equal("SELECT * FROM T WHERE B = :b", GoldenEvaluatorTests.Normalize(Eval(text, V(("b", 2))).Sql));
        Assert.Equal("SELECT * FROM T WHERE A = :a or B = :b", GoldenEvaluatorTests.Normalize(Eval(text, V(("a", 1), ("b", 2))).Sql));
        var none = Eval(text, V());
        Assert.Equal("SELECT * FROM T", none.Sql);
        Assert.False(none.Blocks.Single(b => b.Tag == "where").IsOn);
    }

    [Fact]
    public void Set_drops_the_trailing_comma()
    {
        const string text = """<update id="s">UPDATE T <set><if test="a != null">A = #{a},</if><if test="b != null">B = #{b},</if></set> WHERE K = #{k}</update>""";
        Assert.Equal("UPDATE T SET A = :a WHERE K = :k", GoldenEvaluatorTests.Normalize(Eval(text, V(("a", 1), ("k", 9))).Sql));
    }

    [Fact]
    public void Trim_applies_prefix_suffix_and_overrides()
    {
        const string text = """<insert id="s">INSERT INTO T <trim prefix="(" suffix=")" suffixOverrides=","><if test="a != null">A,</if><if test="b != null">B,</if></trim></insert>""";
        Assert.Equal("INSERT INTO T ( A )", GoldenEvaluatorTests.Normalize(Eval(text, V(("a", 1))).Sql));
        Assert.Equal("INSERT INTO T", GoldenEvaluatorTests.Normalize(Eval(text, V()).Sql));
    }

    [Fact]
    public void Trim_prefix_override_without_space_also_cuts_ORDER_like_MyBatis_3_5_19()
    {
        // MyBatis 3.5.19 실측(TagSamples trimNoSpace/b): prefixOverrides="and|or" 는 ORDER_NO 의 OR 도 뗀다.
        const string text = """<select id="s">SELECT * FROM T <trim prefix="WHERE" prefixOverrides="and|or"><if test="b != null">ORDER_NO = #{b}</if></trim></select>""";
        Assert.Equal("SELECT * FROM T WHERE DER_NO = :b", GoldenEvaluatorTests.Normalize(Eval(text, V(("b", 1))).Sql));
    }

    [Fact]
    public void Choose_takes_the_first_true_when_then_otherwise()
    {
        const string text = """<select id="s">SELECT * FROM T WHERE 1 = 1 <choose><when test="a != null">AND A = #{a}</when><when test="b != null">AND B = #{b}</when><otherwise>AND ROWNUM &lt;= 10</otherwise></choose></select>""";
        Assert.Equal("SELECT * FROM T WHERE 1 = 1 AND A = :a", GoldenEvaluatorTests.Normalize(Eval(text, V(("a", 1), ("b", 2))).Sql));
        Assert.Equal("SELECT * FROM T WHERE 1 = 1 AND B = :b", GoldenEvaluatorTests.Normalize(Eval(text, V(("b", 2))).Sql));
        var other = Eval(text, V());
        Assert.Equal("SELECT * FROM T WHERE 1 = 1 AND ROWNUM <= 10", GoldenEvaluatorTests.Normalize(other.Sql));
        Assert.Equal([false, false, true], other.Blocks.Where(b => b.Tag is "when" or "otherwise").Select(b => b.IsOn));
    }

    [Fact]
    public void Foreach_numbers_item_binds_and_empty_list_drops_open_close()
    {
        const string text = """<select id="s">SELECT * FROM T WHERE ID IN <foreach collection="ids" item="id" open="(" separator="," close=")">#{id}</foreach></select>""";
        var r = Eval(text, V(("ids", L(10, 20, 30))));
        Assert.Equal("SELECT * FROM T WHERE ID IN(:id_1,:id_2,:id_3)", Shape(r.Sql));
        Assert.Equal(["ids[0]", "ids[1]", "ids[2]"], r.Binds.Select(b => b.Parameter));
        Assert.Equal([10, 20, 30], r.Binds.Select(b => b.Value));

        // 주어진 빈 목록은 MyBatis 처럼 아무것도 붙이지 않는다(로그 붙여넣기가 0개 로그와 맞춘다). 값을 모를 때만 바인드 자리 하나.
        var empty = Eval(text, V(("ids", L())));
        Assert.Equal("SELECT * FROM T WHERE ID IN", empty.Sql);
        Assert.False(empty.Blocks.Single().IsOn);
        Assert.Empty(empty.Issues);
    }

    [Fact]
    public void Foreach_over_null_or_a_missing_value_is_one_bind_slot_like_an_equals_bind()
    {
        // 2026-10-24 사용자 결정: foreach 는 '=' 의 #{x} 같은 바인드 자리. 값을 모르면 IN (:id) 로 펼쳐 구문 검사가 진짜 오류를 찾게 한다.
        const string text = """<select id="s">SELECT * FROM T WHERE ID IN <foreach collection="ids" item="id" open="(" separator="," close=")">#{id}</foreach> AND X = 1</select>""";
        foreach (var values in new[] { V(("ids", null)), V() })
        {
            var r = Eval(text, values);
            Assert.Equal("SELECT * FROM T WHERE ID IN(:id)AND X = 1", Shape(r.Sql));
            var bind = Assert.Single(r.Binds);
            Assert.Equal(("id", null), (bind.Name, bind.Value));
            var issue = Assert.Single(r.Issues);
            Assert.Equal(MyBatisEvalIssueKind.Assumed, issue.Kind);
            Assert.Contains("바인드 자리 하나로 펼쳤습니다", issue.Reason, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Nullable_foreach_without_a_value_says_so_and_nested_ones_report_once()
    {
        const string nullable = """<select id="s">SELECT * FROM T WHERE ID IN <foreach collection="ids" item="id" nullable="true" open="(" separator="," close=")">#{id}</foreach></select>""";
        var r = Eval(nullable, V());
        Assert.Equal("SELECT * FROM T WHERE ID IN(:id)", Shape(r.Sql));
        Assert.Contains("nullable 이면", Assert.Single(r.Issues).Reason, StringComparison.Ordinal);

        // 바깥 foreach 가 두 번 돌아도 안쪽 값 없는 foreach 알림은 하나. 계산 못 한 collection 식도 번호 없는 바인드 자리 하나.
        const string nested = """<select id="s">SELECT * FROM T WHERE <foreach collection="groups" item="g" separator=" OR ">(D = #{g.deptNo} AND E IN <foreach collection="g.ids" item="id" open="(" separator="," close=")">#{id}</foreach>)</foreach></select>""";
        var two = Eval(nested, V(("groups", L(V(("deptNo", 10)), V(("deptNo", 20))))));
        Assert.Equal(MyBatisEvalIssueKind.Assumed, Assert.Single(two.Issues).Kind);
        Assert.Equal(4, two.Binds.Count);
    }

    [Fact]
    public void Nested_foreach_numbers_inner_items_across_the_outer_loop()
    {
        const string text = """<select id="s">SELECT * FROM T WHERE <foreach collection="groups" item="g" separator=" OR ">(D = #{g.deptNo} AND E IN <foreach collection="g.ids" item="id" open="(" separator="," close=")">#{id}</foreach>)</foreach></select>""";
        var groups = L(V(("deptNo", 10), ("ids", L(1, 2))), V(("deptNo", 20), ("ids", L(3))));
        var r = Eval(text, V(("groups", groups)));
        Assert.Equal(["g_deptNo_1", "id_1", "id_2", "g_deptNo_2", "id_3"], r.Binds.Select(b => b.Name));
        Assert.Equal(["groups[0].deptNo", "groups[0].ids[0]", "groups[0].ids[1]", "groups[1].deptNo", "groups[1].ids[0]"], r.Binds.Select(b => b.Parameter));
        Assert.Equal([10, 1, 2, 20, 3], r.Binds.Select(b => b.Value));
        Assert.Equal(2, r.Blocks.Count); // 반복해도 원문 태그마다 블록 하나
    }

    [Fact]
    public void Bind_tag_builds_the_like_pattern_and_null_operand_is_unevaluable()
    {
        const string text = """<select id="s"><bind name="pat" value="'%' + ename + '%'"/>SELECT * FROM T WHERE N LIKE #{pat}</select>""";
        var r = Eval(text, V(("ename", "KIM")));
        Assert.Equal("%KIM%", Assert.Single(r.Binds).Value);
        Assert.Equal("pat", r.Binds[0].Name);

        var bad = Eval(text, V(("ename", null)));
        Assert.Equal(MyBatisEvalIssueKind.Unevaluable, Assert.Single(bad.Issues).Kind);
        Assert.Null(Assert.Single(bad.Binds).Value);
    }

    [Fact]
    public void Bind_inside_foreach_keeps_the_last_value_like_MyBatis_3_5_19()
    {
        // MyBatis 3.5.19 실측(TagSamples bindInForeach): 모든 #{np} 가 마지막 반복 값으로 묶인다(사용자 결정 Q-C: 그대로 재현).
        const string text = """<select id="s">SELECT * FROM T WHERE <foreach collection="names" item="n" separator=" OR "><bind name="np" value="n + '%'"/>N LIKE #{np}</foreach></select>""";
        var r = Eval(text, V(("names", L("A", "B"))));
        Assert.Equal(["np", "np"], r.Binds.Select(b => b.Name));
        Assert.Equal(["B%", "B%"], r.Binds.Select(b => b.Value));
    }

    // ---- include ----

    private static GoldenMapper Fragments() => GoldenMapper.Of("""
        <mapper namespace="t">
          <sql id="cols">EMPNO, ENAME</sql>
          <sql id="tableRef">${prefix}EMP</sql>
          <sql id="fromEmp">FROM <include refid="tableRef"/></sql>
          <sql id="dept">AND DEPTNO = #{deptNo}<if test="job != null"> AND JOB = #{job}</if></sql>
          <sql id="a"><include refid="b"/></sql>
          <sql id="b"><include refid="a"/></sql>
        </mapper>
        """);

    [Fact]
    public void Include_passes_property_to_nested_fragments()
    {
        const string text = """<select id="s">SELECT <include refid="cols"/> <include refid="fromEmp"><property name="prefix" value="SCOTT."/></include></select>""";
        var r = Eval(text, V(("prefix", "IGNORED.")), fragments: Fragments());
        Assert.Equal("SELECT EMPNO, ENAME FROM SCOTT.EMP", GoldenEvaluatorTests.Normalize(r.Sql));
        Assert.Empty(r.Substitutions); // property 는 파싱 때 바뀐다(실행 때 치환이 아니다)
    }

    [Fact]
    public void Include_fragment_blocks_and_binds_point_at_the_include_tag()
    {
        const string text = """<select id="s">SELECT * FROM T WHERE 1 = 1 <include refid="dept"/></select>""";
        var r = Eval(text, V(("deptNo", 10), ("job", "CLERK")), fragments: Fragments());
        int at = text.IndexOf("<include", StringComparison.Ordinal);
        var includeTag = new TextRange(at, text.IndexOf("/>", at, StringComparison.Ordinal) + 2 - at);

        Assert.Equal("SELECT * FROM T WHERE 1 = 1 AND DEPTNO = :deptNo AND JOB = :job", GoldenEvaluatorTests.Normalize(r.Sql));
        Assert.All(r.Binds, b => Assert.Equal((includeTag, "dept"), (b.Source, b.Fragment)));
        Assert.Equal("#{deptNo}", Fragments().Resolve("dept", null)!.Text.Substring(r.Binds[0].FragmentRange!.Value.Start, r.Binds[0].FragmentRange!.Value.Length));
        var inner = r.Blocks.Single(b => b.Tag == "if");
        Assert.Equal((includeTag, "dept", false), (inner.Range, inner.Fragment, inner.IsCondition));
        Assert.Equal(["include", "if"], r.Blocks.Select(b => b.Tag));
    }

    [Fact]
    public void Include_cycle_and_missing_fragment_are_reported()
    {
        var cycle = Eval("""<select id="s">SELECT 1 FROM DUAL <include refid="a"/></select>""", V(), fragments: Fragments());
        Assert.Contains(cycle.Issues, i => i.Kind == MyBatisEvalIssueKind.IncludeCycle);

        var missing = Eval("""<select id="s">SELECT 1 FROM DUAL <include refid="nope"/></select>""", V(), fragments: Fragments());
        var issue = Assert.Single(missing.Issues);
        Assert.Equal((MyBatisEvalIssueKind.FragmentMissing, "nope"), (issue.Kind, issue.Expression));
        Assert.Equal("SELECT 1 FROM DUAL", missing.Sql);
    }

    // ---- 계산 못 하는 식·덮어쓰기·블록 범위 ----

    [Fact]
    public void Unevaluable_test_is_on_with_a_squiggle_and_the_override_decides()
    {
        const string text = """<select id="s">SELECT * FROM T <where><if test="ename.toUpperCase() == 'KIM'">N = #{ename}</if></where></select>""";
        var r = Eval(text, V(("ename", "kim")));
        var issue = Assert.Single(r.Issues);
        Assert.Equal(MyBatisEvalIssueKind.Unevaluable, issue.Kind);
        Assert.Equal("ename.toUpperCase() == 'KIM'", issue.Expression);
        int testStart = text.IndexOf("ename.toUpperCase", StringComparison.Ordinal);
        Assert.InRange(issue.Range.Start, testStart, testStart + issue.Expression!.Length);
        Assert.Equal("SELECT * FROM T WHERE N = :ename", GoldenEvaluatorTests.Normalize(r.Sql)); // 기본 = 켬(사용자 결정 Q-B)

        var block = r.Blocks.Single(b => b.Tag == "if");
        var off = Eval(text, V(("ename", "kim")), new Dictionary<TextRange, bool> { [block.Range] = false });
        Assert.Equal("SELECT * FROM T", off.Sql);
        Assert.True(off.Blocks.Single(b => b.Tag == "if").IsOverridden);
    }

    [Fact]
    public void Foreach_is_not_a_condition_so_an_override_on_its_range_changes_nothing()
    {
        const string text = """<select id="s">SELECT * FROM T WHERE ID IN <foreach collection="ids" item="id" open="(" separator="," close=")">#{id}</foreach></select>""";
        var block = Eval(text, V(("ids", L(1)))).Blocks.Single();
        Assert.False(block.IsCondition);

        var off = Eval(text, V(("ids", L(1, 2))), new Dictionary<TextRange, bool> { [block.Range] = false });
        Assert.Equal("SELECT * FROM T WHERE ID IN(:id_1,:id_2)", Shape(off.Sql));
        Assert.False(off.Blocks.Single().IsOverridden);
    }

    [Fact]
    public void Blocks_cover_open_to_close_tag_in_source_order_outer_first()
    {
        var r = Evaluator.Evaluate(Emp["selectEmpList"], Emp, V(("deptIds", L(1))), null);
        var text = Emp["selectEmpList"].Text;
        Assert.Equal(["include", "where", "if", "if", "if", "foreach"], r.Blocks.Select(b => b.Tag));
        foreach (var b in r.Blocks.Where(b => b.Tag != "include"))
        {
            var slice = text.Substring(b.Range.Start, b.Range.Length);
            Assert.StartsWith("<" + b.Tag, slice, StringComparison.Ordinal);
            Assert.EndsWith("</" + b.Tag + ">", slice, StringComparison.Ordinal);
        }

        Assert.Equal([false, false, true, true, true, false], r.Blocks.Select(b => b.IsCondition)); // foreach 는 조건이 아니다(바인드 자리)
        Assert.Equal(["empNo != null", "ename != null and ename != ''", "deptIds != null and deptIds.size() > 0"],
            r.Blocks.Where(b => b.Tag == "if").Select(b => b.Test));
        Assert.Equal("#{empNo}", text.Substring(Evaluator.Evaluate(Emp["selectEmpList"], Emp, V(("empNo", 1)), null).Binds[0].Source.Start, 8));
    }

    // ---- 편집 중 입력·경계(리뷰 지적) ----

    [Fact]
    public void Trim_that_cannot_cut_the_suffix_reports_instead_of_throwing()
    {
        // 접두 , 를 떼면 빈 글이라 접미 , 를 뗄 수 없다: MyBatis 3.5 는 StringIndexOutOfBoundsException.
        const string text = """<select id="s">SELECT 1 FROM T <trim prefixOverrides="," suffixOverrides=","><if test="a != null">,</if></trim></select>""";
        var r = Eval(text, V(("a", 1)));
        Assert.Equal(MyBatisEvalIssueKind.InvalidTag, Assert.Single(r.Issues).Kind);
        Assert.Equal("SELECT 1 FROM T", r.Sql);

        var allOn = Evaluator.Evaluate(Stmt(text), Fragments(), V(), null, new MyBatisEvalOptions(AllOn: true, LegacyNotation: true));
        Assert.NotNull(allOn.Sql);
    }

    [Fact]
    public void Bare_less_than_in_sql_stays_text_with_an_invalid_tag_issue()
    {
        const string text = """<select id="s">SELECT * FROM T WHERE A < 1 AND B = #{b}</select>""";
        var r = Eval(text, V(("b", 2)));
        Assert.Equal("SELECT * FROM T WHERE A < 1 AND B = :b", r.Sql);
        Assert.Equal(2, Assert.Single(r.Binds).Value);
        var issue = Assert.Single(r.Issues);
        Assert.Equal((MyBatisEvalIssueKind.InvalidTag, text.IndexOf(" < ", StringComparison.Ordinal) + 1), (issue.Kind, issue.Range.Start));
        Assert.Equal(["b"], Evaluator.ExtractParameters(Stmt(text), Fragments()).Select(p => p.Name));
    }

    [Fact]
    public void Dollar_is_an_ognl_expression_and_hash_uses_MetaObject_paths()
    {
        const string text = """<select id="s">SELECT ${a + '_' + b} FROM T WHERE X = #{ids[1]} AND Y = #{m.size} ORDER BY ${sort == null ? 'K' : sort}</select>""";
        var r = Eval(text, V(("a", "A"), ("b", "B"), ("ids", L(10, 20)), ("m", V(("size", 7))), ("sort", null)));
        Assert.StartsWith("SELECT A_B FROM T WHERE X = :ids_1 AND Y = :m_size ORDER BY", r.Sql, StringComparison.Ordinal);
        Assert.Equal([20, 7], r.Binds.Select(b => b.Value)); // #{m.size} 는 MetaObject 라 열쇠 size
        var issue = Assert.Single(r.Issues); // 삼항은 계산하지 않는다(물결)
        Assert.Equal((MyBatisEvalIssueKind.Unevaluable, "sort == null ? 'K' : sort"), (issue.Kind, issue.Expression));
        Assert.Equal(["a", "b", "ids", "m", "sort"], Evaluator.ExtractParameters(Stmt(text), Fragments()).Select(p => p.Name));
    }

    [Fact]
    public void Double_substitution_uses_the_java_string()
    {
        var r = Eval("""<select id="s">SELECT ${rate}, ${tiny} FROM DUAL</select>""", V(("rate", 1.0d), ("tiny", 0.0001d)));
        Assert.Equal("SELECT 1.0, 1.0E-4 FROM DUAL", r.Sql);
    }

    [Fact]
    public void Include_property_inside_a_hash_token_is_replaced_first()
    {
        var mapper = GoldenMapper.Of("""<mapper namespace="t"><sql id="byCol">A = #{${col}}</sql></mapper>""");
        var r = Eval("""<select id="s">SELECT * FROM T WHERE <include refid="byCol"><property name="col" value="empNo"/><property name="col" value="x"/></include></select>""",
            V(("empNo", 1)), fragments: mapper);
        Assert.Equal("SELECT * FROM T WHERE A = :x", GoldenEvaluatorTests.Normalize(r.Sql)); // 뒤 property 가 이기고
        Assert.Contains(r.Issues, i => i.Kind == MyBatisEvalIssueKind.InvalidTag && i.Expression == "col"); // 겹친 property 는 MyBatis 오류
    }

    [Fact]
    public void Nested_include_is_resolved_in_the_statement_namespace_and_reports_the_written_refid()
    {
        var asked = new List<(string Refid, string? Namespace)>();
        var resolver = new RecordingResolver(asked, new Dictionary<string, Oranda.Core.MyBatis.MyBatisFragment>(StringComparer.Ordinal)
        {
            ["other.Frag"] = new("Frag", "other", """<sql id="Frag">X <include refid="cols"/></sql>"""),
            ["cols"] = new("cols", "t", """<sql id="cols">A, #{b}</sql>"""),
        });
        var r = Evaluator.Evaluate(Stmt("""<select id="s">SELECT <include refid="other.Frag"/></select>"""), resolver, V(("b", 1)), null);
        Assert.Equal([("other.Frag", (string?)null), ("cols", null)], asked); // 중첩도 문장 매퍼(null = 같은 매퍼) 기준
        Assert.Equal("cols", Assert.Single(r.Binds).Fragment);
        Assert.Equal([null, "other.Frag"], r.Blocks.Where(b => b.Tag == "include").Select(b => b.Fragment)); // 조각 안 include 는 바깥 refid 그대로
    }

    private sealed class RecordingResolver(List<(string, string?)> asked, Dictionary<string, Oranda.Core.MyBatis.MyBatisFragment> map) : Oranda.Core.MyBatis.IMyBatisFragmentResolver
    {
        public Oranda.Core.MyBatis.MyBatisFragment? Resolve(string refid, string? currentNamespace)
        {
            asked.Add((refid, currentNamespace));
            return map.GetValueOrDefault(refid);
        }
    }

    [Fact]
    public void Odd_characters_never_throw()
    {
        var surrogate = Eval("""<select id="s">SELECT '&#xD800;' FROM DUAL WHERE A = #{a}</select>""", V(("a", 1)));
        Assert.Contains("&#xD800;", surrogate.Sql, StringComparison.Ordinal);

        var privateUse = Eval("<select id=\"s\">SELECT '\uE0001\uE001' FROM DUAL WHERE A = #{a}</select>", V(("a", 1)));
        Assert.Single(privateUse.Binds);

        foreach (int bangs in new[] { 1000, 5000 }) // 깊이 상한·길이 상한
        {
            var deep = Eval("<select id=\"s\">SELECT 1 FROM DUAL<if test=\"" + new string('!', bangs) + "a\"> X</if></select>", V(("a", 1)));
            Assert.Equal(MyBatisEvalIssueKind.Unevaluable, Assert.Single(deep.Issues).Kind);
        }
    }

    // ---- 바인드·치환 이름 ----

    [Fact]
    public void Bind_names_flatten_dots_keep_jdbc_hints_and_avoid_collisions()
    {
        const string text = """<select id="s">SELECT * FROM T WHERE A = #{user.name, jdbcType=VARCHAR} AND B = #{user_name} AND C = #{user.name} ORDER BY ${col}</select>""";
        var r = Eval(text, V(("user", V(("name", "KIM"))), ("user_name", "X"), ("col", "A")));
        Assert.Equal("SELECT * FROM T WHERE A = :user_name AND B = :user_name_2 AND C = :user_name ORDER BY A", r.Sql);
        Assert.Equal(["VARCHAR", null, null], r.Binds.Select(b => b.JdbcType));
        Assert.Equal(["KIM", "X", "KIM"], r.Binds.Select(b => b.Value));
        var sub = Assert.Single(r.Substitutions);
        Assert.Equal(("col", "A"), (sub.Name, sub.Value));
    }

    [Fact]
    public void Flat_dotted_keys_win_over_nested_dictionaries()
    {
        const string text = """<select id="s">SELECT * FROM T <where><if test="user.name != null">N = #{user.name}</if></where></select>""";
        var r = Eval(text, V(("user.name", "FLAT"), ("user", V(("name", "NESTED")))));
        Assert.Equal("FLAT", Assert.Single(r.Binds).Value);
    }

    // ---- ExtractParameters ----

    [Fact]
    public void ExtractParameters_lists_names_in_first_use_order_with_usage_and_hints()
    {
        var ps = Evaluator.ExtractParameters(Emp["selectEmpList"], Emp);
        Assert.Equal(["empNo", "ename", "deptIds", "orderBy"], ps.Select(p => p.Name));
        Assert.Equal(MyBatisParameterUsage.Test | MyBatisParameterUsage.Bind, ps[0].Usage);
        Assert.Equal(MyBatisParameterUsage.Test | MyBatisParameterUsage.Foreach, ps[2].Usage); // #{id} 는 foreach item 이라 빠진다
        Assert.Equal(MyBatisParameterUsage.Substitution, ps[3].Usage);
        Assert.Equal(2, ps[0].Locations.Count);

        const string text = """<select id="s"><bind name="pat" value="'%' + q + '%'"/>SELECT * FROM T WHERE N LIKE #{pat} AND D = #{d, jdbcType=DATE, javaType=java.util.Date}</select>""";
        var bound = Evaluator.ExtractParameters(Stmt(text), Fragments());
        Assert.Equal(["q", "d"], bound.Select(p => p.Name));
        Assert.Equal(MyBatisParameterUsage.BindTag, bound[0].Usage);
        Assert.Equal(("DATE", "java.util.Date"), (bound[1].JdbcType, bound[1].JavaType));
    }

    [Fact]
    public void ExtractParameters_includes_fragment_parameters_at_the_include_tag()
    {
        const string text = """<select id="s">SELECT * FROM T WHERE 1 = 1 <include refid="dept"/></select>""";
        var ps = Evaluator.ExtractParameters(Stmt(text), Fragments());
        Assert.Equal(["deptNo", "job"], ps.Select(p => p.Name));
        Assert.All(ps, p => Assert.Equal(text.IndexOf("<include", StringComparison.Ordinal), p.Locations.Single().Start));
    }

    // ---- 조합 ----

    [Fact]
    public void Combinations_are_exhaustive_up_to_the_limit_then_one_off_plus_all_on_and_off()
    {
        var allOn = Evaluator.Evaluate(Emp["selectEmpList"], Emp, V(), null, new MyBatisEvalOptions(AllOn: true));
        var combos = Evaluator.Combinations(allOn);
        Assert.Equal(8, combos.Count); // if 3개 = 2^3(foreach 는 조건이 아니다)
        Assert.All(combos, c => Assert.Equal(ConditionCombinationKind.Exhaustive, c.Kind));
        Assert.All(combos[0].Overrides.Values, Assert.True);
        Assert.All(combos[^1].Overrides.Values, Assert.False);

        var sb = new StringBuilder("<select id=\"s\">SELECT * FROM T WHERE 1 = 1");
        for (int i = 0; i < 9; i++)
        {
            sb.Append(CultureInfo.InvariantCulture, $"<if test=\"c{i} != null\"> AND C{i} = #{{c{i}}}</if>");
        }

        var nine = Eval(sb.Append("</select>").ToString(), V());
        var limited = Evaluator.Combinations(nine);
        Assert.Equal(11, limited.Count);
        Assert.Equal([ConditionCombinationKind.AllOn, ConditionCombinationKind.AllOff], limited.Take(2).Select(c => c.Kind));
        Assert.All(limited.Skip(2), c => Assert.Single(c.Overrides.Values, v => !v));

        // 조합의 덮어쓰기를 그대로 넘기면 그 조합의 SQL 이 나온다(모두 끔 = WHERE 1 = 1 만).
        var off = Evaluator.Evaluate(Stmt(sb.ToString()), Fragments(), V(), limited[1].Overrides);
        Assert.Equal("SELECT * FROM T WHERE 1 = 1", off.Sql);
    }

    [Fact]
    public void Fragment_blocks_are_not_combination_conditions()
    {
        const string text = """<select id="s">SELECT * FROM T WHERE 1 = 1 <include refid="dept"/></select>""";
        var allOn = Evaluator.Evaluate(Stmt(text), Fragments(), V(), null, new MyBatisEvalOptions(AllOn: true));
        Assert.Single(Evaluator.Combinations(allOn)); // 조건 0개 = 조합 하나(덮어쓰기 없음)
    }

    // ---- 모두 켬 ----

    [Fact]
    public void All_on_without_values_turns_every_condition_on_and_runs_foreach_once_unnumbered()
    {
        var r = Evaluator.Evaluate(Emp["selectEmpList"], Emp, V(), null, new MyBatisEvalOptions(AllOn: true));
        Assert.Equal("SELECT E.EMPNO, E.ENAME, E.JOB, E.SAL, D.DNAME FROM EMP E JOIN DEPT D ON D.DEPTNO = E.DEPTNO WHERE E.EMPNO = :empNo AND E.ENAME LIKE '%' || :ename || '%' AND E.DEPTNO IN (:id) ORDER BY",
            GoldenEvaluatorTests.Normalize(PunctuationSpacesRegex().Replace(r.Sql, m => m.Value.StartsWith('(') ? "(" : string.Empty)));
        Assert.All(r.Blocks, b => Assert.True(b.IsOn));
        Assert.Empty(r.Issues);
    }

    [GeneratedRegex(@"\(\s+|\s+(?=[),])")]
    private static partial Regex PunctuationSpacesRegex();

    [Fact]
    public void Legacy_notation_matches_the_old_ReadMyBatis_shape()
    {
        var r = Evaluator.Evaluate(Emp["selectEmpList"], Emp, V(), null, new MyBatisEvalOptions(AllOn: true, LegacyNotation: true));
        Assert.Contains("SELECT E.EMPNO, E.ENAME, E.JOB, E.SAL, D.DNAME", r.Sql, StringComparison.Ordinal);
        Assert.Contains("WHERE E.EMPNO = :empNo /* if empNo != null */", r.Sql, StringComparison.Ordinal);
        Assert.Contains("/* foreach deptIds */ (:id)", r.Sql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY &orderBy", r.Sql, StringComparison.Ordinal);
        Assert.Equal(3, r.Binds.Count); // 바인드·블록은 모두 켬 계산 그대로
    }

    // ---- 성능 ----

    [Fact]
    [Trait("Category", "Perf")]
    public void Five_hundred_line_statement_evaluates_within_50_ms()
    {
        var sb = new StringBuilder("<select id=\"big\">\n  SELECT *\n    FROM T\n  <where>\n");
        int lines = 4;
        for (int i = 0; lines < 500; i++)
        {
            sb.Append(CultureInfo.InvariantCulture, $"    <if test=\"c{i} != null and c{i} != ''\">\n      AND C{i} = #{{c{i}, jdbcType=VARCHAR}}\n    </if>\n");
            lines += 3;
            if (i % 10 == 0)
            {
                sb.Append(CultureInfo.InvariantCulture, $"    <if test=\"ids{i} != null and ids{i}.size() > 0\">AND D{i} IN <foreach collection=\"ids{i}\" item=\"id\" open=\"(\" separator=\",\" close=\")\">#{{id}}</foreach></if>\n");
                lines++;
            }
        }

        sb.Append("  </where>\n  ORDER BY ${orderBy}\n</select>\n");
        var statement = Stmt(sb.ToString());
        var values = new Dictionary<string, object?>(StringComparer.Ordinal) { ["orderBy"] = "C0" };
        for (int i = 0; i < 160; i += 2)
        {
            values["c" + i.ToString(CultureInfo.InvariantCulture)] = "v";
            values["ids" + i.ToString(CultureInfo.InvariantCulture)] = L(1, 2, 3);
        }

        var fragments = Fragments();
        Evaluator.Evaluate(statement, fragments, values, null); // 예열(JIT)
        long t0 = Stopwatch.GetTimestamp();
        var r = Evaluator.Evaluate(statement, fragments, values, null);
        var ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;

        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"문장 {sb.ToString().Count(c => c == '\n')}줄 계산 {ms:F1} ms (바인드 {r.Binds.Count}, 블록 {r.Blocks.Count})"));
        Assert.True(sb.ToString().Count(c => c == '\n') >= 500);
        Assert.True(ms <= 50, $"{ms:F1} ms");
    }
}
