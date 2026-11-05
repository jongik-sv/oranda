using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.TextInput;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Rendering;
using Oranda.App.Features.SqlEditor;

namespace Oranda.App.Tests.Features.SqlEditor;

/// <summary>
/// macOS 한글 조합 글자 표시. 실제 입력기는 헤드리스로 재현할 수 없으므로 입력기가 부르는 길(입력기 클라이언트 요청, SetPreeditText, 확정 TextInput)을 직접 부른다.
/// </summary>
public sealed class ImePreeditTests
{
    private static (Window Window, SqlEditorView View, SqlEditorViewModel Vm) Show(string text)
    {
        SqlEditorView.PreeditEnabled = true;
        var vm = new SqlEditorViewModel(Guid.NewGuid(), text);
        var view = new SqlEditorView { DataContext = vm };
        var window = new Window { Width = 900, Height = 600, Content = view };
        window.Show();
        view.TextEditorControl.TextArea.Focus();
        Dispatcher.UIThread.RunJobs();
        return (window, view, vm);
    }

    /// <summary>입력기가 포커스된 편집기에서 클라이언트를 찾는 요청.</summary>
    private static TextInputMethodClient? RequestClient(SqlEditorView view)
    {
        var args = new TextInputMethodClientRequestedEventArgs { RoutedEvent = InputElement.TextInputMethodClientRequestedEvent };
        view.TextEditorControl.TextArea.RaiseEvent(args);
        return args.Client;
    }

    private static void Type(SqlEditorView view, string text)
        => view.TextEditorControl.TextArea.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = text });

    private static void Key(SqlEditorView view, Key key, PhysicalKey physical = PhysicalKey.None)
        => view.TextEditorControl.TextArea.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, PhysicalKey = physical });

    /// <summary>화면에 그려진 줄의 조합 요소들(줄 번호 → 요소 글자).</summary>
    private static List<string> ShownPreedit(SqlEditorView view)
    {
        var textView = view.TextEditorControl.TextArea.TextView;
        textView.EnsureVisualLines();
        return [.. textView.VisualLines.SelectMany(l => l.Elements).OfType<PreeditPresenter.PreeditElement>().Select(e => e.Text)];
    }

    [Fact]
    public Task Client_is_replaced_on_macOS_with_one_that_supports_preedit() => Ui.Run(() =>
    {
        var (window, view, _) = Show("select 1");
        var client = RequestClient(view);

        Assert.IsType<PreeditInputMethodClient>(client);
        Assert.True(client.SupportsPreedit);
        Assert.Same(client, RequestClient(view)); // 요청마다 새로 만들지 않는다
        window.Close();
    });

    [Fact]
    public Task Client_is_left_alone_when_the_option_is_off() => Ui.Run(() =>
    {
        try
        {
            var (window, view, _) = Show("select 1");
            SqlEditorView.PreeditEnabled = false; // macOS 아닌 곳의 상태
            var client = RequestClient(view);

            Assert.NotNull(client);
            Assert.IsNotType<PreeditInputMethodClient>(client);
            Assert.False(client.SupportsPreedit);
            window.Close();
        }
        finally
        {
            SqlEditorView.PreeditEnabled = true;
        }
    });

    [Fact]
    public Task Read_only_editor_gets_no_client_like_before() => Ui.Run(() =>
    {
        var (window, view, vm) = Show("select 1");
        view.TextEditorControl.IsReadOnly = true;

        Assert.Null(RequestClient(view));
        view.Preedit.Set("ㅎ"); // 읽기 전용이면 혹시 불려도 보이지 않는다
        Assert.False(view.Preedit.IsActive);
        Assert.Equal("select 1", vm.Text);
        window.Close();
    });

    [Fact]
    public Task Client_reports_the_same_values_as_the_wrapped_one() => Ui.Run(() =>
    {
        var (window, view, vm) = Show("select 1");
        vm.Select(3, 2);
        var wrapped = Assert.IsType<PreeditInputMethodClient>(RequestClient(view));

        Assert.Equal(wrapped.Inner.CursorRectangle, wrapped.CursorRectangle);
        Assert.Equal(wrapped.Inner.SurroundingText, wrapped.SurroundingText);
        Assert.Equal(wrapped.Inner.Selection, wrapped.Selection);
        Assert.Same(wrapped.Inner.TextViewVisual, wrapped.TextViewVisual);
        Assert.Equal("select 1", wrapped.SurroundingText);
        window.Close();
    });

    [Fact]
    public Task Client_events_follow_the_wrapped_client() => Ui.Run(() =>
    {
        var (window, view, vm) = Show("select 1");
        var client = RequestClient(view)!;
        int rects = 0;
        client.CursorRectangleChanged += (_, _) => rects++;
        vm.Select(4, 0); // 캐럿이 움직이면 안쪽 클라이언트가 알리고, 후보 창 위치가 따라와야 한다

        Assert.True(rects > 0);
        window.Close();
    });

    [Fact]
    public Task Preedit_shows_at_the_caret_without_touching_the_document() => Ui.Run(() =>
    {
        var (window, view, vm) = Show("select  from dual");
        vm.Select(7, 0);
        vm.Document.UndoStack.ClearAll();
        vm.IsDirty = false;
        var client = RequestClient(view)!;
        long version = vm.Version;

        client.SetPreeditText("ㅎ");
        client.SetPreeditText("하");

        Assert.Equal("하", view.Preedit.Text);
        Assert.Equal([7], view.Preedit.Offsets);
        Assert.Equal(["하"], ShownPreedit(view));
        Assert.Equal("select  from dual", vm.Text);
        Assert.Equal(version, vm.Version);
        Assert.False(vm.IsDirty);
        Assert.False(vm.Document.UndoStack.CanUndo);
        Assert.Equal(7, view.TextEditorControl.CaretOffset);
        window.Close();
    });

    [Fact]
    public Task Preedit_pushes_the_following_text_instead_of_overlapping_it() => Ui.Run(() =>
    {
        var (window, view, vm) = Show("select  from dual");
        vm.Select(7, 0);
        var textView = view.TextEditorControl.TextArea.TextView;
        textView.EnsureVisualLines();
        double before = textView.GetVisualLine(1)!.Elements.Sum(e => e.VisualLength);

        RequestClient(view)!.SetPreeditText("한글");
        textView.EnsureVisualLines();
        var line = textView.GetVisualLine(1)!;

        Assert.Equal(before + 2, line.Elements.Sum(e => e.VisualLength)); // 조합 글자만큼 자리를 더 차지한다
        Assert.Equal("select  from dual".Length, line.VisualLength - 2);
        window.Close();
    });

    [Fact]
    public Task Preedit_shows_at_the_end_of_a_line_and_in_an_empty_document() => Ui.Run(() =>
    {
        var (window, view, vm) = Show("abc");
        vm.Select(3, 0);
        RequestClient(view)!.SetPreeditText("한");
        Assert.Equal(["한"], ShownPreedit(view));
        view.Preedit.Clear();

        var (w2, v2, vm2) = Show(string.Empty);
        vm2.Select(0, 0);
        RequestClient(v2)!.SetPreeditText("한");
        Assert.Equal(["한"], ShownPreedit(v2));
        w2.Close();
        window.Close();
    });

    [Fact]
    public Task Empty_preedit_clears_the_display() => Ui.Run(() =>
    {
        var (window, view, vm) = Show("abc");
        vm.Select(1, 0);
        var client = RequestClient(view)!;
        client.SetPreeditText("한");
        client.SetPreeditText(string.Empty);

        Assert.False(view.Preedit.IsActive);
        Assert.Empty(ShownPreedit(view));
        client.SetPreeditText("한");
        client.SetPreeditText(null);
        Assert.Empty(ShownPreedit(view));
        window.Close();
    });

    [Fact]
    public Task Two_argument_overload_shows_the_same_way() => Ui.Run(() =>
    {
        var (window, view, vm) = Show("abc");
        vm.Select(1, 0);
        RequestClient(view)!.SetPreeditText("한", 1);

        Assert.Equal(["한"], ShownPreedit(view));
        window.Close();
    });

    [Fact]
    public Task Commit_text_input_clears_the_preedit_and_enters_the_document_once() => Ui.Run(() =>
    {
        var (window, view, vm) = Show("abc");
        vm.Select(3, 0);
        RequestClient(view)!.SetPreeditText("한");
        Type(view, "한"); // 확정 글자: 입력기가 조합 글자를 이 글자로 바꿔 보낸다

        Assert.Equal("abc한", vm.Text);
        Assert.False(view.Preedit.IsActive);
        Assert.Empty(ShownPreedit(view));
        window.Close();
    });

    [Fact]
    public Task Commit_followed_by_next_composition_shows_only_the_new_one() => Ui.Run(() =>
    {
        var (window, view, vm) = Show("abc");
        vm.Select(3, 0);
        var client = RequestClient(view)!;
        client.SetPreeditText("한");
        Type(view, "한");           // 입력기 순서: 앞 글자 확정(insertText)
        client.SetPreeditText("ㄱ"); // 그다음 새 조합(setMarkedText)

        Assert.Equal("abc한", vm.Text);
        Assert.Equal(["ㄱ"], ShownPreedit(view));
        Assert.Equal([4], view.Preedit.Offsets);
        window.Close();
    });

    [Fact]
    public Task Escape_clears_the_preedit() => Ui.Run(() =>
    {
        var (window, view, vm) = Show("abc");
        vm.Select(3, 0);
        RequestClient(view)!.SetPreeditText("한");
        Key(view, Avalonia.Input.Key.ImeProcessed, PhysicalKey.Escape);

        Assert.False(view.Preedit.IsActive);
        window.Close();
    });

    [Fact]
    public Task Other_keys_during_composition_keep_the_preedit() => Ui.Run(() =>
    {
        var (window, view, vm) = Show("abc");
        vm.Select(3, 0);
        RequestClient(view)!.SetPreeditText("한");
        Key(view, Avalonia.Input.Key.ImeProcessed, PhysicalKey.G);

        Assert.Equal(["한"], ShownPreedit(view));
        window.Close();
    });

    [Fact]
    public Task Focus_loss_clears_the_preedit() => Ui.Run(() =>
    {
        var vm = new SqlEditorViewModel(Guid.NewGuid(), "abc");
        var view = new SqlEditorView { DataContext = vm };
        var other = new Button();
        var window = new Window { Width = 900, Height = 600, Content = new StackPanel { Children = { view, other } } };
        window.Show();
        view.TextEditorControl.TextArea.Focus();
        Dispatcher.UIThread.RunJobs();
        vm.Select(3, 0);
        RequestClient(view)!.SetPreeditText("한");
        other.Focus();
        Dispatcher.UIThread.RunJobs();

        Assert.False(view.Preedit.IsActive);
        window.Close();
    });

    [Fact]
    public Task Pointer_press_clears_the_preedit() => Ui.Run(() =>
    {
        var (window, view, vm) = Show("abc");
        vm.Select(0, 0); // 눌러도 캐럿이 움직이지 않는 자리: 캐럿 이동이 아니라 포인터 처리기가 지우는지 가린다
        RequestClient(view)!.SetPreeditText("한");
        var area = view.TextEditorControl.TextArea;
        var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true);
        area.RaiseEvent(new PointerPressedEventArgs(area, pointer, area, new Point(5, 5), 0, new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None));

        Assert.Equal(0, view.TextEditorControl.CaretOffset);
        Assert.False(view.Preedit.IsActive);
        window.Close();
    });

    [Fact]
    public Task Caret_move_clears_the_preedit() => Ui.Run(() =>
    {
        var (window, view, vm) = Show("abc def");
        vm.Select(3, 0);
        RequestClient(view)!.SetPreeditText("한");
        view.TextEditorControl.CaretOffset = 5;

        Assert.False(view.Preedit.IsActive);
        Assert.Empty(ShownPreedit(view));
        window.Close();
    });

    [Fact]
    public Task Document_replacement_clears_the_preedit() => Ui.Run(() =>
    {
        var (window, view, vm) = Show("abc");
        vm.Select(0, 0); // 새 문서에서도 캐럿이 0 이라 캐럿 이동이 아니라 문서 교체 처리기가 지우는지 가린다
        RequestClient(view)!.SetPreeditText("한");
        view.TextEditorControl.Document = new AvaloniaEdit.Document.TextDocument("xyz");

        Assert.False(view.Preedit.IsActive);
        window.Close();
    });

    [Fact]
    public Task Preedit_shows_at_every_extra_cursor() => Ui.Run(() =>
    {
        var (window, view, vm) = Show("a\nb\nc");
        vm.Select(1, 0);
        view.Carets!.AddCaret(3);
        view.Carets.AddCaret(5);
        RequestClient(view)!.SetPreeditText("한");

        Assert.Equal([1, 3, 5], view.Preedit.Offsets);
        Assert.Equal(["한", "한", "한"], ShownPreedit(view));
        Assert.Equal("a\nb\nc", vm.Text.Replace("\r\n", "\n"));
        window.Close();
    });

    [Fact]
    public Task Preedit_shows_on_every_line_of_a_column_block() => Ui.Run(() =>
    {
        var (window, view, vm) = Show("abcdef\nabcdef\nabcdef");
        view.Carets!.SelectBlock(new TextViewPosition(1, 3), new TextViewPosition(3, 3)); // 폭 0 열 블록
        RequestClient(view)!.SetPreeditText("한");

        Assert.Equal(["한", "한", "한"], ShownPreedit(view));
        Assert.Equal(3, view.Preedit.Offsets.Count);
        window.Close();

        var (w2, v2, vm2) = Show("abcdef\nabcdef\nabcdef");
        v2.Carets!.SelectBlock(new TextViewPosition(1, 2), new TextViewPosition(3, 4)); // 폭 있는 열 블록: 블록 글자는 확정 전까지 그대로
        RequestClient(v2)!.SetPreeditText("한");
        Assert.Equal(["한", "한", "한"], ShownPreedit(v2));
        Assert.Equal("abcdef\nabcdef\nabcdef", vm2.Text.Replace("\r\n", "\n"));
        w2.Close();
    });

    [Fact]
    public Task Commit_on_extra_cursors_still_types_once_at_each() => Ui.Run(() =>
    {
        var (window, view, vm) = Show("a\nb");
        vm.Select(1, 0);
        view.Carets!.AddCaret(3);
        RequestClient(view)!.SetPreeditText("한");
        Type(view, "한");

        Assert.Equal("a한\nb한", vm.Text.Replace("\r\n", "\n"));
        Assert.Empty(ShownPreedit(view));
        window.Close();
    });

    [Fact]
    public Task Preedit_is_not_drawn_off_screen_and_returns_when_scrolled_back() => Ui.Run(() =>
    {
        string text = string.Join('\n', Enumerable.Range(0, 300).Select(i => $"line {i}"));
        var (window, view, vm) = Show(text);
        vm.Select(0, 0);
        RequestClient(view)!.SetPreeditText("한");
        view.TextEditorControl.ScrollToLine(250); // 캐럿이 화면 밖: 그려지지 않지만 조합은 유지된다
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(ShownPreedit(view));
        Assert.True(view.Preedit.IsActive);
        view.TextEditorControl.ScrollToLine(1); // 돌아오면 다시 보인다
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(["한"], ShownPreedit(view));
        window.Close();
    });

    [Fact]
    public Task Preedit_shows_at_the_start_of_a_hyperlink() => Ui.Run(() =>
    {
        const string Source = "-- see https://example.com/a";
        var (window, view, vm) = Show(Source);
        vm.Select(7, 0); // 링크 요소가 시작하는 자리: 링크 생성기가 앞서면 조합 요소가 밀린다
        RequestClient(view)!.SetPreeditText("한");

        Assert.Equal(["한"], ShownPreedit(view));
        window.Close();
    });

    [Fact]
    public Task Preedit_shows_at_the_start_of_a_fold_and_survives_reattaching_the_folding_manager() => Ui.Run(() =>
    {
        var (window, view, vm) = Show("select (\n  1\n) from dual");
        view.RefreshStructureNow();
        view.Fold(FoldAction.FoldAll); // 접힌 구간의 시작에서도 조합 글자가 보여야 한다
        int start = view.Folding!.AllFoldings.Min(f => f.StartOffset);
        vm.Select(start, 0);
        RequestClient(view)!.SetPreeditText("한");
        Assert.Equal(["한"], ShownPreedit(view));
        view.Preedit.Clear();

        // 다른 문서로 바꿨다 돌아오면 접기 관리자가 생성기를 맨 앞에 다시 넣는다.
        view.DataContext = new SqlEditorViewModel(Guid.NewGuid(), "select (\n  1\n) from dual");
        view.RefreshStructureNow();
        view.Fold(FoldAction.FoldAll);
        start = view.Folding!.AllFoldings.Min(f => f.StartOffset);
        view.TextEditorControl.CaretOffset = start;
        RequestClient(view)!.SetPreeditText("한");
        Assert.Equal(["한"], ShownPreedit(view));
        window.Close();
    });
}
