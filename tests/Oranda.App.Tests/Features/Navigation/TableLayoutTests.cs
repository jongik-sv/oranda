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
        Assert.Equal("COIL_ID -- 코일 번호", TableLayoutPaste.Build([Id], Opts(comment: true)));
        Assert.Equal("COIL_ID, -- 코일 번호", TableLayoutPaste.Build([Id], Opts(comma: true, comment: true)));
        Assert.Equal("COIL_ID, -- 코일 번호\nRCD_STS_TP", TableLayoutPaste.Build([Id, Sts], Opts(comment: true)));
        Assert.Equal("RCD_STS_TP", TableLayoutPaste.Build([Sts], Opts(comment: true)));
        Assert.Equal(string.Empty, TableLayoutPaste.Build([], Opts()));
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
