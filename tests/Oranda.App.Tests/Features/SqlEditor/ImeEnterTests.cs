using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using Oranda.App.Features.SqlEditor;

namespace Oranda.App.Tests.Features.SqlEditor;

/// <summary>
/// macOS 한글 조합 중 Enter 한 번으로 줄 바꾸기. 실제 조합은 헤드리스로 재현할 수 없으므로,
/// 사용자 기록(<c>ORANDA_KEYTRACE=1</c>)의 순서만 이벤트로 재현한다: <c>Key.ImeProcessed</c>+<c>PhysicalKey.Enter</c> KeyDown 뒤에 확정 글자 TextInput.
/// 기대 결과는 「확정 글자를 넣고 일반 Return 을 누른 것」과 같은 문서·캐럿이다.
/// </summary>
public sealed class ImeEnterTests
{
    private static (Window Window, SqlEditorView View, SqlEditorViewModel Vm) Show(string text)
    {
        var vm = new SqlEditorViewModel(Guid.NewGuid(), text);
        var view = new SqlEditorView { DataContext = vm };
        var window = new Window { Width = 900, Height = 600, Content = view };
        window.Show();
        view.TextEditorControl.TextArea.Focus(); // 실제 입력은 포커스가 있는 편집기에만 온다(AvaloniaEdit 의 Enter 처리도 포커스가 있어야 동작한다)
        Dispatcher.UIThread.RunJobs();
        return (window, view, vm);
    }

    private static void RaiseKey(SqlEditorView view, Key key, PhysicalKey physical, KeyModifiers modifiers = KeyModifiers.None)
        => view.TextEditorControl.TextArea.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = key,
            PhysicalKey = physical,
            KeyModifiers = modifiers,
        });

    /// <summary>입력기가 먼저 받은 Enter(조합 중). 조합 아닌 Enter 는 <see cref="Return"/>.</summary>
    private static void ImeEnter(SqlEditorView view, KeyModifiers modifiers = KeyModifiers.None, PhysicalKey physical = PhysicalKey.Enter)
        => RaiseKey(view, Key.ImeProcessed, physical, modifiers);

    private static void Return(SqlEditorView view) => RaiseKey(view, Key.Return, PhysicalKey.Enter);

    private static void Type(SqlEditorView view, string text)
        => view.TextEditorControl.TextArea.RaiseEvent(new TextInputEventArgs
        {
            RoutedEvent = InputElement.TextInputEvent,
            Text = text,
        });

    /// <summary>확정 글자를 넣고 일반 Return 을 누른 결과(기준). 문서·캐럿을 돌려준다.</summary>
    private static (string Text, int Caret) Baseline(string text, int caret, string committed, Action<SqlEditorView>? setup = null)
    {
        var (window, view, vm) = Show(text);
        vm.Select(caret, 0);
        setup?.Invoke(view);
        Type(view, committed);
        Return(view);
        var result = (vm.Text, view.TextEditorControl.CaretOffset);
        window.Close();
        return result;
    }

    [Fact]
    public Task Commit_after_ime_enter_inserts_the_char_and_breaks_the_line_once() => Ui.Run(() =>
    {
        SqlEditorView.ImeEnterEnabled = true;
        var (window, view, vm) = Show("한");
        vm.Select(1, 0);
        ImeEnter(view);
        Type(view, "글");

        Assert.Equal("한글\n", vm.Text.Replace("\r\n", "\n"));
        Assert.Equal(vm.Text.Length, view.TextEditorControl.CaretOffset);
        Assert.Equal(Baseline("한", 1, "글"), (vm.Text, view.TextEditorControl.CaretOffset));
        window.Close();
    });

    [Fact]
    public Task Ime_enter_follows_the_normal_return_indentation() => Ui.Run(() =>
    {
        SqlEditorView.ImeEnterEnabled = true;
        const string Source = "    select 1,";
        var (window, view, vm) = Show(Source);
        vm.Select(Source.Length, 0);
        ImeEnter(view);
        Type(view, "글");

        var expected = Baseline(Source, Source.Length, "글");
        Assert.Equal(expected, (vm.Text, view.TextEditorControl.CaretOffset));
        Assert.EndsWith("\n    ", vm.Text.Replace("\r\n", "\n")); // 자동 들여쓰기가 따라온다
        window.Close();
    });

    [Fact]
    public Task Ime_enter_replaces_a_selection_like_normal_return() => Ui.Run(() =>
    {
        SqlEditorView.ImeEnterEnabled = true;
        var (window, view, vm) = Show("select abc from dual");
        vm.Select(7, 3);
        ImeEnter(view);
        Type(view, "글");

        var expected = Baseline("select abc from dual", 7, "글", v => v.TextEditorControl.Select(7, 3));
        Assert.Equal(expected, (vm.Text, view.TextEditorControl.CaretOffset));
        window.Close();
    });

    [Fact]
    public Task Normal_return_breaks_the_line_once_and_leaves_no_pending_mark() => Ui.Run(() =>
    {
        SqlEditorView.ImeEnterEnabled = true;
        var (window, view, vm) = Show("a");
        vm.Select(1, 0);
        Return(view);
        Assert.Equal("a\n", vm.Text.Replace("\r\n", "\n"));

        Type(view, "b"); // 표시가 남지 않았으니 줄이 더 바뀌지 않는다
        Assert.Equal("a\nb", vm.Text.Replace("\r\n", "\n"));
        window.Close();
    });

    [Fact]
    public Task Mark_is_dropped_when_no_commit_arrives_before_the_next_key() => Ui.Run(() =>
    {
        SqlEditorView.ImeEnterEnabled = true;
        var (window, view, vm) = Show("a");
        vm.Select(1, 0);
        ImeEnter(view); // 확정 글자가 오지 않았다
        RaiseKey(view, Key.ImeProcessed, PhysicalKey.B); // 다음 키가 먼저 왔다
        Type(view, "b");

        Assert.Equal("ab", vm.Text);
        window.Close();
    });

    [Fact]
    public Task Mark_expires_when_the_commit_comes_too_late() => Ui.Run(() =>
    {
        SqlEditorView.ImeEnterEnabled = true;
        var (window, view, vm) = Show("a");
        vm.Select(1, 0);
        view.ImeEnterWindow = TimeSpan.FromMilliseconds(1);
        ImeEnter(view);
        Thread.Sleep(30);
        Type(view, "b");

        Assert.Equal("ab", vm.Text);
        window.Close();
    });

    [Fact]
    public Task Mark_is_dropped_when_focus_leaves_the_editor() => Ui.Run(() =>
    {
        SqlEditorView.ImeEnterEnabled = true;
        // 뷰를 옮기지 않도록 처음부터 단추와 함께 배치한다(LostFocus 핸들러만 표시를 지우게 한다).
        var vm = new SqlEditorViewModel(Guid.NewGuid(), "a");
        var view = new SqlEditorView { DataContext = vm };
        var other = new Button();
        var window = new Window { Width = 900, Height = 600, Content = new StackPanel { Children = { view, other } } };
        window.Show();
        view.TextEditorControl.TextArea.Focus();
        Dispatcher.UIThread.RunJobs();
        vm.Select(1, 0);
        ImeEnter(view);
        other.Focus();
        Dispatcher.UIThread.RunJobs();
        Assert.False(view.TextEditorControl.TextArea.IsFocused);
        view.TextEditorControl.TextArea.Focus(); // 돌아와도 표시는 이미 지워졌다
        Type(view, "b");

        Assert.Equal("ab", vm.Text);
        window.Close();
    });

    [Fact]
    public Task Ime_enter_with_a_modifier_is_not_touched() => Ui.Run(() =>
    {
        SqlEditorView.ImeEnterEnabled = true;
        var (window, view, vm) = Show("a");
        vm.Select(1, 0);
        ImeEnter(view, KeyModifiers.Meta); // ⌘↵ 는 실행 명령 쪽이다
        Type(view, "b");

        Assert.Equal("ab", vm.Text);
        window.Close();
    });

    [Fact]
    public Task Numpad_enter_during_composition_also_breaks_the_line() => Ui.Run(() =>
    {
        SqlEditorView.ImeEnterEnabled = true;
        var (window, view, vm) = Show("한");
        vm.Select(1, 0);
        ImeEnter(view, physical: PhysicalKey.NumPadEnter);
        Type(view, "글");

        Assert.Equal("한글\n", vm.Text.Replace("\r\n", "\n"));
        window.Close();
    });

    [Fact]
    public Task Nothing_changes_when_the_option_is_off() => Ui.Run(() =>
    {
        SqlEditorView.ImeEnterEnabled = false; // macOS 아닌 곳의 상태
        try
        {
            var (window, view, vm) = Show("한");
            vm.Select(1, 0);
            ImeEnter(view);
            Type(view, "글");

            Assert.Equal("한글", vm.Text);
            window.Close();
        }
        finally
        {
            SqlEditorView.ImeEnterEnabled = true;
        }
    });

    [Fact]
    public Task Ime_enter_adds_the_line_break_at_every_extra_cursor() => Ui.Run(() =>
    {
        SqlEditorView.ImeEnterEnabled = true;
        const string Source = "a\nb";
        var (window, view, vm) = Show(Source);
        vm.Select(1, 0);
        view.Carets!.AddCaret(3);
        ImeEnter(view);
        Type(view, "글");

        var expected = Baseline(Source, 1, "글", v => v.Carets!.AddCaret(3));
        Assert.Equal(expected, (vm.Text, view.TextEditorControl.CaretOffset));
        Assert.Equal("a글\n\nb글\n", vm.Text.Replace("\r\n", "\n"));
        window.Close();
    });

    [Fact]
    public Task Ime_enter_on_a_column_block_matches_normal_return() => Ui.Run(() =>
    {
        SqlEditorView.ImeEnterEnabled = true;
        const string Source = "abcdef\nabcdef";
        void SetBlock(SqlEditorView v) => v.Carets!.SelectBlock(new TextViewPosition(1, 2), new TextViewPosition(2, 4));
        var (window, view, vm) = Show(Source);
        SetBlock(view);
        ImeEnter(view);
        Type(view, "글");

        var expected = Baseline(Source, 0, "글", SetBlock);
        Assert.Equal(expected, (vm.Text, view.TextEditorControl.CaretOffset));
        window.Close();
    });

    [Fact]
    public Task Ime_enter_in_a_read_only_editor_changes_nothing() => Ui.Run(() =>
    {
        SqlEditorView.ImeEnterEnabled = true;
        var (window, view, vm) = Show("ab");
        vm.Select(2, 0);
        view.TextEditorControl.IsReadOnly = true;
        ImeEnter(view);
        Type(view, "글");

        Assert.Equal("ab", vm.Text);
        window.Close();
    });

    [Fact]
    public Task Commit_and_line_break_undo_in_the_steps_normal_typing_and_return_use() => Ui.Run(() =>
    {
        SqlEditorView.ImeEnterEnabled = true;
        var (window, view, vm) = Show("한");
        vm.Select(1, 0);
        ImeEnter(view);
        Type(view, "글");

        int steps = 0;
        var stack = vm.Document.UndoStack;
        while (stack.CanUndo)
        {
            stack.Undo();
            steps++;
            if (vm.Text == "한")
            {
                break;
            }
        }

        // 일반 경로(확정 글자 입력 뒤 Return)와 같은 단계 수여야 한다.
        var (w2, v2, vm2) = Show("한");
        vm2.Select(1, 0);
        Type(v2, "글");
        Return(v2);
        int baseline = 0;
        while (vm2.Document.UndoStack.CanUndo)
        {
            vm2.Document.UndoStack.Undo();
            baseline++;
            if (vm2.Text == "한")
            {
                break;
            }
        }

        Assert.Equal(baseline, steps);
        Assert.Equal(2, steps); // 확정 글자 입력 1단계 + 줄 바꾸기 1단계
        w2.Close();
        window.Close();
    });

    private sealed class Item(string text) : ICompletionData
    {
        public IImage? Image => null;

        public string Text => text;

        public object Content => text;

        public object Description => text;

        public double Priority => 0;

        public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
            => textArea.Document.Replace(completionSegment, text);
    }

    private static CompletionWindow OpenCompletion(SqlEditorView view)
    {
        var area = view.TextEditorControl.TextArea;
        if (TopLevel.GetTopLevel(area) is { } top)
        {
            // CompletionController.EnsureEditorTheme 와 같다: 목록 템플릿이 붙으려면 AvaloniaEdit 테마가 필요하다.
            top.Styles.Add(new StyleInclude(new Uri("avares://Oranda.App/")) { Source = new Uri("avares://AvaloniaEdit/Themes/Fluent/AvaloniaEdit.xaml") });
        }

        var window = new CompletionWindow(area) { StartOffset = 0, EndOffset = area.Caret.Offset };
        window.CompletionList.CompletionData.Add(new Item("글자"));
        window.Show();
        window.CompletionList.ApplyTemplate();
        Dispatcher.UIThread.RunJobs();
        Assert.NotNull(window.CompletionList.ListBox); // 팝업에 AvaloniaEdit 템플릿이 붙었다
        window.CompletionList.SelectedItem = window.CompletionList.CompletionData[0];
        return window;
    }

    [Fact]
    public Task Ime_enter_with_the_completion_list_open_does_what_normal_return_does() => Ui.Run(() =>
    {
        SqlEditorView.ImeEnterEnabled = true;
        var (w1, v1, vm1) = Show("");
        var c1 = OpenCompletion(v1);
        ImeEnter(v1);
        Type(v1, "글");
        Dispatcher.UIThread.RunJobs();

        var (w2, v2, vm2) = Show("");
        var c2 = OpenCompletion(v2);
        Type(v2, "글");
        Return(v2);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(vm2.Text, vm1.Text);
        Assert.Equal(v2.TextEditorControl.CaretOffset, v1.TextEditorControl.CaretOffset);
        c1.Close();
        c2.Close();
        w1.Close();
        w2.Close();
    });

    [Theory]
    [InlineData("글", true)]
    [InlineData("ㄱ", true)]          // 호환 자모
    [InlineData("\u1112", true)]      // 조합용 자모
    [InlineData("\uA960", true)]      // 자모 확장 A
    [InlineData("\uD7B0", true)]      // 자모 확장 B
    [InlineData("abc글", true)]        // 마지막 문자 기준
    [InlineData("글a", false)]
    [InlineData("韓", false)]
    [InlineData("か", false)]
    [InlineData("你", false)]
    [InlineData("\U0001F600", false)]
    [InlineData("a", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Hangul_check_looks_at_the_last_character(string? text, bool expected)
        => Assert.Equal(expected, SqlEditorView.EndsWithHangul(text));

    [Theory]
    [InlineData("韓")]
    [InlineData("か")]
    [InlineData("你")]
    [InlineData("\U0001F600")]
    [InlineData("a")]
    public Task Non_hangul_commit_after_ime_enter_does_not_break_the_line(string committed) => Ui.Run(() =>
    {
        SqlEditorView.ImeEnterEnabled = true;
        var (window, view, vm) = Show("x");
        vm.Select(1, 0);
        ImeEnter(view);
        Type(view, committed);

        Assert.Equal("x" + committed, vm.Text);
        window.Close();
    });

    [Fact]
    public Task Jamo_only_commit_after_ime_enter_breaks_the_line() => Ui.Run(() =>
    {
        SqlEditorView.ImeEnterEnabled = true;
        var (window, view, vm) = Show("x");
        vm.Select(1, 0);
        ImeEnter(view);
        Type(view, "ㄱ");

        Assert.Equal("xㄱ\n", vm.Text.Replace("\r\n", "\n"));
        window.Close();
    });

    [Fact]
    public Task Plain_return_still_breaks_the_line_for_latin_input() => Ui.Run(() =>
    {
        SqlEditorView.ImeEnterEnabled = true;
        var (window, view, vm) = Show("x");
        vm.Select(1, 0);
        Type(view, "a");
        Return(view); // 영문 입력기는 조합이 없어 Key=Return 으로 온다
        Assert.Equal("xa\n", vm.Text.Replace("\r\n", "\n"));
        window.Close();
    });
}
