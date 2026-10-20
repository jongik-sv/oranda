using System.Diagnostics;
using System.Text;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.SqlEditor;
using Oranda.Language.Lexing;
using Xunit.Abstractions;

namespace Oranda.App.Tests.Features.SqlEditor;

/// <summary>헤드리스 UI 시험: 편집기 View 가 AvaloniaEdit 위에서 강조·접기·괄호 짝·다중 커서·찾기 패널을 제대로 잇는지, 그리고 입력 지연.</summary>
public sealed class SqlEditorUiTests(ITestOutputHelper output)
{
    private static (Window Window, SqlEditorView View, SqlEditorViewModel Vm) Show(string text)
    {
        var vm = new SqlEditorViewModel(Guid.NewGuid(), text);
        var view = new SqlEditorView { DataContext = vm };
        var window = new Window { Width = 900, Height = 600, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, view, vm);
    }

    [Fact]
    public Task View_binds_document_and_surface_and_releases_them_on_detach() => Ui.Run(() =>
    {
        var (window, view, vm) = Show("select 1 from dual");

        Assert.Same(vm.Document, view.TextEditorControl.Document);
        Assert.Same(view, vm.Surface);
        Assert.NotNull(view.Colorizer);
        Assert.NotNull(view.Folding);

        view.DataContext = null;
        Assert.Null(vm.Surface);
        Assert.Null(view.Colorizer);
        window.Close();
    });

    [Fact]
    public Task Code_view_highlights_read_only_source_and_follows_text_changes() => Ui.Run(() =>
    {
        var code = new SqlCodeView { Text = "CREATE OR REPLACE PROCEDURE p IS BEGIN NULL; END;" };
        var window = new Window { Width = 600, Height = 300, Content = code };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.True(code.TextEditorControl.IsReadOnly);
        Assert.Contains(code.Colorizer!.Lexer.TokensOf(0), t => t.Kind == TokenKind.Keyword);

        code.Text = "-- only a comment";
        Assert.All(code.Colorizer.Lexer.TokensOf(0), t => Assert.Equal(TokenKind.Comment, t.Kind));

        window.Close();
        Assert.Null(code.Colorizer);
    });

    [Fact]
    public Task Highlight_tokens_cover_keyword_string_comment_bind_and_follow_edits() => Ui.Run(() =>
    {
        var (window, view, vm) = Show("select 'a', :b, 12 from dual -- tail\nwhere x = 1");
        var lexer = view.Colorizer!.Lexer;

        var kinds = lexer.TokensOf(0).Select(t => t.Kind).ToHashSet();
        Assert.Contains(TokenKind.Keyword, kinds);
        Assert.Contains(TokenKind.StringLiteral, kinds);
        Assert.Contains(TokenKind.Bind, kinds);
        Assert.Contains(TokenKind.Number, kinds);
        Assert.Contains(TokenKind.Comment, kinds);

        // 첫 줄 맨 앞에 여는 주석을 넣으면 둘째 줄도 주석으로 바뀐다(상태 전파).
        vm.Document.Insert(0, "/* ");
        Assert.True(view.Colorizer.LastRelexedLines >= 2);
        Assert.All(lexer.TokensOf(1), t => Assert.Equal(TokenKind.Comment, t.Kind));
        vm.Document.Remove(0, 3);
        Assert.Contains(lexer.TokensOf(1), t => t.Kind == TokenKind.Keyword);
        window.Close();
    });

    [Fact]
    public Task Incremental_lexing_matches_a_full_relex_after_random_edits() => Ui.Run(() =>
    {
        var (window, view, vm) = Show("select 1 from dual;\n-- c\nwhere a = 'x'\n/* b */");
        var rnd = new Random(20261019);
        string[] pieces = ["/*", "*/", "'", "\"", "\n", "-- ", "q'[", "]'", "x", " select ", ";", "\n\n"];
        for (int i = 0; i < 300; i++)
        {
            var doc = vm.Document;
            if (rnd.Next(3) == 0 && doc.TextLength > 0)
            {
                int off = rnd.Next(doc.TextLength);
                doc.Remove(off, Math.Min(rnd.Next(1, 6), doc.TextLength - off));
            }
            else
            {
                doc.Insert(rnd.Next(doc.TextLength + 1), pieces[rnd.Next(pieces.Length)]);
            }

            if (i % 25 == 0)
            {
                var fresh = new IncrementalLexer(ListLineSource.FromText(vm.Text));
                fresh.LexAll();
                var live = view.Colorizer!.Lexer;
                Assert.Equal(fresh.LineCount, live.LineCount);
                for (int line = 0; line < fresh.LineCount; line++)
                {
                    Assert.Equal(fresh.TokensOf(line), live.TokensOf(line));
                }
            }
        }

        window.Close();
    });

    [Fact]
    public Task Colors_reach_the_rendered_visual_line() => Ui.Run(() =>
    {
        var (window, view, _) = Show("select 'a' from dual -- c");
        var tv = view.TextEditorControl.TextArea.TextView;
        tv.EnsureVisualLines();
        var line = tv.GetVisualLine(1)!;
        var brushes = line.Elements.Select(e => e.TextRunProperties?.ForegroundBrush).Where(b => b is not null).Distinct().ToList();
        Assert.True(brushes.Count >= 3, $"서로 다른 전경색 {brushes.Count}개");
        window.Close();
    });

    [Fact]
    public Task Folding_covers_blocks_and_fold_commands_work() => Ui.Run(() =>
    {
        const string Text = "BEGIN\n  IF a THEN\n    NULL;\n  END IF;\nEND;\n/\nSELECT 1\nFROM dual;";
        var (window, view, vm) = Show(Text);
        var folding = view.Folding!;
        Assert.True(folding.AllFoldings.Count() >= 3, "BEGIN, IF, 여러 줄 문장");

        vm.Surface!.Fold(FoldAction.FoldAll);
        Assert.All(folding.AllFoldings, f => Assert.True(f.IsFolded));
        vm.Surface.Fold(FoldAction.UnfoldAll);
        Assert.All(folding.AllFoldings, f => Assert.False(f.IsFolded));

        vm.Select(Text.IndexOf("NULL", StringComparison.Ordinal), 0);
        vm.Surface.Fold(FoldAction.Fold); // 가장 안쪽(IF)
        var folded = folding.AllFoldings.Single(f => f.IsFolded);
        Assert.Equal(Text.IndexOf("IF a", StringComparison.Ordinal), folded.StartOffset);
        vm.Surface.Fold(FoldAction.Unfold);
        Assert.DoesNotContain(folding.AllFoldings, f => f.IsFolded);

        // 편집 뒤에 다시 계산하면 구간이 따라온다.
        const string Header = "-- 주석\n";
        vm.Document.Insert(0, Header);
        view.RefreshStructureNow();
        Assert.Contains(folding.AllFoldings, f => f.StartOffset == Header.Length);
        window.Close();
    });

    [Fact]
    public Task Bracket_pair_is_highlighted_at_caret() => Ui.Run(() =>
    {
        var (window, view, vm) = Show("select (a + b) from t;\nBEGIN\n  NULL;\nEND;");
        vm.Select(7, 0);
        Assert.Equal(new Oranda.Core.Text.TextRange(7, 1), view.Brackets.First);
        Assert.Equal(new Oranda.Core.Text.TextRange(13, 1), view.Brackets.Second);

        vm.Select(0, 0);
        Assert.Null(view.Brackets.First);
        vm.GoToLine(2);
        Assert.True(vm.GoToMatchingBracket());
        Assert.Equal(vm.Text.IndexOf("END", StringComparison.Ordinal) + 3, vm.CaretOffset); // END 의 끝
        window.Close();
    });

    [Fact]
    public Task Flash_paints_the_range_fades_out_and_stops_its_timer() => Ui.Run(() =>
    {
        var (window, view, vm) = Show("select 1 from dual;\nselect 2 from dual;");
        var flash = view.Flash;
        Assert.Null(flash.Range);
        Assert.False(flash.IsTimerRunning); // 강조가 없으면 타이머도 없다

        vm.FlashRange(20, 18);
        Assert.Equal(new Oranda.Core.Text.TextRange(20, 18), flash.Range);
        Assert.Equal(0, flash.Step);
        Assert.True(flash.IsTimerRunning);
        Assert.Contains(flash, view.TextEditorControl.TextArea.TextView.BackgroundRenderers);

        flash.Advance(RangeFlashRenderer.HoldTime + (RangeFlashRenderer.FadeTime / 2));
        Assert.True(flash.Step > 0, "머문 뒤에는 옅어진다");
        flash.Advance(RangeFlashRenderer.HoldTime + RangeFlashRenderer.FadeTime);
        Assert.Null(flash.Range);
        Assert.False(flash.IsTimerRunning);

        // 문서 밖 범위는 잘리고, 빈 범위는 무시한다.
        vm.FlashRange(30, 1000);
        Assert.Equal(new Oranda.Core.Text.TextRange(30, vm.Document.TextLength - 30), flash.Range);
        flash.Clear();
        vm.FlashRange(5, 0);
        Assert.Null(flash.Range);
        window.Close();
    });

    [Fact]
    public Task Flash_is_cleared_by_typing_and_by_detaching_the_view() => Ui.Run(() =>
    {
        var (window, view, vm) = Show("select 1 from dual");
        vm.FlashRange(0, 8);
        vm.Document.Insert(0, "-- x\n");
        Assert.Null(view.Flash.Range);
        Assert.False(view.Flash.IsTimerRunning);

        vm.FlashRange(0, 8);
        view.DataContext = null;
        Assert.Null(view.Flash.Range);
        Assert.False(view.Flash.IsTimerRunning);
        Assert.DoesNotContain(view.Flash, view.TextEditorControl.TextArea.TextView.BackgroundRenderers);

        vm.FlashRange(0, 8); // 떨어진 View 는 더 듣지 않는다
        Assert.Null(view.Flash.Range);
        window.Close();
    });

    [Fact]
    public void Flash_steps_hold_then_fade_to_nothing()
    {
        Assert.Equal(0, RangeFlashRenderer.StepAt(TimeSpan.Zero));
        Assert.Equal(0, RangeFlashRenderer.StepAt(RangeFlashRenderer.HoldTime - TimeSpan.FromMilliseconds(1)));
        var steps = Enumerable.Range(0, 20)
            .Select(i => RangeFlashRenderer.StepAt(RangeFlashRenderer.HoldTime + (RangeFlashRenderer.FadeTime * i / 20)))
            .ToList();
        Assert.Equal(steps.Order(), steps); // 단계는 줄곧 옅어지기만 한다
        Assert.True(steps[^1] > 0);
        Assert.Equal(-1, RangeFlashRenderer.StepAt(RangeFlashRenderer.HoldTime + RangeFlashRenderer.FadeTime));
        Assert.InRange(RangeFlashRenderer.HoldTime + RangeFlashRenderer.FadeTime, TimeSpan.FromMilliseconds(600), TimeSpan.FromMilliseconds(800));
    }

    [Fact]
    public Task Caret_and_selection_flow_both_ways_between_view_and_view_model() => Ui.Run(() =>
    {
        var (window, view, vm) = Show("line1\nline2\nline3");
        view.TextEditorControl.Select(2, 5);
        Assert.Equal((2, 5, 7), (vm.SelectionStart, vm.SelectionLength, vm.CaretOffset));
        Assert.True(vm.HasSelection);

        vm.Select(6, 0);
        Assert.Equal(6, view.TextEditorControl.CaretOffset);
        Assert.Equal("줄 2, 열 1", vm.PositionText);
        window.Close();
    });

    [Fact]
    public Task Multi_cursor_types_into_every_cursor_with_one_undo_step() => Ui.Run(() =>
    {
        var (window, view, vm) = Show("a\nb\nc");
        vm.Select(1, 0);
        vm.Surface!.AddCursor(+1);
        vm.Surface.AddCursor(+1);
        Assert.Equal(3, vm.CursorCount);

        view.TextEditorControl.TextArea.RaiseEvent(new TextInputEventArgs
        {
            RoutedEvent = InputElement.TextInputEvent,
            Text = "X",
        });
        Assert.Equal("aX\nbX\ncX", vm.Text);

        vm.Document.UndoStack.Undo();
        Assert.Equal("a\nb\nc", vm.Text);
        window.Close();
    });

    [Fact]
    public Task Find_panel_shows_and_find_commands_select_matches() => Ui.Run(() =>
    {
        var (window, view, vm) = Show("select a from t where a = 1");
        var panel = view.FindControl<Border>("FindPanel")!;
        Assert.False(panel.IsVisible);

        vm.OpenFind(replace: true);
        Dispatcher.UIThread.RunJobs();
        Assert.True(panel.IsVisible);
        Assert.True(view.FindControl<Button>("ReplaceAllButton")!.IsEffectivelyVisible);

        vm.FindText = "a";
        vm.WholeWord = true;
        vm.ReplaceText = "col";
        Assert.Equal(2, vm.ReplaceAll());
        Assert.Equal("select col from t where col = 1", vm.Text);
        Assert.Equal("select col from t where col = 1", view.TextEditorControl.Text);

        vm.CloseFind();
        Assert.False(panel.IsVisible);
        window.Close();
    });

    [Fact]
    public Task Clipboard_style_surface_calls_work_on_the_control() => Ui.Run(() =>
    {
        var (window, view, vm) = Show("abc def");
        vm.Surface!.SelectAll();
        Assert.Equal(7, view.TextEditorControl.SelectionLength);
        vm.Select(4, 3);
        Assert.Equal("def", view.TextEditorControl.SelectedText);
        Assert.Equal(1, vm.Surface.FirstVisibleLine);
        window.Close();
    });

    // ---- 성능: 1만 줄 문서에서 키 입력 반영(p95 ≤ 16 ms, D-007: 렌더 틱 대기를 뺀 처리 시간) --------------

    private static string BuildDocument(int lines)
    {
        var sb = new StringBuilder(lines * 40);
        for (int i = 0; i < lines; i++)
        {
            sb.Append((i % 20) switch
            {
                0 => "-- section " + i,
                5 => "/* block comment " + i,
                6 => "   still comment */",
                10 => "BEGIN",
                11 => "  IF x" + i + " > 1 THEN y := fn(" + i + ", 'text " + i + "'); END IF;",
                12 => "END;",
                _ => "SELECT col" + i + ", :bind" + i + ", 'literal' FROM tab_" + i + " WHERE id = " + i + ";",
            }).Append('\n');
        }

        return sb.ToString();
    }

    private static double P95(List<double> ms)
    {
        ms.Sort();
        return ms[(int)Math.Ceiling(ms.Count * 0.95) - 1];
    }

    [Fact]
    [Trait("Category", "Perf")]
    public Task Typing_in_10k_line_document_stays_within_frame_budget() => Ui.Run(() =>
    {
        var (window, view, vm) = Show(BuildDocument(10_000));
        Assert.True(vm.Document.LineCount >= 10_000);
        var editor = view.TextEditorControl;
        var tv = editor.TextArea.TextView;
        editor.ScrollToLine(5000);
        Dispatcher.UIThread.RunJobs();
        vm.Select(vm.Document.GetLineByNumber(5000).Offset + 3, 0);

        var data = new List<double>();
        var withLayout = new List<double>();
        for (int i = 0; i < 400; i++)
        {
            int off = vm.CaretOffset;
            var t0 = Stopwatch.GetTimestamp();
            if (i % 2 == 0)
            {
                vm.Document.Insert(off, "x"); // 글자 + 증분 렉싱 + ViewModel 처리
            }
            else
            {
                vm.Document.Remove(off, 1);
            }

            var t1 = Stopwatch.GetTimestamp();
            tv.EnsureVisualLines(); // 보이는 줄 다시 만들기(강조 입히기 포함)
            var t2 = Stopwatch.GetTimestamp();
            if (i >= 50)
            {
                data.Add(Stopwatch.GetElapsedTime(t0, t1).TotalMilliseconds);
                withLayout.Add(Stopwatch.GetElapsedTime(t0, t2).TotalMilliseconds);
            }
        }

        double p95Data = P95(data), p95Layout = P95(withLayout);
        data.Sort();
        output.WriteLine($"1만 줄 입력(헤드리스): 문서+렉싱 p50={data[data.Count / 2]:F3} p95={p95Data:F3} ms, +보이는 줄 재구성 p95={p95Layout:F3} ms, 재렉싱 줄 마지막={view.Colorizer!.LastRelexedLines}");
        // 판정은 D-007 정의(렌더 틱 대기를 뺀 처리 시간)의 앞 절반 = 문서 변경·증분 렉싱·ViewModel 처리.
        // 보이는 줄 재구성은 헤드리스 플랫폼의 글자 배치 오버헤드가 커서(TSK-01-05 RESULT 2.2: 헤드리스 25~45 ms 는 플랫폼 몫)
        // 숫자만 남기고 판정하지 않는다. 실창 측정은 spikes/TSK-01-05 의 typing-window 방식으로 한다.
#if DEBUG
        const double Budget = 40; // Debug 빌드·다른 작업과 겹친 PC 의 여유. 16 ms 판정은 Release 로 돌린 값(RESULT 참고).
#else
        const double Budget = 16;
#endif
        Assert.True(p95Data <= Budget, $"문서+렉싱 p95 {p95Data:F2} ms (한도 {Budget} ms)");
        window.Close();
    });

    [Fact]
    [Trait("Category", "Perf")]
    public Task Open_10k_line_document_and_compute_structure_is_quick() => Ui.Run(() =>
    {
        var text = BuildDocument(10_000);
        var sw = Stopwatch.StartNew();
        var (window, view, _) = Show(text);
        var open = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        view.RefreshStructureNow();
        var structure = sw.Elapsed.TotalMilliseconds;
        output.WriteLine($"1만 줄: 열기+첫 렌더 {open:F0} ms, 접기·짝 계산 {structure:F0} ms, 접기 구간 {view.Folding!.AllFoldings.Count()}개");
#if DEBUG
        const double Limit = 3000; // Debug + 동시에 도는 다른 빌드. 입력 중에는 돌지 않고 멈춘 뒤 한 번 계산한다.
#else
        const double Limit = 500;
#endif
        Assert.True(structure < Limit, $"구조 계산 {structure:F0} ms");
        window.Close();
    });
}
