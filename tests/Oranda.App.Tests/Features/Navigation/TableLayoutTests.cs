using Oranda.App.Features.Navigation;
using Oranda.App.Features.SqlEditor;
using Oranda.Core.Metadata;
using Oranda.Core.Settings;

namespace Oranda.App.Tests.Features.Navigation;

/// <summary>TSK-13-32 테이블 레이아웃 팝업: 모델·붙여 넣기 글·편집기 반영·옵션 저장.</summary>
public class TableLayoutTests
{
    private static readonly TableLayoutRow Id = new(1, "COIL_ID", "VARCHAR2(7)", true, null, "코일 번호", true);
    private static readonly TableLayoutRow Sts = new(2, "RCD_STS_TP", "VARCHAR2(1)", false, "'A'", null, false);
    private static readonly TableLayoutRow Mixed = new(3, "Mixed Case", "NUMBER", false, null, "혼합", false);

    private static TableLayoutOptions Opts(string alias = "", bool comma = false, bool lower = false, bool pk = false, bool comment = false)
        => new(alias, comma, lower, pk, comment);

    [Fact]
    public void Model_reads_columns_in_position_order_with_pk_default_and_comment()
    {
        var obj = new DbObjectRef("MESAPUSER", "TB_M47_COIL_CMN", "TABLE");
        var table = new TableInfo(obj,
        [
            new ColumnInfo("COIL_THK", 7, "NUMBER") { Precision = 5, Scale = 3 },
            new ColumnInfo("COIL_ID", 1, "VARCHAR2") { Length = 7, IsNullable = false, Comment = "번호" },
            new ColumnInfo("STS", 2, "VARCHAR2") { Length = 1, DefaultValue = "'A'\n" },
        ])
        {
            Constraints = [new ConstraintInfo("PK_COIL", ConstraintKind.PrimaryKey, ["COIL_ID"])],
        };

        var m = TableLayoutModel.From(obj, table);
        Assert.Equal("MESAPUSER.TB_M47_COIL_CMN", m.Title);
        Assert.Equal(["COIL_ID", "STS", "COIL_THK"], m.Rows.Select(r => r.Name).ToArray());
        Assert.Equal(["VARCHAR2(7)", "VARCHAR2(1)", "NUMBER(5,3)"], m.Rows.Select(r => r.TypeText).ToArray());
        Assert.Equal([true, false, false], m.Rows.Select(r => r.NotNull).ToArray());
        Assert.Equal([true, false, false], m.Rows.Select(r => r.IsPk).ToArray());
        Assert.Equal("'A'", m.Rows[1].DefaultValue);
        Assert.Equal("번호", m.Rows[0].Comment);
    }

    [Fact]
    public void Paste_text_follows_alias_comma_lowercase_and_comment_options()
    {
        Assert.Equal("COIL_ID", TableLayoutPaste.Build([Id], Opts()));
        Assert.Equal("COIL_ID, ", TableLayoutPaste.Build([Id], Opts(comma: true)));
        Assert.Equal("a.coil_id", TableLayoutPaste.Build([Id], Opts("A", lower: true)));
        Assert.Equal("T.COIL_ID, T.RCD_STS_TP", TableLayoutPaste.Build([Id, Sts], Opts("T")));
        Assert.Equal("T.COIL_ID, T.RCD_STS_TP, ", TableLayoutPaste.Build([Id, Sts], Opts("T", comma: true)));
        Assert.Equal("COIL_ID -- 코일 번호\n", TableLayoutPaste.Build([Id], Opts(comment: true)));
        Assert.Equal("COIL_ID, -- 코일 번호\n", TableLayoutPaste.Build([Id], Opts(comma: true, comment: true)));
        Assert.Equal("COIL_ID, -- 코일 번호\nRCD_STS_TP", TableLayoutPaste.Build([Id, Sts], Opts(comment: true)));
        Assert.Equal("RCD_STS_TP", TableLayoutPaste.Build([Sts], Opts(comment: true)));
        Assert.Equal(string.Empty, TableLayoutPaste.Build([], Opts()));
    }

    [Fact]
    public void Several_rows_join_on_one_line_in_table_order_whatever_the_selection_order()
    {
        Assert.Equal("COIL_ID, RCD_STS_TP, \"Mixed Case\"", TableLayoutPaste.Build([Mixed, Id, Sts], Opts()));
        Assert.Equal("a.coil_id, a.rcd_sts_tp, ", TableLayoutPaste.Build([Sts, Id], Opts("A", comma: true, lower: true)));
    }

    [Fact]
    public void Comment_option_breaks_the_line_only_after_a_column_with_a_comment_indented_to_the_caret_column()
    {
        Assert.Equal("COIL_ID, -- 코일 번호\n    RCD_STS_TP, \"Mixed Case\" -- 혼합\n    ", TableLayoutPaste.Build([Sts, Mixed, Id], Opts(comment: true), "    "));
        Assert.Equal("t.rcd_sts_tp, t.\"Mixed Case\", -- 혼합\n\t", TableLayoutPaste.Build([Sts, Mixed], Opts("T", comma: true, lower: true, comment: true), "\t"));
        var noComment = Sts with { Index = 4, Name = "UPD_DT" };
        Assert.Equal("RCD_STS_TP, UPD_DT", TableLayoutPaste.Build([noComment, Sts], Opts(comment: true)));
        Assert.Equal("RCD_STS_TP, UPD_DT", TableLayoutPaste.Build([noComment, Sts], Opts()));
    }

    // 실앱 결함(2026-10-22): 별칭 a·쉼표 넣기·주석 넣기로 넣었더니 「a.DEPT_CD, -- 부서 코드a.USE_TP, -- 사용 구분…」처럼
    // 줄바꿈이 없어 뒤 열이 모두 주석에 묻히고 고른 순서대로 나왔다. 한 번에 넣든 한 행씩 이어 넣든 열마다 한 줄, 표 순서여야 한다.
    [Fact]
    public void Comment_mode_regression_columns_never_end_up_inside_a_previous_comment()
    {
        TableLayoutRow Col(int i, string name, string comment) => new(i, name, "VARCHAR2(10)", false, null, comment, false);
        var userCategory = Col(3, "USER_CATEGORY_CD", "사용자 분류 코드");
        var email = Col(5, "EMAIL", "이메일");
        var dept = Col(7, "DEPT_CD", "부서 코드");
        var use = Col(9, "USE_TP", "사용 구분");
        var options = Opts("a", comma: true, comment: true);
        var expected = "a.USER_CATEGORY_CD, -- 사용자 분류 코드\n       a.EMAIL, -- 이메일\n       a.DEPT_CD, -- 부서 코드\n       a.USE_TP, -- 사용 구분\n       ";

        var (tab, session, _) = NewSession("select ", options);
        tab.UpdateCaret(7, 7, 0);
        Assert.True(session.Paste([dept, use, email, userCategory]));
        Assert.Equal("select " + expected, tab.Text);

        (tab, session, _) = NewSession("select ", options);
        tab.UpdateCaret(7, 7, 0);
        foreach (var row in new[] { userCategory, email, dept, use })
        {
            Assert.True(session.Paste([row]));
        }

        Assert.Equal("select " + expected, tab.Text);

        // 주석 없는 열은 줄을 바꾸지 않고 이어 붙이고, 주석 붙은 열 뒤에서만 줄을 바꾼다(TSK-13-38 사용자 요청).
        (tab, session, _) = NewSession("select ", options);
        tab.UpdateCaret(7, 7, 0);
        var plain = Col(4, "USE_YN", "x") with { Comment = null };
        Assert.True(session.Paste([plain]));
        Assert.True(session.Paste([email]));
        Assert.Equal("select a.USE_YN, a.EMAIL, -- 이메일\n" + new string(' ', 17), tab.Text); // 이어 넣은 행의 들여쓰기 = 그때 커서 열
    }

    [Fact]
    public void Comment_mode_uses_the_documents_line_ending()
    {
        var (tab, session, _) = NewSession("select\r\n  ", Opts(comment: true));
        tab.UpdateCaret(10, 10, 0);
        Assert.Equal("COIL_ID, -- 코일 번호\r\n  RCD_STS_TP, \"Mixed Case\" -- 혼합\r\n  ", session.TextFor([Id, Sts, Mixed]));
    }

    [Fact]
    public void Indent_copies_the_text_before_the_caret_as_spaces_and_tabs()
    {
        Assert.Equal("       ", TableLayoutPaste.IndentFor("select ", 7));
        Assert.Equal("\t   ", TableLayoutPaste.IndentFor("x\n\tab c", 6));
        Assert.Equal(string.Empty, TableLayoutPaste.IndentFor("abc\n", 4));
    }

    [Fact]
    public void Double_click_inside_the_selection_takes_the_selection_and_outside_only_that_row()
    {
        Assert.Equal([Id, Sts], TableLayoutPaste.DoubleClickRows(Sts, [Id, Sts]));
        Assert.Equal([Mixed], TableLayoutPaste.DoubleClickRows(Mixed, [Id, Sts]));
    }

    [Fact]
    public void Filter_matches_name_or_comment_ignoring_case()
    {
        TableLayoutRow[] rows = [Id, Sts, Mixed];
        Assert.Equal([Sts], TableLayoutPaste.Filter(rows, "sts"));
        Assert.Equal([Id], TableLayoutPaste.Filter(rows, "코일"));
        Assert.Equal(rows, TableLayoutPaste.Filter(rows, "  "));
    }

    [Fact]
    public void Copied_text_equals_the_pasted_text_including_the_caret_indent()
    {
        var (tab, session, _) = NewSession("select\n  from emp", Opts("e", comment: true));
        tab.UpdateCaret(7, 7, 0);
        var copied = session.TextFor([Sts, Id]);
        Assert.Equal("e.COIL_ID, -- 코일 번호\ne.RCD_STS_TP", copied);

        tab.UpdateCaret(9, 9, 0);
        copied = session.TextFor([Mixed, Sts, Id]);
        Assert.Equal("e.COIL_ID, -- 코일 번호\n  e.RCD_STS_TP, e.\"Mixed Case\" -- 혼합\n  ", copied);
        Assert.True(session.Paste([Mixed, Sts, Id]));
        Assert.Equal("select\n  " + copied + "from emp", tab.Text);
    }

    [Fact]
    public void Names_that_need_quotes_are_pasted_quoted_and_keep_their_case()
    {
        Assert.Equal("\"Mixed Case\"", TableLayoutPaste.Build([Mixed], Opts()));
        Assert.Equal("t.\"Mixed Case\"", TableLayoutPaste.Build([Mixed], Opts("T", lower: true)));
        Assert.Equal("\"A\"\"B\"", TableLayoutPaste.ColumnName("A\"B", lower: false));
    }

    private static (SqlEditorViewModel Tab, TableLayoutSession Session, List<TableLayoutSettings> Saved) NewSession(string text, TableLayoutOptions options)
    {
        var tab = new SqlEditorViewModel(Guid.NewGuid(), text);
        var saved = new List<TableLayoutSettings>();
        var model = new TableLayoutModel("SCOTT.EMP", [Id, Sts]);
        return (tab, new TableLayoutSession(tab, model, options, saved.Add), saved);
    }

    [Fact]
    public void Paste_inserts_at_the_caret_ends_after_the_text_and_undoes_in_one_step()
    {
        var (tab, session, _) = NewSession("select  from emp", Opts("e"));
        tab.UpdateCaret(7, 7, 0);

        Assert.True(session.Paste([Id, Sts]));
        Assert.Equal("select e.COIL_ID, e.RCD_STS_TP from emp", tab.Text);
        Assert.Equal("select e.COIL_ID, e.RCD_STS_TP".Length, tab.CaretOffset);

        tab.Document.UndoStack.Undo();
        Assert.Equal("select  from emp", tab.Text);
    }

    [Fact]
    public void Paste_replaces_the_selection_and_skips_read_only_tabs()
    {
        var (tab, session, _) = NewSession("select * from emp", Opts());
        tab.Select(7, 1);
        Assert.True(session.Paste([Sts]));
        Assert.Equal("select RCD_STS_TP from emp", tab.Text);

        tab.IsReadOnly = true;
        Assert.False(session.Paste([Id]));
        Assert.Equal("select RCD_STS_TP from emp", tab.Text);
    }

    [Fact]
    public void Only_the_four_checkboxes_are_saved_and_the_alias_is_not()
    {
        var (_, session, saved) = NewSession("x", Opts());
        session.SetOptions(Opts("T"));
        Assert.Empty(saved);

        session.SetOptions(Opts("T", comma: true, pk: true));
        var s = Assert.Single(saved);
        Assert.True(s.InsertComma);
        Assert.True(s.ShowPk);
        Assert.False(s.LowerCase);
        Assert.False(s.ShowComment);
        Assert.Equal("T", session.Options.Alias);

        var again = TableLayoutOptions.From(s);
        Assert.True(again.InsertComma);
        Assert.Equal(string.Empty, again.Alias);
    }
}
