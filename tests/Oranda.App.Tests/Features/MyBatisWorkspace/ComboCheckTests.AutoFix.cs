using Oranda.App.Features.MyBatisWorkspace.Combos;

namespace Oranda.App.Tests.Features.MyBatisWorkspace;

/// <summary>자동 고침 엔진(<see cref="MyBatisAutoFix"/>) 시험: SET·WHERE 감싸기, 제외 조건, 멱등, 줄바꿈 유지.</summary>
public partial class ComboCheckTests
{
    private const string UpdateEmpSal = """
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

    [Fact]
    public void Wrap_set_turns_set_keyword_into_set_tag()
    {
        var proposals = MyBatisAutoFix.Find(UpdateEmpSal);

        var proposal = Assert.Single(proposals);
        Assert.Equal(AutoFixKind.WrapSet, proposal.Kind);
        Assert.Equal(3, proposal.FirstLine);
        Assert.Equal(9, proposal.LastLine);
        Assert.Equal(
            """
            <update id="updateEmpSal" parameterType="map">
              UPDATE EMP
                 <set>
              <if test="sal != null">
                   SAL = #{sal},
              </if>
              <if test="comm != null">
                   COMM = #{comm}
              </if>
                 </set>
               WHERE EMPNO = #{empNo}
            </update>
            """,
            MyBatisAutoFix.Apply(UpdateEmpSal, proposal));
    }

    [Fact]
    public void Wrap_where_closes_before_following_order_by()
    {
        const string text = """
            <select id="selectEmp" resultType="map">
              SELECT * FROM EMP
               WHERE
              <if test="a != null">AND A = #{a}</if>
              <if test="b != null">AND B = #{b}</if>
               ORDER BY ID
            </select>
            """;

        var proposal = Assert.Single(MyBatisAutoFix.Find(text));

        Assert.Equal(AutoFixKind.WrapWhere, proposal.Kind);
        Assert.Equal(
            """
            <select id="selectEmp" resultType="map">
              SELECT * FROM EMP
               <where>
              <if test="a != null">AND A = #{a}</if>
              <if test="b != null">AND B = #{b}</if>
               </where>
               ORDER BY ID
            </select>
            """,
            MyBatisAutoFix.Apply(text, proposal));
    }

    [Fact]
    public void Statement_with_set_and_where_gets_two_proposals_in_source_order()
    {
        const string text = """
            <update id="u">
              UPDATE EMP
                 SET
              <if test="sal != null">SAL = #{sal}</if>
               WHERE
              <if test="empNo != null">EMPNO = #{empNo}</if>
            </update>
            """;

        var proposals = MyBatisAutoFix.Find(text);

        Assert.Equal([AutoFixKind.WrapSet, AutoFixKind.WrapWhere], proposals.Select(p => p.Kind).ToArray());
        Assert.True(proposals[0].Range.End <= proposals[1].Range.Start);

        var afterSet = MyBatisAutoFix.Apply(text, proposals[0]);
        var rest = Assert.Single(MyBatisAutoFix.Find(afterSet));
        Assert.Equal(AutoFixKind.WrapWhere, rest.Kind);
        var both = MyBatisAutoFix.Apply(afterSet, rest);
        Assert.Contains("<set>", both, StringComparison.Ordinal);
        Assert.Contains("</where>", both, StringComparison.Ordinal);
        Assert.Empty(MyBatisAutoFix.Find(both));
    }

    [Fact]
    public void Where_one_equals_one_is_not_proposed()
    {
        const string text = """
            <select id="s">
              SELECT * FROM EMP WHERE 1=1
              <if test="a != null">AND A = #{a}</if>
            </select>
            """;

        Assert.Empty(MyBatisAutoFix.Find(text));
    }

    [Fact]
    public void Keyword_already_inside_where_tag_is_not_proposed()
    {
        const string text = """
            <select id="s">
              SELECT * FROM EMP
              <where>
                <if test="a != null">AND A = #{a}</if>
              </where>
            </select>
            """;

        Assert.Empty(MyBatisAutoFix.Find(text));
    }

    [Fact]
    public void Static_condition_after_blocks_prevents_proposal()
    {
        const string text = """
            <select id="s">
              SELECT * FROM EMP
               WHERE
              <if test="a != null">A = #{a}</if>
               AND B = 1
            </select>
            """;

        Assert.Empty(MyBatisAutoFix.Find(text));
    }

    [Theory]
    [InlineData("<update id=\"u\">\n  UPDATE EMP\n     SET\n  <if test=\"a != null\">A = #{a},</if>\n     UPDATED_AT = SYSDATE\n   WHERE EMPNO = #{empNo}\n</update>")]
    [InlineData("<select id=\"s\">\n  SELECT * FROM EMP\n   WHERE\n  <if test=\"a != null\">A = #{a} AND</if>\n   B = 1\n</select>")]
    [InlineData("<select id=\"s\">\n  SELECT * FROM EMP\n   WHERE\n  <if test=\"a != null\">A = #{a}</if>\n  <include refid=\"more\"/>\n</select>")]
    public void Static_text_or_include_after_blocks_prevents_proposal(string text)
    {
        // <set>·<where> 로 감싸면 뒤의 정적 열·조건·include 가 태그 밖에 남아 문장이 깨진다.
        Assert.Empty(MyBatisAutoFix.Find(text));
    }

    [Fact]
    public void Keyword_inside_sql_comment_is_not_proposed()
    {
        const string text = """
            <select id="s">
              SELECT * FROM EMP
              -- WHERE
              <if test="a != null">A = #{a}</if>
            </select>
            """;

        Assert.Empty(MyBatisAutoFix.Find(text));
    }

    [Fact]
    public void Broken_xml_yields_no_proposals_without_throwing()
    {
        const string mismatched = "<update id=\"u\">\n  UPDATE EMP SET\n  <if test=\"a != null\">\n    SAL = 1\n</update>";
        const string unterminated = "<update id=\"u\">\n  UPDATE EMP SET\n  <if test=\"a != null";

        Assert.Empty(MyBatisAutoFix.Find(mismatched));
        Assert.Empty(MyBatisAutoFix.Find(unterminated));
        Assert.Empty(MyBatisAutoFix.Find(string.Empty));
    }

    [Fact]
    public void Apply_is_idempotent_for_the_same_kind()
    {
        var proposal = Assert.Single(MyBatisAutoFix.Find(UpdateEmpSal));

        var fixedText = MyBatisAutoFix.Apply(UpdateEmpSal, proposal);

        Assert.DoesNotContain(MyBatisAutoFix.Find(fixedText), p => p.Kind == AutoFixKind.WrapSet);
        Assert.Empty(MyBatisAutoFix.Find(fixedText));
    }

    [Fact]
    public void Crlf_source_keeps_crlf_line_breaks()
    {
        // 원본 줄 끝은 체크아웃마다 다르다(윈도우 = CRLF). 바꾸기 대신 맞춘다.
        var text = UpdateEmpSal.ReplaceLineEndings("\r\n");

        var proposal = Assert.Single(MyBatisAutoFix.Find(text));
        var result = MyBatisAutoFix.Apply(text, proposal);

        Assert.Equal(3, proposal.FirstLine);
        Assert.Equal(9, proposal.LastLine);
        Assert.Contains("</if>\r\n     </set>\r\n", result, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", result.Replace("\r\n", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public void Angle_bracket_inside_attribute_value_does_not_break_tag_scan()
    {
        var text = """
            <update id="u">
              UPDATE EMP
                 SET
              <if test="sal &gt; 1 and name != 'a>b'">SAL = #{sal},</if>
              <if test='comm > 1'>COMM = #{comm}</if>
               WHERE EMPNO = #{empNo}
            </update>
            """.ReplaceLineEndings("\n");

        var proposal = Assert.Single(MyBatisAutoFix.Find(text));

        Assert.Equal(AutoFixKind.WrapSet, proposal.Kind);
        Assert.Equal(5, proposal.LastLine);
        var result = MyBatisAutoFix.Apply(text, proposal);
        Assert.Contains("<if test='comm > 1'>COMM = #{comm}</if>\n     </set>\n", result, StringComparison.Ordinal);
    }
}
