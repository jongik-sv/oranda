using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.MyBatisWorkspace.Save;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Tests.Features.MyBatisWorkspace.Flow;

namespace Oranda.App.Tests.Shell.Headless;

/// <summary>제안 편집 비교 판: 긴 문장도 화면 안에 들어와 적용·거절 단추가 보이고, 바뀐 줄이 diff 로 드러난다.</summary>
public sealed class PreviewEditPanelTests
{
    private static string Lines(int count, string format)
    {
        var text = new StringBuilder();
        for (var i = 1; i <= count; i++)
        {
            text.AppendLine(string.Format(System.Globalization.CultureInfo.InvariantCulture, format, i));
        }

        return text.ToString();
    }

    private static Window Show(Control panel)
    {
        var window = new Window { Content = panel, SizeToContent = SizeToContent.WidthAndHeight };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    [Fact]
    public Task Long_statement_keeps_the_panel_small_enough_for_the_buttons_to_stay_on_screen() => HeadlessHost.Run(() =>
    {
        var before = Lines(200, "     , A.SOME_FAIRLY_LONG_COLUMN_NAME_{0:000} AS SOME_FAIRLY_LONG_COLUMN_ALIAS_{0:000} -- 비교 창 너비를 넓히는 긴 줄");
        var after = before.Replace("_100 AS", "_100 + 1 AS", StringComparison.Ordinal);
        var panel = AvaloniaEditorDialogs.BuildPreviewEditPanel(before, after);
        var window = Show(panel);

        Assert.InRange(panel.Bounds.Height, 1, AvaloniaEditorDialogs.PreviewMaxHeight);
        Assert.InRange(panel.Bounds.Width, 1, AvaloniaEditorDialogs.PreviewMaxWidth);
        window.Close();
    });

    [Fact]
    public void Max_size_is_the_smaller_of_the_cap_and_a_share_of_the_screen_work_area()
    {
        Assert.Equal(new Size(1100, 560), AvaloniaEditorDialogs.PreviewMaxSize(new Size(1920, 1040)));
        Assert.Equal(new Size(1024, 476), AvaloniaEditorDialogs.PreviewMaxSize(new Size(1280, 680))); // 80% × 70%
        Assert.Equal(new Size(1100, 560), AvaloniaEditorDialogs.PreviewMaxSize(null)); // 화면을 모르면 상한
    }

    [Fact]
    public Task Long_statement_on_a_small_screen_stays_within_that_screen_share() => HeadlessHost.Run(() =>
    {
        var before = Lines(200, "     , A.SOME_FAIRLY_LONG_COLUMN_NAME_{0:000} AS SOME_FAIRLY_LONG_COLUMN_ALIAS_{0:000} -- 비교 창 너비를 넓히는 긴 줄");
        var max = AvaloniaEditorDialogs.PreviewMaxSize(new Size(1280, 680));
        var panel = AvaloniaEditorDialogs.BuildPreviewEditPanel(before, before.Replace("_100 AS", "_100 + 1 AS", StringComparison.Ordinal), max);
        var window = Show(panel);

        Assert.InRange(panel.Bounds.Height, 1, max.Height);
        Assert.InRange(panel.Bounds.Width, 1, max.Width);
        window.Close();
    });

    private static void SelectTab(PreviewEditPanel panel, int index)
    {
        panel.Tabs.SelectedIndex = index;
        Dispatcher.UIThread.RunJobs();
    }

    [Fact]
    public Task Panel_opens_on_the_diff_tab_with_old_on_the_left_and_new_on_the_right() => HeadlessHost.Run(() =>
    {
        var panel = AvaloniaEditorDialogs.BuildPreviewEditPanel("select a\nfrom t\nwhere x = 1", "select a\nfrom t\nwhere x = 2");
        var window = Show(panel);

        Assert.Equal(0, panel.Tabs.SelectedIndex);
        Assert.Same(panel.DiffTab, panel.Tabs.SelectedItem);
        var diff = window.GetVisualDescendants().OfType<MapperDiffView>().Single();
        Assert.Contains(diff.Rows, r => r.Pair is { Left.Text: "where x = 1", Right.Text: "where x = 2" });
        Assert.NotNull(diff.FindAncestorOfType<ScrollViewer>()); // 바뀐 줄이 많아도 칸 안에서 스크롤된다
        window.Close();
    });

    [Fact]
    public Task Second_tab_keeps_current_and_proposed_text_and_the_proposal_is_editable() => HeadlessHost.Run(() =>
    {
        var panel = AvaloniaEditorDialogs.BuildPreviewEditPanel("select 1 from dual", "select 2 from dual");
        var window = Show(panel);
        SelectTab(panel, 1);

        var views = window.GetVisualDescendants().OfType<SqlCodeView>().ToList();
        Assert.Equal(2, views.Count);
        Assert.True(views[0].TextEditorControl.IsReadOnly);
        Assert.Equal("select 1 from dual", views[0].TextEditorControl.Text);
        Assert.False(panel.ProposedView.TextEditorControl.IsReadOnly);
        Assert.Equal("select 2 from dual", panel.ProposedText);
        window.Close();
    });

    [Fact]
    public Task Edited_proposal_is_diffed_again_when_the_user_returns_to_the_diff_tab() => HeadlessHost.Run(() =>
    {
        var panel = AvaloniaEditorDialogs.BuildPreviewEditPanel("select a\nfrom t", "select b\nfrom t");
        var window = Show(panel);
        SelectTab(panel, 1);
        panel.ProposedView.TextEditorControl.Text = "select c\nfrom t";
        SelectTab(panel, 0);

        Assert.Equal("select c\nfrom t", panel.ProposedText);
        Assert.Contains(panel.DiffView.Rows, r => r.Pair is { Left.Text: "select a", Right.Text: "select c" });
        Assert.DoesNotContain(panel.DiffView.Rows, r => r.Pair is { Right.Text: "select b" });
        window.Close();
    });

    // 스크롤하면 새 줄(점점 긴 줄)이 그려지고 내용 크기 어림이 바뀐다. 창은 그 영향을 받지 않아야 한다.
    [Fact]
    public Task Wheel_scrolling_a_long_proposal_does_not_change_the_dialog_size() => HeadlessHost.Run(() =>
    {
        var text = new StringBuilder();
        for (var i = 1; i <= 300; i++)
        {
            text.AppendLine(string.Concat("     , COL_", i.ToString("000", System.Globalization.CultureInfo.InvariantCulture), new string('X', i > 200 ? (i - 200) / 2 : 0)));
        }

        var before = text.ToString();
        var panel = AvaloniaEditorDialogs.BuildPreviewEditPanel(before, before.Replace("COL_100", "COL_100 + 1", StringComparison.Ordinal), new Size(1100, 560));
        var dialog = AvaloniaEditorDialogs.BuildDialog("t", panel, "적용", "거절", () => { }, fill: true);
        dialog.Show();
        Dispatcher.UIThread.RunJobs();
        SelectTab(panel, 1);
        var window = dialog;
        var size = window.Bounds.Size;
        var panelSize = panel.Bounds.Size;

        var point = panel.ProposedView.TranslatePoint(new Point(40, 40), window) ?? new Point(400, 200);
        for (var i = 0; i < 40; i++)
        {
            window.MouseWheel(point, new Vector(0, -3));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(size, window.Bounds.Size); // 스크롤 도중 어느 순간에도 같아야 한다
        }

        // 줄이 점점 길어지는 구간(201 줄부터)이 실제로 화면에 들어와야 시험이 뜻을 가진다
        Assert.Contains(panel.ProposedView.TextEditorControl.TextArea.TextView.VisualLines, v => v.FirstDocumentLine.LineNumber > 210);
        Assert.Equal(size, window.Bounds.Size);
        Assert.Equal(panelSize, panel.Bounds.Size);
        window.Close();
    });

    [Fact]
    public Task Releasing_the_fixed_size_lets_the_panel_follow_a_larger_window() => HeadlessHost.Run(() =>
    {
        var panel = AvaloniaEditorDialogs.BuildPreviewEditPanel("select 1", "select 2", new Size(900, 400));
        var dialog = AvaloniaEditorDialogs.BuildDialog("t", panel, "적용", "거절", () => { }, fill: true);
        dialog.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.InRange(panel.Bounds.Width, 1, 900);

        panel.ReleaseFixedSize();
        dialog.Width = 1400;
        dialog.Height = 800;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(SizeToContent.Manual, dialog.SizeToContent);
        Assert.True(panel.Bounds.Width > 1200 && panel.Bounds.Height > 600, $"{panel.Bounds.Size}");
        dialog.Close();
    });

    [Fact]
    public Task Apply_button_hands_back_the_edited_proposal_even_after_the_dialog_closed() => HeadlessHost.Run(() =>
    {
        var accepted = false;
        var panel = AvaloniaEditorDialogs.BuildPreviewEditPanel("select a", "select b");
        var dialog = AvaloniaEditorDialogs.BuildDialog("t", panel, "적용", "거절", () => accepted = true, fill: true);
        dialog.Show();
        Dispatcher.UIThread.RunJobs();
        SelectTab(panel, 1);
        panel.ProposedView.TextEditorControl.Text = "select c";

        var apply = dialog.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "적용"));
        apply.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.True(accepted);
        Assert.False(dialog.IsVisible);
        Assert.Equal("select c", panel.ProposedText);
    });

    [Fact]
    public Task Enter_in_the_proposal_inserts_a_line_break_instead_of_pressing_the_default_button() => HeadlessHost.Run(() =>
    {
        var accepted = false;
        var panel = AvaloniaEditorDialogs.BuildPreviewEditPanel("select a", "select b");
        var dialog = AvaloniaEditorDialogs.BuildDialog("t", panel, "적용", "거절", () => accepted = true, fill: true);
        dialog.Show();
        Dispatcher.UIThread.RunJobs();
        SelectTab(panel, 1);
        var area = panel.ProposedView.TextEditorControl.TextArea;
        area.Focus();
        area.Caret.Offset = area.Document.TextLength;
        Dispatcher.UIThread.RunJobs();

        dialog.KeyPress(Avalonia.Input.Key.Enter, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Enter, null);
        Dispatcher.UIThread.RunJobs();

        Assert.False(accepted);
        Assert.True(dialog.IsVisible);
        Assert.Contains('\n', panel.ProposedText);
        dialog.Close();
    });

    [Fact]
    public void Preferred_size_depends_only_on_the_text_and_stays_inside_the_limit()
    {
        var limit = new Size(1100, 560);
        Assert.Equal(new Size(640, 240), PreviewEditPanel.PreferredSize("select 1", "select 2", limit));
        Assert.Equal(limit, PreviewEditPanel.PreferredSize(Lines(500, "{0}" + new string('x', 200)), "x", limit));
        Assert.Equal(PreviewEditPanel.PreferredSize("a\nb", "a\nc", limit), PreviewEditPanel.PreferredSize("a\nb", "a\nc", limit));
    }

    // ORANDA_RENDER_DIR 가 있을 때만 비교 창 PNG 를 라이트·다크로 뽑는다(렌더 점검용): 탭 1, 탭 2(제안을 고친 뒤), 긴 SQL 을 휠로 스크롤한 전후.
    [Fact]
    public Task Render_preview_dialog() => HeadlessHost.Run(() =>
    {
        const string before = "select e.empno, e.ename, d.dname\n  from emp e, dept d\n where e.deptno = d.deptno\n   and e.sal > 1000\n order by e.ename";
        const string after = "select e.empno, e.ename, d.dname\n  from emp e\n  join dept d on d.deptno = e.deptno\n where e.sal > 1000\n order by e.ename";
        var panel = AvaloniaEditorDialogs.BuildPreviewEditPanel(before, after);
        var dialog = AvaloniaEditorDialogs.BuildDialog("조인 문법 변환", panel, "적용", "거절", () => { }, fill: true);
        dialog.Show();
        Dispatcher.UIThread.RunJobs();
        Render(dialog, "preview-tab1-diff");
        SelectTab(panel, 1);
        panel.ProposedView.TextEditorControl.Text = after + "\n   and e.job <> 'CLERK'";
        Render(dialog, "preview-tab2-text");
        dialog.Close();

        var text = new StringBuilder();
        for (var i = 1; i <= 300; i++)
        {
            text.AppendLine(string.Concat("     , COL_", i.ToString("000", System.Globalization.CultureInfo.InvariantCulture), new string('X', i > 200 ? (i - 200) / 2 : 0)));
        }

        var longBefore = text.ToString();
        var longPanel = AvaloniaEditorDialogs.BuildPreviewEditPanel(longBefore, longBefore.Replace("COL_100", "COL_100 + 1", StringComparison.Ordinal), new Size(1100, 560));
        var longDialog = AvaloniaEditorDialogs.BuildDialog("긴 SQL", longPanel, "적용", "거절", () => { }, fill: true);
        longDialog.Show();
        Dispatcher.UIThread.RunJobs();
        SelectTab(longPanel, 1);
        Render(longDialog, "preview-long-before-scroll");
        var point = longPanel.ProposedView.TranslatePoint(new Point(40, 40), longDialog) ?? new Point(400, 200);
        for (var i = 0; i < 40; i++)
        {
            longDialog.MouseWheel(point, new Vector(0, -3));
            Dispatcher.UIThread.RunJobs();
        }

        Render(longDialog, "preview-long-after-scroll");
        longDialog.Close();
    });

    private static void Render(Window window, string name)
        => MyBatisFlowRig.Render(window, name, (int)Math.Ceiling(window.Bounds.Width), (int)Math.Ceiling(window.Bounds.Height));
}
