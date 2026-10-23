using System.Globalization;
using Oranda.App.Features.MyBatisWorkspace.Conversion;
using Oranda.Core.Execution;
using Oranda.Core.Formatting;
using Oranda.Core.MyBatis;
using Oranda.Core.Text;
using Oranda.Language.Lexing;
using Oranda.Language.MyBatis;

namespace Oranda.App.Tests.Features.MyBatisWorkspace;

/// <summary>TSK-03-05 J1·J2: 일반 SQL 탭 → MyBatis 문장 바꾸기 엔진(<see cref="ConvertEngine"/>). 순수 계산이라 화면·파일 없이 시험한다.</summary>
public class ConvertTests
{
    private static readonly ConvertBind[] NoBinds = [];

    private static ConvertCandidate Cand(string sql) => new(0, "t", 0, sql, ConvertStatementKind.Select, true, "x", 0, []);

    private static ConvertedStatement Conv(
        string sql,
        ConvertStatementKind kind = ConvertStatementKind.Select,
        ConvertBind[]? binds = null,
        ConvertOptions? options = null,
        ISqlFormatter? formatter = null,
        string id = "x") =>
        ConvertEngine.Convert(Cand(sql), kind, id, binds ?? NoBinds, options ?? new ConvertOptions(), formatter);

    private static ConvertBind In(string name, string text, BindDataType type = BindDataType.Varchar2) => new(name, type, BindDirection.In, text);

    private static readonly ConvertOptions Wrap = new() { WrapWhere = true };

    private static IReadOnlyList<ConvertCandidate> Analyze(string title, string text, params string[] existing) =>
        ConvertEngine.Analyze([new ConvertSourceTab(title, text, NoBinds)], existing);

    // ---- 1. 바꾸기 ----

    [Fact]
    public void Binds_and_substitutions_are_rewritten_but_literals_comments_and_assignments_stay()
    {
        var r = Conv("SELECT ':lit' a, \":q\" b FROM &&sch.EMP /* :c &d */ WHERE id = :id AND n = &n -- :tail");
        Assert.Contains("SELECT ':lit' a, \":q\" b FROM ${sch}EMP /* :c &d */ WHERE id = #{id} AND n = ${n} -- :tail", r.ElementText);
        Assert.Equal([ConvertNoteKind.Substitution, ConvertNoteKind.Substitution], r.Notes.Select(n => n.Kind));
        Assert.Equal(["sch", "n"], r.Notes.Select(n => n.Name));

        var plsql = Conv("BEGIN :r := f(:a); END;", ConvertStatementKind.Callable);
        Assert.Contains("BEGIN #{r,mode=OUT,jdbcType=VARCHAR} := f(#{a}); END;", plsql.ElementText);
    }

    [Fact]
    public void Double_dot_after_a_substitution_keeps_the_schema_dot()
    {
        var r = Conv("SELECT * FROM &schema..ORDERS WHERE a = :a");
        Assert.Contains("FROM ${schema}.ORDERS WHERE a = #{a}", r.ElementText);
        Assert.Equal("selectOrders", Analyze("사원", "SELECT * FROM &schema..ORDERS")[0].SuggestedId);
        Assert.Equal("updateOrders", Analyze("사원", "UPDATE &&schema..ORDERS SET a = 1")[0].SuggestedId);
    }

    [Fact]
    public void Template_tokens_in_literals_comments_and_quoted_names_are_escaped()
    {
        var r = Conv("SELECT '#{x}' a, \"${y}\" b /* #{c} */ FROM t WHERE z = :z -- ${d}");
        Assert.Contains("SELECT '\\#{x}' a, \"\\${y}\" b /* \\#{c} */ FROM t WHERE z = #{z} -- \\${d}", r.ElementText);
        Assert.Equal(["z"], r.ScenarioValues.Keys);
    }

    [Fact]
    public void Template_tokens_with_a_backslash_already_in_front_get_a_second_backslash()
    {
        var r = Conv("SELECT '\\#{x}' FROM t");
        Assert.Contains("SELECT '\\\\#{x}' FROM t", r.ElementText);
    }

    [Fact]
    public void Positional_bind_becomes_p1_with_a_note_and_a_scenario_key()
    {
        var r = Conv("SELECT * FROM T WHERE a = :1", binds: [new ConvertBind("1", BindDataType.Number, BindDirection.In, "5")]);
        Assert.Contains("a = #{p1}", r.ElementText);
        var note = Assert.Single(r.Notes);
        Assert.Equal(ConvertNoteKind.PositionalBind, note.Kind);
        Assert.Equal("p1", note.Name);
        Assert.Equal(new MyBatisScenarioValue(MyBatisScenarioValueKind.Number, "5"), r.ScenarioValues["p1"]);
    }

    [Fact]
    public void Positional_bind_name_avoids_a_named_bind_that_already_uses_it()
    {
        var r = Conv("SELECT * FROM T WHERE a = :1 AND b = :P1 AND c = :1 AND d = :2", binds: [In("1", "5", BindDataType.Number)]);
        Assert.Contains("a = #{p1_2} AND b = #{P1} AND c = #{p1_2} AND d = #{p2}", r.ElementText);
        Assert.Equal(["p1_2", "p2"], r.Notes.Where(n => n.Kind == ConvertNoteKind.PositionalBind).Select(n => n.Name));
        Assert.Equal(new MyBatisScenarioValue(MyBatisScenarioValueKind.Number, "5"), r.ScenarioValues["p1_2"]);
    }

    [Fact]
    public void Bind_names_differing_only_in_case_use_the_first_spelling_everywhere()
    {
        var r = Conv("SELECT * FROM T WHERE a = :custId OR b = :CUSTID", binds: [new ConvertBind("CUSTID", BindDataType.Varchar2, BindDirection.In, "A1")]);
        Assert.Contains("a = #{custId} OR b = #{custId}", r.ElementText);
        var value = Assert.Single(r.ScenarioValues);
        Assert.Equal("custId", value.Key);
        Assert.Equal(new MyBatisScenarioValue(MyBatisScenarioValueKind.Text, "A1"), value.Value);
    }

    [Fact]
    public void Formatter_runs_before_rewriting_and_restores_bind_spelling()
    {
        var r = Conv("select a from emp where x = :custId and y = :CUSTID", options: new ConvertOptions { Format = new FormatterOptions() }, formatter: new UpperFormatter());
        Assert.Contains("SELECT A\n", r.ElementText);
        Assert.Contains("WHERE X = #{custId} AND Y = #{custId}", r.ElementText);
        Assert.DoesNotContain("#{CUSTID}", r.ElementText);
        Assert.Empty(r.Notes);
    }

    [Fact]
    public void Failed_format_keeps_the_original_text_with_a_note()
    {
        var r = Conv("select a from emp where x = :id", options: new ConvertOptions { Format = new FormatterOptions() }, formatter: new UpperFormatter(fail: true));
        Assert.Contains("select a from emp where x = #{id}", r.ElementText);
        Assert.Equal(ConvertNoteKind.FormatSkipped, Assert.Single(r.Notes).Kind);
    }

    // ---- 2. 종류 ----

    [Fact]
    public void Kinds_are_suggested_per_statement_and_non_convertible_ones_are_left_out()
    {
        var rows = Analyze("Emp List", "SELECT * FROM emp;\nINSERT INTO t VALUES (1);\nUPDATE t SET a = 1;\nMERGE INTO t USING s ON (1 = 1) WHEN MATCHED THEN UPDATE SET a = 1;\nDELETE FROM t;\nBEGIN NULL; END;\nCREATE TABLE x (a NUMBER);");
        Assert.Equal(
            [ConvertStatementKind.Select, ConvertStatementKind.Insert, ConvertStatementKind.Update, ConvertStatementKind.Update, ConvertStatementKind.Delete, ConvertStatementKind.Callable, null],
            rows.Select(c => c.Kind));
        Assert.Equal([true, true, true, true, true, false, false], rows.Select(c => c.IncludeByDefault));
        Assert.Equal(["selectEmpList", "insertEmpList", "updateEmpList", "mergeEmpList", "deleteEmpList", "callEmpList", "statementEmpList"], rows.Select(c => c.SuggestedId));
        Assert.Equal(ConvertNoteKind.PlSqlBlock, Assert.Single(rows[5].Notes).Kind);
        Assert.Equal(ConvertNoteKind.Unsupported, Assert.Single(rows[6].Notes).Kind);
        Assert.Equal([0, 1, 2, 3, 4, 5, 6], rows.Select(c => c.StatementIndex));
    }

    [Fact]
    public void PlSql_block_sql_keeps_END_and_converts_to_a_callable_update()
    {
        var row = Analyze("Proc", "BEGIN\n  pkg.run(:a);\nEND;\n/\nSELECT 1 FROM dual")[0];
        Assert.EndsWith("END;", row.Sql, StringComparison.Ordinal);
        Assert.Equal(1, row.BindCount);
        var r = ConvertEngine.Convert(row, ConvertStatementKind.Callable, "callProc", NoBinds, new ConvertOptions(), null);
        Assert.StartsWith("<update id=\"callProc\" statementType=\"CALLABLE\">\n", r.ElementText, StringComparison.Ordinal);
        Assert.Contains("    BEGIN\n      pkg.run(#{a});\n    END;\n</update>\n", r.ElementText);
    }

    // ---- 3. id 제안·겹침 ----

    [Fact]
    public void Id_comes_from_the_tab_name_then_the_first_table_then_a_sequence()
    {
        Assert.Equal("selectOrders", Analyze("orders", "SELECT 1 FROM x")[0].SuggestedId);
        Assert.Equal("selectEmpHist", Analyze("사원 목록", "SELECT * FROM hr.emp_hist")[0].SuggestedId);
        Assert.Equal("select1", Analyze("사원", "SELECT 1")[0].SuggestedId);
        Assert.Equal("updateOrders", Analyze("상태 바꾸기", "UPDATE &schema.ORDERS SET STATUS = :s")[0].SuggestedId);
        Assert.Equal("updateDeptList", Analyze("dept-list", "UPDATE t SET a = 1")[0].SuggestedId);
    }

    [Fact]
    public void Suggested_ids_do_not_collide_with_each_other_or_with_the_existing_mapper()
    {
        var tabs = new[] { new ConvertSourceTab("Orders", "SELECT 1 FROM t; SELECT 2 FROM t", NoBinds), new ConvertSourceTab("orders", "SELECT 3 FROM t", NoBinds) };
        var ids = ConvertEngine.Analyze(tabs, ["selectOrders"]).Select(c => c.SuggestedId).ToList();
        Assert.Equal(["selectOrders2", "selectOrders3", "selectOrders4"], ids);
    }

    [Fact]
    public void UniqueId_and_IsValidId_follow_the_contract()
    {
        var taken = new HashSet<string>(["a", "a2"]);
        Assert.Equal("a3", ConvertEngine.UniqueId("a", taken));
        Assert.Equal("b", ConvertEngine.UniqueId("b", taken));
        Assert.Contains("a3", taken);
        Assert.True(ConvertEngine.IsValidId("selectEmp_1"));
        Assert.True(ConvertEngine.IsValidId("_x$"));
        Assert.False(ConvertEngine.IsValidId("a.b"));
        Assert.False(ConvertEngine.IsValidId("1abc"));
        Assert.False(ConvertEngine.IsValidId(""));
    }

    // ---- 4. CDATA·엔티티 ----

    [Fact]
    public void Plain_sql_is_indented_without_cdata()
    {
        var r = Conv("SELECT *\n  FROM EMP\n WHERE EMPNO = :empNo");
        Assert.Equal("<select id=\"x\">\n    SELECT *\n      FROM EMP\n     WHERE EMPNO = #{empNo}\n</select>\n", r.ElementText);
    }

    [Fact]
    public void Comparison_signs_get_cdata_or_entities()
    {
        const string sql = "SELECT * FROM EMP WHERE SAL > :sal AND a <> 'x&y'";
        Assert.Equal(
            "<select id=\"x\">\n  <![CDATA[\n    SELECT * FROM EMP WHERE SAL > #{sal} AND a <> 'x&y'\n  ]]>\n</select>\n",
            Conv(sql).ElementText);
        Assert.Equal(
            "<select id=\"x\">\n    SELECT * FROM EMP WHERE SAL &gt; #{sal} AND a &lt;&gt; 'x&amp;y'\n</select>\n",
            Conv(sql, options: new ConvertOptions { UseCdata = false }).ElementText);
    }

    [Fact]
    public void Cdata_end_marker_in_text_splits_the_section()
    {
        var r = Conv("SELECT ']]>' FROM T");
        Assert.Contains("SELECT ']]]]><![CDATA[>' FROM T", r.ElementText);
    }

    [Fact]
    public void Multiline_literal_lines_are_not_indented()
    {
        var r = Conv("SELECT 'a\nb' FROM T WHERE x = :x");
        Assert.Equal("<select id=\"x\">\n    SELECT 'a\nb' FROM T WHERE x = #{x}\n</select>\n", r.ElementText);
    }

    [Fact]
    public void IndentElement_indents_from_the_second_line_but_not_multiline_literals_or_blank_lines()
    {
        const string element = "<select id=\"x\">\n    SELECT 'a\n  b' c,\n\n  \"q\n r\" d\n      FROM t\n</select>\n";
        Assert.Equal(
            "<select id=\"x\">\n      SELECT 'a\n  b' c,\n\n    \"q\n r\" d\n        FROM t\n  </select>",
            ConvertEngine.IndentElement(element.Replace("\n", "\r\n", StringComparison.Ordinal), "  "));
    }

    [Fact]
    public void Where_wrap_adds_if_for_bind_conditions_and_cdata_per_fragment()
    {
        const string sql = "SELECT * FROM EMP\n WHERE SAL > :sal\n   AND DEPTNO = 10\n   AND ENAME LIKE :name\n ORDER BY ENAME";
        var r = Conv(sql, binds: [In("SAL", "1000", BindDataType.Number), In("NAME", "S%")], options: Wrap);
        Assert.Equal(
            "<select id=\"x\">\n"
            + "    SELECT * FROM EMP\n"
            + "  <where>\n"
            + "    <if test=\"sal != null\">\n"
            + "      <![CDATA[\n"
            + "        AND SAL > #{sal}\n"
            + "      ]]>\n"
            + "    </if>\n"
            + "      AND DEPTNO = 10\n"
            + "    <if test=\"name != null\">\n"
            + "        AND ENAME LIKE #{name}\n"
            + "    </if>\n"
            + "  </where>\n"
            + "    ORDER BY ENAME\n"
            + "</select>\n",
            r.ElementText);
        Assert.Empty(r.Notes);
    }

    [Fact]
    public void Where_wrap_keeps_between_together_and_joins_several_binds_with_and()
    {
        var r = Conv("SELECT * FROM T WHERE a BETWEEN :lo AND :hi AND b = :b", binds: [In("LO", "1"), In("HI", "2"), In("B", "3")], options: Wrap);
        Assert.Contains("<if test=\"lo != null and hi != null\">", r.ElementText);
        Assert.Contains("AND a BETWEEN #{lo} AND #{hi}", r.ElementText);
        Assert.Contains("<if test=\"b != null\">", r.ElementText);
    }

    [Fact]
    public void Where_wrap_does_not_split_at_AND_inside_a_case_expression()
    {
        var r = Conv(
            "SELECT * FROM T WHERE st = CASE WHEN :f = 'Y' AND x = 1 THEN 'A' ELSE 'B' END AND z = :z",
            binds: [In("F", "Y"), In("Z", "1")],
            options: Wrap);
        Assert.Contains("<if test=\"f != null\">", r.ElementText);
        Assert.Contains("AND st = CASE WHEN #{f} = 'Y' AND x = 1 THEN 'A' ELSE 'B' END\n", r.ElementText);
        Assert.Contains("<if test=\"z != null\">", r.ElementText);
        Assert.Empty(r.Notes);
    }

    [Fact]
    public void Where_wrap_treats_clause_keywords_after_a_dot_as_column_names()
    {
        var r = Conv(
            "SELECT * FROM T p WHERE p.MODEL = :m AND p.OFFSET = :o AND p.WINDOW = 1 AND p.FETCH = :f ORDER BY p.ID",
            binds: [In("M", "a"), In("O", "b"), In("F", "c")],
            options: Wrap);
        string text = r.ElementText;
        Assert.Contains("AND p.MODEL = #{m}", text);
        Assert.Contains("AND p.OFFSET = #{o}", text);
        Assert.Contains("AND p.FETCH = #{f}", text);
        Assert.True(text.IndexOf("p.WINDOW = 1", StringComparison.Ordinal) < text.IndexOf("</where>", StringComparison.Ordinal));
        Assert.True(text.IndexOf("</where>", StringComparison.Ordinal) < text.IndexOf("ORDER BY p.ID", StringComparison.Ordinal));
    }

    [Fact]
    public void Where_wrap_ends_the_clause_at_offset_and_fetch_only_when_followed_by_row_limiting_syntax()
    {
        var r = Conv(
            "SELECT * FROM T WHERE offset = :a AND fetch = :b OFFSET :n ROWS FETCH NEXT 5 ROWS ONLY",
            binds: [In("A", "1"), In("B", "2"), In("N", "3", BindDataType.Number)],
            options: Wrap);
        string text = r.ElementText;
        Assert.Contains("AND offset = #{a}", text);
        Assert.Contains("AND fetch = #{b}", text);
        Assert.Contains("</where>\n    OFFSET #{n} ROWS FETCH NEXT 5 ROWS ONLY", text);
    }

    [Fact]
    public void Where_wrap_leaves_conditions_whose_first_scenario_value_is_null_unwrapped()
    {
        // 빈 값·탭 바인드 없음은 NULL 비교라 감싸서 조건을 빼면 원래 탭 결과와 달라진다.
        var r = Conv(
            "SELECT * FROM T WHERE a = :a AND b = :b AND c = :c AND d BETWEEN :lo AND :hi",
            binds: [In("A", "1"), In("B", ""), In("LO", "1"), In("HI", "")],
            options: Wrap);
        Assert.Contains("<if test=\"a != null\">", r.ElementText);
        Assert.DoesNotContain("b != null", r.ElementText);
        Assert.DoesNotContain("c != null", r.ElementText);
        Assert.DoesNotContain("hi != null", r.ElementText);
        Assert.Contains("AND b = #{b}\n      AND c = #{c}\n      AND d BETWEEN #{lo} AND #{hi}\n", r.ElementText);
        Assert.Empty(r.Notes);

        var none = Conv("SELECT * FROM T WHERE a = :a", options: Wrap);
        Assert.Equal("<select id=\"x\">\n    SELECT * FROM T WHERE a = #{a}\n</select>\n", none.ElementText);
    }

    [Theory]
    [InlineData("SELECT * FROM T WHERE a = :a OR b = :b")]
    [InlineData("SELECT * FROM T WHERE a = :a UNION SELECT * FROM U WHERE b = :b")]
    [InlineData("SELECT * FROM T WHERE a = :a CONNECT BY PRIOR x = y")]
    public void Where_wrap_is_skipped_with_a_note_for_or_set_operations_and_connect_by(string sql)
    {
        var r = Conv(sql, options: new ConvertOptions { WrapWhere = true });
        Assert.DoesNotContain("<where>", r.ElementText);
        Assert.Contains(r.Notes, n => n.Kind == ConvertNoteKind.WhereNotWrapped);
    }

    [Fact]
    public void Where_wrap_is_for_select_only()
    {
        var r = Conv("UPDATE T SET a = :a WHERE b = :b", ConvertStatementKind.Update, options: new ConvertOptions { WrapWhere = true });
        Assert.DoesNotContain("<where>", r.ElementText);
        Assert.Contains(r.Notes, n => n.Kind == ConvertNoteKind.WhereNotWrapped);
    }

    // ---- 5. jdbcType ----

    [Fact]
    public void Date_like_binds_always_get_jdbcType_and_the_option_adds_the_rest()
    {
        ConvertBind[] binds =
        [
            new("D", BindDataType.Date, BindDirection.In, "2024-01-01"),
            new("TS", BindDataType.Timestamp, BindDirection.In, ""),
            new("TZ", BindDataType.TimestampTz, BindDirection.In, ""),
            new("N", BindDataType.Number, BindDirection.In, "1"),
            new("S", BindDataType.Varchar2, BindDirection.In, "x"),
        ];
        const string sql = "SELECT * FROM T WHERE a = :d AND b = :ts AND c = :tz AND n = :n AND s = :s AND u = :unknown";
        var off = Conv(sql, binds: binds).ElementText;
        Assert.Contains("a = #{d,jdbcType=DATE} AND b = #{ts,jdbcType=TIMESTAMP} AND c = #{tz,jdbcType=TIMESTAMP_WITH_TIMEZONE} AND n = #{n} AND s = #{s} AND u = #{unknown}", off);

        var on = Conv(sql, binds: binds, options: new ConvertOptions { AddJdbcType = true }).ElementText;
        Assert.Contains("n = #{n,jdbcType=NUMERIC} AND s = #{s,jdbcType=VARCHAR} AND u = #{unknown}", on);
    }

    [Fact]
    public void Fixed_char_binds_always_get_jdbcType_char()
    {
        var r = Conv("SELECT * FROM T WHERE a = :c AND b = :s", binds: [In("C", "AB ", BindDataType.FixedChar), In("S", "x")]);
        Assert.Contains("a = #{c,jdbcType=CHAR} AND b = #{s}", r.ElementText);
    }

    [Fact]
    public void Callable_out_binds_without_a_jdbc_mapping_fall_back_to_varchar()
    {
        ConvertBind[] binds =
        [
            new("J", BindDataType.Json, BindDirection.Out, ""),
            new("IO", BindDataType.Json, BindDirection.InOut, "{}"),
            new("I", BindDataType.Json, BindDirection.In, "{}"),
        ];
        var r = Conv("BEGIN p(:j, :io, :i, :r, :t); :t := 1; :r := 2; END;", ConvertStatementKind.Callable, binds);
        Assert.Contains("p(#{j,mode=OUT,jdbcType=VARCHAR}, #{io,mode=INOUT,jdbcType=VARCHAR}, #{i}, #{r}, #{t});", r.ElementText);
        Assert.Contains("#{t,mode=OUT,jdbcType=VARCHAR} := 1; #{r,mode=OUT,jdbcType=VARCHAR} := 2;", r.ElementText);
    }

    [Fact]
    public void Callable_out_and_inout_binds_carry_mode_and_jdbcType()
    {
        ConvertBind[] binds =
        [
            new("R", BindDataType.Number, BindDirection.Out, ""),
            new("A", BindDataType.Varchar2, BindDirection.In, "x"),
            new("IO", BindDataType.Varchar2, BindDirection.InOut, "y"),
            new("C", BindDataType.RefCursor, BindDirection.Out, ""),
        ];
        var r = Conv("BEGIN :r := f(:a, :io); OPEN :c FOR SELECT 1 FROM dual; END;", ConvertStatementKind.Callable, binds);
        Assert.Contains("#{r,mode=OUT,jdbcType=NUMERIC} := f(#{a}, #{io,mode=INOUT,jdbcType=VARCHAR}); OPEN #{c,mode=OUT,jdbcType=CURSOR}", r.ElementText);
        var note = Assert.Single(r.Notes);
        Assert.Equal(ConvertNoteKind.RefCursorOut, note.Kind);
        Assert.Equal("c", note.Name);
    }

    // ---- 시나리오 값 ----

    [Fact]
    public void Scenario_values_cover_every_referenced_bind_with_the_right_kind()
    {
        ConvertBind[] binds =
        [
            new("NUM", BindDataType.Number, BindDirection.In, " 12.5 "),
            new("BAD", BindDataType.Number, BindDirection.In, "1,5x"),
            new("EMPTY", BindDataType.Varchar2, BindDirection.In, ""),
            new("FLAG", BindDataType.Boolean, BindDirection.In, "Y"),
            new("TXT", BindDataType.Date, BindDirection.In, "2024-05-06"),
            new("UNUSED", BindDataType.Varchar2, BindDirection.In, "z"),
        ];
        var r = Conv("SELECT * FROM &tbl WHERE a = :num AND b = :bad AND c = :empty AND d = :flag AND e = :txt AND f = :missing", binds: binds);
        Assert.Equal(
            new Dictionary<string, MyBatisScenarioValue>
            {
                ["num"] = new(MyBatisScenarioValueKind.Number, "12.5"),
                ["bad"] = new(MyBatisScenarioValueKind.Text, "1,5x"),
                ["empty"] = MyBatisScenarioValue.Null,
                ["flag"] = new(MyBatisScenarioValueKind.Boolean, "true"),
                ["txt"] = new(MyBatisScenarioValueKind.Text, "2024-05-06"),
                ["missing"] = MyBatisScenarioValue.Null,
                ["tbl"] = MyBatisScenarioValue.Null,
            },
            r.ScenarioValues);
    }

    // ---- 10. 로컬 대리: 펼친 SQL 토큰 열 = 원래 SQL 토큰 열 ----

    public static TheoryData<string, ConvertStatementKind, bool, bool> RoundTripCases() => new()
    {
        { "SELECT * FROM EMP WHERE EMPNO = :EMPNO AND ENAME LIKE :ename AND SAL BETWEEN :lo AND :hi ORDER BY ENAME", ConvertStatementKind.Select, false, true },
        { "SELECT * FROM EMP WHERE EMPNO = :EMPNO AND ENAME LIKE :ename AND SAL BETWEEN :lo AND :hi ORDER BY ENAME", ConvertStatementKind.Select, true, true },
        { "SELECT a FROM t WHERE x < :lo AND y > :hi AND z = 'a<b' -- :c\n ORDER BY a", ConvertStatementKind.Select, true, true },
        { "SELECT a FROM t WHERE x < :lo AND y > :hi AND z = 'a<b' -- :c\n ORDER BY a", ConvertStatementKind.Select, true, false },
        { "SELECT a, b FROM t WHERE x < :lo AND y > :hi", ConvertStatementKind.Select, false, false },
        { "UPDATE EMP SET SAL = :SAL, ENAME = :ename WHERE EMPNO = :empno", ConvertStatementKind.Update, true, true },
        { "SELECT * FROM EMP WHERE EMPNO = :1", ConvertStatementKind.Select, true, true },
        { "SELECT '#{x}' a, '${y}' b, \"#{q}\" c FROM EMP WHERE EMPNO = :EMPNO /* #{c} */ AND ENAME LIKE '\\#{k}'", ConvertStatementKind.Select, false, true },
        { "SELECT '#{x}' a, '${y}' b FROM EMP WHERE SAL < :hi AND ENAME <> '${z}' -- #{c}\n ORDER BY a", ConvertStatementKind.Select, true, false },
    };

    [Theory]
    [MemberData(nameof(RoundTripCases))]
    public void Evaluated_converted_statement_has_the_same_tokens_as_the_original(string sql, ConvertStatementKind kind, bool wrapWhere, bool useCdata)
    {
        ConvertBind[] binds =
        [
            new("EMPNO", BindDataType.Number, BindDirection.In, "7369"),
            new("ENAME", BindDataType.Varchar2, BindDirection.In, "SMITH"),
            new("LO", BindDataType.Number, BindDirection.In, "10"),
            new("HI", BindDataType.Number, BindDirection.In, "20"),
            new("SAL", BindDataType.Number, BindDirection.In, "800"),
            new("1", BindDataType.Number, BindDirection.In, "5"),
        ];
        var options = new ConvertOptions { WrapWhere = wrapWhere, UseCdata = useCdata };
        var converted = Conv(sql, kind, binds, options);

        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (name, v) in converted.ScenarioValues)
        {
            values[name] = v.Kind switch
            {
                MyBatisScenarioValueKind.Number => decimal.Parse(v.Text!, CultureInfo.InvariantCulture),
                MyBatisScenarioValueKind.Null => null,
                _ => v.Text,
            };
        }

        var element = new MyBatisElement
        {
            Kind = kind == ConvertStatementKind.Update ? MyBatisElementKind.Update : MyBatisElementKind.Select,
            Name = kind == ConvertStatementKind.Update ? "update" : "select",
            Id = converted.Id,
            Text = converted.ElementText,
        };
        var result = new MyBatisEvaluator().Evaluate(element, new NoFragments(), values, null);

        Assert.Empty(result.Issues);
        Assert.Equal(Shape(sql), Shape(result.Sql));
        if (wrapWhere && kind == ConvertStatementKind.Select) Assert.Contains("<where>", converted.ElementText);
    }

    // ---- 탭 4개 정답(통합 시험 MyBatisConvertTests 가 같은 파일로 Oracle 결과를 비교, CI 몫) ----

    [Fact]
    public void Four_tab_workspace_converts_to_the_golden_elements_and_first_scenarios()
    {
        var path = Path.Combine(RepoRoot(), "tests", "Oranda.Oracle.IntegrationTests", "MyBatis", "MyBatisConvertCases.json");
        var cases = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsArray();
        var tabs = cases.Select(c => new ConvertSourceTab((string)c!["tab"]!, (string)c["sql"]!, [.. c["binds"]!.AsArray()
            .Select(b => new ConvertBind((string)b!["name"]!, Enum.Parse<BindDataType>((string)b["type"]!), BindDirection.In, (string)b["value"]!))])).ToList();

        var candidates = ConvertEngine.Analyze(tabs, []);

        Assert.Equal(4, candidates.Count);
        foreach (var (c, expected) in candidates.Zip(cases))
        {
            var r = ConvertEngine.Convert(c, c.Kind!.Value, c.SuggestedId, tabs[c.TabIndex].Binds, new ConvertOptions(), null);
            Assert.Equal((string)expected!["id"]!, r.Id);
            Assert.Equal((string)expected["kind"]!, r.Kind.ToString());
            Assert.Equal((string)expected["element"]!, r.ElementText);
            Assert.Equal(
                expected["scenario"]!.AsObject().Select(kv => (kv.Key, (string)kv.Value!["kind"]!, (string?)kv.Value["text"])),
                r.ScenarioValues.Select(kv => (kv.Key, kv.Value.Kind.ToString(), kv.Value.Text)));
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Oranda.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Oranda.sln 을 찾지 못했다");
    }

    /// <summary>주석을 뺀 토큰 글 열. 바인드 이름은 소문자·위치 바인드는 p 접두로 맞춘다.</summary>
    private static List<string> Shape(string sql)
    {
        var shape = new List<string>();
        foreach (var t in SqlLexer.Tokenize(sql))
        {
            if (t.Kind is TokenKind.Comment) continue;
            string text = sql.Substring(t.Start, t.Length);
            if (t.Kind == TokenKind.Bind)
            {
                string name = text[1..].ToLowerInvariant();
                text = ":" + (char.IsAsciiDigit(name[0]) ? "p" : "") + name;
            }

            shape.Add(text);
        }

        return shape;
    }

    private sealed class NoFragments : IMyBatisFragmentResolver
    {
        public MyBatisFragment? Resolve(string refid, string? currentNamespace) => null;
    }

    /// <summary>시험용 포매터: 대문자로 바꾸고(바인드 이름 포함) FROM 앞에서 줄을 바꾼다. <c>fail</c> 이면 실패를 돌려준다.</summary>
    private sealed class UpperFormatter(bool fail = false) : ISqlFormatter
    {
        public FormatResult Format(string text, FormatterOptions o, TextRange? range = null) =>
            fail
                ? new FormatResult(text, default, FormatFailure.TokenMismatch)
                : new FormatResult(text.ToUpperInvariant().Replace(" FROM", "\n  FROM", StringComparison.Ordinal) + "\n", default);
    }
}
