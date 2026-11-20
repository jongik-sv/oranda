using Oranda.App.Features.Update;
using Oranda.App.Shell;

namespace Oranda.App.Tests.Features.Update;

/// <summary>릴리스 노트 변환(머리글·목록·빈 노트·굵게·코드 조각)과 한국어 상태 문구의 조사 없음.</summary>
public sealed class ReleaseNotesFormatterTests
{
    [Fact]
    public void Headings_become_bold_lines_and_bullets_get_a_dot()
    {
        var lines = ReleaseNotesFormatter.Format("## 0.1.9\r\n- 자동 업데이트\n* 상태 표시줄\n\n그대로 둔 줄");

        Assert.Equal(
            [new NoteLine("0.1.9", true), new NoteLine("• 자동 업데이트", false), new NoteLine("• 상태 표시줄", false), new NoteLine("", false), new NoteLine("그대로 둔 줄", false)],
            lines);
    }

    [Fact]
    public void Double_stars_become_a_bold_span()
    {
        Assert.Equal(
            [new NoteSpan("앞 ", false, false), new NoteSpan("굵게", true, false), new NoteSpan(" 뒤", false, false)],
            ReleaseNotesFormatter.SplitInline("앞 **굵게** 뒤"));
    }

    [Fact]
    public void Backticks_become_a_code_span()
    {
        Assert.Equal(
            [new NoteSpan("예: ", false, false), new NoteSpan("GTT·T", false, true), new NoteSpan("(임시)", false, false)],
            ReleaseNotesFormatter.SplitInline("예: `GTT·T`(임시)"));
    }

    [Theory]
    [InlineData("a * b")]
    [InlineData("**열린 채")]
    [InlineData("빈 **** 짝")]
    [InlineData("하나 ` 만")]
    [InlineData("빈 `` 짝")]
    public void Unpaired_or_empty_marks_stay_as_text(string text) =>
        Assert.Equal([new NoteSpan(text, false, false)], ReleaseNotesFormatter.SplitInline(text));

    [Fact]
    public void Several_spans_on_one_line_keep_their_order()
    {
        Assert.Equal(
            [
                new NoteSpan("A", true, false), new NoteSpan(" 와 ", false, false), new NoteSpan("x", false, true),
                new NoteSpan(", ", false, false), new NoteSpan("B ", true, false), new NoteSpan("y", true, true),
            ],
            ReleaseNotesFormatter.SplitInline("**A** 와 `x`, **B `y`**"));
    }

    [Fact]
    public void Bold_inside_a_bullet_line_and_heading_spans_are_bold()
    {
        var lines = ReleaseNotesFormatter.Format("## 새 `기능`\n- **스키마 브라우저**: 표식");

        Assert.Equal([new NoteSpan("새 ", true, false), new NoteSpan("기능", true, true)], lines[0].Spans);
        Assert.Equal(
            [new NoteSpan("• ", false, false), new NoteSpan("스키마 브라우저", true, false), new NoteSpan(": 표식", false, false)],
            lines[1].Spans);
    }

    [Fact]
    public void Empty_line_gives_one_empty_span() =>
        Assert.Equal([new NoteSpan(string.Empty, false, false)], new NoteLine(string.Empty, false).Spans);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \n ")]
    public void Empty_notes_give_no_lines(string? markdown) => Assert.Empty(ReleaseNotesFormatter.Format(markdown));

    [Fact]
    public void Korean_status_strings_have_no_optional_particles()
    {
        var text = new ShellText(language: "ko");
        foreach (var key in new[] { "available", "availableSkipped", "downloading", "ready", "readyRestart" })
        {
            var s = text.Lookup("Update.state." + key);
            Assert.DoesNotContain("(가)", s, StringComparison.Ordinal);
            Assert.DoesNotContain("(를)", s, StringComparison.Ordinal);
        }

        Assert.Equal("새 버전이 나왔습니다: {0}", text.Lookup("Update.state.available"));
        Assert.Equal("새 버전이 나왔습니다: {0} (건너뛴 버전)", text.Lookup("Update.state.availableSkipped"));
        Assert.Equal("릴리스 노트 없음", text.Lookup("Update.notesNone"));
    }
}
