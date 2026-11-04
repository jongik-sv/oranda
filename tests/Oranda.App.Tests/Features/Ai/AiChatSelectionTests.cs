using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.Ai.Chat;
using Oranda.App.Shell.Keymaps;
using Oranda.App.Tests.Features.SqlEditor;

namespace Oranda.App.Tests.Features.Ai;

/// <summary>
/// AI 대화의 여러 문단 드래그 선택(Headless). 문단·목록·표·코드·앞뒤 대화 항목을 가로질러 고르고, 복사 글은 화면 순서·구분을 지킨다.
/// <c>ORANDA_RENDER_DIR</c> 가 있으면 선택 강조 PNG(ai-chat-selection-*.png)를 남긴다(저장소 밖 폴더).
/// </summary>
public sealed class AiChatSelectionTests
{
    private const string Paragraphs = "첫째 문단입니다.\n\n둘째 문단입니다.\n\n셋째 문단입니다.";

    private const string Mixed = """
        제목 문단

        - 하나
        - 둘

        | 이름 | 값 |
        |:--|--:|
        | a | 1 |
        | b | 2 |

        ```sql
        SELECT 1
          FROM dual
        ```

        끝 문단
        """;

    private static void Pump()
    {
        for (var i = 0; i < 4; i++)
        {
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static Window Show(AiChatViewModel vm, double height = 900)
    {
        var window = new Window { Width = 460, Height = height, Content = new AiChatView { DataContext = vm } };
        window.Show();
        Pump();
        window.UpdateLayout();
        return window;
    }

    private static AiChatViewModel Chat(string user, params string[] answers)
    {
        var vm = new AiChatViewModel();
        vm.StartConversation("대화");
        vm.BeginTurn(user);
        foreach (var answer in answers)
        {
            vm.AddAssistantText(answer);
        }

        vm.EndTurn();
        return vm;
    }

    private static string TextOf(SelectableTextBlock block) => block.Inlines is { Count: > 0 } ? block.Inlines.Text ?? string.Empty : block.Text ?? string.Empty;

    private static SelectableTextBlock Block(Window window, string startsWith) =>
        window.GetVisualDescendants().OfType<SelectableTextBlock>().First(b => b.IsEffectivelyVisible && TextOf(b).StartsWith(startsWith, StringComparison.Ordinal));

    /// <summary>글 칸의 가로 비율(0~1)·세로 가운데 자리를 창 좌표로.</summary>
    private static Point At(Window window, Control control, double fx, double dy = 0) =>
        control.TranslatePoint(new Point(control.Bounds.Width * fx, (control.Bounds.Height / 2) + dy), window)!.Value;

    /// <summary>글 칸 왼쪽에서 <paramref name="x"/> 픽셀 안쪽(칸이 창 폭이라 글 길이보다 넓으므로 짧은 글의 가운데를 가리킬 때).</summary>
    private static Point AtPx(Window window, Control control, double x) =>
        control.TranslatePoint(new Point(x, control.Bounds.Height / 2), window)!.Value;

    private static void Drag(Window window, Point from, Point to)
    {
        window.MouseDown(from, MouseButton.Left);
        Pump();
        // 중간 자리를 거쳐 가야 칸 사이 빈 곳도 지난다.
        for (var i = 1; i <= 4; i++)
        {
            var step = new Point(from.X + ((to.X - from.X) * i / 4), from.Y + ((to.Y - from.Y) * i / 4));
            window.MouseMove(step, RawInputModifiers.LeftMouseButton);
            Pump();
        }

        window.MouseUp(to, MouseButton.Left);
        Pump();
        window.UpdateLayout();
    }

    private static async Task<string?> CopyAsync(Window window)
    {
        await window.Clipboard!.SetTextAsync("before");
        // 복사 키는 플랫폼 설정을 따른다(macOS Headless 의 수식키는 실제 앱과 다를 수 있다).
        var primary = Application.Current!.PlatformSettings!.HotkeyConfiguration.CommandModifiers == KeyModifiers.Meta ? RawInputModifiers.Meta : RawInputModifiers.Control;
        window.KeyPress(Key.C, primary, PhysicalKey.C, "c");
        window.KeyRelease(Key.C, primary, PhysicalKey.C, "c");
        string? text = "before";
        for (var i = 0; i < 200 && text == "before"; i++)
        {
            Pump();
            text = await window.Clipboard!.TryGetTextAsync();
            if (text == "before")
            {
                await Task.Delay(10);
            }
        }

        return text;
    }

    private static void Shots(Window window, string name)
    {
        var dir = Environment.GetEnvironmentVariable("ORANDA_RENDER_DIR");
        if (string.IsNullOrEmpty(dir))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(dir);
            foreach (var (variant, suffix) in new[] { (ThemeVariant.Light, "light"), (ThemeVariant.Dark, "dark") })
            {
                Application.Current!.RequestedThemeVariant = variant;
                Pump();
                window.UpdateLayout();
                var size = new PixelSize((int)Math.Ceiling(window.Bounds.Width), (int)Math.Ceiling(window.Bounds.Height));
                using var bmp = new RenderTargetBitmap(size);
                bmp.Render(window);
#pragma warning disable CS0618 // 렌더 확인용: 새 오버로드가 없는 헤드리스 비트맵
                bmp.Save(Path.Combine(dir, $"ai-chat-selection-{name}-{suffix}.png"));
#pragma warning restore CS0618
            }
        }
        finally
        {
            Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
        }
    }

    private static AiChatView ViewOf(Window window) => (AiChatView)window.Content!;

    [Fact]
    public Task Dragging_from_the_first_to_the_third_paragraph_selects_all_three() => Ui.Run(async () =>
    {
        var window = Show(Chat("질문", Paragraphs));
        try
        {
            var first = Block(window, "첫째");
            var second = Block(window, "둘째");
            var third = Block(window, "셋째");
            Drag(window, At(window, first, 0.0), At(window, third, 0.98));

            Assert.True(ViewOf(window).Selection.HasSpanSelection);
            Assert.Equal(Paragraphs, ViewOf(window).Selection.SelectedText());
            Assert.Equal(TextOf(second).Length, Math.Abs(second.SelectionEnd - second.SelectionStart)); // 사이 칸은 통째로
            Assert.Equal(Paragraphs, await CopyAsync(window)); // 키로 복사

            Shots(window, "paragraphs");

            // 빈 곳을 누르면 선택이 풀린다.
            window.MouseDown(new Point(window.Bounds.Width - 4, window.Bounds.Height - 200), MouseButton.Left);
            window.MouseUp(new Point(window.Bounds.Width - 4, window.Bounds.Height - 200), MouseButton.Left);
            Pump();
            Assert.False(ViewOf(window).Selection.HasSpanSelection);
            Assert.All(new[] { first, second, third }, b => Assert.Equal(b.SelectionStart, b.SelectionEnd));
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Dragging_upward_selects_the_same_paragraphs_and_back_to_one_block_shrinks_the_range() => Ui.Run(() =>
    {
        var window = Show(Chat("질문", Paragraphs));
        try
        {
            var first = Block(window, "첫째");
            var second = Block(window, "둘째");
            var third = Block(window, "셋째");
            Drag(window, At(window, third, 0.98), At(window, first, 0.0));
            Assert.Equal(Paragraphs, ViewOf(window).Selection.SelectedText());

            // 같은 칸 안으로 되돌아오면 여러 칸 선택이 아니다: 다른 칸의 선택은 풀린다.
            var down = At(window, third, 0.9); // 앞 드래그의 시작 자리와 겹치면 연달아 누름(두 번 누름)으로 센다
            window.MouseDown(down, MouseButton.Left);
            window.MouseMove(AtPx(window, first, 30), RawInputModifiers.LeftMouseButton);
            Pump();
            Assert.True(ViewOf(window).Selection.HasSpanSelection);
            window.MouseMove(AtPx(window, third, 30), RawInputModifiers.LeftMouseButton);
            Pump();
            window.MouseUp(AtPx(window, third, 30), MouseButton.Left);
            Pump();
            Assert.False(ViewOf(window).Selection.HasSpanSelection);
            Assert.Equal(first.SelectionStart, first.SelectionEnd);
            Assert.Equal(second.SelectionStart, second.SelectionEnd);
            Assert.NotEqual(third.SelectionStart, third.SelectionEnd);
            return Task.CompletedTask;
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Copy_text_keeps_screen_order_list_markers_tab_separated_table_rows_and_code_lines() => Ui.Run(async () =>
    {
        var window = Show(Chat("질문", Mixed), height: 1100);
        try
        {
            var head = Block(window, "제목 문단");
            var tail = Block(window, "끝 문단");
            Drag(window, At(window, head, 0.0), At(window, tail, 0.98));

            var expected = "제목 문단\n\n• 하나\n• 둘\n\n이름\t값\na\t1\nb\t2\n\nSELECT 1\n  FROM dual\n\n끝 문단";
            Assert.Equal(expected, ViewOf(window).Selection.SelectedText());
            Assert.Equal(expected, await CopyAsync(window));
            Shots(window, "mixed");
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Selection_crosses_the_user_text_and_the_answer_and_a_partial_start_skips_the_list_marker() => Ui.Run(async () =>
    {
        var window = Show(Chat("사용자 질문 입니다", "- 하나 둘 셋\n\n뒤 문단"));
        try
        {
            var user = Block(window, "사용자 질문");
            var item = Block(window, "하나");
            var tail = Block(window, "뒤 문단");
            Drag(window, At(window, user, 0.0), At(window, tail, 0.98));
            Assert.Equal("사용자 질문 입니다\n\n• 하나 둘 셋\n\n뒤 문단", ViewOf(window).Selection.SelectedText());

            // 항목 가운데서 시작하면 표지는 붙지 않는다.
            Drag(window, AtPx(window, item, 20), At(window, tail, 0.98));
            var text = ViewOf(window).Selection.SelectedText();
            Assert.DoesNotContain("•", text, StringComparison.Ordinal);
            Assert.EndsWith("\n\n뒤 문단", text, StringComparison.Ordinal);
            Assert.Equal(text, await CopyAsync(window));
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task A_press_on_a_button_starts_no_selection_and_the_code_copy_button_still_copies() => Ui.Run(async () =>
    {
        var window = Show(Chat("질문", "앞 문단\n\n```sql\nSELECT 1\n```\n\n뒤 문단"));
        try
        {
            var button = window.GetVisualDescendants().OfType<Button>().First(b => ToolTip.GetTip(b) is "코드를 복사합니다");
            var tail = Block(window, "뒤 문단");

            Drag(window, At(window, button, 0.5), At(window, tail, 0.9));
            Assert.False(ViewOf(window).Selection.HasSpanSelection);
            Assert.False(ViewOf(window).Selection.IsDragging);

            await window.Clipboard!.SetTextAsync("before");
            window.MouseDown(At(window, button, 0.5), MouseButton.Left);
            window.MouseUp(At(window, button, 0.5), MouseButton.Left);
            string? text = "before";
            for (var i = 0; i < 200 && text == "before"; i++)
            {
                Pump();
                text = await window.Clipboard!.TryGetTextAsync();
                await Task.Delay(10);
            }

            Assert.Equal("SELECT 1", text);
        }
        finally
        {
            window.Close();
        }
    });

    /// <summary>글 칸에 누름을 직접 올린다. Headless 는 글자 위 누름을 칸까지 보내 주지 않아(빈 곳으로 잡힌다) 칸 자신의 처리를 확인하려면 이렇게 한다.</summary>
    private static void PressOn(Window window, SelectableTextBlock block, double x, int clickCount)
    {
        var at = AtPx(window, block, x);
        var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true);
        var properties = new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed);
        block.RaiseEvent(new PointerPressedEventArgs(block, pointer, window, at, 0, properties, KeyModifiers.None, clickCount));
        Pump();
    }

    [Fact]
    public Task A_selection_inside_one_block_and_the_double_click_word_are_left_to_the_block() => Ui.Run(() =>
    {
        var window = Show(Chat("질문", Paragraphs));
        try
        {
            var second = Block(window, "둘째");
            Drag(window, At(window, second, 0.0), AtPx(window, second, 30));
            Assert.False(ViewOf(window).Selection.HasSpanSelection);
            Assert.NotEqual(second.SelectionStart, second.SelectionEnd);

            // 글자 위 두 번 누름: 칸이 단어를 고르고, 컨트롤러는 드래그를 시작하지도 그 선택을 지우지도 않는다.
            PressOn(window, second, 10, clickCount: 1);
            PressOn(window, second, 10, clickCount: 2);
            Assert.False(ViewOf(window).Selection.IsDragging);
            Assert.StartsWith("둘째", second.SelectedText, StringComparison.Ordinal);
            return Task.CompletedTask;
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task A_press_on_the_glyphs_of_a_block_anchors_the_selection_at_the_caret_the_block_placed() => Ui.Run(() =>
    {
        var window = Show(Chat("질문", Paragraphs));
        try
        {
            var second = Block(window, "둘째");
            var third = Block(window, "셋째");
            PressOn(window, second, 30, clickCount: 1);
            var caret = second.SelectionStart;
            Assert.True(caret > 0 && caret < TextOf(second).Length);
            window.MouseMove(At(window, third, 0.98), RawInputModifiers.LeftMouseButton);
            Pump();
            var expected = TextOf(second)[caret..] + "\n\n" + TextOf(third);
            Assert.Equal(expected, ViewOf(window).Selection.SelectedText());
            window.MouseUp(At(window, third, 0.98), MouseButton.Left);
            return Task.CompletedTask;
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Redrawing_the_text_clears_the_selection_without_an_exception() => Ui.Run(() =>
    {
        var window = Show(Chat("질문", Paragraphs));
        try
        {
            var first = Block(window, "첫째");
            var third = Block(window, "셋째");
            Drag(window, At(window, first, 0.0), At(window, third, 0.98));
            Assert.True(ViewOf(window).Selection.HasSpanSelection);

            // 스트리밍처럼 글이 바뀌어 통째로 다시 그려진다.
            var view = window.GetVisualDescendants().OfType<MarkdownView>().Single();
            view.Markdown = Paragraphs + "\n\n넷째 문단입니다.";
            Pump();
            window.UpdateLayout();
            Assert.False(ViewOf(window).Selection.HasSpanSelection);
            Assert.Equal(string.Empty, ViewOf(window).Selection.SelectedText());

            // 선택하는 중에 다시 그려져도 마찬가지고, 그 뒤 새 선택이 다시 된다.
            var again = Block(window, "첫째");
            window.MouseDown(At(window, again, 0.0), MouseButton.Left);
            window.MouseMove(AtPx(window, Block(window, "둘째"), 30), RawInputModifiers.LeftMouseButton);
            view.Markdown = Paragraphs;
            Pump();
            window.MouseMove(AtPx(window, Block(window, "셋째"), 30), RawInputModifiers.LeftMouseButton);
            window.MouseUp(AtPx(window, Block(window, "셋째"), 30), MouseButton.Left);
            Pump();
            Assert.False(ViewOf(window).Selection.IsDragging);
            Drag(window, At(window, Block(window, "첫째"), 0.1), At(window, Block(window, "셋째"), 0.98));
            Assert.EndsWith("\n\n둘째 문단입니다.\n\n셋째 문단입니다.", ViewOf(window).Selection.SelectedText(), StringComparison.Ordinal); // 첫 칸은 가운데부터
            return Task.CompletedTask;
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Dragging_below_the_viewport_scrolls_the_conversation_and_extends_the_selection() => Ui.Run(async () =>
    {
        var many = string.Join("\n\n", Enumerable.Range(1, 40).Select(i => $"문단 {i:00} 입니다."));
        var window = Show(Chat("질문", many), height: 360);
        try
        {
            var scroll = window.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.Name == "ConversationScroll");
            Assert.True(scroll.Extent.Height > scroll.Viewport.Height * 2);
            var first = Block(window, "문단 01");
            var down = At(window, first, 0.0);
            window.MouseDown(down, MouseButton.Left);
            var bottom = scroll.TranslatePoint(new Point(40, scroll.Bounds.Height - 4), window)!.Value;
            window.MouseMove(bottom, RawInputModifiers.LeftMouseButton);
            for (var i = 0; i < 100 && scroll.Offset.Y < 200; i++)
            {
                await Task.Delay(30);
                Pump();
            }

            Assert.True(scroll.Offset.Y >= 200, $"offset {scroll.Offset.Y}");
            window.MouseUp(bottom, MouseButton.Left);
            Pump();
            var selected = ViewOf(window).Selection.SelectedText();
            Assert.StartsWith("문단 01 입니다.\n\n문단 02", selected, StringComparison.Ordinal);
            Assert.True(selected.Split("\n\n").Length > 8, selected);
            Assert.False(ViewOf(window).Selection.IsDragging);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Streaming_replaces_the_last_entry_each_token_but_keeps_a_selection_in_an_older_answer() => Ui.Run(async () =>
    {
        var vm = Chat("질문", Paragraphs);
        vm.AddAssistantText("새 답 시작");
        var window = Show(vm);
        try
        {
            Drag(window, At(window, Block(window, "첫째"), 0.0), At(window, Block(window, "셋째"), 0.98));
            Assert.Equal(Paragraphs, ViewOf(window).Selection.SelectedText());

            // 스트리밍: 마지막 항목이 토큰마다 새 항목으로 바뀐다(Replace).
            for (var i = 0; i < 3; i++)
            {
                vm.Entries[^1] = new ChatAssistantEntry("새 답 시작" + new string('.', i + 1));
                Pump();
                await Task.Delay(20);
                Pump();
            }

            Assert.Equal(Paragraphs, ViewOf(window).Selection.SelectedText());

            // 선택한 항목이 지워지면 선택도 사라진다.
            vm.Entries.RemoveAt(vm.Entries.Count - 2);
            for (var i = 0; i < 20 && ViewOf(window).Selection.HasSpanSelection; i++)
            {
                Pump();
                await Task.Delay(20);
            }

            Assert.False(ViewOf(window).Selection.HasSpanSelection);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task The_context_menu_copy_event_copies_the_whole_multi_block_selection() => Ui.Run(async () =>
    {
        var window = Show(Chat("질문", Paragraphs));
        try
        {
            var second = Block(window, "둘째");
            Drag(window, At(window, Block(window, "첫째"), 0.0), At(window, Block(window, "셋째"), 0.98));
            await window.Clipboard!.SetTextAsync("before");
            second.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(SelectableTextBlock.CopyingToClipboardEvent));
            string? text = "before";
            for (var i = 0; i < 200 && text == "before"; i++)
            {
                Pump();
                text = await window.Clipboard!.TryGetTextAsync();
                await Task.Delay(10);
            }

            Assert.Equal(Paragraphs, text);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task A_new_conversation_drops_the_multi_block_selection() => Ui.Run(() =>
    {
        var vm = Chat("질문", Paragraphs);
        var window = Show(vm);
        try
        {
            Drag(window, At(window, Block(window, "첫째"), 0.0), At(window, Block(window, "셋째"), 0.98));
            Assert.True(ViewOf(window).Selection.HasSpanSelection);
            vm.Entries.Clear();
            Pump();
            Assert.False(ViewOf(window).Selection.HasSpanSelection);
            return Task.CompletedTask;
        }
        finally
        {
            window.Close();
        }
    });

    private static string Many => string.Join("\n\n", Enumerable.Range(1, 40).Select(n => $"문단 {n:00} 입니다."));

    private static ScrollViewer ScrollOf(Window window) =>
        window.GetVisualDescendants().OfType<ScrollViewer>().First(v => v.Name == "ConversationScroll");

    [Fact]
    public Task Dragging_while_the_conversation_is_scrolled_ends_at_the_block_under_the_pointer() => Ui.Run(() =>
    {
        var window = Show(Chat("질문", Many), height: 360);
        try
        {
            var scroll = ScrollOf(window);
            scroll.Offset = new Vector(0, 300);
            Pump();
            window.UpdateLayout();
            Pump();

            var visible = window.GetVisualDescendants().OfType<SelectableTextBlock>()
                .Where(b => TextOf(b).StartsWith("문단", StringComparison.Ordinal))
                .Where(b => b.TranslatePoint(default, scroll) is { } at && at.Y > 20 && at.Y < scroll.Bounds.Height - 60)
                .ToList();
            Assert.True(visible.Count >= 4, $"visible {visible.Count}");
            Drag(window, At(window, visible[0], 0.0), AtPx(window, visible[3], 30));

            var lines = ViewOf(window).Selection.SelectedText().Split("\n\n");
            Assert.Equal(4, lines.Length);
            Assert.StartsWith(TextOf(visible[3])[..2], lines[^1], StringComparison.Ordinal);
            return Task.CompletedTask;
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task A_click_near_the_edge_without_moving_neither_scrolls_nor_selects() => Ui.Run(async () =>
    {
        var window = Show(Chat("질문", Many), height: 360);
        try
        {
            var scroll = ScrollOf(window);
            var at = scroll.TranslatePoint(new Point(40, scroll.Bounds.Height - 8), window)!.Value;
            window.MouseDown(at, MouseButton.Left);
            for (var i = 0; i < 10; i++)
            {
                await Task.Delay(40);
                Pump();
            }

            window.MouseUp(at, MouseButton.Left);
            Pump();
            Assert.Equal(0, scroll.Offset.Y);
            Assert.False(ViewOf(window).Selection.HasSpanSelection);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Ending_a_drag_on_the_right_edge_of_a_right_aligned_table_cell_keeps_that_cells_text() => Ui.Run(() =>
    {
        const string table = "앞 문단\n\n| 열 |\n|--:|\n| 아주아주 긴 글자가 들어 있는 칸입니다 |\n| 짧음 |";
        var window = Show(Chat("질문", table));
        try
        {
            var head = Block(window, "앞 문단");
            var cell = Block(window, "짧음");
            Drag(window, At(window, head, 0.0), AtPx(window, cell, cell.Bounds.Width - 1));
            Assert.EndsWith("짧음", ViewOf(window).Selection.SelectedText(), StringComparison.Ordinal);
            return Task.CompletedTask;
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Selecting_takes_the_focus_from_the_draft_box_without_touching_its_text_and_the_draft_copies_again_after_a_click() => Ui.Run(async () =>
    {
        var window = Show(Chat("질문", Paragraphs));
        try
        {
            var draft = window.GetVisualDescendants().OfType<TextBox>().First(t => t.Name == "DraftBox");
            draft.Text = "쓰던 글";
            draft.Focus();
            Pump();

            // 글자 위를 누를 때와 같게 시작 칸이 초점을 가져간다(복사 키가 대화 영역을 지나야 macOS 시스템 메뉴 경로도 닿는다). 입력칸 글은 그대로.
            Drag(window, At(window, Block(window, "첫째"), 0.0), At(window, Block(window, "셋째"), 0.98));
            Assert.IsType<SelectableTextBlock>(window.FocusManager!.GetFocusedElement());
            Assert.Equal("쓰던 글", draft.Text);
            Assert.Equal(Paragraphs, await CopyAsync(window));

            // 입력칸으로 초점이 가면 여러 칸 선택은 풀리고, 입력칸은 자기 복사를 한다.
            draft.Focus();
            draft.SelectAll();
            Pump();
            Assert.False(ViewOf(window).Selection.HasSpanSelection);
            Assert.Equal("쓰던 글", await CopyAsync(window));
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task A_right_press_in_the_gap_between_selected_blocks_keeps_the_selection_and_outside_it_clears_it() => Ui.Run(() =>
    {
        var window = Show(Chat("질문", Paragraphs + "\n\n넷째 문단입니다."));
        try
        {
            var first = Block(window, "첫째");
            var second = Block(window, "둘째");
            var third = Block(window, "셋째");
            Drag(window, At(window, first, 0.0), At(window, third, 0.98));
            Assert.True(ViewOf(window).Selection.HasSpanSelection);

            // 선택한 칸 사이 틈(둘째 아래)을 오른쪽 클릭해도 선택이 남는다.
            var gap = second.TranslatePoint(new Point(second.Bounds.Width / 2, second.Bounds.Height + 5), window)!.Value;
            window.MouseDown(gap, MouseButton.Right);
            window.MouseUp(gap, MouseButton.Right);
            Pump();
            Assert.True(ViewOf(window).Selection.HasSpanSelection);

            // 선택 밖(넷째 칸 아래 빈 곳)을 오른쪽 클릭하면 지워진다.
            var outside = new Point(window.Bounds.Width - 6, window.Bounds.Height - 220);
            window.MouseDown(outside, MouseButton.Right);
            window.MouseUp(outside, MouseButton.Right);
            Pump();
            Assert.False(ViewOf(window).Selection.HasSpanSelection);
            return Task.CompletedTask;
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Streaming_replaces_during_a_drag_in_an_older_answer_do_not_interrupt_it() => Ui.Run(async () =>
    {
        var vm = Chat("질문", Paragraphs, "스트리밍 답 하나");
        var window = Show(vm);
        try
        {
            window.MouseDown(At(window, Block(window, "첫째"), 0.0), MouseButton.Left);
            window.MouseMove(AtPx(window, Block(window, "둘째"), 30), RawInputModifiers.LeftMouseButton);
            Pump();

            // 끄는 동안 마지막 항목이 토큰마다 바뀐다(운영: Replace). 끌기는 이어진다.
            for (var n = 0; n < 4; n++)
            {
                vm.Entries[^1] = new ChatAssistantEntry("스트리밍 답 하나" + new string('.', n + 1));
                Pump();
                await Task.Delay(30);
                Pump();
                Assert.True(ViewOf(window).Selection.IsDragging, $"token {n}");
            }

            window.MouseMove(AtPx(window, Block(window, "셋째"), 30), RawInputModifiers.LeftMouseButton);
            window.MouseUp(AtPx(window, Block(window, "셋째"), 30), MouseButton.Left);
            Pump();
            Assert.False(ViewOf(window).Selection.IsDragging);
            var selected = ViewOf(window).Selection.SelectedText();
            Assert.Contains("둘째 문단입니다.\n\n", selected, StringComparison.Ordinal);
            Assert.DoesNotContain("스트리밍", selected, StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Replacing_the_entry_that_holds_the_start_block_ends_the_drag_without_stale_text() => Ui.Run(async () =>
    {
        var vm = Chat("질문", "앞 문단입니다.", "스트리밍 답 하나\n\n스트리밍 답 둘");
        var window = Show(vm);
        try
        {
            // 시작 칸이 마지막 항목 안에 있는 채로 그 항목이 바뀐다.
            window.MouseDown(At(window, Block(window, "스트리밍 답 하나"), 0.0), MouseButton.Left);
            window.MouseMove(AtPx(window, Block(window, "스트리밍 답 둘"), 30), RawInputModifiers.LeftMouseButton);
            Pump();
            Assert.True(ViewOf(window).Selection.IsDragging);
            vm.Entries[^1] = new ChatAssistantEntry("스트리밍 답 하나\n\n스트리밍 답 둘 더");
            for (var i = 0; i < 20 && ViewOf(window).Selection.IsDragging; i++)
            {
                Pump();
                await Task.Delay(20);
            }

            Assert.False(ViewOf(window).Selection.IsDragging);
            Assert.Equal(string.Empty, ViewOf(window).Selection.SelectedText());
            window.MouseMove(AtPx(window, Block(window, "앞 문단"), 30), RawInputModifiers.LeftMouseButton);
            window.MouseUp(AtPx(window, Block(window, "앞 문단"), 30), MouseButton.Left);
            Pump();
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Releasing_the_pointer_after_a_press_on_the_glyphs_ends_the_drag_and_a_right_click_keeps_the_range() => Ui.Run(() =>
    {
        var window = Show(Chat("질문", Paragraphs));
        try
        {
            var first = Block(window, "첫째");
            var second = Block(window, "둘째");
            var third = Block(window, "셋째");
            var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true);
            var down = AtPx(window, first, 10);
            first.RaiseEvent(new PointerPressedEventArgs(first, pointer, window, down, 0, new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None, 1));
            Assert.True(ViewOf(window).Selection.IsDragging);
            window.MouseMove(At(window, third, 0.98), RawInputModifiers.LeftMouseButton);
            Pump();
            first.RaiseEvent(new PointerReleasedEventArgs(first, pointer, window, At(window, third, 0.98), 0, new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left));
            Assert.False(ViewOf(window).Selection.IsDragging);
            Assert.True(ViewOf(window).Selection.HasSpanSelection);

            // 선택 안 가운데 칸 글자 위를 오른쪽 클릭하고 놓아도 범위가 그대로다(칸이 놓을 때 접은 선택은 되돌린다).
            var right = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true);
            var at = AtPx(window, second, 5);
            second.RaiseEvent(new PointerPressedEventArgs(second, right, window, at, 0, new PointerPointProperties(RawInputModifiers.RightMouseButton, PointerUpdateKind.RightButtonPressed), KeyModifiers.None, 1));
            second.RaiseEvent(new PointerReleasedEventArgs(second, right, window, at, 0, new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.RightButtonReleased), KeyModifiers.None, MouseButton.Right));
            Pump();
            Assert.True(ViewOf(window).Selection.HasSpanSelection);
            Assert.Equal(TextOf(second).Length, Math.Abs(second.SelectionEnd - second.SelectionStart));
            return Task.CompletedTask;
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task The_conversation_growing_during_a_drag_does_not_break_the_selection() => Ui.Run(() =>
    {
        var vm = Chat("질문", Paragraphs);
        var window = Show(vm);
        try
        {
            window.MouseDown(At(window, Block(window, "첫째"), 0.0), MouseButton.Left);
            window.MouseMove(At(window, Block(window, "둘째"), 0.98), RawInputModifiers.LeftMouseButton);
            vm.AddAssistantText("추가된 답");
            Pump();
            window.UpdateLayout();
            window.MouseMove(At(window, Block(window, "셋째"), 0.98), RawInputModifiers.LeftMouseButton);
            window.MouseUp(At(window, Block(window, "셋째"), 0.98), MouseButton.Left);
            Pump();
            Assert.Equal(Paragraphs, ViewOf(window).Selection.SelectedText());
            return Task.CompletedTask;
        }
        finally
        {
            window.Close();
        }
    });

    private const string LongParagraph = "둘째 문단입니다. 긴 글이 이어집니다 아주 길게 이어집니다.\n\n마지막 문단입니다.";

    private static KeyGesture CopyGesture => new(Key.C, OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control);

    private static async Task<string?> ClipboardAfterAsync(Window window, string old)
    {
        string? text = old;
        for (var i = 0; i < 100 && text == old; i++)
        {
            Pump();
            text = await window.Clipboard!.TryGetTextAsync();
            if (text == old)
            {
                await Task.Delay(10);
            }
        }

        return text;
    }

    [Fact]
    public Task The_copy_key_copies_only_the_selected_text_of_one_block_through_the_system_menu_route_too() => Ui.Run(async () =>
    {
        var window = Show(Chat("질문", LongParagraph));
        try
        {
            var block = Block(window, "둘째");
            Drag(window, AtPx(window, block, 6), AtPx(window, block, 60)); // 한 칸 안의 일부
            Assert.NotEqual(block.SelectionStart, block.SelectionEnd);
            var part = block.SelectedText;
            Assert.True(part.Length > 0 && part.Length < TextOf(block).Length);

            Assert.Equal(part, await CopyAsync(window)); // 키 경로

            // 시스템 메뉴 경로: NativeMenuCommand 가 초점 칸에 같은 키를 올린다(TryRoute). 처리된 것으로 끝나야 편집기 복사 등이 이어지지 않는다.
            await window.Clipboard!.SetTextAsync("before");
            Assert.True(EditKeyScope.TryRoute(window.FocusManager!.GetFocusedElement(), CopyGesture));
            Assert.Equal(part, await ClipboardAfterAsync(window, "before"));
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task The_copy_key_copies_the_double_clicked_word_only_and_nothing_when_nothing_is_selected() => Ui.Run(async () =>
    {
        var window = Show(Chat("질문", LongParagraph));
        try
        {
            var block = Block(window, "마지막");
            PressOn(window, block, 10, clickCount: 1);
            PressOn(window, block, 10, clickCount: 2);
            block.Focus();
            Pump();
            var word = block.SelectedText;
            Assert.StartsWith("마지막", word, StringComparison.Ordinal);
            Assert.True(word.Length < TextOf(block).Length);
            Assert.Equal(word, await CopyAsync(window));

            // 강조가 없는 상태(빈 곳을 눌러 시작 칸만 초점을 가진 때)의 복사 키는 클립보드를 바꾸지 않고, 키는 처리된 것으로 끝난다.
            Drag(window, new Point(window.Bounds.Width - 6, window.Bounds.Height - 220), new Point(window.Bounds.Width - 6, window.Bounds.Height - 220));
            await window.Clipboard!.SetTextAsync("before");
            Assert.True(EditKeyScope.TryRoute(window.FocusManager!.GetFocusedElement(), CopyGesture));
            Pump();
            await Task.Delay(100);
            Assert.Equal("before", await window.Clipboard!.TryGetTextAsync());
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task The_shared_context_menu_says_copy_in_korean_and_copies_the_whole_multi_block_selection() => Ui.Run(async () =>
    {
        var window = Show(Chat("질문", Paragraphs));
        try
        {
            var second = Block(window, "둘째");
            Assert.Same(ChatTextSelection.CopyFlyout, second.ContextFlyout);
            Assert.Same(ChatTextSelection.CopyFlyout, Block(window, "질문").ContextFlyout); // 사용자 글도
            Assert.All(window.GetVisualDescendants().OfType<SelectableTextBlock>(), b => Assert.Same(ChatTextSelection.CopyFlyout, b.ContextFlyout));
            Assert.All(window.GetVisualDescendants().OfType<SelectableTextBlock>(), b => Assert.NotEqual(Avalonia.Media.Color.Parse("#0078d7"), (b.SelectionBrush as Avalonia.Media.ISolidColorBrush)?.Color)); // 테마 기본 파랑이 아니라 편집기 선택색
            var item = Assert.IsType<MenuItem>(Assert.Single(ChatTextSelection.CopyFlyout.Items));
            Assert.Equal("복사", item.Header);

            Drag(window, At(window, Block(window, "첫째"), 0.0), At(window, Block(window, "셋째"), 0.98));
            await window.Clipboard!.SetTextAsync("before");
            ChatTextSelection.CopyFlyout.ShowAt(second);
            Pump();
            Assert.True(item.IsEnabled);
            item.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Equal(Paragraphs, await ClipboardAfterAsync(window, "before"));
            ChatTextSelection.CopyFlyout.Hide();
        }
        finally
        {
            window.Close();
        }
    });
}
