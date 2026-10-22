using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.Navigation;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Formatting;

namespace Oranda.App.Tests.Features.Navigation;

/// <summary>TSK-13-38 테이블 레이아웃 창(Alt+C·F4) 넣기 글: 쉼표 위치 = 설정 「SQL 포맷」 목록 쉼표, 줄바꿈은 주석 붙은 열 뒤에서만. 주석 없음·있음·섞임 × 뒤·앞 × 쉼표 넣기.</summary>
public class TableLayoutCommaTests
{
    private static readonly TableLayoutRow Id = new(1, "COIL_ID", "VARCHAR2(7)", true, null, "코일 번호", true);
    private static readonly TableLayoutRow Sts = new(2, "RCD_STS_TP", "VARCHAR2(1)", false, "'A'", null, false);
    private static readonly TableLayoutRow Thk = new(3, "COIL_THK", "NUMBER(5,3)", false, null, "두께", false);

    private static TableLayoutOptions Opts(CommaPosition comma, bool insert = false, bool comment = false, string alias = "")
        => new TableLayoutOptions(alias, insert, false, false, comment) { Comma = comma };

    [Fact]
    public void Default_comma_position_is_trailing_so_existing_options_paste_as_before()
    {
        var plain = new TableLayoutOptions("", false, false, false, false);
        Assert.Equal(CommaPosition.Trailing, plain.Comma);
        Assert.Equal(TableLayoutPaste.Build([Id, Sts], plain), TableLayoutPaste.Build([Id, Sts], Opts(CommaPosition.Trailing)));
        Assert.Equal(CommaPosition.Trailing, TableLayoutOptions.From(new Oranda.Core.Settings.TableLayoutSettings()).Comma);
    }

    private static readonly TableLayoutRow Wth = new(4, "COIL_WTH", "NUMBER(6,1)", false, null, null, false);

    private static TableLayoutRow[] Set(string name) => name switch
    {
        "none" => [Wth, Sts],
        "comment" => [Thk, Id],
        "mixed" => [Thk, Id, Sts],
        _ => [Sts, Id], // 주석 있는 열 다음 주석 없는 열로 끝남
    };

    [Theory]
    // 한 줄 모드(주석 넣기 끔)
    [InlineData("mixed", CommaPosition.Trailing, false, false, "COIL_ID, RCD_STS_TP, COIL_THK")]
    [InlineData("mixed", CommaPosition.Trailing, true, false, "COIL_ID, RCD_STS_TP, COIL_THK, ")]
    [InlineData("mixed", CommaPosition.Leading, false, false, "COIL_ID, RCD_STS_TP, COIL_THK")]
    [InlineData("mixed", CommaPosition.Leading, true, false, ", COIL_ID, RCD_STS_TP, COIL_THK")]
    // 주석 넣기 · 주석 없는 열만: 줄을 바꾸지 않는다
    [InlineData("none", CommaPosition.Trailing, false, true, "RCD_STS_TP, COIL_WTH")]
    [InlineData("none", CommaPosition.Trailing, true, true, "RCD_STS_TP, COIL_WTH, ")]
    [InlineData("none", CommaPosition.Leading, false, true, "RCD_STS_TP, COIL_WTH")]
    [InlineData("none", CommaPosition.Leading, true, true, ", RCD_STS_TP, COIL_WTH")]
    // 주석 넣기 · 주석 있는 열만: 열마다 한 줄
    [InlineData("comment", CommaPosition.Trailing, false, true, "COIL_ID, -- 코일 번호\n  COIL_THK -- 두께\n  ")]
    [InlineData("comment", CommaPosition.Trailing, true, true, "COIL_ID, -- 코일 번호\n  COIL_THK, -- 두께\n  ")]
    [InlineData("comment", CommaPosition.Leading, false, true, "COIL_ID -- 코일 번호\n  , COIL_THK -- 두께\n  ")]
    [InlineData("comment", CommaPosition.Leading, true, true, ", COIL_ID -- 코일 번호\n  , COIL_THK -- 두께\n  ")]
    // 주석 넣기 · 섞임: 주석 붙은 열 뒤에서만 줄을 바꾼다
    [InlineData("mixed", CommaPosition.Trailing, false, true, "COIL_ID, -- 코일 번호\n  RCD_STS_TP, COIL_THK -- 두께\n  ")]
    [InlineData("mixed", CommaPosition.Trailing, true, true, "COIL_ID, -- 코일 번호\n  RCD_STS_TP, COIL_THK, -- 두께\n  ")]
    [InlineData("mixed", CommaPosition.Leading, false, true, "COIL_ID -- 코일 번호\n  , RCD_STS_TP, COIL_THK -- 두께\n  ")]
    [InlineData("mixed", CommaPosition.Leading, true, true, ", COIL_ID -- 코일 번호\n  , RCD_STS_TP, COIL_THK -- 두께\n  ")]
    [InlineData("tail", CommaPosition.Trailing, false, true, "COIL_ID, -- 코일 번호\n  RCD_STS_TP")]
    [InlineData("tail", CommaPosition.Trailing, true, true, "COIL_ID, -- 코일 번호\n  RCD_STS_TP, ")]
    [InlineData("tail", CommaPosition.Leading, false, true, "COIL_ID -- 코일 번호\n  , RCD_STS_TP")]
    [InlineData("tail", CommaPosition.Leading, true, true, ", COIL_ID -- 코일 번호\n  , RCD_STS_TP")]
    public void Paste_text_follows_the_comma_setting(string rows, CommaPosition comma, bool insert, bool comment, string expected)
    {
        Assert.Equal(expected, TableLayoutPaste.Build(Set(rows), Opts(comma, insert, comment), indent: "  "));
    }

    [Fact]
    public void Leading_with_one_row_empty_rows_and_alias()
    {
        Assert.Equal("COIL_ID", TableLayoutPaste.Build([Id], Opts(CommaPosition.Leading)));
        Assert.Equal(", COIL_ID", TableLayoutPaste.Build([Id], Opts(CommaPosition.Leading, insert: true)));
        Assert.Equal("RCD_STS_TP", TableLayoutPaste.Build([Sts], Opts(CommaPosition.Leading, comment: true)));
        Assert.Equal(string.Empty, TableLayoutPaste.Build([], Opts(CommaPosition.Leading, insert: true, comment: true)));
        Assert.Equal("t.COIL_ID -- 코일 번호\n, t.RCD_STS_TP", TableLayoutPaste.Build([Id, Sts], Opts(CommaPosition.Leading, comment: true, alias: "t")));
    }

    [Fact]
    public void Leading_paste_lands_in_the_editor_with_the_caret_column_indent()
    {
        var tab = new SqlEditorViewModel(Guid.NewGuid(), "select ");
        tab.UpdateCaret(7, 7, 0);
        var session = new TableLayoutSession(tab, new TableLayoutModel("SCOTT.COIL", [Id, Sts, Thk]), Opts(CommaPosition.Leading, comment: true, alias: "c"));

        Assert.True(session.Paste([Id, Sts, Thk]));
        Assert.Equal("select c.COIL_ID -- 코일 번호\n       , c.RCD_STS_TP, c.COIL_THK -- 두께\n       ", tab.Text);
    }

    [Fact]
    public Task Window_toggles_keep_the_comma_setting() => HeadlessHost.Run(() =>
    {
        var tab = new SqlEditorViewModel(Guid.NewGuid(), "select ");
        tab.UpdateCaret(7, 7, 0);
        var session = new TableLayoutSession(tab, new TableLayoutModel("SCOTT.COIL", [Id, Sts]), Opts(CommaPosition.Leading));
        var window = new TableLayoutWindow(session);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            // 「주석 넣기」(--) 토글을 켜도 쉼표 위치는 설정 값(앞) 그대로다.
            var comment = window.GetVisualDescendants().OfType<ToggleButton>().Single(t => Avalonia.Automation.AutomationProperties.GetName(t) == "주석 넣기");
            comment.IsChecked = true;
            Dispatcher.UIThread.RunJobs();

            Assert.True(session.Options.ShowComment);
            Assert.Equal(CommaPosition.Leading, session.Options.Comma);
            Assert.Equal("COIL_ID -- 코일 번호\n       , RCD_STS_TP", session.TextFor([Id, Sts]));
        }
        finally
        {
            window.Close();
        }
    });
}
