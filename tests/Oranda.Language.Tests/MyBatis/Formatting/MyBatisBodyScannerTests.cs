using Oranda.Language.MyBatis.Formatting;

namespace Oranda.Language.Tests.MyBatis.Formatting;

// MyBatis 문장 본문 스캐너: 구간 나누기·통째 구간·짝 맞추기·포맷 불가 모양.
public class MyBatisBodyScannerTests
{
    static List<BodySegment> Segments(string body)
    {
        var scan = MyBatisBodyScanner.Scan(body);
        Assert.True(scan.Ok, scan.Error);
        return [.. scan.Segments];
    }

    static string Kinds(string body) => string.Join(' ', Segments(body).Select(s => s.Kind.ToString()));

    static string Error(string body)
    {
        var scan = MyBatisBodyScanner.Scan(body);
        Assert.False(scan.Ok, "읽을 수 없는 모양인데 스캔이 성공했다.");
        return scan.Error!;
    }

    [Fact]
    public void Plain_text_is_one_text_segment()
    {
        var s = Segments("SELECT 1 FROM dual");
        var only = Assert.Single(s);
        Assert.Equal(BodySegmentKind.Text, only.Kind);
        Assert.Equal(0, only.Start);
        Assert.Equal(18, only.Length);
    }

    [Fact]
    public void Segments_cover_the_body_without_gaps()
    {
        const string body = "a <if test=\"x\">\n b #{c} &lt; ${d}\n</if> e <!-- z --> <include refid=\"i\"/>";
        int pos = 0;
        foreach (var s in Segments(body))
        {
            Assert.Equal(pos, s.Start);
            pos = s.End;
        }

        Assert.Equal(body.Length, pos);
    }

    [Fact]
    public void Params_and_entities_are_recognized_in_text()
    {
        var s = Segments("a = #{x,jdbcType=DATE} and b = ${y} and c &lt; 1 and d &gt;= 2 and e = '&amp;' &quot;&apos;");
        Assert.Equal(
            "Text Param Text Param Text Entity Text Entity Text Entity Text Entity Entity",
            string.Join(' ', s.Select(x => x.Kind.ToString())));
        Assert.Equal('#', s[1].Decoded);
        Assert.Equal('$', s[3].Decoded);
        Assert.Equal(['<', '>', '&', '"', '\''], s.Where(x => x.Kind == BodySegmentKind.Entity).Select(x => x.Decoded));
    }

    [Fact]
    public void Numeric_entity_is_decoded_when_it_is_a_single_bmp_character()
    {
        var s = Segments("a &#60; b &#x3E; c");
        Assert.Equal(['<', '>'], s.Where(x => x.Kind == BodySegmentKind.Entity).Select(x => x.Decoded));
    }

    [Fact]
    public void Cdata_hides_tags_and_entities_but_not_params()
    {
        const string body = "x <![CDATA[ a < b <if> &lt; #{p} ${q} ]]> y";
        Assert.Equal("Text CdataStart Text Param Text Param Text CdataEnd Text", Kinds(body));
        var s = Segments(body);
        Assert.Equal(s.Count - 2, s[1].Pair);
        Assert.Equal(1, s[^2].Pair);
        Assert.Equal(" a < b <if> &lt; ", body.Substring(s[2].Start, s[2].Length));
    }

    [Fact]
    public void Tags_are_classified_and_paired()
    {
        const string body = "a\n<where>\n  <if test=\"x\">\n    b\n  </if>\n  <include refid=\"i\"/>\n</where>\n<!-- c -->";
        var s = Segments(body);
        Assert.Equal("Text OpenTag Text OpenTag Text CloseTag Text EmptyTag Text CloseTag Text XmlComment", string.Join(' ', s.Select(x => x.Kind.ToString())));
        Assert.Equal(9, s[1].Pair);
        Assert.Equal(1, s[9].Pair);
        Assert.Equal(5, s[3].Pair);
        Assert.Equal(3, s[5].Pair);
        Assert.Equal("where", s[1].Name);
        Assert.Equal("include", s[7].Name);
    }

    [Fact]
    public void Quote_in_attribute_may_hold_a_greater_than_sign()
    {
        const string body = "<if test=\"ids != null and ids.size() > 0 and a == 'x>y'\">\n  a\n</if>";
        Assert.Equal("OpenTag Text CloseTag", Kinds(body));
    }

    [Fact]
    public void One_line_dynamic_element_is_one_whole_segment()
    {
        const string body = "a\n<if test=\"x\">AND b = #{b}</if>\nc <foreach collection=\"l\" item=\"i\">#{i}<if test=\"j\">,</if></foreach>";
        var s = Segments(body);
        Assert.Equal("Text Whole Text Whole", string.Join(' ', s.Select(x => x.Kind.ToString())));
        Assert.Equal("<if test=\"x\">AND b = #{b}</if>", body.Substring(s[1].Start, s[1].Length));
        Assert.StartsWith("<foreach", body.Substring(s[3].Start, s[3].Length), StringComparison.Ordinal);
        Assert.EndsWith("</foreach>", body.Substring(s[3].Start, s[3].Length), StringComparison.Ordinal);
    }

    [Fact]
    public void Multi_line_element_stays_split_and_inner_one_line_elements_are_whole()
    {
        const string body = "<where>\n  <if test=\"a\">\n    x\n    <if test=\"b\">y</if>\n  </if>\n</where>";
        Assert.Equal("OpenTag Text OpenTag Text Whole Text CloseTag Text CloseTag", Kinds(body));
    }

    [Fact]
    public void SelectKey_is_whole_even_across_lines()
    {
        const string body = "<selectKey keyProperty=\"id\">\n  SELECT seq.nextval FROM dual\n</selectKey>\nINSERT INTO t VALUES (#{id})";
        var s = Segments(body);
        Assert.Equal("Whole Text Param Text", string.Join(' ', s.Select(x => x.Kind.ToString())));
        Assert.EndsWith("</selectKey>", body.Substring(s[0].Start, s[0].Length), StringComparison.Ordinal);
    }

    [Fact]
    public void Markup_property_marks_xml_segments_only()
    {
        var s = Segments("a #{b} &lt; <if test=\"x\">c</if> <![CDATA[d]]> <!-- e --> <bind name=\"n\" value=\"v\"/>");
        Assert.Equal(
            [false, false, false, false, false, true, false, true, false, true, false, true, false, true],
            s.Select(x => x.IsMarkup));
    }

    [Theory]
    [InlineData("a <if test=\"x\">b")]
    [InlineData("a <if test=\"x\"")]
    public void Unclosed_tag_is_rejected(string body) => Assert.Contains("닫히지 않", Error(body), StringComparison.Ordinal);

    [Fact]
    public void Unclosed_attribute_quote_is_rejected() =>
        Assert.Contains("따옴표", Error("a <if test=\"x>b</if>"), StringComparison.Ordinal);

    [Fact]
    public void Closing_tag_without_matching_open_is_rejected()
    {
        Assert.Contains("</when>", Error("a </when>"), StringComparison.Ordinal);
        Assert.Contains("</if>", Error("<where>a</if></where>"), StringComparison.Ordinal);
    }

    [Fact]
    public void Unclosed_cdata_and_xml_comment_are_rejected()
    {
        Assert.Contains("CDATA", Error("a <![CDATA[ b"), StringComparison.Ordinal);
        Assert.Contains("XML 주석", Error("a <!-- b"), StringComparison.Ordinal);
    }

    [Fact]
    public void Multi_line_or_unclosed_param_is_rejected()
    {
        Assert.Contains("여러 줄", Error("a = #{b\n}"), StringComparison.Ordinal);
        Assert.Contains("여러 줄", Error("a = ${b"), StringComparison.Ordinal);
    }

    [Fact]
    public void Existing_placeholder_marker_is_rejected() =>
        Assert.Contains("/*@", Error("a = 1 /*@1*/"), StringComparison.Ordinal);

    [Theory]
    [InlineData("a &foo; b")]
    [InlineData("a & b")]
    [InlineData("a &#32; b")]
    [InlineData("a &#xD800; b")]
    [InlineData("a &#x1F600; b")]
    public void Unknown_entity_is_rejected(string body) => Assert.Contains("엔티티", Error(body), StringComparison.Ordinal);

    [Theory]
    [InlineData("a < b")]
    [InlineData("a <> b")]
    [InlineData("a <?x?> b")]
    public void Less_than_that_is_not_a_tag_is_rejected(string body) => Assert.NotEmpty(Error(body));

    [Theory]
    [InlineData("tb_<if test=\"x\">a</if>")]
    [InlineData("tb_<include refid=\"a\"/>b")]
    [InlineData("a<!-- c -->b")]
    [InlineData("x<![CDATA[y]]>z")]
    [InlineData("1<bind name=\"n\" value=\"v\"/>2")]
    [InlineData("#<if test=\"x\">a</if>")]
    public void Xml_boundary_that_splits_a_word_is_rejected(string body) =>
        Assert.Contains("낱말을 가르는", Error(body), StringComparison.Ordinal);

    [Fact]
    public void Word_splitting_boundary_inside_a_whole_segment_is_rejected() =>
        Assert.Contains("낱말을 가르는", Error("a\n<if test=\"x\">tb_<include refid=\"a\"/>b</if>"), StringComparison.Ordinal);

    [Theory]
    [InlineData("tb <if test=\"x\">a</if>")]
    [InlineData("(<if test=\"x\">a</if>)")]
    [InlineData("a,<include refid=\"i\"/>,b")]
    [InlineData("#{a}<if test=\"x\">a</if>")]
    public void Xml_boundary_next_to_a_non_word_character_is_accepted(string body) => Assert.True(MyBatisBodyScanner.Scan(body).Ok);
}
