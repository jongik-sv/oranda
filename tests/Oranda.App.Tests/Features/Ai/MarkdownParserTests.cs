using Oranda.App.Features.Ai.Chat;

namespace Oranda.App.Tests.Features.Ai;

/// <summary>대화 창 Markdown 파서(f4): 부분 집합을 읽고, 그 밖이거나 깨진 글은 예외 없이 일반 글로 둔다.</summary>
public class MarkdownParserTests
{
    private static T Single<T>(string markdown)
        where T : MarkdownBlock => Assert.IsType<T>(Assert.Single(MarkdownParser.Parse(markdown)));

    [Fact]
    public void Headings_lists_and_paragraphs_are_read_in_order()
    {
        var blocks = MarkdownParser.Parse("# 제목\n\n본문 한 줄\n둘째 줄\n\n## 소제목\n- 가\n- 나\n  - 나-1\n\n다음 순서:\n1. 하나\n2. 둘");

        Assert.Equal(6, blocks.Count);
        Assert.Equal(1, Assert.IsType<MarkdownHeading>(blocks[0]).Level);
        Assert.Equal("본문 한 줄\n둘째 줄", MarkdownParser.ToPlainText(Assert.IsType<MarkdownParagraph>(blocks[1]).Spans));
        Assert.Equal(2, Assert.IsType<MarkdownHeading>(blocks[2]).Level);
        var bullets = Assert.IsType<MarkdownList>(blocks[3]).Items;
        Assert.Equal(["•", "•", "•"], bullets.Select(i => i.Marker));
        Assert.Equal([0, 0, 1], bullets.Select(i => i.Depth));
        Assert.Equal("다음 순서:", MarkdownParser.ToPlainText(Assert.IsType<MarkdownParagraph>(blocks[4]).Spans));
        Assert.Equal(["1.", "2."], Assert.IsType<MarkdownList>(blocks[5]).Items.Select(i => i.Marker));
    }

    [Fact]
    public void Inline_bold_italic_and_code_are_split_into_styled_spans()
    {
        var spans = MarkdownParser.ParseInline("앞 **굵게** 와 *기울임* 그리고 `TB_ORDER` 뒤");

        Assert.Equal("앞 굵게 와 기울임 그리고 TB_ORDER 뒤", MarkdownParser.ToPlainText(spans));
        Assert.Contains(spans, s => s.Text == "굵게" && s.Style == MarkdownStyle.Bold);
        Assert.Contains(spans, s => s.Text == "기울임" && s.Style == MarkdownStyle.Italic);
        Assert.Contains(spans, s => s.Text == "TB_ORDER" && s.Style == MarkdownStyle.Code);
    }

    [Theory]
    [InlineData("**FULL**을 읽음", "FULL", MarkdownStyle.Bold)]
    [InlineData("(**쓰지 않음**)", "쓰지 않음", MarkdownStyle.Bold)]
    [InlineData("**주의:** 값", "주의:", MarkdownStyle.Bold)]
    [InlineData("**COUNT(*)** 는", "COUNT(*)", MarkdownStyle.Bold)]
    [InlineData("조건 _STAT_CD_ 에", "STAT_CD", MarkdownStyle.Italic)]
    [InlineData("그리고 ***둘 다*** 적용", "둘 다", MarkdownStyle.Bold | MarkdownStyle.Italic)]
    public void Ordinary_emphasis_still_applies_with_the_narrow_rules(string text, string styled, MarkdownStyle style)
    {
        var spans = MarkdownParser.ParseInline(text);

        Assert.Contains(spans, s => s.Text == styled && s.Style == style);
    }

    [Theory]
    [InlineData("SELECT * FROM TB_ORDER_ITEM WHERE a * b > 1")]
    [InlineData("바인드 :v1 과 :v2 는 그대로")]
    [InlineData("깨진 **굵게 표시와 `닫히지 않은 코드")]
    [InlineData(@"경로 C:\temp\*.sql 은 그대로")]
    [InlineData("SELECT t.*, u.* FROM t, u")]
    [InlineData("COUNT(*) 와 COUNT(*) 두 번")]
    [InlineData("힌트 /*+INDEX(t) */ 와 SELECT *, ROWID")]
    [InlineData("LIKE '%_ORDER_%' 와 LIKE '_A_'")]
    [InlineData(@"LIKE 'A\_%' ESCAPE '\'")]
    [InlineData("a*b*c 와 snake_case_name 과 x_ y_")]
    public void Sql_stars_identifier_underscores_placeholders_and_unclosed_marks_stay_literal(string text)
    {
        var spans = MarkdownParser.ParseInline(text);

        Assert.Equal(text, MarkdownParser.ToPlainText(spans));
        Assert.All(spans, s => Assert.Equal(MarkdownStyle.None, s.Style));
    }

    [Fact]
    public void A_code_fence_keeps_its_language_and_text_and_survives_being_unclosed()
    {
        var closed = MarkdownParser.Parse("앞\n\n```sql\nSELECT *\n  FROM t;\n```\n뒤");
        Assert.Equal(3, closed.Count);
        var code = Assert.IsType<MarkdownCode>(closed[1]);
        Assert.Equal("sql", code.Language);
        Assert.Equal("SELECT *\n  FROM t;", code.Code);

        var streaming = MarkdownParser.Parse("설명\n```sql\nSELECT 1 FROM dual");
        Assert.Equal("SELECT 1 FROM dual", Assert.IsType<MarkdownCode>(streaming[1]).Code);
    }

    [Fact]
    public void A_table_reads_header_alignment_and_pads_short_rows()
    {
        var table = Single<MarkdownTable>("| 이름 | 개수 | 비고 |\n|:--|--:|:-:|\n| A | 1 |\n| B | 2 | `x \\| y` |");

        Assert.Equal(["이름", "개수", "비고"], table.Header.Select(MarkdownParser.ToPlainText));
        Assert.Equal([MarkdownAlign.Left, MarkdownAlign.Right, MarkdownAlign.Center], table.Aligns);
        Assert.Equal(2, table.Rows.Count);
        Assert.Equal(3, table.Rows[0].Count);
        Assert.Equal(string.Empty, MarkdownParser.ToPlainText(table.Rows[0][2]));
        Assert.Equal("x | y", MarkdownParser.ToPlainText(table.Rows[1][2]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n\n  ")]
    [InlineData("|")]
    [InlineData("| a |\n|---")]
    [InlineData("```")]
    [InlineData("****")]
    [InlineData("**``**``__")]
    [InlineData("#")]
    [InlineData("- \n-\n1.")]
    [InlineData("\\")]
    public void Odd_input_never_throws(string markdown)
    {
        var blocks = MarkdownParser.Parse(markdown);

        Assert.NotNull(blocks);
    }

    [Fact]
    public void Huge_or_pathological_input_is_bounded_and_returns()
    {
        var huge = new string('*', MarkdownParser.MaxChars + 1);
        Assert.Equal(huge, MarkdownParser.ToPlainText(Single<MarkdownParagraph>(huge).Spans));

        var manyOpeners = string.Concat(Enumerable.Repeat("*a ", 20_000));
        Assert.NotEmpty(MarkdownParser.Parse(manyOpeners));

        // 긴 공백이 낀 제목·표 구분 줄이 제곱 시간이 되지 않는다.
        var spaces = new string(' ', 60_000);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.NotEmpty(MarkdownParser.Parse("# a" + spaces + "b"));
        Assert.NotEmpty(MarkdownParser.Parse("a | b\n" + spaces + "x"));
        Assert.NotEmpty(MarkdownParser.Parse("| a |\n|" + spaces + "-" + spaces + "x"));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"{watch.Elapsed}");
    }

    [Fact]
    public void A_closing_hash_run_is_dropped_from_a_heading_but_a_trailing_hash_word_is_kept()
    {
        Assert.Equal("제목", MarkdownParser.ToPlainText(Single<MarkdownHeading>("## 제목 ##").Spans));
        Assert.Equal("C#", MarkdownParser.ToPlainText(Single<MarkdownHeading>("# C#").Spans));
    }
}
