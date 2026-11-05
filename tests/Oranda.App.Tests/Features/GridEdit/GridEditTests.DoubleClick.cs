using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Oranda.App.Features.ResultGrid;
using Oranda.App.Tests.Shell.Headless;

namespace Oranda.App.Tests.Features.GridEdit;

public sealed partial class GridEditTests
{
    /// <summary>ENAME 열(둘째)의 첫 행 칸을 두 번 누른다. 반환 = 편집기에 넣겠다고 한 글 목록.</summary>
    private static async Task<(List<string> Inserted, bool CellEditorOpen)> DoubleClickEnameAsync(SqlResultPaneViewModel pane)
    {
        var inserted = new List<string>();
        pane.EditorInserter = text =>
        {
            inserted.Add(text);
            return null;
        };
        var view = new SqlResultPaneView { DataContext = pane };
        var window = new Window { Content = view, Width = 900, Height = 400 };
        window.Show();
        try
        {
            var grid = view.FindControl<ResultGridControl>("GridControl")!;
            await HeadlessHost.WaitUntil(() => grid.Bounds.Height > 0 && pane.Model!.LoadedRowCount == 2);
            Dispatcher.UIThread.RunJobs();
            var m = pane.Model!;
            var p = grid.TranslatePoint(new Point(grid.RowHeaderWidth + m.GetColumnWidth(0) + 12, grid.HeaderHeight + (grid.RowHeight / 2)), window)!.Value;

            window.MouseDown(p, MouseButton.Left);
            window.MouseUp(p, MouseButton.Left);
            window.MouseDown(p, MouseButton.Left);
            window.MouseUp(p, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            return (inserted, view.FindControl<TextBox>("CellEditor")!.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    [Fact]
    public Task Double_clicking_a_value_in_edit_mode_opens_the_cell_editor_and_inserts_nothing() => HeadlessHost.Run(async () =>
    {
        var (pane, _, _, _) = await RowsPaneAsync(edit: true);

        var (inserted, editorOpen) = await DoubleClickEnameAsync(pane);

        Assert.True(editorOpen);
        Assert.Empty(inserted);
        pane.Clear();
    });

    [Fact]
    public Task Double_clicking_a_value_outside_edit_mode_inserts_the_sql_literal_and_does_not_open_the_cell_editor() => HeadlessHost.Run(async () =>
    {
        var (pane, _, _, _) = await RowsPaneAsync(edit: false);

        var (inserted, editorOpen) = await DoubleClickEnameAsync(pane);

        Assert.Equal(["'SMITH'"], inserted);
        Assert.False(editorOpen);
        Assert.False(pane.HasEditHint); // 예전처럼 「편집 모드로 다시 실행」 안내를 띄우지 않는다
        pane.Clear();
    });
}
