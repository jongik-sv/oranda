using Oranda.App.Features.GridEdit;
using Oranda.App.Features.ResultGrid;
using Oranda.Core.Grid;

namespace Oranda.App.Tests.Features.GridEdit;

/// <summary>
/// 편집 모드와 열 정렬(GRID-04): 편집 세션은 바꾼 값·지울 행을 화면 행 번호로 기억하므로, 반영하지 않은 변경이 있을 때 정렬하면
/// 바꾼 값이 다른 행에 보이고 그 행을 고치면 원래 행이 바뀐다. 그래서 그때는 정렬하지 않고 알린다. 변경이 없을 때 정렬한 뒤 고치는 것,
/// 정렬된 결과·페이지를 더 가져오는 결과에서 고친 값은 제자리에 있다.
/// </summary>
public sealed partial class GridEditTests
{
    private const string SortRefused = "반영하거나 되돌린 뒤 정렬할 수 있습니다";

    [Fact]
    public async Task Sorting_with_an_unapplied_edit_is_refused_and_the_edit_stays_on_its_row()
    {
        var (pane, m, s, _) = await RowsPaneAsync(edit: true);
        try
        {
            Assert.True(s!.SetCell(0, 3, 900m)); // 7369 SMITH 의 SAL

            await m.ToggleSortAsync(0);
            await m.ToggleSortAsync(0); // EMPNO 내림차순이면 7499 가 맨 위로 온다

            Assert.False(m.IsSorted); // 머리글 화살표도 그대로(정렬하지 않았다)
            Assert.Equal(-1, m.SortColumn);
            Assert.True(m.TryGetValue(0, 0, out var key) && Equals(key, 7369m));
            Assert.True(m.TryGetValue(0, 3, out var sal) && Equals(sal, 900m)); // 바꾼 값은 제 행에
            Assert.True(m.TryGetValue(1, 3, out var other) && Equals(other, 1600.5m));
            Assert.Contains(SortRefused, pane.StatusText, StringComparison.Ordinal);
            var change = Assert.Single(s.BuildChanges());
            Assert.Equal(7369m, change.Key["EMPNO"]);

            // 지울 행만 있어도, 새 행만 있어도 정렬하지 않는다.
            s.ResetAll();
            s.DeleteRow(1);
            await m.ToggleSortAsync(0);
            Assert.False(m.IsSorted);
            Assert.Equal(GridEditRowState.Deleted, s.GetRowState(1));
            s.ResetAll();
            s.AddRow();
            await m.ToggleSortAsync(0);
            Assert.False(m.IsSorted);

            // 되돌리면 정렬할 수 있고, 정렬하지 않았다는 안내는 사라진다.
            s.ResetAll();
            await m.ToggleSortAsync(0);
            Assert.True(m.IsSorted);
            Assert.DoesNotContain(SortRefused, pane.StatusText, StringComparison.Ordinal);
        }
        finally
        {
            pane.Clear();
        }
    }

    [Fact]
    public async Task Sorting_a_clean_edit_result_then_editing_changes_the_row_shown()
    {
        var (pane, m, s, _) = await RowsPaneAsync(edit: true);
        try
        {
            await m.ToggleSortAsync(0);
            await m.ToggleSortAsync(0); // 변경이 없으니 정렬한다: 화면 0행 = 7499 O'NEIL
            Assert.True(m.IsSorted && m.SortDescending);
            Assert.True(m.TryGetValue(0, 0, out var top) && Equals(top, 7499m));

            Assert.True(s!.SetCell(GridEditBinding.ToEditRow(m, s, 0), 1, "X"));

            Assert.True(m.TryGetValue(0, 1, out var shown) && Equals(shown, "X"));
            Assert.True(m.TryGetValue(1, 1, out var smith) && Equals(smith, "SMITH"));
            var change = Assert.Single(s.BuildChanges());
            Assert.Equal(7499m, change.Key["EMPNO"]); // 화면에 보이는 그 행(원래 키)
            Assert.Contains(":v1 = 'X', :k1 = 7499", s.BuildPreview(), StringComparison.Ordinal);

            await m.ToggleSortAsync(0); // 이제 변경이 있어 정렬을 풀지 않는다
            Assert.True(m.IsSorted && m.SortDescending);
            Assert.True(m.TryGetValue(0, 1, out var still) && Equals(still, "X"));
        }
        finally
        {
            pane.Clear();
        }
    }

    [Fact]
    public async Task Edits_on_a_sorted_result_stay_put_while_rows_load_and_filters_are_refused()
    {
        var (pane, m, s, _) = await RowsPaneAsync(edit: true);
        var calls = 0;
        ResultGridCommands.Filter = (_, _) =>
        {
            calls++;
            return Task.FromResult<IResultSet>(new MemoryResult(EmpRowCols, EmpRows()));
        };
        try
        {
            await m.ToggleSortAsync(0);
            await m.ToggleSortAsync(0); // 정렬한 상태에서 고친다
            Assert.True(s!.SetCell(GridEditBinding.ToEditRow(m, s, 0), 3, 2000m));

            await m.FetchAllAsync(); // 「모두 가져오기」
            m.Prefetch(0, 10);       // 화면이 페이지를 더 요청해도
            pane.WhereText = "SAL > 1000";
            await pane.ApplyFilterAsync(); // 결과 거르기 줄
            await pane.FilterByCellAsync(CellFilterKind.Equal);

            Assert.Equal(0, calls); // 반영하지 않은 편집이 있어 거르지 않는다
            Assert.True(m.IsSorted && m.SortDescending);
            Assert.True(m.TryGetValue(0, 0, out var key) && Equals(key, 7499m));
            Assert.True(m.TryGetValue(0, 3, out var sal) && Equals(sal, 2000m));
            Assert.True(m.TryGetValue(1, 3, out var smith) && Equals(smith, 800m));
            Assert.Equal(7499m, Assert.Single(s.BuildChanges()).Key["EMPNO"]);
        }
        finally
        {
            ResultGridCommands.Filter = null;
            pane.Clear();
        }
    }

    [Fact]
    public async Task An_edit_made_while_the_sort_is_still_fetching_rows_cancels_the_sort()
    {
        var rows = Enumerable.Range(0, 30).Select(i => new object?[] { 7000m + i, "E" + i, 1000m + i }).ToArray();
        var gate = new TaskCompletionSource();
        var (m, s) = await EditGridAsync(new GatedResult(EmpCols, rows, pageSize: 10, gate.Task), pageSize: 10);
        await using var scope = m;

        var sorting = m.ToggleSortAsync(0); // 끝까지 가져오는 동안(2쪽부터 막혀 있다)
        Assert.True(s.SetCell(0, 2, 1.5m)); // 셀을 고쳤다
        gate.SetResult();
        await sorting;

        Assert.False(m.IsSorted);
        Assert.True(m.TryGetValue(0, 2, out var sal) && Equals(sal, 1.5m));
        Assert.Equal(7000m, Assert.Single(s.BuildChanges()).Key["EMPNO"]);
    }

    /// <summary>첫 페이지 뒤의 페이지는 <paramref name="gate"/> 가 열릴 때까지 기다리는 결과.</summary>
    private sealed class GatedResult(ResultColumn[] columns, object?[][] rows, int pageSize, Task gate) : IResultSet
    {
        private readonly PagedResult _inner = new(columns, rows, pageSize);

        public IReadOnlyList<ResultColumn> Columns => columns;

        public bool IsComplete => _inner.IsComplete;

        public long? KnownRowCount => _inner.KnownRowCount;

        public async Task<ResultPage> GetPageAsync(int pageIndex, CancellationToken ct)
        {
            if (pageIndex > 0)
            {
                await gate.WaitAsync(ct);
            }

            return await _inner.GetPageAsync(pageIndex, ct);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Edits_and_new_rows_stay_with_their_rows_while_more_pages_load()
    {
        var rows = Enumerable.Range(0, 120).Select(i => new object?[] { 7000m + i, "E" + i, 1000m + i }).ToArray();
        var (m, s) = await EditGridAsync(new PagedResult(EmpCols, rows, pageSize: 10), pageSize: 10);
        await using var scope = m;
        Assert.True(m.HasMoreRows);
        Assert.True(s.SetCell(3, 2, 1.5m));
        var added = s.AddRow();
        Assert.True(s.SetCell(added, 0, 9000m));
        Assert.Equal(GridRowMark.Inserted, m.RowMarkOf(m.BaseRowCount)); // 새 행은 지금 받은 행 뒤

        await m.FetchAllAsync(); // 페이지를 더 가져온다

        Assert.Equal(120, m.BaseRowCount);
        Assert.True(m.TryGetValue(3, 2, out var sal) && Equals(sal, 1.5m)); // 바꾼 값은 제 행에
        Assert.True(m.TryGetValue(4, 2, out var next) && Equals(next, 1004m));
        Assert.Equal(GridRowMark.Inserted, m.RowMarkOf(120)); // 새 행은 끝으로 밀리되 같은 새 행
        Assert.True(m.TryGetValue(120, 0, out var no) && Equals(no, 9000m));
        Assert.Equal(GridRowMark.None, m.RowMarkOf(10));
        var update = Assert.Single(s.BuildChanges(), c => c.Kind == RowChangeKind.Update);
        Assert.Equal(7003m, update.Key["EMPNO"]);
    }
}
