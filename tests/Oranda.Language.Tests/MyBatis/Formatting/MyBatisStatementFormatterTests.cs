using System.Reflection;
using Oranda.Core.Composition;
using Oranda.Core.Formatting;
using Oranda.Core.Text;
using Oranda.Language.MyBatis.Formatting;

namespace Oranda.Language.Tests.MyBatis.Formatting;

// MyBatis 문장 포매터. 골든은 기대 출력을 원시 문자열로 고정하고 모두 멱등을 검사한다.
public class MyBatisStatementFormatterTests
{
    static readonly FormatterOptions Default = new();

    static readonly FormatterOptions Lower = new() { Case = new CaseOptions { Keyword = LetterCase.Lower, Function = LetterCase.Lower, Identifier = LetterCase.Lower } };

    static readonly MyBatisStatementFormatter Formatter = new();

    static string Crlf(string lf) => lf.Replace("\n", "\r\n", StringComparison.Ordinal);

    /// <summary>성공해야 하고, 범위는 문장 전체이며, 결과를 다시 포맷해도 같아야 한다.</summary>
    static string Ok(string text, FormatterOptions? o = null)
    {
        o ??= Default;
        var r = Formatter.Format(text, o);
        Assert.True(r.Succeeded, $"{r.Failure}: {r.Message}");
        Assert.Equal(new TextRange(0, text.Length), r.Range);
        var again = Formatter.Format(r.Text, o);
        Assert.True(again.Succeeded, $"{again.Failure}: {again.Message}");
        Assert.Equal(r.Text, again.Text);
        return r.Text;
    }

    static void Golden(string input, string expected, FormatterOptions? o = null) => Assert.Equal(expected, Ok(input, o));

    static void Rejected(string input, FormatFailure failure, string? messagePart = null, FormatterOptions? o = null)
    {
        var r = Formatter.Format(input, o ?? Default);
        Assert.Equal(failure, r.Failure);
        Assert.False(r.Succeeded);
        Assert.Equal(input, r.Text);
        Assert.Equal(new TextRange(0, input.Length), r.Range);
        Assert.False(string.IsNullOrEmpty(r.Message));
        if (messagePart is not null) Assert.Contains(messagePart, r.Message, StringComparison.Ordinal);
    }

    // ---- 등록·계약 ----

    [Fact]
    public void Is_registered_as_the_statement_formatter_service()
    {
        var attr = typeof(MyBatisStatementFormatter).GetCustomAttribute<OrandaServiceAttribute>();
        Assert.NotNull(attr);
        Assert.Equal(typeof(IMyBatisStatementFormatter), attr.ServiceType);
        Assert.IsAssignableFrom<ISqlFormatter>(Formatter);
    }

    [Fact]
    public void Range_argument_is_ignored_and_the_whole_statement_is_formatted()
    {
        const string xml = "<select id=\"a\">\n    select a from tb\n</select>";
        var all = Formatter.Format(xml, Default);
        var ranged = Formatter.Format(xml, Default, new TextRange(20, 5));
        Assert.Equal(all.Text, ranged.Text);
        Assert.Equal(new TextRange(0, xml.Length), ranged.Range);
    }

    [Fact]
    public void Already_formatted_statement_succeeds_unchanged()
    {
        const string xml = """
            <select id="a">
                SELECT a
                FROM tb
            </select>
            """;
        var r = Formatter.Format(xml, Default);
        Assert.True(r.Succeeded);
        Assert.Equal(xml, r.Text);
    }

    [Theory]
    [InlineData("<select id=\"a\"></select>")]
    [InlineData("<select id=\"a\">\n    \n</select>")]
    [InlineData("<select id=\"a\"/>")]
    public void Statement_without_body_succeeds_unchanged(string xml)
    {
        var r = Formatter.Format(xml, Default);
        Assert.True(r.Succeeded, r.Message);
        Assert.Equal(xml, r.Text);
    }

    // ---- 골든: 동적 태그 ----

    const string WhereIfs = """
        <select id="a" resultType="map">
            select a.x, a.y from tb a
            <where>
              <if test="x != null">and a.x = #{x}</if>
              <if test="y != null">
                and a.y = #{y}
              </if>
            </where>
            order by a.x
        </select>
        """;

    [Fact]
    public void Where_with_one_line_and_multi_line_ifs()
    {
        Golden(WhereIfs, """
            <select id="a" resultType="map">
                SELECT a.x,
                    a.y
                FROM tb a
                    <where>
                    <if test="x != null">and a.x = #{x}</if>
                    <if test="y != null">
                    AND a.y = #{y}
                    </if>
                    </where>
                ORDER BY a.x
            </select>
            """);
    }

    [Fact]
    public void Where_with_ifs_in_the_house_profile()
    {
        Golden(WhereIfs, """
            <select id="a" resultType="map">
                SELECT A.X
                     , A.Y
                FROM   TB A
                       <where>
                       <if test="x != null">and a.x = #{x}</if>
                       <if test="y != null">
                       AND A.Y = #{y}
                       </if>
                       </where>
                ORDER BY A.X
            </select>
            """, FormatterOptions.OrandaDefault);
    }

    [Fact]
    public void Set_update_with_indented_mapper_close_tag()
    {
        // 닫는 태그가 2칸 들여 있으면 본문은 2 + 4 = 6칸에서 시작한다.
        Golden("""
            <update id="u">
                update tb
                <set>
                  <if test="a != null">a = #{a},</if>
                  <if test="b != null">b = #{b},</if>
                </set>
                where id = #{id}
              </update>
            """, """
            <update id="u">
                  UPDATE tb
                  <set>
                  <if test="a != null">a = #{a},</if>
                  <if test="b != null">b = #{b},</if>
                  </set>
                  WHERE id = #{id}
              </update>
            """);
    }

    [Fact]
    public void Set_update_in_the_house_profile()
    {
        Golden("""
            <update id="u">
                update tb
                <set>
                  <if test="a != null">a = #{a},</if>
                  <if test="b != null">
                    b = #{b},
                  </if>
                </set>
                where id = #{id};
            </update>
            """, """
            <update id="u">
                UPDATE TB
                       <set>
                       <if test="a != null">a = #{a},</if>
                       <if test="b != null">
                       B = #{b},
                       </if>
                       </set>
                WHERE  ID = #{id};
            </update>
            """, FormatterOptions.OrandaDefault);
    }

    [Fact]
    public void Trim_prefix_suffix_insert()
    {
        const string input = """
            <insert id="i">
                insert into tb
                <trim prefix="(" suffix=")" suffixOverrides=",">
                  <if test="a != null">a,</if>
                  <if test="b != null">b,</if>
                </trim>
                values
                <trim prefix="(" suffix=")" suffixOverrides=",">
                  <if test="a != null">#{a},</if>
                  <if test="b != null">#{b},</if>
                </trim>
            </insert>
            """;
        Golden(input, """
            <insert id="i">
                INSERT INTO tb
                <trim prefix="(" suffix=")" suffixOverrides=",">
                <if test="a != null">a,</if>
                <if test="b != null">b,</if>
                </trim>
                VALUES
                <trim prefix="(" suffix=")" suffixOverrides=",">
                <if test="a != null">#{a},</if>
                <if test="b != null">#{b},</if>
                </trim>
            </insert>
            """);
        Golden(input, """
            <insert id="i">
                INSERT INTO TB
                <trim prefix="(" suffix=")" suffixOverrides=",">
                <if test="a != null">a,</if>
                <if test="b != null">b,</if>
                </trim>
                VALUES
                <trim prefix="(" suffix=")" suffixOverrides=",">
                <if test="a != null">#{a},</if>
                <if test="b != null">#{b},</if>
                </trim>
            </insert>
            """, FormatterOptions.OrandaDefault);
    }

    const string ForeachChoose = """
        <select id="a">
            select * from tb where deptno in
            <foreach collection="ids" item="id" open="(" separator="," close=")">#{id}</foreach>
            and 1 = 1
            <choose>
              <when test="a != null">and a = #{a}</when>
              <otherwise>and rownum &lt;= 10</otherwise>
            </choose>
        </select>
        """;

    [Fact]
    public void Foreach_in_list_and_choose_when_otherwise()
    {
        Golden(ForeachChoose, """
            <select id="a">
                SELECT *
                FROM tb
                WHERE deptno IN
                    <foreach collection="ids" item="id" open="(" separator="," close=")">#{id}</foreach>
                    AND 1 = 1
                <choose>
                <when test="a != null">and a = #{a}</when>
                <otherwise>and rownum &lt;= 10</otherwise>
                </choose>
            </select>
            """);
    }

    [Fact]
    public void Foreach_in_list_and_choose_in_the_house_profile()
    {
        Golden(ForeachChoose, """
            <select id="a">
                SELECT *
                FROM   TB
                WHERE  DEPTNO IN
                <foreach collection="ids" item="id" open="(" separator="," close=")">#{id}</foreach>
                AND    1 = 1
                <choose>
                <when test="a != null">and a = #{a}</when>
                <otherwise>and rownum &lt;= 10</otherwise>
                </choose>
            </select>
            """, FormatterOptions.OrandaDefault);
    }

    [Fact]
    public void Multi_line_choose_with_nested_elements()
    {
        Golden("""
            <select id="a">
                select x from tb where 1=1
                <choose>
                  <when test="a != null">
                    and a = #{a}
                    <if test="b != null">and b = #{b}</if>
                  </when>
                  <otherwise>
                    and c = 1
                  </otherwise>
                </choose>
            </select>
            """, """
            <select id="a">
                SELECT x
                FROM tb
                WHERE 1 = 1
                    <choose>
                    <when test="a != null">
                    AND a = #{a}
                    <if test="b != null">and b = #{b}</if>
                    </when>
                    <otherwise>
                    AND c = 1
                    </otherwise>
                    </choose>
            </select>
            """);
    }

    [Fact]
    public void Nested_if_closing_tags_follow_their_opening_tags()
    {
        const string input = """
            <select id="a">
                select * from tb a
                <where>
                  <if test="y != null">
                    and a.y = #{y}
                    <if test="z != null">and a.z = #{z}</if>
                  </if>
                </where>
            </select>
            """;
        Golden(input, """
            <select id="a">
                SELECT *
                FROM tb a
                    <where>
                    <if test="y != null">
                    AND a.y = #{y}
                    <if test="z != null">and a.z = #{z}</if>
                    </if>
                    </where>
            </select>
            """);
    }

    [Fact]
    public void Foreach_over_several_lines()
    {
        Golden("""
            <select id="a">
                select x from tb
                where
                <foreach collection="l" item="i" separator=" or ">
                  (a = #{i.a}
                   and b = #{i.b})
                </foreach>
            </select>
            """, """
            <select id="a">
                SELECT x
                FROM tb
                WHERE
                    <foreach collection="l" item="i" separator=" or ">
                    (a = #{i.a} AND b = #{i.b})
                    </foreach>
            </select>
            """);
    }

    [Fact]
    public void Statement_wrapped_in_a_dynamic_element()
    {
        Golden("""
            <select id="a">
                <if test="x != null">
                select 1 from dual
                </if>
            </select>
            """, """
            <select id="a">
                <if test="x != null">
                SELECT 1
                FROM dual
                </if>
            </select>
            """);
    }

    // ---- 골든: 붙은 경계 ----

    [Fact]
    public void Tags_glued_to_neighbouring_text_stay_glued()
    {
        const string input = """
            <select id="a">
                select a.x from tb a where 1=1
                <if test="a != null">AND a.x = #{a}
                    AND a.y = #{y}</if>
                <if test="b != null">and a.b = 1
                </if>
                <if test="c != null">
                  and a.c = #{c}</if>
                and a.d = 2
            </select>
            """;
        Golden(input, """
            <select id="a">
                SELECT a.x
                FROM tb a
                WHERE 1 = 1
                    <if test="a != null">AND a.x = #{a}
                    AND a.y = #{y}</if>
                    <if test="b != null">AND a.b = 1
                    </if>
                    <if test="c != null">
                    AND a.c = #{c}</if>
                    AND a.d = 2
            </select>
            """);
        Golden(input, """
            <select id="a">
                SELECT A.X
                FROM   TB A
                WHERE  1 = 1
                <if test="a != null">AND    A.X = #{a}
                AND    A.Y = #{y}</if>
                <if test="b != null">AND    A.B = 1
                </if>
                <if test="c != null">
                AND    A.C = #{c}</if>
                AND    A.D = 2
            </select>
            """, FormatterOptions.OrandaDefault);
    }

    const string WhereNewline = "<select id=\"whereNewline\" resultType=\"map\">\n    SELECT * FROM EMP\n    <where>\n      <if test=\"a != null\">AND\n        A = #{a}</if>\n      <if test=\"b != null\">OR\tB = #{b}</if>\n    </where>\n</select>";

    [Fact]
    public void Multi_line_if_that_collapses_to_one_line_keeps_its_tags()
    {
        // 여는 태그 뒤에 SQL 이 붙고 닫는 태그가 SQL 에 붙은 여러 줄 if 는 포맷 뒤 한 줄이 될 수 있다.
        Golden(WhereNewline, "<select id=\"whereNewline\" resultType=\"map\">\n    SELECT *\n    FROM EMP\n    <where>\n    <if test=\"a != null\">AND A = #{a}</if>\n    <if test=\"b != null\">OR\tB = #{b}</if>\n    </where>\n</select>");
        Golden(WhereNewline, "<select id=\"whereNewline\" resultType=\"map\">\n    SELECT *\n    FROM   EMP\n    <where>\n    <if test=\"a != null\">AND A = #{a}</if>\n    <if test=\"b != null\">OR\tB = #{b}</if>\n    </where>\n</select>", FormatterOptions.OrandaDefault);
    }

    [Fact]
    public void Open_tag_glued_to_sql_over_several_lines_in_other_elements()
    {
        Golden("""
            <update id="u">
                update tb
                <set>
                  <if test="a != null">a =
                    #{a},</if>
                  <if test="b != null">b = #{b},
                    c = #{c},</if>
                </set>
                where id = #{id}
            </update>
            """, """
            <update id="u">
                UPDATE tb
                <set>
                <if test="a != null">a = #{a},</if>
                <if test="b != null">b = #{b}, c = #{c},</if>
                </set>
                WHERE id = #{id}
            </update>
            """);
        Golden("""
            <select id="a">
                select x from tb where deptno in
                <foreach collection="ids" item="id" open="(" separator=",">#{id}
                  </foreach>
                and y = 1
            </select>
            """, """
            <select id="a">
                SELECT x
                FROM tb
                WHERE deptno IN
                    <foreach collection="ids" item="id" open="(" separator=",">#{id}
                    </foreach>
                    AND y = 1
            </select>
            """);
    }

    // ---- 골든: 구간 종류 ----

    const string IncludeBindEntities = """
        <select id="a">
            <bind name="pat" value="'%' + x + '%'"/>
            select <include refid="cols"/>, b from tb_${x} where c = 'a &amp; b' and d &gt;= #{d} and e like '%${kw}%'
            order by ${col} ${dir}
        </select>
        """;

    [Fact]
    public void Leading_bind_include_in_select_list_entities_and_substitutions()
    {
        Golden(IncludeBindEntities, """
            <select id="a">
                <bind name="pat" value="'%' + x + '%'"/>
                SELECT <include refid="cols"/>, b
                FROM tb_${x}
                WHERE c = 'a &amp; b'
                    AND d &gt;= #{d}
                    AND e LIKE '%${kw}%'
                ORDER BY ${col} ${dir}
            </select>
            """);
    }

    [Fact]
    public void Placeholders_follow_the_identifier_case_of_the_profile()
    {
        // 식별자가 대문자인 프로필에서 tb_${x} 는 TB_${x} 가 되고, 치환·바인드 철자는 원문 그대로다.
        Golden(IncludeBindEntities, """
            <select id="a">
                <bind name="pat" value="'%' + x + '%'"/>
                SELECT <include refid="cols"/>, B
                FROM   TB_${x}
                WHERE  C = 'a &amp; b'
                AND    D &gt;= #{d}
                AND    E LIKE '%${kw}%'
                ORDER BY ${col} ${dir}
            </select>
            """, FormatterOptions.OrandaDefault);
    }

    [Fact]
    public void Lower_case_profile_with_schema_substitution_in_an_indented_mapper()
    {
        // 닫는 태그가 4칸 들여 있으면 본문은 4 + 4 = 8칸에서 시작한다.
        const string input = """
            <select id="a">
                    select a.x, b.y from ${schema}.t a, tb_${x} b where a.id = b.id
                    <if test="x != null">and a.x = #{x}</if>
                    order by ${col} ${dir}
                </select>
            """;
        Golden(input, """
            <select id="a">
                    select a.x,
                        b.y
                    from ${schema}.t a, tb_${x} b
                    where a.id = b.id
                    <if test="x != null">and a.x = #{x}</if>
                    order by ${col} ${dir}
                </select>
            """, Lower);
        Golden(input, """
            <select id="a">
                    SELECT A.X
                         , B.Y
                    FROM   ${schema}.T A
                         , TB_${x} B
                    WHERE  A.ID = B.ID
                    <if test="x != null">and a.x = #{x}</if>
                    ORDER BY ${col} ${dir}
                </select>
            """, FormatterOptions.OrandaDefault);
    }

    [Fact]
    public void Tabs_indent_is_added_after_the_close_tag_indent()
    {
        Golden("""
            <select id="a">
                    select a.x, b.y from ${schema}.t a, tb_${x} b where a.id = b.id
                    <if test="x != null">and a.x = #{x}</if>
                    order by ${col} ${dir}
                </select>
            """, "<select id=\"a\">\n    \tSELECT a.x,\n    \t\tb.y\n    \tFROM ${schema}.t a, tb_${x} b\n    \tWHERE a.id = b.id\n    \t<if test=\"x != null\">and a.x = #{x}</if>\n    \tORDER BY ${col} ${dir}\n    </select>",
            new FormatterOptions { Indent = new IndentOptions { UseTabs = true } });
    }

    [Fact]
    public void Cdata_xml_comment_line_comment_and_hint()
    {
        const string input = """
            <select id="a">
                select a from tb where x <![CDATA[ < ]]> #{x} -- comment
                /* c */ and y = 1 /*+ hint */
                <!-- xml comment -->
                and z = 2
            </select>
            """;
        Golden(input, """
            <select id="a">
                SELECT a
                FROM tb
                WHERE x <![CDATA[ < ]]> #{x} -- comment
                    /* c */ AND y = 1 /*+ hint */
                    <!-- xml comment -->
                    AND z = 2
            </select>
            """);
        Golden(input, """
            <select id="a">
                SELECT A
                FROM   TB
                WHERE  X <![CDATA[ < ]]> #{x} -- comment
                /* c */
                AND    Y = 1 /*+ hint */
                <!-- xml comment -->
                AND    Z = 2
            </select>
            """, FormatterOptions.OrandaDefault);
    }

    [Fact]
    public void Line_comment_may_hold_a_bind_expression()
    {
        Golden("""
            <select id="a">
                select a.x -- 코드 #{x} 주석
                  , a.y
                from tb a where a.z = #{z, jdbcType=VARCHAR} and a.w in ('a', 'b')
            </select>
            """, """
            <select id="a">
                SELECT a.x -- 코드 #{x} 주석
                    , a.y
                FROM tb a
                WHERE a.z = #{z, jdbcType=VARCHAR}
                    AND a.w IN ('a', 'b')
            </select>
            """);
    }

    [Fact]
    public void SelectKey_is_kept_verbatim()
    {
        Golden("""
            <insert id="i">
                <selectKey keyProperty="id" resultType="long" order="BEFORE">
                    select seq.nextval from dual
                </selectKey>
                insert into tb (id, a) values (#{id}, #{a})
            </insert>
            """, """
            <insert id="i">
                <selectKey keyProperty="id" resultType="long" order="BEFORE">
                    select seq.nextval from dual
                </selectKey>
                INSERT INTO tb (id, a)
                VALUES (#{id}, #{a})
            </insert>
            """);
    }

    [Fact]
    public void Sql_fragment_with_include()
    {
        Golden("<sql id=\"s\">a.x, a.y, <include refid=\"z\"/></sql>", """
            <sql id="s">
                a.x, a.y, <include refid="z"/>
            </sql>
            """);
        Golden("<sql id=\"s\">a.x, a.y, <include refid=\"z\"/></sql>", """
            <sql id="s">
                A.X, A.Y, <include refid="z"/>
            </sql>
            """, FormatterOptions.OrandaDefault);
    }

    [Fact]
    public void Group_by_having_with_substitution_and_bind_in_a_condition()
    {
        Golden("""
            <select id="a">
                select a from tb a, tb b where a.id = b.id and (a.x = #{x} or a.y = ${y}) group by a.x having count(*) > 1
            </select>
            """, """
            <select id="a">
                SELECT a
                FROM tb a, tb b
                WHERE a.id = b.id
                    AND (a.x = #{x} OR a.y = ${y})
                GROUP BY a.x
                HAVING COUNT(*) > 1
            </select>
            """);
    }

    // ---- 골든: 줄바꿈·옵션 ----

    [Fact]
    public void Crlf_is_kept()
    {
        string input = Crlf("""
            <select id="a">
                select a.x, a.y from tb a
                <where>
                  <if test="x != null">
                    and a.x = #{x}
                  </if>
                </where>
                order by a.x -- done
            </select>
            """);
        Golden(input, Crlf("""
            <select id="a">
                SELECT a.x,
                    a.y
                FROM tb a
                    <where>
                    <if test="x != null">
                    AND a.x = #{x}
                    </if>
                    </where>
                ORDER BY a.x -- done
            </select>
            """));
        Golden(input, Crlf("""
            <select id="a">
                SELECT A.X
                     , A.Y
                FROM   TB A
                       <where>
                       <if test="x != null">
                       AND A.X = #{x}
                       </if>
                       </where>
                ORDER BY A.X -- done
            </select>
            """), FormatterOptions.OrandaDefault);
    }

    [Fact]
    public void Statement_terminator_add_is_ignored_and_remove_keeps_the_semicolon()
    {
        const string input = """
            <select id="a">
                select <include refid="cols"/>, t.x
                  from tb t where t.k = #{k}
            </select>
            """;
        const string expected = """
            <select id="a">
                SELECT <include refid="cols"/>, t.x
                FROM tb t
                WHERE t.k = #{k}
            </select>
            """;
        Golden(input, expected, new FormatterOptions { StatementTerminator = StatementTerminator.Add });
        Golden(input.Replace("#{k}", "#{k};", StringComparison.Ordinal), expected.Replace("#{k}", "#{k};", StringComparison.Ordinal),
            new FormatterOptions { StatementTerminator = StatementTerminator.Remove });
        Golden(input.Replace("#{k}", "#{k};", StringComparison.Ordinal), expected.Replace("#{k}", "#{k};", StringComparison.Ordinal),
            new FormatterOptions { StatementTerminator = StatementTerminator.Add });
    }

    [Fact]
    public void Leading_whitespace_before_the_open_tag_and_trailing_newline_are_kept()
    {
        Golden("  <select id=\"a\">select a from tb</select>\n", "  <select id=\"a\">\n    SELECT a\n    FROM tb\n</select>\n");
    }

    [Fact]
    public void Leading_comma_stays_attached_when_a_closing_tag_follows_it()
    {
        const string input = """
            <update id="updateEmpSal" parameterType="map">
              UPDATE EMP
                 SET
              <if test="sal != null">
                   SAL = #{sal},
              </if>
              <if test="comm != null">
                   COMM = #{comm}
              </if>
               WHERE EMPNO = #{empNo}
            </update>
            """;
        Golden(input, """
            <update id="updateEmpSal" parameterType="map">
                UPDATE EMP
                SET
                    <if test="sal != null">
                    SAL = #{sal},
                    </if>
                    <if test="comm != null">
                    COMM = #{comm}
                    </if>
                WHERE EMPNO = #{empNo}
            </update>
            """);
        Golden(input, """
            <update id="updateEmpSal" parameterType="map">
                UPDATE EMP
                SET
                       <if test="sal != null">
                       SAL = #{sal},
                       </if>
                       <if test="comm != null">
                       COMM = #{comm}
                       </if>
                WHERE  EMPNO = #{empNo}
            </update>
            """, FormatterOptions.OrandaDefault);
    }

    [Fact]
    public void Multi_line_select_key_keeps_its_original_text_and_column()
    {
        // 여러 줄 통째 구간은 안쪽 글을 바꾸지 않고, 시작 줄도 원문 칸에 둔다(안쪽 줄과 칸이 맞는다).
        Golden("""
            <insert id="i">
              <selectKey keyProperty="id" resultType="long" order="BEFORE">
                select seq.nextval
                  from dual
              </selectKey>
              insert into tb (id, a) values (#{id}, #{a})
            </insert>
            """, """
            <insert id="i">
              <selectKey keyProperty="id" resultType="long" order="BEFORE">
                select seq.nextval
                  from dual
              </selectKey>
                INSERT INTO tb (id, a)
                VALUES (#{id}, #{a})
            </insert>
            """);
    }

    [Fact]
    public void Multi_line_xml_comment_with_tab_indented_sql_is_kept_verbatim()
    {
        // 탭으로 들여쓴 SQL 을 주석 처리한 여러 줄 XML 주석이 SELECT 목록 가운데 있다. 주석 안 글은 한 글자도 바뀌지 않는다.
        const string comment = "<!--\n\t\t\t   (select min(PROC_CD)\n\t\t\t\t  from TB_A\n\t\t\t\t where A_NO = I.A_NO) AS A_CD1, -->";
        string input = "<select id=\"a\">\n\tselect (select max(B.PROC_CD)\n\t\t\t  from TB_A B\n\t\t\t where B.A_NO = I.A_NO) AS B_CD,\n\t\t\t"
            + comment + "\n\t\t\t   NVL(I.C, 'x') AS C\n\t  from TB_I I\n\t</select>";

        string output = Ok(input);

        Assert.NotEqual(input, output);
        Assert.Contains("\n\t\t\t" + comment + "\n", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Xml_comment_inside_a_sql_line_comment_is_kept()
    {
        // 「--<!-- … -->」 주석 처리 관례: 줄 주석은 글자 그대로라 안의 XML 주석도 원문 그대로 남는다.
        string output = Ok("<select id=\"a\">\n  select a from tb\n   where b = 1\n     --<!-- and c is not null -->\n     and d = #{d}\n</select>");

        Assert.Contains("--<!-- and c is not null -->\n", output, StringComparison.Ordinal);
        Assert.Contains("#{d}", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Xml_comment_inside_a_sql_block_comment_is_refused()
    {
        Rejected("<select id=\"a\">\n  select a /* x <!-- y --> z */ from tb\n</select>", FormatFailure.MarkupUnsupported);
    }

    const string InForeach = """
        <select id="a">
            select a, b from tb where a in (
              <foreach collection="ids" item="id" separator=",">
                #{id}
              </foreach>
            )
        </select>
        """;

    [Fact]
    public void Multi_line_foreach_between_in_list_parentheses()
    {
        Golden(InForeach, """
            <select id="a">
                SELECT a,
                    b
                FROM tb
                WHERE a IN (
                    <foreach collection="ids" item="id" separator=",">
                    #{id}
                    </foreach>
                    )
            </select>
            """);
        Golden(InForeach, """
            <select id="a">
                SELECT A
                     , B
                FROM   TB
                WHERE  A IN (
                       <foreach collection="ids" item="id" separator=",">
                       #{id}
                       </foreach>
                       )
            </select>
            """, FormatterOptions.OrandaDefault);
    }

    [Fact]
    public void Tag_after_an_open_parenthesis_keeps_the_space_it_had()
    {
        Golden("<select id=\"a\">select a from tb where a in ( <if test=\"x\">b = 1\n</if> )</select>", """
            <select id="a">
                SELECT a
                FROM tb
                WHERE a IN ( <if test="x">b = 1
                    </if> )
            </select>
            """);
    }

    [Fact]
    public void Optimizer_hint_and_numeric_entities()
    {
        const string input = """
            <select id="a">
                select /*+ INDEX(a ix) */ a.x from tb a where a.y &lt; #{y} and a.z &gt;&#61; 1 and a.k = &#39;x&#39;
            </select>
            """;
        Golden(input, """
            <select id="a">
                SELECT /*+ INDEX(a ix) */ a.x
                FROM tb a
                WHERE a.y &lt; #{y}
                    AND a.z &gt;&#61; 1
                    AND a.k = &#39;x&#39;
            </select>
            """);
        Golden(input, """
            <select id="a">
                SELECT /*+ INDEX(a ix) */ A.X
                FROM   TB A
                WHERE  A.Y &lt; #{y}
                AND    A.Z &gt;&#61; 1
                AND    A.K = &#39;x&#39;
            </select>
            """, FormatterOptions.OrandaDefault);
    }

    [Fact]
    public void Character_outside_the_bmp_next_to_code_is_kept()
    {
        Golden("<select id=\"a\">select a from tb where a = 1 \U0001F600</select>", "<select id=\"a\">\n    SELECT a FROM tb WHERE a = 1 \U0001F600\n</select>");
    }

    [Fact]
    public void Lone_surrogate_detection()
    {
        Assert.False(MyBatisStatementFormatter.HasLoneSurrogate("a \U0001F600 b"));
        Assert.True(MyBatisStatementFormatter.HasLoneSurrogate("a \uD83D \uDE00 b"));
        Assert.True(MyBatisStatementFormatter.HasLoneSurrogate("\uDE00\uD83D"));
        Assert.True(MyBatisStatementFormatter.HasLoneSurrogate("a\uD83D"));
    }

    // ---- 실패: 원문 그대로 + 사유 ----

    [Fact]
    public void Tag_that_splits_a_word_is_rejected() =>
        Rejected("<select id=\"a\">\n    select * from tb_<if test=\"x\">a</if>\n</select>", FormatFailure.MarkupUnsupported, "낱말을 가르는");

    [Fact]
    public void Unclosed_tag_is_rejected() =>
        Rejected("<select id=\"a\">\n    select * from tb <if test=\"x\">where b = 1\n</select>", FormatFailure.MarkupUnsupported, "닫히지 않");

    [Fact]
    public void Unclosed_attribute_quote_is_rejected() =>
        Rejected("<select id=\"a\">\n    select * from tb <if test=\"x>where b = 1</if>\n</select>", FormatFailure.MarkupUnsupported, "따옴표");

    [Fact]
    public void Existing_marker_is_rejected() =>
        Rejected("<select id=\"a\">\n    select * from tb where b = 1 /*@1*/\n</select>", FormatFailure.MarkupUnsupported, "/*@");

    [Fact]
    public void Multi_line_bind_is_rejected() =>
        Rejected("<select id=\"a\">\n    select * from tb where b = #{b\n    }\n</select>", FormatFailure.MarkupUnsupported, "여러 줄");

    [Fact]
    public void Entity_that_leaves_an_unclosed_string_is_rejected() =>
        Rejected("<select id=\"a\">\n    select * from tb where b = 'it&apos;s'\n</select>", FormatFailure.MarkupUnsupported, "닫히지 않은 문자열");

    [Fact]
    public void Tag_inside_a_string_is_rejected() =>
        Rejected("<select id=\"a\">\n    select * from tb where x = '<if test=\"y\">a</if>'\n</select>", FormatFailure.MarkupUnsupported, "문자열");

    [Fact]
    public void Tag_inside_a_line_comment_is_rejected() =>
        Rejected("<select id=\"a\">\n    select * from tb -- <if test=\"y\">x</if>\n    where x = 1\n</select>", FormatFailure.MarkupUnsupported, "주석");

    [Theory]
    [InlineData("<select id=\"a\">select a from tb where a = 1 /&#42;@99999999999*/</select>")]
    [InlineData("<select id=\"a\">select a from tb where a = 1 /&#42;@1*/ and <if test=\"x\">b = 1</if></select>")]
    public void Marker_built_from_an_entity_is_rejected(string xml) => Rejected(xml, FormatFailure.MarkupUnsupported, "/*@");

    [Theory]
    [InlineData("<select id=\"a\">\n    select * from tb where c = 1 </when>\n</select>", "짝이 맞지 않는")]
    [InlineData("<select id=\"a\">\n    select * from tb <!-- never closed\n</select>", "XML 주석")]
    [InlineData("<select id=\"a\">\n    select * from tb <![CDATA[ x\n</select>", "CDATA")]
    [InlineData("<select id=\"a\">\n    select * from tb where c = 1 and d &foo; 2\n</select>", "엔티티")]
    [InlineData("<select id=\"a\">\n    select * from tb where c < 1\n</select>", "'<'")]
    public void Other_unsupported_shapes_are_rejected(string xml, string part) =>
        Rejected(xml, FormatFailure.MarkupUnsupported, part);

    [Theory]
    [InlineData("select * from tb")]
    [InlineData("<select id=\"a\">\n    select * from tb\n")]
    [InlineData("<select id=\"a\"\n    select * from tb\n</select>")]
    [InlineData("<select id=\"a\">select * from tb</select> select 1")]
    public void Malformed_outer_element_is_rejected(string xml) => Rejected(xml, FormatFailure.MarkupUnsupported);

    [Fact]
    public void Failure_keeps_the_original_even_when_the_profile_would_change_it() =>
        Rejected("<select id=\"a\">\n    select * from tb_<if test=\"x\">a</if>\n</select>", FormatFailure.MarkupUnsupported, null, FormatterOptions.OrandaDefault);

    // ---- 검사(c)(d) ----

    [Fact]
    public void Markup_check_accepts_the_same_sequence()
    {
        const string before = "a <if test=\"x\">\n  b\n</if> c";
        string after = "a <if test=\"x\">\n    B\n    </if> c";
        var segs = MyBatisBodyScanner.Scan(before);
        Assert.Null(MyBatisStatementFormatter.CheckMarkup(before, segs, after));
    }

    [Theory]
    [InlineData("a <if test=\"y\">\n  b\n</if> c", "XML 구간 1번째")]
    [InlineData("a <if test=\"x\">\n  b\n</unless> c", "짝이 맞지 않는")]
    [InlineData("a <if test=\"x\">\n  b <!-- n -->\n</if> c", "XML 구간 수")]
    public void Markup_check_reports_a_changed_sequence(string after, string part)
    {
        const string before = "a <if test=\"x\">\n  b\n</if> c";
        var msg = MyBatisStatementFormatter.CheckMarkup(before, MyBatisBodyScanner.Scan(before), after);
        Assert.NotNull(msg);
        Assert.Contains(part, msg, StringComparison.Ordinal);
    }

    [Fact]
    public void Markup_check_reports_a_glued_boundary_that_came_apart()
    {
        const string before = "a = #{y}<if test=\"x\">\n  b\n</if>";
        const string after = "a = #{y} <if test=\"x\">\n  b\n</if>";
        var msg = MyBatisStatementFormatter.CheckMarkup(before, MyBatisBodyScanner.Scan(before), after);
        Assert.NotNull(msg);
        Assert.Contains("붙은 경계", msg, StringComparison.Ordinal);
    }

    [Fact]
    public void Markup_check_reports_a_separated_boundary_that_became_glued()
    {
        const string before = "a = ( <if test=\"x\">\n  b\n</if>";
        const string after = "a = (<if test=\"x\">\n  b\n</if>";
        var msg = MyBatisStatementFormatter.CheckMarkup(before, MyBatisBodyScanner.Scan(before), after);
        Assert.NotNull(msg);
        Assert.Contains("붙은 경계", msg, StringComparison.Ordinal);
    }
}
