using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using Oranda.App.Features.SqlEditor;

namespace Oranda.App.Tests.Features.SqlEditor;

/// <summary>
/// 세로 다중 커서와 열 블록 선택(D-007·D-041). 마우스는 헤드리스 입력으로 실제 경로(Window → TextArea 터널 핸들러)를 지나게 하고,
/// 키·글자 입력은 TextArea 에 라우트 이벤트를 올린다.
/// </summary>
public sealed class MultiCaretTests
{
    private const string Sql = "select 1 from dual;\nselect 2 from dual;\nselect 3 from dual;";

    private static (Window Window, SqlEditorView View, SqlEditorViewModel Vm) Show(string text)
    {
        var vm = new SqlEditorViewModel(Guid.NewGuid(), text);
        var view = new SqlEditorView { DataContext = vm };
        var window = new Window { Width = 900, Height = 600, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, view, vm);
    }

    /// <summary>문서 위치(줄·열 1부터)의 창 좌표(줄 가운데).</summary>
    private static Point PointOf(Window window, SqlEditorView view, int line, int column)
    {
        var tv = view.TextEditorControl.TextArea.TextView;
        var inView = tv.GetVisualPosition(new TextViewPosition(line, column), VisualYPosition.TextMiddle) - tv.ScrollOffset;
        return tv.TranslatePoint(inView, window)!.Value;
    }

    private static void Key(SqlEditorView view, Key key, KeyModifiers modifiers = KeyModifiers.None)
        => view.TextEditorControl.TextArea.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = key,
            KeyModifiers = modifiers,
        });

    private static void Type(SqlEditorView view, string text)
        => view.TextEditorControl.TextArea.RaiseEvent(new TextInputEventArgs
        {
            RoutedEvent = InputElement.TextInputEvent,
            Text = text,
        });

    private static void AltDrag(Window window, Point from, Point to)
    {
        window.MouseDown(from, MouseButton.Left, RawInputModifiers.Alt);
        Dispatcher.UIThread.RunJobs();
        for (int i = 1; i <= 4; i++)
        {
            var step = new Point(from.X + ((to.X - from.X) * i / 4), from.Y + ((to.Y - from.Y) * i / 4));
            window.MouseMove(step, RawInputModifiers.LeftMouseButton | RawInputModifiers.Alt);
            Dispatcher.UIThread.RunJobs();
        }

        window.MouseUp(to, MouseButton.Left, RawInputModifiers.Alt);
        Dispatcher.UIThread.RunJobs();
    }

    // ---- e1: Alt 왼쪽 버튼 -------------------------------------------------------------------

    [Fact]
    public Task Alt_press_and_release_in_place_adds_a_cursor() => Ui.Run(() =>
    {
        var (window, view, vm) = Show(Sql);
        var p = PointOf(window, view, 2, 8);
        window.MouseDown(p, MouseButton.Left, RawInputModifiers.Alt);
        window.MouseUp(p, MouseButton.Left, RawInputModifiers.Alt);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(2, vm.CursorCount);
        Assert.True(view.TextEditorControl.TextArea.Selection.IsEmpty);
        Assert.Contains(20 + 7, view.Carets!.AllOffsets()); // 2번째 줄(오프셋 20) 열 8
        window.Close();
    });

    [Fact]
    public Task Alt_drag_makes_a_rectangle_selection_and_drops_extra_cursors() => Ui.Run(() =>
    {
        var (window, view, vm) = Show(Sql);
        view.Carets!.AddCaret(5);
        Assert.Equal(2, vm.CursorCount);

        AltDrag(window, PointOf(window, view, 1, 3), PointOf(window, view, 3, 7));

        var selection = view.TextEditorControl.TextArea.Selection;
        Assert.IsType<RectangleSelection>(selection);
        Assert.Equal(1, vm.CursorCount);
        var segments = selection.Segments.ToList();
        Assert.Equal(3, segments.Count);
        Assert.All(segments, s => Assert.Equal(4, s.Length));
        window.Close();
    });

    [Fact]
    public Task Plain_click_clears_extra_cursors_and_adding_one_clears_the_rectangle() => Ui.Run(() =>
    {
        var (window, view, vm) = Show(Sql);
        view.Carets!.AddCaret(5);
        view.Carets.AddCaret(25);
        Assert.Equal(3, vm.CursorCount);

        var p = PointOf(window, view, 3, 2);
        window.MouseDown(p, MouseButton.Left);
        window.MouseUp(p, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, vm.CursorCount);

        AltDrag(window, PointOf(window, view, 1, 3), PointOf(window, view, 2, 7));
        Assert.IsType<RectangleSelection>(view.TextEditorControl.TextArea.Selection);
        view.Carets.AddCaret(45);
        Assert.IsNotType<RectangleSelection>(view.TextEditorControl.TextArea.Selection);
        Assert.Equal(2, vm.CursorCount);
        window.Close();
    });

    [Fact]
    public Task Alt_right_click_does_not_add_a_cursor() => Ui.Run(() =>
    {
        var (window, view, vm) = Show(Sql);
        var p = PointOf(window, view, 2, 8);
        window.MouseDown(p, MouseButton.Right, RawInputModifiers.Alt);
        window.MouseUp(p, MouseButton.Right, RawInputModifiers.Alt);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, vm.CursorCount);
        window.Close();
    });

    // ---- e2: 모든 커서 이동 ------------------------------------------------------------------

    [Fact]
    public Task Arrow_keys_move_every_cursor() => Ui.Run(() =>
    {
        var (window, view, vm) = Show("abcdef\nabcdef");
        vm.Select(2, 0); // 줄 1 열 3
        var carets = view.Carets!;
        carets.AddCaret(7 + 4); // 줄 2 열 5

        Key(view, Avalonia.Input.Key.Right);
        Assert.Equal([3, 12], Sorted(carets));
        Key(view, Avalonia.Input.Key.Left);
        Key(view, Avalonia.Input.Key.Left);
        Assert.Equal([1, 10], Sorted(carets));
        Key(view, Avalonia.Input.Key.Down);
        Assert.Equal([8, 13], Sorted(carets)); // 둘째 커서는 마지막 줄에서 아래로 = 문서 끝
        window.Close();
    });

    private static List<int> Sorted(MultiCaretController carets) => [.. carets.AllOffsets().Order()];

    [Fact]
    public Task Vertical_moves_remember_the_wide_column_across_a_short_line() => Ui.Run(() =>
    {
        var (window, view, vm) = Show("abcdef\nab\nabcdef\nabcdef");
        vm.Select(5, 0);
        var carets = view.Carets!;
        carets.AddCaret(16); // 줄 3 끝
        Key(view, Avalonia.Input.Key.Down);
        Key(view, Avalonia.Input.Key.Down);
        Assert.Contains(10 + 5, carets.AllOffsets()); // 줄 3 열 6 에 도달: 짧은 줄 2 를 지나도 목표 열을 유지
        window.Close();
    });

    [Fact]
    public Task Home_end_and_word_moves_apply_to_every_cursor() => Ui.Run(() =>
    {
        var (window, view, vm) = Show("select a from t\nselect b from u");
        vm.Select(3, 0);
        var carets = view.Carets!;
        carets.AddCaret(16 + 3);

        Key(view, Avalonia.Input.Key.End);
        Assert.Equal([15, 31], Sorted(carets));
        Key(view, Avalonia.Input.Key.Home);
        Assert.Equal([0, 16], Sorted(carets));
        window.Close();
    });

    [Fact]
    public Task Caret_moves_outside_the_controller_clear_extra_cursors() => Ui.Run(() =>
    {
        var (window, view, vm) = Show(Sql);
        view.Carets!.AddCaret(25);
        Assert.Equal(2, vm.CursorCount);
        vm.GoToLine(3);
        Assert.Equal(1, vm.CursorCount);
        window.Close();
    });

    // ---- e3: 커서별 선택 ---------------------------------------------------------------------

    [Fact]
    public Task Shift_arrow_selects_per_cursor_and_typing_replaces_every_selection_in_one_undo() => Ui.Run(() =>
    {
        var (window, view, vm) = Show("aaa\nbbb\nccc");
        vm.Select(0, 0);
        var carets = view.Carets!;
        carets.AddCaret(4);
        carets.AddCaret(8);

        Key(view, Avalonia.Input.Key.Right, KeyModifiers.Shift);
        Key(view, Avalonia.Input.Key.Right, KeyModifiers.Shift);
        Assert.True(vm.HasSelection);
        Assert.Equal([(0, 2, 2), (4, 6, 6), (8, 10, 10)], carets.Ranges());

        Type(view, "X");
        Assert.Equal("Xa\nXb\nXc", vm.Text);
        Assert.Equal(3, vm.CursorCount);

        vm.Document.UndoStack.Undo();
        Assert.Equal("aaa\nbbb\nccc", vm.Text);
        window.Close();
    });

    [Fact]
    public Task Backspace_with_selection_deletes_the_selection_and_overlapping_selections_merge() => Ui.Run(() =>
    {
        var (window, view, vm) = Show("abcdefghij");
        vm.Select(1, 0);
        var carets = view.Carets!;
        carets.AddCaret(3);
        carets.AddCaret(8);
        Key(view, Avalonia.Input.Key.Right, KeyModifiers.Shift); // 주 커서 [1,2), 추가 커서 [3,4)
        Assert.Equal(3, carets.Ranges().Count);
        Key(view, Avalonia.Input.Key.Right, KeyModifiers.Shift); // 주 커서 [1,3) 과 추가 [3,5) 가 맞닿아 합쳐지지는 않는다(둘 다 선택)
        Key(view, Avalonia.Input.Key.Right, KeyModifiers.Shift); // 주 커서 [1,4) 가 추가 커서 [3,6) 과 겹쳐 [1,6) 하나가 된다(세 번째 커서는 문서 끝에서 [8,10))
        Assert.Equal(2, carets.Ranges().Count);
        Assert.Equal(2, vm.CursorCount);
        Key(view, Avalonia.Input.Key.Back);
        Assert.Equal("agh", vm.Text); // [1,6) 과 [8,10) 이 지워진다
        window.Close();
    });

    // ---- e4: 복사·잘라내기·붙여넣기·Tab ---------------------------------------------------------

    private static async Task WaitFor(Func<bool> done)
    {
        for (int i = 0; i < 300 && !done(); i++)
        {
            await Task.Delay(10);
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>클립보드 글이 <paramref name="before"/> 와 달라질 때까지 기다려 돌려준다(복사는 비동기).</summary>
    private static async Task<string?> ClipboardAfterAsync(Window window, string before)
    {
        string? text = before;
        for (int i = 0; i < 300 && text == before; i++)
        {
            await Task.Delay(10);
            Dispatcher.UIThread.RunJobs();
            text = await window.Clipboard!.TryGetTextAsync();
        }

        return text;
    }

    private static string Unix(string? text) => (text ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal);

    [Fact]
    public Task Copy_joins_the_selection_of_every_cursor_and_cut_removes_them_in_one_undo() => Ui.Run(async () =>
    {
        var (window, view, vm) = Show("aaa\nbbb\nccc");
        vm.Select(0, 0);
        var carets = view.Carets!;
        carets.AddCaret(4);
        carets.AddCaret(8);
        Key(view, Avalonia.Input.Key.Right, KeyModifiers.Shift);
        Key(view, Avalonia.Input.Key.Right, KeyModifiers.Shift);
        Assert.True(vm.HasSelection);

        await window.Clipboard!.SetTextAsync("before");
        view.Copy();
        Assert.Equal("aa\nbb\ncc", Unix(await ClipboardAfterAsync(window, "before")));

        view.Cut();
        Assert.Equal("a\nb\nc", vm.Text);
        vm.Document.UndoStack.Undo();
        Assert.Equal("aaa\nbbb\nccc", vm.Text);
        window.Close();
    });

    [Fact]
    public Task Copy_without_any_selection_leaves_the_clipboard_alone_while_extra_cursors_exist() => Ui.Run(async () =>
    {
        var (window, view, vm) = Show("aaa\nbbb");
        view.Carets!.AddCaret(5);
        Assert.False(vm.HasSelection);
        await window.Clipboard!.SetTextAsync("keep");
        view.Copy();
        await Task.Delay(50);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("keep", await window.Clipboard!.TryGetTextAsync());
        window.Close();
    });

    [Fact]
    public Task Paste_gives_one_line_per_cursor_when_counts_match_otherwise_the_whole_text_to_each() => Ui.Run(() =>
    {
        var (window, view, vm) = Show("a\nb\nc");
        var carets = view.Carets!;
        carets.AddCaret(2);
        carets.AddCaret(4);

        carets.PasteText("X\nY\nZ", block: false);
        Assert.Equal("Xa\nYb\nZc", vm.Text);

        vm.Document.UndoStack.Undo();
        Assert.Equal("a\nb\nc", vm.Text);
        vm.Select(0, 0); // 실행 취소로 캐럿이 밖에서 움직이면 추가 커서는 해제된다: 다시 둔다
        carets.AddCaret(2);
        carets.AddCaret(4);
        carets.PasteText("Q", block: false);
        Assert.Equal("Qa\nQb\nQc", vm.Text);
        window.Close();
    });

    [Fact]
    public Task Block_copy_pastes_as_a_block_and_plain_text_with_the_same_lines_does_not() => Ui.Run(async () =>
    {
        var (window, view, vm) = Show("abcdef\nabcdef\nabcdef");
        view.Carets!.SelectBlock(new TextViewPosition(1, 2), new TextViewPosition(3, 4));
        await window.Clipboard!.SetTextAsync("before");
        view.Copy();
        Assert.Equal("bc\nbc\nbc", Unix(await ClipboardAfterAsync(window, "before")));

        vm.Select(0, 0);
        view.Paste();
        await WaitFor(() => vm.Text != "abcdef\nabcdef\nabcdef");
        Assert.Equal("bcabcdef\nbcabcdef\nbcabcdef", Unix(vm.Text));
        window.Close();
    });

    [Fact]
    public Task Tab_inserts_at_every_cursor_and_shift_tab_outdents_their_lines() => Ui.Run(() =>
    {
        var (window, view, vm) = Show("ab\n    cd");
        vm.Select(1, 0);
        var carets = view.Carets!;
        carets.AddCaret(3 + 4 + 1);
        Key(view, Avalonia.Input.Key.Tab);
        // 들여쓰기 칸 수 4: 열 2 → 3칸, 열 6 → 3칸.
        Assert.Equal("a   b\n    c   d", vm.Text);

        Key(view, Avalonia.Input.Key.Tab, KeyModifiers.Shift);
        Assert.Equal("a   b\nc   d", vm.Text); // 앞 공백 4칸이 걷힌다
        window.Close();
    });

    // ---- e5: 입력 결함 정리 ------------------------------------------------------------------

    [Fact]
    public Task Control_characters_are_not_typed_into_the_document_and_cursor_changes_are_announced() => Ui.Run(() =>
    {
        var (window, view, vm) = Show("a\nb");
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        view.Carets!.AddCaret(2);
        Assert.Contains(nameof(SqlEditorViewModel.CursorCount), changed);

        Type(view, "\b");
        Type(view, "\u001b");
        Assert.Equal("a\nb", vm.Text);
        window.Close();
    });

    [Fact]
    public Task Ctrl_alt_arrows_are_left_to_the_window_command_and_not_handled_by_the_controller() => Ui.Run(() =>
    {
        var (window, view, vm) = Show("a\nb\nc");
        var args = new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Avalonia.Input.Key.Down,
            KeyModifiers = KeyModifiers.Control | KeyModifiers.Alt,
        };
        view.TextEditorControl.TextArea.RaiseEvent(args);
        Assert.Equal(1, vm.CursorCount);
        window.Close();
    });

    // ---- 리뷰 지적 회귀 ----------------------------------------------------------------------

    [Fact]
    public Task Tab_on_a_column_block_inserts_at_the_left_edge_without_deleting_the_block() => Ui.Run(() =>
    {
        var (window, view, vm) = Show("abcdef\nabcdef");
        view.Carets!.SelectBlock(new TextViewPosition(1, 2), new TextViewPosition(2, 3));
        Key(view, Avalonia.Input.Key.Tab);
        Assert.Equal("a   bcdef\na   bcdef", vm.Text);
        window.Close();
    });

    [Fact]
    public Task Backspace_and_delete_remove_a_whole_surrogate_pair() => Ui.Run(() =>
    {
        var (window, view, vm) = Show("x\U0001F600\ny\U0001F600");
        vm.Select(3, 0);
        view.Carets!.AddCaret(7);
        Key(view, Avalonia.Input.Key.Back);
        Assert.Equal("x\ny", vm.Text);
        window.Close();
    });

    [Fact]
    public Task Alt_drag_below_a_short_last_line_keeps_the_block_width_on_longer_lines() => Ui.Run(() =>
    {
        var (window, view, vm) = Show("abcdefgh\nabcdefgh\nab");
        var from = PointOf(window, view, 1, 2);
        var to = new Point(PointOf(window, view, 1, 7).X, PointOf(window, view, 3, 1).Y);
        AltDrag(window, from, to);

        var segments = view.TextEditorControl.TextArea.Selection.Segments.ToList();
        Assert.Equal(3, segments.Count);
        Assert.Equal(5, segments[0].Length);
        Assert.Equal(5, segments[1].Length);
        Assert.Equal(1, segments[2].Length); // 짧은 줄은 줄 끝에서 잘린다
        window.Close();
    });

    [Fact]
    public Task A_block_with_text_on_only_one_line_is_still_not_a_plain_selection() => Ui.Run(() =>
    {
        var (window, view, vm) = Show("ab\nabcdefgh\nab");
        view.Carets!.SelectBlock(new TextViewPosition(1, 5, 4), new TextViewPosition(3, 7, 6));

        Assert.Single(vm.SelectionPieces); // 열 5~6 에 글이 있는 줄은 둘째 줄뿐이다
        Assert.True(vm.SelectionLength > vm.SelectionPieces[0].Length); // 둘레 범위는 세 줄을 덮는다
        Assert.True(vm.HasMultiPieceSelection);
        window.Close();
    });

    // ---- 2차 리뷰 지적 ------------------------------------------------------------------------

    [Fact]
    public Task Crlf_is_one_step_for_every_cursor_so_typing_never_splits_a_line_break() => Ui.Run(() =>
    {
        var (window, view, vm) = Show("a\r\nb\r\nc");
        vm.Select(1, 0); // 1번째 줄 끝(\r 앞)
        var carets = view.Carets!;
        carets.AddCaret(4); // 2번째 줄 끝
        Assert.Equal([1, 4], Sorted(carets));

        Key(view, Avalonia.Input.Key.Right);
        Assert.Equal([3, 6], Sorted(carets)); // CR 과 LF 사이에서 멈추지 않는다
        Key(view, Avalonia.Input.Key.Left);
        Assert.Equal([1, 4], Sorted(carets));

        Key(view, Avalonia.Input.Key.Right, KeyModifiers.Shift);
        Assert.Equal([(1, 3, 3), (4, 6, 6)], carets.Ranges()); // 선택이 접히지 않고 줄바꿈 전체를 덮는다

        Type(view, "X");
        Assert.Equal("aXbXc", vm.Text);
        window.Close();
    });

    [Fact]
    public Task Pasting_a_block_near_the_document_end_adds_the_missing_lines_in_one_undo() => Ui.Run(() =>
    {
        var (window, view, vm) = Show("ab\ncd");
        vm.Select(3, 0); // 마지막 줄 처음
        var carets = view.Carets!;
        carets.PasteText("X\nY\nZ", block: true);
        Assert.Equal("ab\nXcd\nY\nZ", Unix(vm.Text));
        vm.Document.UndoStack.Undo();
        Assert.Equal("ab\ncd", Unix(vm.Text));
        window.Close();
    });

    [Fact]
    public Task Pasting_a_block_over_a_column_block_near_the_document_end_keeps_the_old_block_undoable() => Ui.Run(() =>
    {
        var (window, view, vm) = Show("abcd\nefgh");
        var carets = view.Carets!;
        carets.SelectBlock(new TextViewPosition(1, 2), new TextViewPosition(2, 3));
        carets.PasteText("1\n2\n3", block: true);
        Assert.Equal("a1cd\ne2gh\n 3", Unix(vm.Text)); // 블록 "b"/"f" 가 지워지고 세 줄이 같은 열(2)에 붙는다. 더한 셋째 줄은 열을 맞춘다
        vm.Document.UndoStack.Undo();
        Assert.Equal("abcd\nefgh", Unix(vm.Text));
        window.Close();
    });

    [Fact]
    public Task Pasting_a_block_over_a_plain_selection_replaces_the_selection() => Ui.Run(() =>
    {
        var (window, view, vm) = Show("abcdef\nabcdef");
        vm.Select(1, 3); // "bcd"
        view.Carets!.PasteText("X\nY", block: true);
        Assert.Equal("aXef\naYbcdef", Unix(vm.Text)); // 선택 "bcd" 는 지워지고, 블록은 그 자리(열 2)부터 줄마다 붙는다
        window.Close();
    });

    [Fact]
    public Task Dragging_a_block_above_the_editor_pins_it_to_the_first_visible_line() => Ui.Run(() =>
    {
        // 편집기 위에 창 영역을 남겨 두어 편집기 위쪽 밖으로 끌 수 있게 한다.
        var vm = new SqlEditorViewModel(Guid.NewGuid(), string.Join("\n", Enumerable.Range(1, 200).Select(i => $"select {i} from dual;")));
        var view = new SqlEditorView { DataContext = vm };
        var window = new Window { Width = 900, Height = 700, Content = new Border { Padding = new Thickness(0, 100, 0, 0), Child = view } };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var tv = view.TextEditorControl.TextArea.TextView;
        view.TextEditorControl.ScrollToLine(vm.Document.LineCount);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        view.TextEditorControl.ScrollToVerticalOffset(tv.ScrollOffset.Y - 300); // 끝 줄이 보이도록 되감는다
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        tv.EnsureVisualLines();
        Assert.True(tv.VisualLinesValid);
        int firstVisible = tv.VisualLines[0].FirstDocumentLine.LineNumber;
        int lastVisible = tv.VisualLines[^1].FirstDocumentLine.LineNumber;
        Assert.True(firstVisible > 100);

        var from = PointOf(window, view, lastVisible - 1, 3);
        AltDrag(window, from, new Point(from.X, tv.TranslatePoint(new Point(0, -30), window)!.Value.Y));

        var selection = view.TextEditorControl.TextArea.Selection;
        Assert.IsType<RectangleSelection>(selection);
        int endLine = Math.Max(selection.StartPosition.Line, selection.EndPosition.Line);
        int startLine = Math.Min(selection.StartPosition.Line, selection.EndPosition.Line);
        Assert.Equal(lastVisible - 1, endLine);
        Assert.Equal(firstVisible, startLine); // 마지막 줄로 튀지 않고 첫 보이는 줄에서 멈춘다
        window.Close();
    });

    [Fact]
    public Task A_new_view_for_a_tab_with_a_column_block_restores_only_the_caret_not_the_surrounding_selection() => Ui.Run(() =>
    {
        var (window, view, vm) = Show("XXabYY\nXXcdYY\nXXefYY");
        view.Carets!.SelectBlock(new TextViewPosition(1, 3), new TextViewPosition(3, 5));
        Assert.True(vm.SelectionIsBlock);
        Assert.True(vm.SelectionLength > 6); // 둘레 범위는 블록 밖 글을 품는다

        var again = new SqlEditorView { DataContext = vm }; // 탭을 다시 보일 때처럼 같은 탭으로 View 를 새로 만든다
        var second = new Window { Width = 900, Height = 600, Content = again };
        window.Close();
        second.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.True(again.TextEditorControl.TextArea.Selection.IsEmpty);
        Assert.Equal(0, vm.SelectionLength);
        Assert.False(vm.HasMultiPieceSelection);
        Assert.Empty(vm.SelectionPieces);
        second.Close();
    });

    [Fact]
    public Task Text_marked_with_the_block_format_by_another_process_pastes_as_a_block() => Ui.Run(async () =>
    {
        var (window, view, vm) = Show("abc\nabc");
        // 이 프로세스가 복사하지 않은 글(마지막 블록 복사본과 다른 글)에 표지만 붙어 있는 경우.
        var item = new DataTransferItem();
        item.SetText("Q1\nQ2");
        item.Set(MultiCaretController.BlockFormat, "1");
        var data = new Avalonia.Input.DataTransfer();
        data.Add(item);
        await window.Clipboard!.SetDataAsync(data);

        vm.Select(1, 0);
        view.Paste();
        await WaitFor(() => vm.Text != "abc\nabc");
        Assert.Equal("aQ1bc\naQ2bc", Unix(vm.Text)); // 표지가 없으면 한 덩어리로 붙어 "aQ1\nQ2bc\nabc" 가 된다
        window.Close();
    });

    [Fact]
    public Task Block_paste_at_a_middle_column_creates_and_fills_the_end_line_so_it_is_not_dropped() => Ui.Run(() =>
    {
        var (window, view, vm) = Show("ab\ncd");
        vm.Select(4, 0); // 마지막 줄 열 2: 끝 줄이 새로 생기고 시작 열보다 짧다
        view.Carets!.PasteText("X\nY\nZ", block: true);
        Assert.Equal("ab\ncXd\n Y\n Z", Unix(vm.Text));
        vm.Document.UndoStack.Undo();
        Assert.Equal("ab\ncd", Unix(vm.Text));
        window.Close();
    });

    [Fact]
    public Task Block_paste_pads_an_existing_end_line_that_is_shorter_than_the_start_column() => Ui.Run(() =>
    {
        var (window, view, vm) = Show("abcdef\nab\nabcdef");
        vm.Select(4, 0); // 1번째 줄 열 5
        view.Carets!.PasteText("1\n2", block: true);
        Assert.Equal("abcd1ef\nab  2\nabcdef", Unix(vm.Text));
        window.Close();
    });

    [Fact]
    public Task Block_copy_ending_with_a_line_break_still_pastes_all_rows() => Ui.Run(() =>
    {
        var (window, view, vm) = Show("ab\ncd");
        vm.Select(3, 0);
        view.Carets!.PasteText("X\nY\n", block: true); // 마지막 행이 빈 행인 블록의 글(끝 줄바꿈)
        Assert.StartsWith("ab\nXcd\nY", Unix(vm.Text), StringComparison.Ordinal);
        window.Close();
    });

    [Fact]
    public Task Selecting_a_range_clears_the_block_flag_even_without_a_view() => Ui.Run(() =>
    {
        var (window, view, vm) = Show("abc\nabc");
        view.Carets!.SelectBlock(new TextViewPosition(1, 1), new TextViewPosition(2, 3));
        Assert.True(vm.SelectionIsBlock);
        view.DataContext = null; // View 가 떨어진 탭
        vm.Select(1, 2);
        Assert.False(vm.SelectionIsBlock);
        window.Close();
    });

    // ---- 순수 계산 ---------------------------------------------------------------------------

    [Fact]
    public void Merge_joins_overlapping_and_touching_slots_and_keeps_the_main_caret()
    {
        var merged = MultiCaretMath.Merge(
        [
            new CaretSlot(0, 3, 3, false),
            new CaretSlot(2, 6, 2, true),
            new CaretSlot(6, 6, 6, false),
            new CaretSlot(10, 12, 12, false),
        ]);
        Assert.Equal(2, merged.Count);
        Assert.Equal(new CaretSlot(0, 6, 0, true), merged[0]); // 주 커서가 선택 앞쪽에 있었으므로 합친 뒤에도 앞쪽
        Assert.Equal(new CaretSlot(10, 12, 12, false), merged[1]);
    }

    [Theory]
    [InlineData("a\nb\nc", 3, new[] { "a", "b", "c" })]
    [InlineData("a\nb\nc\n", 3, new[] { "a", "b", "c" })]
    [InlineData("a\r\nb", 2, new[] { "a", "b" })]
    [InlineData("a\nb", 3, new[] { "a\nb", "a\nb", "a\nb" })]
    [InlineData("one", 2, new[] { "one", "one" })]
    public void DistributePaste_gives_one_line_per_cursor_when_counts_match(string clipboard, int cursors, string[] expected)
        => Assert.Equal(expected, MultiCaretMath.DistributePaste(clipboard, cursors));

    [Fact]
    public void FilterTypedText_drops_control_characters_but_keeps_tab_and_newline()
    {
        Assert.Equal(string.Empty, MultiCaretMath.FilterTypedText("\b"));
        Assert.Equal(string.Empty, MultiCaretMath.FilterTypedText("\u001b"));
        Assert.Equal("a\tb\n", MultiCaretMath.FilterTypedText("a\tb\n"));
    }
}
