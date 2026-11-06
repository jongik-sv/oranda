using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Oranda.App.Features.ResultGrid;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Grid;

namespace Oranda.App.Tests.Features.ResultGrid;

/// <summary>
/// 떨어진 열 여러 개 고르기: 머리글 Ctrl+클릭으로 열 전체를 더하거나 빼고, 셀 Ctrl+끌기로 칸 범위를 더한다(엑셀처럼).
/// 복사는 고른 열을 화면 순서로, 고른 행(범위들의 행 합집합)마다 이어 붙인다.
/// </summary>
public sealed class MultiColumnSelectionTests
{
    private const int ColCount = 8;
    private const int RowTotal = 5;
    private static readonly int[] PickedColumns = [0, 2, 3, 6];

    private sealed class Rows : IResultSet
    {
        public IReadOnlyList<ResultColumn> Columns { get; } =
            [.. Enumerable.Range(0, ColCount).Select(c => new ResultColumn(c, "C" + c, "VARCHAR2", ColumnDataKind.Text))];

        public bool IsComplete => true;

        public long? KnownRowCount => RowTotal;

        public Task<ResultPage> GetPageAsync(int pageIndex, CancellationToken ct)
        {
            IReadOnlyList<object?> Col(int c) => [.. Enumerable.Range(0, RowTotal).Select(r => (object?)$"r{r}c{c}")];
            return Task.FromResult(new ResultPage(pageIndex, 0, RowTotal, [.. Enumerable.Range(0, ColCount).Select(Col)]) { IsLast = true });
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static async Task<ResultGridModel> LoadAsync()
    {
        var m = new ResultGridModel(new Rows());
        await m.LoadFirstPageAsync();
        return m;
    }

    private static string[] Lines(string text) => text.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);

    private static string Row(int r, params int[] cols) => string.Join('\t', cols.Select(c => $"r{r}c{c}"));

    [Fact]
    public async Task Ctrl_clicked_headers_copy_only_those_columns_for_every_row()
    {
        await using var m = await LoadAsync();
        m.SelectColumn(0);
        m.ToggleColumnInSelection(2);
        m.ToggleColumnInSelection(3);
        m.ToggleColumnInSelection(6);

        Assert.Equal([0, 2, 3, 6], m.SelectedVisibleColumns());
        var lines = Lines(await m.BuildCopyTextAsync(CopyFormat.Tsv, includeHeaders: true));
        Assert.Equal("C0\tC2\tC3\tC6", lines[0]);
        Assert.Equal(RowTotal + 1, lines.Length);
        for (var r = 0; r < RowTotal; r++)
        {
            Assert.Equal(Row(r, 0, 2, 3, 6), lines[r + 1]);
        }
    }

    [Fact]
    public async Task A_cell_selected_before_the_first_ctrl_click_is_not_copied()
    {
        await using var m = await LoadAsync();
        m.SelectCell(new GridCell(3, 1)); // 보던 셀
        foreach (var col in PickedColumns)
        {
            m.ToggleColumnInSelection(col);
        }

        Assert.Equal(PickedColumns, m.SelectedVisibleColumns());
        Assert.Equal([(0L, RowTotal - 1L)], m.SelectedRowRanges());
    }

    [Fact]
    public async Task Ctrl_clicking_a_selected_header_again_removes_that_column()
    {
        await using var m = await LoadAsync();
        m.SelectColumn(0);
        m.ToggleColumnInSelection(2);
        m.ToggleColumnInSelection(3);
        m.ToggleColumnInSelection(2);

        Assert.Equal([0, 3], m.SelectedVisibleColumns());
        Assert.False(m.IsSelected(1, 2));
        Assert.True(m.IsSelected(1, 3));
    }

    [Fact]
    public async Task Removing_the_middle_of_a_column_range_keeps_both_sides()
    {
        await using var m = await LoadAsync();
        m.SelectColumn(1);
        m.ExtendTo(new GridCell(RowTotal - 1, 4)); // 1~4 열 전체
        m.ToggleColumnInSelection(2);

        Assert.Equal([1, 3, 4], m.SelectedVisibleColumns());
    }

    [Fact]
    public async Task The_last_selected_column_is_not_removed()
    {
        await using var m = await LoadAsync();
        m.SelectColumn(5);
        m.ToggleColumnInSelection(5);

        Assert.True(m.HasSelection);
        Assert.Equal([5], m.SelectedVisibleColumns());
    }

    [Fact]
    public async Task Ctrl_dragged_cell_ranges_copy_those_columns_for_the_chosen_rows()
    {
        await using var m = await LoadAsync();
        m.SelectCell(new GridCell(1, 0));
        m.ExtendTo(new GridCell(3, 0));
        m.AddCellRange(new GridCell(1, 2));
        m.ExtendTo(new GridCell(3, 3));
        m.AddCellRange(new GridCell(1, 6));
        m.ExtendTo(new GridCell(3, 6));

        var lines = Lines(await m.BuildCopyTextAsync(CopyFormat.Tsv));
        Assert.Equal([Row(1, 0, 2, 3, 6), Row(2, 0, 2, 3, 6), Row(3, 0, 2, 3, 6)], lines);
        Assert.Equal([(1L, 3L)], m.SelectedRowRanges());
    }

    [Fact]
    public async Task Ranges_on_different_rows_copy_the_union_of_rows_and_columns()
    {
        await using var m = await LoadAsync();
        m.SelectCell(new GridCell(0, 1));
        m.AddCellRange(new GridCell(3, 4));

        var lines = Lines(await m.BuildCopyTextAsync(CopyFormat.Tsv));
        Assert.Equal([Row(0, 1, 4), Row(3, 1, 4)], lines);
        Assert.True(m.IsSelected(0, 1));
        Assert.False(m.IsSelected(0, 4)); // 그리기는 고른 칸만 칠한다
        Assert.True(m.IsSelected(3, 4));
    }

    [Fact]
    public async Task Selection_summary_counts_only_the_chosen_cells()
    {
        await using var m = await LoadAsync();
        m.SelectColumn(0);
        m.ToggleColumnInSelection(3); // 0·3 열만: 사이 1·2 열은 세지 않는다

        var a = SelectionAggregate.Compute(m)!;

        Assert.Equal(2 * RowTotal, a.Count);
    }

    [Fact]
    public async Task Excel_export_of_the_selection_writes_the_chosen_columns_and_rows()
    {
        await using var m = await LoadAsync();
        m.SelectCell(new GridCell(1, 1));
        m.ExtendTo(new GridCell(2, 1));
        m.AddCellRange(new GridCell(1, 5));
        m.ExtendTo(new GridCell(2, 5));

        Assert.Equal((2L, 2), Oranda.App.Features.DataTransfer.ExcelExportPlan.SelectionSize(m));
        var data = Oranda.App.Features.DataTransfer.ExcelExportPlan.FromGrid(m, Oranda.App.Features.DataTransfer.ExcelExportScope.Selection);
        Assert.Equal(["C1", "C5"], data.Columns.Select(c => c.Name));
        var rows = new List<string>();
        await foreach (var row in data.Rows)
        {
            rows.Add(string.Join('\t', row));
        }

        Assert.Equal([Row(1, 1, 5), Row(2, 1, 5)], rows);
    }

    [Fact]
    public async Task A_plain_click_drops_the_added_ranges()
    {
        await using var m = await LoadAsync();
        m.SelectColumn(0);
        m.ToggleColumnInSelection(4);
        m.SelectCell(new GridCell(2, 2));

        Assert.Equal([2], m.SelectedVisibleColumns());
        Assert.False(m.IsSelected(2, 0));
    }

    [Fact]
    public async Task Moving_a_column_drops_the_added_ranges()
    {
        await using var m = await LoadAsync();
        m.SelectColumn(0);
        m.ToggleColumnInSelection(4);
        Assert.True(m.MoveColumn(6, 1));

        Assert.Single(m.SelectedVisibleColumns());
    }

    [Fact]
    public async Task Hide_selected_columns_hides_every_chosen_column()
    {
        await using var m = await LoadAsync();
        m.SelectColumn(1);
        m.ToggleColumnInSelection(5);

        ResultGridCommandHandler.HideSelectedColumns(m);

        Assert.Equal([0, 2, 3, 4, 6, 7], m.VisibleColumns);
    }

    [Fact]
    public Task Ctrl_clicking_headers_in_the_grid_adds_columns() => HeadlessHost.Run(async () =>
    {
        await using var m = new ResultGridModel(new Rows());
        var grid = new ResultGridControl { Model = m };
        var window = new Window { Content = grid, Width = 1400, Height = 300 };
        window.Show();
        try
        {
            await HeadlessHost.WaitUntil(() => m.LoadedRowCount == RowTotal && grid.Bounds.Height > 0);
            m.SelectCell(new GridCell(2, 1)); // 보던 셀이 있어도 그 열은 더하지 않는다
            Point Header(int col) => grid.TranslatePoint(new Point(grid.RowHeaderWidth + Enumerable.Range(0, col).Sum(c => m.GetColumnWidth(c)) + 12, grid.HeaderHeight / 2), window)!.Value;
            foreach (var col in PickedColumns)
            {
                window.MouseDown(Header(col), MouseButton.Left, RawInputModifiers.Control);
                window.MouseUp(Header(col), MouseButton.Left, RawInputModifiers.Control);
            }

            Assert.Equal([0, 2, 3, 6], m.SelectedVisibleColumns());
            Assert.Equal(-1, m.SortColumn); // Ctrl+클릭은 정렬하지 않는다
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Ctrl_dragging_cells_in_the_grid_adds_a_range() => HeadlessHost.Run(async () =>
    {
        await using var m = new ResultGridModel(new Rows());
        var grid = new ResultGridControl { Model = m };
        var window = new Window { Content = grid, Width = 1400, Height = 300 };
        window.Show();
        try
        {
            await HeadlessHost.WaitUntil(() => m.LoadedRowCount == RowTotal && grid.Bounds.Height > 0);
            Point Cell(long row, int col) => grid.TranslatePoint(
                new Point(grid.RowHeaderWidth + Enumerable.Range(0, col).Sum(c => m.GetColumnWidth(c)) + 12, grid.HeaderHeight + (row * grid.RowHeight) + (grid.RowHeight / 2)), window)!.Value;

            window.MouseDown(Cell(1, 0), MouseButton.Left);
            window.MouseMove(Cell(2, 0));
            window.MouseUp(Cell(2, 0), MouseButton.Left);
            window.MouseDown(Cell(1, 3), MouseButton.Left, RawInputModifiers.Control);
            window.MouseMove(Cell(2, 3), RawInputModifiers.Control | RawInputModifiers.LeftMouseButton);
            window.MouseUp(Cell(2, 3), MouseButton.Left, RawInputModifiers.Control);

            Assert.Equal([0, 3], m.SelectedVisibleColumns());
            Assert.Equal([(1L, 2L)], m.SelectedRowRanges());
        }
        finally
        {
            window.Close();
        }
    });
}
