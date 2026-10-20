using Oranda.App.Features.Update;
using Oranda.App.Shell;

namespace Oranda.App.Tests.Features.Update;

/// <summary>릴리스 노트 변환(머리글·목록·빈 노트)과 한국어 상태 문구의 조사 없음.</summary>
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
