using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Oranda.App.Features.ResultGrid;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Grid;

namespace Oranda.App.Tests.Features.ResultGrid;

/// <summary>현재 행 표시(GRID-12): 행 머리 ▶ 가 키보드 이동·마우스 클릭·스크롤을 따라가고, 열은 행 머리 너비만큼 오른쪽에서 시작한다.</summary>
public sealed class CurrentRowMarkerTests
{
    private sealed class Rows(int count) : IResultSet
    {
        public IReadOnlyList<ResultColumn> Columns { get; } =
        [
            new(0, "ID", "NUMBER", ColumnDataKind.Number),
            new(1, "NAME", "VARCHAR2", ColumnDataKind.Text),
        ];

        public bool IsComplete => true;

        public long? KnownRowCount => count;

        public Task<ResultPage> GetPageAsync(int pageIndex, CancellationToken ct)
        {
            var n = pageIndex == 0 ? count : 0;
            IReadOnlyList<object?> ids = [.. Enumerable.Range(0, n).Select(i => (object?)(decimal)i)];
            IReadOnlyList<object?> names = [.. Enumerable.Range(0, n).Select(i => (object?)("n" + i))];
            return Task.FromResult(new ResultPage(pageIndex, 0, n, [ids, names]) { IsLast = true });
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public Task Marker_follows_keyboard_click_and_scroll() => HeadlessHost.Run(async () =>
    {
        var vm = new SqlResultPaneViewModel();
        vm.SetResult(new Rows(200));
        var view = new SqlResultPaneView { DataContext = vm };
        var window = new Window { Content = view, Width = 700, Height = 400 };
        window.Show();
        try
        {
            var m = vm.Model!;
            await HeadlessHost.WaitUntil(() => m.LoadedRowCount == 200);
            var grid = view.FindControl<ResultGridControl>("GridControl")!;
            await HeadlessHost.WaitUntil(() => grid.Bounds.Height > 0);
            var rh = grid.RowHeight;
            var head = grid.RowHeaderWidth;
            Assert.True(head > 0);
            Assert.Null(grid.CurrentRowMarkerBounds); // 고른 칸이 없으면 표시가 없다
            Assert.Equal(head, grid.CellBounds(new GridCell(0, 0))!.Value.X); // 열은 행 머리 오른쪽에서 시작

            double MarkerTop() => grid.CurrentRowMarkerBounds!.Value.Y;

            grid.Focus();
            m.SelectCell(new GridCell(0, 0));
            var top0 = MarkerTop();
            Assert.InRange(top0, grid.HeaderHeight, grid.HeaderHeight + rh);

            window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
            window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
            Assert.Equal(2, m.Active.Row);
            Assert.Equal(top0 + (2 * rh), MarkerTop(), 3);

            // 셀을 누르면 그 행으로
            var cell = grid.TranslatePoint(new Point(head + 20, grid.HeaderHeight + (5.5 * rh)), window)!.Value;
            window.MouseDown(cell, MouseButton.Left);
            window.MouseUp(cell, MouseButton.Left);
            Assert.Equal(new GridCell(5, 0), m.Active);
            Assert.Equal(top0 + (5 * rh), MarkerTop(), 3);

            // 행 머리를 누르면 그 행 전체를 고른다
            var header = grid.TranslatePoint(new Point(head / 2, grid.HeaderHeight + (7.5 * rh)), window)!.Value;
            window.MouseDown(header, MouseButton.Left);
            window.MouseUp(header, MouseButton.Left);
            Assert.Equal(7, m.Active.Row);
            Assert.True(m.IsSelected(7, 0) && m.IsSelected(7, 1));
            Assert.Equal(top0 + (7 * rh), MarkerTop(), 3);

            // 스크롤하면 표시도 같이 움직이고, 화면 밖으로 나가면 그리지 않는다
            grid.ScrollTo(0, 3 * rh);
            Assert.Equal(top0 + (4 * rh), MarkerTop(), 3);
            grid.ScrollTo(0, 50 * rh);
            Assert.Null(grid.CurrentRowMarkerBounds);

            // 키로 그 행을 떠나 멀리 가면 표시가 보이는 곳으로 따라온다(EnsureVisible)
            window.KeyPress(Key.End, RawInputModifiers.Control, PhysicalKey.End, null);
            Assert.Equal(199, m.Active.Row);
            var last = grid.CurrentRowMarkerBounds!.Value;
            Assert.InRange(last.Bottom, grid.HeaderHeight, grid.Bounds.Height);

            // 텍스트 결과 모드에는 행 머리가 없다
            m.ToggleTextMode();
            Assert.Equal(0, grid.RowHeaderWidth);
            Assert.Null(grid.CurrentRowMarkerBounds);
        }
        finally
        {
            window.Close();
            vm.Clear();
        }
    });

    /// <summary>편집 모드 행 상태를 흉내 내는 덮어쓰기: 0 고친 행(키 칸 경고), 1 지울 행, 2 실패, 끝에 새 행 1개.</summary>
    private sealed class MarksOverlay(long baseRows) : IGridEditOverlay
    {
        public int ExtraRows => 1;

        public bool TryGetValue(long viewRow, int ordinal, out object? value)
        {
            value = viewRow >= baseRows ? "new" : null;
            return viewRow >= baseRows;
        }

        public bool IsEdited(long viewRow, int ordinal) => viewRow == 0 && ordinal == 1;

        public GridRowMark RowMark(long viewRow) => viewRow switch
        {
            0 => GridRowMark.Modified,
            1 => GridRowMark.Deleted,
            2 => GridRowMark.Failed,
            _ when viewRow >= baseRows => GridRowMark.Inserted,
            _ => GridRowMark.None,
        };

        public long BaseRowOf(long viewRow) => viewRow < baseRows ? viewRow : -1;

        public bool NeedsChange(long viewRow, int ordinal) => viewRow >= baseRows && ordinal == 0;
    }

    [Fact]
    public Task Rendering_draws_row_header_marks_band_and_frozen_columns_without_error() => HeadlessHost.Run(async () =>
    {
        var vm = new SqlResultPaneViewModel();
        vm.SetResult(new Rows(5));
        var view = new SqlResultPaneView { DataContext = vm };
        var window = new Window { Content = view, Width = 500, Height = 300 };
        window.Show();
        try
        {
            var m = vm.Model!;
            await HeadlessHost.WaitUntil(() => m.LoadedRowCount == 5);
            var grid = view.FindControl<ResultGridControl>("GridControl")!;
            m.EditOverlay = new MarksOverlay(m.BaseRowCount);
            m.FreezeColumns(1);
            m.SelectCell(new GridCell(5, 1)); // 새 행에 커서
            grid.ScrollTo(15, 0);
            using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(500, 300));
            using (var ctx = bitmap.CreateDrawingContext())
            {
                grid.Render(ctx); // 행 머리(▶·*·✎·✕·!)·현재 행 띠·경고 칸·고정 열 경계를 그린다
            }

            Assert.Equal(6, m.RowCount);
            Assert.NotNull(grid.CurrentRowMarkerBounds);
            Assert.True(m.NeedsChange(5, 0));
        }
        finally
        {
            window.Close();
            vm.Clear();
        }
    });

    [Fact]
    public void Row_header_glyphs_match_edit_row_states()
    {
        Assert.Equal("*", ResultGridControl.RowMarkGlyph(GridRowMark.Inserted));
        Assert.Equal("✎", ResultGridControl.RowMarkGlyph(GridRowMark.Modified));
        Assert.Equal("✕", ResultGridControl.RowMarkGlyph(GridRowMark.Deleted));
        Assert.Equal("!", ResultGridControl.RowMarkGlyph(GridRowMark.Failed));
        Assert.Equal(string.Empty, ResultGridControl.RowMarkGlyph(GridRowMark.None));
    }
}
