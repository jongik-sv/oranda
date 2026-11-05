using Avalonia.Controls;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Rendering;
using Oranda.App.Features.Execution;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Tests.Features.SqlEditor;
using Oranda.Core.Execution;

namespace Oranda.App.Tests.Features.Execution;

/// <summary>
/// 열 블록(사각) 선택 실행: 블록 밖 글이 실행되면 안 된다. 사용자가 실행하는 SQL 을 정하는 길이므로 실제 View 경로
/// (RectangleSelection → 편집기 SelectionStart/Length → 실행 컨트롤러)를 거쳐 시험한다.
/// </summary>
public sealed class BlockSelectionRunTests
{
    // 줄마다 20글자: 앞 4글자 + 가운데 10글자(블록) + 뒤 6글자. 블록 밖 글은 실행되면 안 되는 표지(XXXX·YYYYYY)로 채운다.
    private const string Doc = "XXXXselect 1  YYYYYY\nXXXXfrom dual YYYYYY\nXXXXwhere 1=1 YYYYYY";

    private static (SqlEditorViewModel Tab, SqlEditorView View, Window Window) Open(Rig rig, string text)
    {
        var tab = rig.Host.NewDocument(text);
        var view = new SqlEditorView { DataContext = tab };
        var window = new Window { Width = 900, Height = 600, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (tab, view, window);
    }

    [Fact]
    public Task Selection_run_over_a_column_block_sends_only_the_block_characters() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var (tab, view, window) = Open(rig, Doc);
        view.Carets!.SelectBlock(new TextViewPosition(1, 5), new TextViewPosition(3, 15));

        await rig.Controller.RunAsync(RunMode.Selection);

        var ran = Assert.Single(rig.Exec.Statements);
        Assert.DoesNotContain("XXXX", ran.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("YYYY", ran.Text, StringComparison.Ordinal);
        Assert.Equal("select 1  \nfrom dual \nwhere 1=1", ran.Text.TrimEnd());
        window.Close();
    });

    [Fact]
    public Task Statement_run_over_a_column_block_also_sends_only_the_block_characters() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var (tab, view, window) = Open(rig, Doc);
        view.Carets!.SelectBlock(new TextViewPosition(1, 5), new TextViewPosition(3, 15));

        await rig.Controller.RunAsync(RunMode.Statement);

        var ran = Assert.Single(rig.Exec.Statements);
        Assert.DoesNotContain("XXXX", ran.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("YYYY", ran.Text, StringComparison.Ordinal);
        window.Close();
    });

    [Fact]
    public Task Statement_ranges_and_flash_follow_the_block_pieces_not_the_text_between_them() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var (tab, view, window) = Open(rig, Doc);
        view.Carets!.SelectBlock(new TextViewPosition(1, 5), new TextViewPosition(3, 15));
        var flashes = new List<IReadOnlyList<Oranda.Core.Text.TextRange>>();
        tab.FlashRangesRequested += (_, r) => flashes.Add(r);

        var (statements, _) = rig.Controller.SelectStatements(tab, RunMode.Selection);
        var stmt = Assert.Single(statements);
        // 문장 글 "select 1  \nfrom dual \nwhere 1=1": 둘째 조각 첫 글자(11)는 문서 둘째 줄의 블록 시작(21 + 4)에 놓인다.
        Assert.Equal(4, ExecutionController.DocumentOffset(stmt, 0));
        Assert.Equal(25, ExecutionController.DocumentOffset(stmt, 11));
        Assert.Equal(46, ExecutionController.DocumentOffset(stmt, 22));

        await rig.Controller.RunAsync(RunMode.Selection);
        var flash = Assert.Single(flashes);
        Assert.Equal(
            [new Oranda.Core.Text.TextRange(4, 10), new Oranda.Core.Text.TextRange(25, 10), new Oranda.Core.Text.TextRange(46, 9)], // 문장 끝의 공백은 문장 범위에 넣지 않는다
            flash);
        window.Close();
    });

    [Fact]
    public Task Case_change_over_a_column_block_touches_only_the_block_characters_in_one_undo() => Ui.Run(() =>
    {
        using var rig = new Rig();
        var (tab, view, window) = Open(rig, "aaaabbbbcccc\naaaabbbbcccc");
        view.Carets!.SelectBlock(new TextViewPosition(1, 5), new TextViewPosition(2, 9));

        Assert.True(tab.ApplyToPieces((t, a, l) => EditorTextOps.ChangeCase(t, a, l, EditorTextOps.CaseMode.Upper)));

        Assert.Equal("aaaaBBBBcccc\naaaaBBBBcccc", tab.Text);
        tab.Document.UndoStack.Undo();
        Assert.Equal("aaaabbbbcccc\naaaabbbbcccc", tab.Text);
        window.Close();
        return Task.CompletedTask;
    });

    [Fact]
    public Task Blank_line_delimiter_does_not_split_a_block_statement_at_a_line_that_is_blank_only_inside_the_block() => Ui.Run(() =>
    {
        using var rig = new Rig();
        rig.State.BlankLineDelimiter = true;
        // 둘째 줄은 블록 안이 공백뿐이다: 이은 글에서 빈 줄로 보여 DELETE 와 WHERE 가 갈라지면 WHERE 없는 DELETE 가 따로 실행된다.
        var (tab, view, window) = Open(rig, "XXXXdelete from emp   YY\nXXXX                  YY\nXXXXwhere empno = 7369YY");
        view.Carets!.SelectBlock(new TextViewPosition(1, 5), new TextViewPosition(3, 23));

        var (statements, _) = rig.Controller.SelectStatements(tab, RunMode.Selection);

        var stmt = Assert.Single(statements);
        Assert.Contains("delete from emp", stmt.Text, StringComparison.Ordinal);
        Assert.Contains("where empno = 7369", stmt.Text, StringComparison.Ordinal);
        window.Close();
        return Task.CompletedTask;
    });

    [Fact]
    public Task Error_position_mapping_survives_a_copy_of_the_statement() => Ui.Run(() =>
    {
        using var rig = new Rig();
        var (tab, view, window) = Open(rig, Doc);
        view.Carets!.SelectBlock(new TextViewPosition(1, 5), new TextViewPosition(3, 15));
        var (statements, _) = rig.Controller.SelectStatements(tab, RunMode.Selection);
        var copy = Assert.Single(statements) with { Text = "changed by substitution" }; // 실행 엔진이 치환 변수를 풀며 만드는 사본
        Assert.Equal(25, ExecutionController.DocumentOffset(copy, 11));
        window.Close();
        return Task.CompletedTask;
    });

    [Fact]
    public Task Compile_error_navigation_lands_inside_the_block_not_on_the_text_outside_it() => Ui.Run(() =>
    {
        using var rig = new Rig();
        // 줄마다 앞 4글자(XXXX) + 블록 32글자 + 뒤 2글자(YY). 오류 위치는 블록 글 기준으로 문서의 블록 안 글자를 가리켜야 한다.
        var (tab, view, window) = Open(rig, "XXXXcreate or replace procedure p as  YY\nXXXXbegin null; end;                YY");
        view.Carets!.SelectBlock(new TextViewPosition(1, 5), new TextViewPosition(2, 37));
        var (statements, _) = rig.Controller.SelectStatements(tab, RunMode.Selection);
        var stmt = Assert.Single(statements);
        var source = new Oranda.App.Features.PlsqlEditor.SqlCompileErrorSource("p", tab);
        source.Anchor(tab, 1, stmt.Text, stmt);

        Assert.True(source.NavigateTo(new Oranda.Core.Metadata.CompileError(2, 3, "PLS-00103", false)));

        int line2 = tab.Document.GetLineByNumber(2).Offset;
        Assert.Equal(line2 + 4 + 2, tab.CaretOffset); // 둘째 줄 블록 시작(앞 4글자 뒤)에서 2글자 안쪽
        window.Close();
        return Task.CompletedTask;
    });

    [Fact]
    public Task Go_to_error_after_a_block_run_lands_on_the_block_character_the_server_reported() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var (tab, view, window) = Open(rig, Doc);
        view.Carets!.SelectBlock(new TextViewPosition(1, 5), new TextViewPosition(3, 15));
        // 서버가 보고한 오류 위치 11 = 이은 글 "select 1  \nfrom dual \nwhere 1=1" 의 둘째 조각 첫 글자.
        rig.Exec.Handler = (s, _) => Task.FromResult(new ExecutionResult(s, ExecutionStatus.Failed)
        {
            Error = new Oranda.Core.Execution.DbError("ORA-00942", "x") { Number = 942, Offset = 11 },
        });

        await rig.Controller.RunAsync(RunMode.Selection);
        tab.Select(0, 0);

        Assert.True(rig.Controller.GoToError());
        Assert.Equal(21 + 4, tab.CaretOffset); // 둘째 줄(오프셋 21) 블록 시작
        window.Close();
    });

    [Fact]
    public Task A_plain_statement_with_the_range_of_an_earlier_block_run_is_not_remapped() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        // 줄 전체를 덮는 블록의 문장 Range 는 같은 글을 일반 실행할 때의 Range 와 같다. 줄바꿈이 두 글자(CRLF)라 조각 대응과 일반 대응이 달라진다.
        var (tab, view, window) = Open(rig, "select 1\r\nfrom dual");
        view.Carets!.SelectBlock(new TextViewPosition(1, 1), new TextViewPosition(2, 10));
        await rig.Controller.RunAsync(RunMode.Selection);

        tab.Select(0, 0);
        rig.Exec.Handler = (s, _) => Task.FromResult(new ExecutionResult(s, ExecutionStatus.Failed)
        {
            Error = new Oranda.Core.Execution.DbError("ORA-00942", "x") { Number = 942, Offset = 11 },
        });
        await rig.Controller.RunAsync(RunMode.Statement);
        tab.Select(0, 0);

        Assert.True(rig.Controller.GoToError());
        Assert.Equal(11, tab.CaretOffset); // 일반 문장이므로 Range.Start + 11. 앞선 블록 실행의 대응(12)을 쓰면 안 된다
        window.Close();
    });

    [Fact]
    public Task Duplicating_a_tab_without_a_view_does_not_turn_its_block_into_the_surrounding_selection() => Ui.Run(() =>
    {
        using var rig = new Rig();
        var (tab, view, window) = Open(rig, Doc);
        view.Carets!.SelectBlock(new TextViewPosition(1, 5), new TextViewPosition(3, 15));
        view.DataContext = null; // 다른 탭으로 전환되어 View 가 떨어진 탭
        Assert.True(tab.SelectionIsBlock);

        var copy = rig.Host.Duplicate(tab);

        Assert.Equal(0, copy.SelectionLength);
        window.Close();
        return Task.CompletedTask;
    });
}
