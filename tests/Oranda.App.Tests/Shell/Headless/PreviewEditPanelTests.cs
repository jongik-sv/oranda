using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.MyBatisWorkspace.Save;
using Oranda.App.Features.SqlEditor;

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

    [Fact]
    public Task Panel_marks_the_changed_lines_as_a_diff() => HeadlessHost.Run(() =>
    {
        var window = Show(AvaloniaEditorDialogs.BuildPreviewEditPanel("select a\nfrom t\nwhere x = 1", "select a\nfrom t\nwhere x = 2"));

        var diff = window.GetVisualDescendants().OfType<MapperDiffView>().Single();
        Assert.Contains(diff.Rows, r => r.Line is { Kind: MapperDiffKind.Removed, Text: "where x = 1" });
        Assert.Contains(diff.Rows, r => r.Line is { Kind: MapperDiffKind.Added, Text: "where x = 2" });
        Assert.NotNull(diff.FindAncestorOfType<ScrollViewer>()); // 바뀐 줄이 많아도 칸 안에서 스크롤된다
        window.Close();
    });
}
