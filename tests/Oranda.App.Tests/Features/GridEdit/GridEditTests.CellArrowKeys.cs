using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Oranda.App.Features.GridEdit;
using Oranda.App.Features.ResultGrid;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Grid;

namespace Oranda.App.Tests.Features.GridEdit;

public sealed partial class GridEditTests
{
    [Fact]
    public Task Arrow_up_and_down_in_the_cell_editor_commit_the_value_and_continue_in_the_same_column_of_the_next_row() => HeadlessHost.Run(async () =>
    {
        var (pane, m, session, _) = await RowsPaneAsync(edit: true);
        var view = new SqlResultPaneView { DataContext = pane };
        var window = new Window { Content = view, Width = 900, Height = 400 };
        window.Show();
        try
        {
            var grid = view.FindControl<ResultGridControl>("GridControl")!;
            var editor = view.FindControl<TextBox>("CellEditor")!;
            await HeadlessHost.WaitUntil(() => grid.Bounds.Height > 0 && m.LoadedRowCount == 2);
            Dispatcher.UIThread.RunJobs();
            var ename = new GridCell(0, 1);
            var p = grid.TranslatePoint(new Point(grid.RowHeaderWidth + m.GetColumnWidth(0) + 12, grid.HeaderHeight + (grid.RowHeight / 2)), window)!.Value;
            window.MouseDown(p, MouseButton.Left);
            window.MouseUp(p, MouseButton.Left);
            window.MouseDown(p, MouseButton.Left);
            window.MouseUp(p, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.True(editor.IsVisible);
            Assert.Equal("SMITH", editor.Text);

            editor.Text = "SMYTHE";
            window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
            Dispatcher.UIThread.RunJobs();

            Assert.True(editor.IsVisible); // 같은 열의 아래 행 편집으로 이어 갔다
            Assert.Equal("O'NEIL", editor.Text);
            Assert.Equal(new GridCell(1, 1), m.Active);
            Assert.True(session!.IsDirty);
            Assert.True(m.TryGetValue(0, m.VisibleColumns[1], out var committed));
            Assert.Equal("SMYTHE", committed); // 앞 칸의 값은 반영됐다

            window.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null);
            Dispatcher.UIThread.RunJobs();
            Assert.True(editor.IsVisible);
            Assert.Equal("SMYTHE", editor.Text); // 위 행으로 돌아오면 반영한 값이 보인다
            Assert.Equal(ename, m.Active);
        }
        finally
        {
            window.Close();
            pane.Clear();
        }
    });

    [Fact]
    public Task Arrow_up_on_the_first_row_and_down_on_the_last_row_commit_and_close_the_editor() => HeadlessHost.Run(async () =>
    {
        var (pane, m, session, _) = await RowsPaneAsync(edit: true);
        var view = new SqlResultPaneView { DataContext = pane };
        var window = new Window { Content = view, Width = 900, Height = 400 };
        window.Show();
        try
        {
            var grid = view.FindControl<ResultGridControl>("GridControl")!;
            var editor = view.FindControl<TextBox>("CellEditor")!;
            await HeadlessHost.WaitUntil(() => grid.Bounds.Height > 0 && m.LoadedRowCount == 2);
            Dispatcher.UIThread.RunJobs();
            var x = grid.RowHeaderWidth + m.GetColumnWidth(0) + 12;
            void DoubleClickRow(int row)
            {
                var p = grid.TranslatePoint(new Point(x, grid.HeaderHeight + (grid.RowHeight * (row + 0.5))), window)!.Value;
                window.MouseDown(p, MouseButton.Left);
                window.MouseUp(p, MouseButton.Left);
                window.MouseDown(p, MouseButton.Left);
                window.MouseUp(p, MouseButton.Left);
                Dispatcher.UIThread.RunJobs();
            }

            DoubleClickRow(0);
            Assert.True(editor.IsVisible);
            editor.Text = "FIRST";
            window.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null);
            Dispatcher.UIThread.RunJobs();
            Assert.False(editor.IsVisible); // 첫 행의 ↑: 반영만 하고 닫는다
            Assert.True(session!.IsDirty);
            Assert.Equal(new GridCell(0, 1), m.Active);

            await Task.Delay(700); // 앞 더블클릭과 묶이지 않게 시간을 둔다
            DoubleClickRow(1);
            Assert.True(editor.IsVisible);
            editor.Text = "LAST";
            window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
            Dispatcher.UIThread.RunJobs();
            Assert.False(editor.IsVisible); // 끝 행의 ↓
            Assert.True(m.TryGetValue(1, m.VisibleColumns[1], out var last));
            Assert.Equal("LAST", last);
        }
        finally
        {
            window.Close();
            pane.Clear();
        }
    });

    [Fact]
    public Task Tab_still_moves_to_the_next_column_of_the_same_row() => HeadlessHost.Run(async () =>
    {
        var (pane, m, _, _) = await RowsPaneAsync(edit: true);
        var view = new SqlResultPaneView { DataContext = pane };
        var window = new Window { Content = view, Width = 900, Height = 400 };
        window.Show();
        try
        {
            var grid = view.FindControl<ResultGridControl>("GridControl")!;
            var editor = view.FindControl<TextBox>("CellEditor")!;
            await HeadlessHost.WaitUntil(() => grid.Bounds.Height > 0 && m.LoadedRowCount == 2);
            Dispatcher.UIThread.RunJobs();
            var p = grid.TranslatePoint(new Point(grid.RowHeaderWidth + m.GetColumnWidth(0) + 12, grid.HeaderHeight + (grid.RowHeight / 2)), window)!.Value;
            window.MouseDown(p, MouseButton.Left);
            window.MouseUp(p, MouseButton.Left);
            window.MouseDown(p, MouseButton.Left);
            window.MouseUp(p, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.True(editor.IsVisible);

            window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(0, m.Active.Row); // 행은 그대로
            Assert.Equal(2, m.Active.Col); // 같은 행의 다음 열
        }
        finally
        {
            window.Close();
            pane.Clear();
        }
    });

    private static async Task<(Window Window, SqlResultPaneView View, ResultGridControl Grid, TextBox Editor)> ShowAsync(SqlResultPaneViewModel pane)
    {
        var view = new SqlResultPaneView { DataContext = pane };
        var window = new Window { Content = view, Width = 900, Height = 400 };
        window.Show();
        var grid = view.FindControl<ResultGridControl>("GridControl")!;
        await HeadlessHost.WaitUntil(() => grid.Bounds.Height > 0 && pane.Model!.LoadedRowCount == 2);
        Dispatcher.UIThread.RunJobs();
        return (window, view, grid, view.FindControl<TextBox>("CellEditor")!);
    }

    private static void DoubleClickEname(Window window, ResultGridControl grid, ResultGridModel m, int row)
    {
        var p = grid.TranslatePoint(new Point(grid.RowHeaderWidth + m.GetColumnWidth(0) + 12, grid.HeaderHeight + (grid.RowHeight * (row + 0.5))), window)!.Value;
        window.MouseDown(p, MouseButton.Left);
        window.MouseUp(p, MouseButton.Left);
        window.MouseDown(p, MouseButton.Left);
        window.MouseUp(p, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private static object?[][] SameNameRows(string name) =>
    [
        [7369m, name, new DateTime(1980, 12, 17), 800m, null, "AAAR3sAAEAAAACXAAA"],
        [7499m, name, new DateTime(1981, 2, 20), 1600.5m, 300m, "AAAR3sAAEAAAACXAAB"],
    ];

    [Fact]
    public Task Passing_through_a_multi_line_value_with_the_arrow_keys_does_not_change_it() => HeadlessHost.Run(async () =>
    {
        var (pane, m, session, _) = await RowsPaneAsync(edit: true, rows: SameNameRows("a\nb"));
        var (window, _, grid, editor) = await ShowAsync(pane);
        try
        {
            DoubleClickEname(window, grid, m, 0);
            Assert.True(editor.IsVisible);

            window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null); // 지나가기만 한다
            window.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null);
            window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
            window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null); // 끝 행: 닫는다
            Dispatcher.UIThread.RunJobs();

            Assert.False(editor.IsVisible);
            Assert.False(session!.IsDirty); // 표시 글(a↵b)이 값으로 확정되면 변경으로 잡힌다
            Assert.False(pane.IsEditDirty);
            Assert.True(m.TryGetValue(0, m.VisibleColumns[1], out var v0));
            Assert.Equal("a\nb", v0);
            Assert.True(m.TryGetValue(1, m.VisibleColumns[1], out var v1));
            Assert.Equal("a\nb", v1);
        }
        finally
        {
            window.Close();
            pane.Clear();
        }
    });

    [Fact]
    public Task Passing_through_a_hidden_repeated_value_with_Tab_Enter_and_arrows_does_not_turn_it_into_NULL() => HeadlessHost.Run(async () =>
    {
        var (pane, m, session, _) = await RowsPaneAsync(edit: true, rows: SameNameRows("SAME"));
        m.ToggleRepeatHide(); // 위 행과 같은 값은 빈 글자로 보인다
        var (window, _, grid, editor) = await ShowAsync(pane);
        try
        {
            DoubleClickEname(window, grid, m, 0);
            window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null); // 둘째 행: 보이는 글은 빈 글자
            Assert.True(editor.IsVisible);
            Assert.Equal(string.Empty, editor.Text);
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Dispatcher.UIThread.RunJobs();

            DoubleClickEname(window, grid, m, 1);
            window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
            Dispatcher.UIThread.RunJobs();

            Assert.False(session!.IsDirty);
            Assert.True(m.TryGetValue(1, m.VisibleColumns[1], out var v1));
            Assert.Equal("SAME", v1);
        }
        finally
        {
            window.Close();
            pane.Clear();
        }
    });

    [Fact]
    public Task Clearing_a_hidden_repeated_cell_on_purpose_still_sets_it_to_NULL() => HeadlessHost.Run(async () =>
    {
        var (pane, m, session, _) = await RowsPaneAsync(edit: true, rows: SameNameRows("SAME"));
        m.ToggleRepeatHide();
        var (window, _, grid, editor) = await ShowAsync(pane);
        try
        {
            DoubleClickEname(window, grid, m, 1); // 둘째 행: 보이는 글은 빈 글자
            Assert.Equal(string.Empty, editor.Text);
            editor.Text = "x"; // 쳤다가
            editor.Text = string.Empty; // 지웠다: 글은 열 때와 같지만 NULL 로 만들겠다는 뜻
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Dispatcher.UIThread.RunJobs();

            Assert.True(session!.IsDirty);
            Assert.True(m.TryGetValue(1, m.VisibleColumns[1], out var v1));
            Assert.Null(v1);
        }
        finally
        {
            window.Close();
            pane.Clear();
        }
    });

    [Fact]
    public Task A_cell_you_really_changed_is_still_committed_by_the_arrow_keys() => HeadlessHost.Run(async () =>
    {
        var (pane, m, session, _) = await RowsPaneAsync(edit: true, rows: SameNameRows("a\nb"));
        var (window, _, grid, editor) = await ShowAsync(pane);
        try
        {
            DoubleClickEname(window, grid, m, 0);
            editor.Text = "changed";
            window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
            Dispatcher.UIThread.RunJobs();

            Assert.True(session!.IsDirty);
            Assert.True(m.TryGetValue(0, m.VisibleColumns[1], out var v0));
            Assert.Equal("changed", v0);
        }
        finally
        {
            window.Close();
            pane.Clear();
        }
    });

    [Fact]
    public Task A_late_header_sort_commits_the_open_cell_editor_first_so_the_value_stays_on_its_row() => HeadlessHost.Run(async () =>
    {
        var (pane, m, session, _) = await RowsPaneAsync(edit: true);
        pane.EditorInserter = _ => null; // 편집기 결과 탭처럼: 머리글 클릭 정렬이 더블클릭 시간만큼 미뤄진다
        var (window, _, grid, editor) = await ShowAsync(pane);
        grid.HeaderSortDelayOverride = TimeSpan.FromMilliseconds(600);
        grid.HeaderDoubleClickInserts = true;
        try
        {
            var header = grid.TranslatePoint(new Point(grid.RowHeaderWidth + (m.GetColumnWidth(0) * 3) + 20, grid.HeaderHeight / 2), window)!.Value;
            window.MouseDown(header, MouseButton.Left);
            window.MouseUp(header, MouseButton.Left); // 정렬 예약

            await Task.Delay(700); // 앞 클릭과 묶이지 않게 두되, 예약은 이미 실행됐다 — 아래에서 다시 예약한다
            window.MouseDown(header, MouseButton.Left);
            window.MouseUp(header, MouseButton.Left);
            DoubleClickEname(window, grid, m, 0); // 지연 안에 값 칸을 더블클릭해 셀 편집기가 열린다
            Assert.True(editor.IsVisible);
            editor.Text = "TYPED";

            await Task.Delay(900); // 미룬 정렬이 실행될 시간

            Assert.False(editor.IsVisible); // 열린 편집기는 먼저 확정됐다
            Assert.True(session!.IsDirty);
            Assert.True(m.TryGetValue(0, m.VisibleColumns[1], out var row0));
            Assert.Equal("TYPED", row0); // 입력한 값이 고른 행 그대로에 있다
            Assert.True(m.TryGetValue(1, m.VisibleColumns[1], out var row1));
            Assert.Equal("O'NEIL", row1);
        }
        finally
        {
            window.Close();
            pane.Clear();
        }
    });

    [Fact]
    public async Task FetchNextPageAsync_loads_one_more_page_for_a_result_of_unknown_size()
    {
        await using var m = new ResultGridModel(new Unsized(1200), pageSize: 500);
        await m.LoadFirstPageAsync();
        Assert.Equal(500, m.LoadedRowCount);
        Assert.True(m.HasMoreRows);

        await m.FetchNextPageAsync();

        Assert.Equal(1000, m.LoadedRowCount);
        Assert.Equal(1000, m.RowCount);
    }

    private sealed class Unsized(int rows) : IResultSet
    {
        private static readonly ResultColumn[] Cols = [new(0, "N", "NUMBER", ColumnDataKind.Number)];

        public IReadOnlyList<ResultColumn> Columns => Cols;

        public bool IsComplete => false;

        public long? KnownRowCount => null;

        public Task<ResultPage> GetPageAsync(int pageIndex, CancellationToken ct)
        {
            var first = pageIndex * 500;
            var count = Math.Clamp(rows - first, 0, 500);
            object?[] values = [.. Enumerable.Range(first, count).Select(i => (object?)(decimal)i)];
            return Task.FromResult(new ResultPage(pageIndex, first, count, [values]) { IsLast = first + count >= rows });
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
