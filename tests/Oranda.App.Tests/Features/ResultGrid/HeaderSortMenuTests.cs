using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Oranda.App.Features.ResultGrid;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Grid;
using Oranda.Core.Shell;

namespace Oranda.App.Tests.Features.ResultGrid;

/// <summary>열 필터를 끈 결과: 머리글 클릭은 열 선택이고 정렬은 오른쪽 클릭 메뉴의 명령(오름차순·내림차순·정렬 해제)이 맡는다.</summary>
public sealed class HeaderSortMenuTests
{
    private sealed class Rows : IResultSet
    {
        public IReadOnlyList<ResultColumn> Columns { get; } =
        [
            new(0, "A", "NUMBER", ColumnDataKind.Number),
            new(1, "B", "NUMBER", ColumnDataKind.Number),
        ];

        public bool IsComplete => true;

        public long? KnownRowCount => 5;

        public Task<ResultPage> GetPageAsync(int pageIndex, CancellationToken ct)
        {
            IReadOnlyList<object?> Col(int c) => [.. Enumerable.Range(0, 5).Select(i => (object?)(decimal)((5 - i) * 10 + c))];
            return Task.FromResult(new ResultPage(pageIndex, 0, 5, [Col(0), Col(1)]) { IsLast = true });
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NoKeys : IContextKeys
    {
        public bool Evaluate(string? expression) => true;
    }

    private static async Task<ResultGridModel> OpenAsync()
    {
        var m = new ResultGridModel(new Rows());
        await m.LoadFirstPageAsync();
        return m;
    }

    private static Task Run(string id, ResultGridModel grid) =>
        new ResultGridCommandHandler().ExecuteAsync(new CommandContext(id, new NoKeys(), grid), default);

    private static async Task<string[]> FirstColumnAsync(ResultGridModel m)
    {
        var rows = await m.ReadRowsAsync(0, m.RowCount - 1, [0]);
        return [.. rows.Select(r => Convert.ToString(r[0], System.Globalization.CultureInfo.InvariantCulture)!)];
    }

    [Fact]
    public async Task Sort_commands_set_the_direction_directly_and_clear_restores_the_original_order()
    {
        await using var m = await OpenAsync();
        m.SelectCell(new GridCell(0, 0));

        await Run("oranda.grid.sortAscending", m);
        Assert.Equal(["10", "20", "30", "40", "50"], await FirstColumnAsync(m));
        Assert.False(m.SortDescending);

        await Run("oranda.grid.sortAscending", m); // 같은 방향을 다시 고르면 그대로(머리글 클릭처럼 내림차순으로 넘어가지 않는다)
        Assert.False(m.SortDescending);

        await Run("oranda.grid.sortDescending", m);
        Assert.Equal(["50", "40", "30", "20", "10"], await FirstColumnAsync(m));
        Assert.True(m.SortDescending);

        await Run("oranda.grid.clearSort", m);
        Assert.Equal(-1, m.SortColumn);
        Assert.Equal(["50", "40", "30", "20", "10"], await FirstColumnAsync(m)); // 원래 순서(큰 값부터 들어 있다)

        m.SelectCell(new GridCell(0, 1));
        await Run("oranda.grid.sortAscending", m); // 현재 칸의 열을 정렬한다
        Assert.Equal(1, m.SortColumn);
    }

    [Fact]
    public async Task The_right_clicked_header_column_wins_over_the_active_cell_when_several_columns_are_selected()
    {
        await using var m = await OpenAsync();
        m.SelectCell(new GridCell(0, 0));
        m.ExtendTo(new GridCell(4, 1)); // 두 열을 고른 범위: 현재 칸은 B 열(1)
        m.HeaderContextColumn = 0; // A 머리글을 오른쪽 클릭했다(선택은 그대로)

        await Run("oranda.grid.sortAscending", m);

        Assert.Equal(0, m.SortColumn);
        m.HeaderContextColumn = -1;
    }

    [Fact]
    public async Task Sorting_is_refused_while_unsaved_edits_exist_like_the_header_click()
    {
        await using var m = await OpenAsync();
        m.EditOverlay = new Pending();
        m.SelectCell(new GridCell(0, 0));

        await Run("oranda.grid.sortAscending", m);

        Assert.Equal(-1, m.SortColumn);
        Assert.Equal(["50", "40", "30", "20", "10"], await FirstColumnAsync(m));
    }

    private static async Task WithGrid(bool filterOn, Func<Window, ResultGridControl, ResultGridModel, Func<int, Point>, Task> body)
    {
        await using var m = new ResultGridModel(new Rows());
        m.SetColumnFilterEnabled(filterOn);
        var grid = new ResultGridControl { Model = m };
        var window = new Window { Content = grid, Width = 700, Height = 300 };
        window.Show();
        try
        {
            await HeadlessHost.WaitUntil(() => m.LoadedRowCount == 5 && grid.Bounds.Height > 0);
            Point Header(int col) => grid.TranslatePoint(new Point(grid.RowHeaderWidth + (m.GetColumnWidth(0) * col) + 20, grid.HeaderHeight / 2), window)!.Value;
            await body(window, grid, m, Header);
        }
        finally
        {
            window.Close();
        }
    }

    [Fact]
    public Task With_the_filter_off_a_header_click_selects_the_whole_column_and_does_not_sort() => HeadlessHost.Run(() => WithGrid(false, async (window, _, m, header) =>
    {
        var p = header(1);
        window.MouseDown(p, MouseButton.Left);
        window.MouseUp(p, MouseButton.Left);

        await Task.Delay(100); // 정렬이 잘못 시작됐다면 이 안에 끝난다
        Assert.Equal(-1, m.SortColumn);
        Assert.True(m.IsSelected(0, 1));
        Assert.True(m.IsSelected(4, 1)); // 끝 행까지 열 전체
        Assert.False(m.IsSelected(0, 0));
    }));

    [Fact]
    public Task With_the_filter_off_dragging_a_header_still_reorders_columns() => HeadlessHost.Run(() => WithGrid(false, async (window, _, m, header) =>
    {
        var from = header(0);
        var to = new Point(header(1).X + 40, header(1).Y); // B 의 오른쪽 절반: B 뒤
        window.MouseDown(from, MouseButton.Left);
        window.MouseMove(new Point(from.X + 30, from.Y));
        window.MouseMove(to);
        window.MouseUp(to, MouseButton.Left);

        await Task.Delay(100);
        Assert.Equal(-1, m.SortColumn);
        Assert.Equal([1, 0], m.VisibleColumns);
    }));

    private sealed class Pending : IGridEditOverlay
    {
        public int ExtraRows => 0;

        public bool HasPendingChanges => true;

        public bool TryGetValue(long viewRow, int ordinal, out object? value)
        {
            value = null;
            return false;
        }

        public bool IsEdited(long viewRow, int ordinal) => false;

        public GridRowMark RowMark(long viewRow) => GridRowMark.None;

        public long BaseRowOf(long viewRow) => viewRow;

        public bool NeedsChange(long viewRow, int ordinal) => false;
    }
}
