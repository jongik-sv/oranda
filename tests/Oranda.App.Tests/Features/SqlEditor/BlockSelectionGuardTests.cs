using Oranda.App.Features.Binds;
using Oranda.App.Features.MyBatisLog;
using Oranda.App.Features.Navigation;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Features.Templates;
using Oranda.Language.Snippets;
using Oranda.Core.Text;

namespace Oranda.App.Tests.Features.SqlEditor;

/// <summary>
/// 열 블록(조각 둘 이상) 선택에서 선택을 한 덩어리로 읽거나 바꾸는 기능이 블록 밖 글을 건드리지 않는지.
/// 판정은 <see cref="SqlEditorViewModel.SelectionPieces"/> 하나에서 나오므로, 조각만 돌려주는 가짜 표면으로 대표 사례를 시험한다
/// (열 블록이 실제로 조각을 만드는 길은 <see cref="MultiCaretTests"/>·<c>BlockSelectionRunTests</c> 가 View 로 시험한다).
/// </summary>
public sealed class BlockSelectionGuardTests
{
    private const string Doc = "AAAAselect 1 BBBB\nAAAAfrom dual BBBB";

    private sealed class PiecesSurface(params TextRange[] pieces) : IEditorSurface
    {
        public int CursorCount => 1;

        public bool HasExtraSelection => false;

        public IReadOnlyList<TextRange> SelectionSegments { get; } = pieces;

        public int FirstVisibleLine => 1;

        public void Cut()
        {
        }

        public void Copy()
        {
        }

        public void Paste()
        {
        }

        public void SelectAll()
        {
        }

        public void SelectRange(int start, int length)
        {
        }

        public void ScrollToLine(int line)
        {
        }

        public void Fold(FoldAction action)
        {
        }

        public void AddCursor(int direction)
        {
        }

        public void ShowFind(bool replace)
        {
        }

        public void FocusEditor()
        {
        }
    }

    // 두 줄의 가운데 글(select 1 / from dual)을 블록으로 잡은 모양: 둘레 범위는 AAAA… 부터 …BBBB 앞까지다.
    private static SqlEditorViewModel BlockTab()
    {
        var tab = new SqlEditorViewModel(Guid.NewGuid(), Doc);
        tab.Surface = new PiecesSurface(new TextRange(4, 8), new TextRange(22, 9));
        tab.UpdateCaret(31, 4, 27);
        return tab;
    }

    [Fact]
    public void Multi_piece_selection_is_reported_and_one_piece_is_not()
    {
        var tab = BlockTab();
        Assert.True(tab.HasMultiPieceSelection);
        tab.Surface = new PiecesSurface(new TextRange(4, 8));
        tab.UpdateCaret(12, 4, 8); // 일반 선택 하나(조각과 둘레 범위가 같다)
        Assert.False(tab.HasMultiPieceSelection);
        tab.UpdateCaret(31, 4, 27); // 글이 있는 줄이 하나뿐인 블록: 조각은 하나인데 둘레 범위는 더 넓다
        Assert.True(tab.HasMultiPieceSelection);
    }

    [Fact]
    public void Editor_context_reports_no_selection_for_a_block_so_the_surrounding_range_is_never_exported()
    {
        var host = new SqlEditorHost();
        var tab = host.NewDocument(Doc);
        tab.Surface = new PiecesSurface(new TextRange(4, 8), new TextRange(22, 9));
        tab.UpdateCaret(31, 4, 27);
        var context = new EditorContext { Host = host };

        var snapshot = context.GetActiveDocument()!;

        Assert.True(context.HasMultiPieceSelection);
        Assert.Equal(new TextRange(31, 0), snapshot.Selection);
    }

    [Fact]
    public void MyBatis_log_paste_does_not_replace_a_block_selection()
    {
        var tab = BlockTab();
        const string log = "DEBUG ==>  Preparing: SELECT * FROM emp WHERE id = ?\nDEBUG ==> Parameters: 7(Integer)\n";

        Assert.True(MyBatisLogPaste.IsBlocked(tab));
        Assert.Null(MyBatisLogPaste.Apply(tab, new BindVariableStore(), log));
        Assert.Equal(Doc, tab.Text);
    }

    [Fact]
    public void Table_layout_paste_and_snippet_insert_leave_a_block_selection_alone()
    {
        var tab = BlockTab();
        var row = new TableLayoutRow(1, "ID", "NUMBER", false, null, null, false);
        var session = new TableLayoutSession(tab, new TableLayoutModel("SCOTT.T", [row]), new TableLayoutOptions("a", false, false, true, false));

        Assert.True(session.IsBlocked);
        Assert.False(session.Paste([row]));
        Assert.False(SnippetInserter.Insert(tab, new Snippet("sel", "select", "", "select ${1:*} from dual", SnippetKind.Snippet, IsUser: false)));
        Assert.Equal(Doc, tab.Text);
    }
}
