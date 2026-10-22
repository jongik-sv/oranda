using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Oranda.App.Features.ResultGrid;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Grid;

namespace Oranda.App.Tests.Features.ResultGrid;

/// <summary>TSK-13-17 머리글 누르기와 끌기: 끌기 없이 놓으면 정렬, 문턱 이상 끌면 열 순서만 바꾸고 정렬하지 않는다. Esc 로 취소한다.</summary>
public sealed class HeaderDragTests
{
    private sealed class Rows : IResultSet
    {
        public IReadOnlyList<ResultColumn> Columns { get; } =
        [
            new(0, "A", "NUMBER", ColumnDataKind.Number),
            new(1, "B", "NUMBER", ColumnDataKind.Number),
            new(2, "C", "NUMBER", ColumnDataKind.Number),
            new(3, "D", "NUMBER", ColumnDataKind.Number),
        ];

        public bool IsComplete => true;

        public long? KnownRowCount => 5;

        public Task<ResultPage> GetPageAsync(int pageIndex, CancellationToken ct)
        {
            IReadOnlyList<object?> Col(int c) => [.. Enumerable.Range(0, 5).Select(i => (object?)(decimal)((5 - i) * 10 + c))];
            return Task.FromResult(new ResultPage(pageIndex, 0, 5, [Col(0), Col(1), Col(2), Col(3)]) { IsLast = true });
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static async Task WithGrid(Func<Window, ResultGridControl, ResultGridModel, Func<int, Point>, Task> body)
    {
        await using var m = new ResultGridModel(new Rows());
        var grid = new ResultGridControl { Model = m };
        var window = new Window { Content = grid, Width = 700, Height = 300 };
        window.Show();
        try
        {
            await HeadlessHost.WaitUntil(() => m.LoadedRowCount == 5 && grid.Bounds.Height > 0);
            // 머리글 열 가운데의 창 좌표(열 폭 기본 100)
            Point Header(int col) => grid.TranslatePoint(new Point(grid.RowHeaderWidth + (m.GetColumnWidth(0) * col) + 50, grid.HeaderHeight / 2), window)!.Value;
            await body(window, grid, m, Header);
        }
        finally
        {
            window.Close();
        }
    }

    [Fact]
    public Task Press_and_release_without_dragging_sorts_that_column() => HeadlessHost.Run(() => WithGrid(async (window, _, m, header) =>
    {
        var p = header(1);
        window.MouseDown(p, MouseButton.Left);
        Assert.Equal(-1, m.SortColumn); // 누르는 순간에는 정렬하지 않는다
        window.MouseMove(new Point(p.X + 2, p.Y)); // 문턱 미만
        window.MouseUp(new Point(p.X + 2, p.Y), MouseButton.Left);

        await HeadlessHost.WaitUntil(() => m.SortColumn == 1);
        Assert.Equal([0, 1, 2, 3], m.VisibleColumns);
    }));

    [Fact]
    public Task Dragging_past_the_threshold_reorders_columns_and_does_not_sort() => HeadlessHost.Run(() => WithGrid(async (window, _, m, header) =>
    {
        var from = header(0);
        var to = new Point(header(2).X - 30, header(2).Y); // C 의 왼쪽 절반: C 앞에 놓인다
        window.MouseDown(from, MouseButton.Left);
        window.MouseMove(new Point(from.X + 30, from.Y));
        window.MouseMove(to);
        window.MouseUp(to, MouseButton.Left);

        await Task.Delay(100); // 정렬이 잘못 시작됐다면 이 안에 끝난다
        Assert.Equal(-1, m.SortColumn);
        Assert.Equal([1, 0, 2, 3], m.VisibleColumns); // A 가 B 뒤·C 앞으로 간다
    }));

    [Fact]
    public Task Escape_cancels_the_drag_without_sorting_or_moving() => HeadlessHost.Run(() => WithGrid(async (window, grid, m, header) =>
    {
        grid.Focus();
        var from = header(0);
        var to = header(3);
        window.MouseDown(from, MouseButton.Left);
        window.MouseMove(new Point(from.X + 30, from.Y));
        window.MouseMove(to);
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        window.MouseUp(to, MouseButton.Left);

        await Task.Delay(100);
        Assert.Equal(-1, m.SortColumn);
        Assert.Equal([0, 1, 2, 3], m.VisibleColumns);
    }));

    [Fact]
    public async Task MoveColumn_keeps_frozen_columns_in_place_and_keeps_column_identity()
    {
        await using var m = new ResultGridModel(new Rows());
        m.FreezeColumns(1);

        Assert.False(m.MoveColumn(0, 3)); // 고정 열은 옮기지 못한다
        Assert.False(m.MoveColumn(2, 0)); // 고정 열 안으로 놓지 못한다
        Assert.True(m.MoveColumn(1, 4)); // B 를 맨 뒤로
        Assert.Equal([0, 2, 3, 1], m.VisibleColumns);
        Assert.False(m.MoveColumn(3, 4)); // 이미 맨 뒤: 제자리
        Assert.True(m.MoveColumn(3, 1)); // B 를 다시 앞으로
        Assert.Equal([0, 1, 2, 3], m.VisibleColumns);
    }
}
