using Oranda.App.Features.ResultGrid;
using Oranda.Core.Grid;

namespace Oranda.App.Features.GridEdit;

/// <summary>
/// 고른 모든 행에 하는 편집(TSK-11-01): 행 삭제·행 되돌리기, 그리고 편집 모드가 꺼진 결과에서 삭제·복제를 누르면 편집 모드를 먼저 켜는 일.
/// 「고른 행」은 <see cref="ResultGridModel.SelectedRowRanges"/>(칸 범위가 걸친 행 + Ctrl 로 더한 행)다.
/// </summary>
public static class GridRowOps
{
    /// <summary>한 번에 읽어 두고 지우는 행 수(그 행의 페이지가 메모리에 있을 때 원래 값을 기억해야 하므로 덩어리로 나눈다).</summary>
    private const int Chunk = 2000;

    /// <summary>구간 목록을 행 번호 하나씩으로 펼친다(화면 순서).</summary>
    internal static IEnumerable<long> Rows(IReadOnlyList<(long First, long Last)> ranges)
    {
        foreach (var (first, last) in ranges)
        {
            for (var r = first; r <= last; r++)
            {
                yield return r;
            }
        }
    }

    /// <summary>
    /// 편집할 수 있는 상태의 그리드와 세션을 돌려준다. 이미 편집 모드면 그대로, 아니면 편집 모드로 다시 실행해 고른 행을 새 결과에서 다시 고른다
    /// (다시 읽은 행의 값이 처음 고른 행과 다르면 엉뚱한 행을 지우지 않도록 멈추고 알린다). 켤 수 없으면 안내 막대를 띄우고 null.
    /// </summary>
    public static async Task<(ResultGridModel Grid, GridEditSession Session)?> EnsureEditModeAsync(ResultGridModel grid, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(grid);
        if (GridEditBinding.Of(grid) is { CanEdit: true } ready)
        {
            return (grid, ready);
        }

        if (GridEditBinding.Of(grid) is not null || grid.EditModeStarter is not { } start || !grid.HasSelection)
        {
            grid.RequestEditHint(); // 고칠 수 없는 결과(이유가 안내 막대에)·켤 수 없는 결과
            return null;
        }

        var ranges = grid.SelectedRowRanges();
        var ordinals = Enumerable.Range(0, grid.Columns.Count).ToList();
        var before = await grid.ReadSelectedRowsAsync(ordinals, ct).ConfigureAwait(true);
        var activeBefore = GridEditCommands.Active;
        if (!await start(ct).ConfigureAwait(true))
        {
            grid.RequestEditHint();
            return null;
        }

        // 다시 실행하면 결과가 새로 열린다: 새 그리드와 그 편집 세션을 찾는다. 고칠 수 없으면 안내 막대가 이유를 보인다.
        if (GridEditBinding.ModelOf(GridEditCommands.Active) is not { } next || ReferenceEquals(next, grid)
            || ReferenceEquals(GridEditCommands.Active, activeBefore) || GridEditBinding.Of(next) is not { CanEdit: true } session)
        {
            return null;
        }

        next.SetSelectedRows(ranges);
        var after = await next.ReadSelectedRowsAsync(ordinals, ct).ConfigureAwait(true);
        if (!SameRows(before, after))
        {
            next.ReportNotice("편집 모드로 다시 실행했더니 결과의 행이 달라졌습니다. 행을 다시 골라 실행하세요.");
            return null;
        }

        return (next, session);
    }

    private static bool SameRows(List<IReadOnlyList<object?>> a, List<IReadOnlyList<object?>> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        for (var i = 0; i < a.Count; i++)
        {
            if (a[i].Count != b[i].Count)
            {
                return false;
            }

            for (var k = 0; k < a[i].Count; k++)
            {
                if (!SameValue(a[i][k], b[i][k]))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool SameValue(object? x, object? y)
    {
        x = x is DBNull ? null : x;
        y = y is DBNull ? null : y;
        return x is null || y is null
            ? x is null && y is null
            : x is byte[] bx && y is byte[] by ? bx.AsSpan().SequenceEqual(by) : Equals(x, y) || string.Equals(x.ToString(), y.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// 고른 모든 행을 지운다(새 행은 바로 없애고 기존 행은 삭제 표시). 편집 모드가 꺼진 결과면 먼저 켠다(<see cref="EnsureEditModeAsync"/>).
    /// 지운 결과를 돌려준다(편집할 수 없거나 켤 수 없으면 null).
    /// </summary>
    public static async Task<RowsDeleted?> DeleteSelectedAsync(ResultGridModel grid, CancellationToken ct)
    {
        if (await EnsureEditModeAsync(grid, ct).ConfigureAwait(true) is not { } ready)
        {
            return null;
        }

        var (g, s) = ready;

        var ranges = g.SelectedRowRanges();
        if (ranges.Count == 0)
        {
            return null;
        }

        var total = ranges.Sum(r => r.Last - r.First + 1);
        var col = g.Active.Col;
        var firstRow = ranges[0].First;

        // 새 행을 지우면 뒤 화면 행의 편집 행 번호 찾기가 달라지므로, 먼저 모든 행의 편집 행 번호를 구해 둔다.
        var viewRows = Rows(ranges).ToList();
        var editRows = GridEditBinding.ToEditRows(g, s, viewRows);
        int marked = 0, removed = 0, skipped = 0;
        using (s.Batch())
        {
            for (var at = 0; at < viewRows.Count; at += Chunk)
            {
                var n = Math.Min(Chunk, viewRows.Count - at);
                var slice = editRows.GetRange(at, n);

                // 이 덩어리 행의 페이지를 메모리에 올린 뒤 바로 지운다(삭제는 그 행의 원래 값을 기억해 WHERE 키로 쓴다).
                // 구간 하나씩 읽는다: 떨어진 행 사이를 읽지 않는다.
                await LoadAsync(g, viewRows, at, n, ct).ConfigureAwait(true);
                var done = s.DeleteRows(slice);
                marked += done.Marked;
                removed += done.Removed;
                skipped += done.Skipped;
            }
        }

        var result = new RowsDeleted(marked, removed, skipped);
        if (removed > 0)
        {
            // 새 행이 없어져 뒤 행이 당겨졌다: 엉뚱한 행이 골라진 채로 남지 않게 첫 행 자리 하나로 접는다.
            g.SelectCell(new GridCell(Math.Min(firstRow, Math.Max(0, g.RowCount - 1)), col));
        }

        if (total > 1)
        {
            g.ReportNotice(DeleteNotice(result));
        }

        return result;
    }

    private static async Task LoadAsync(ResultGridModel g, List<long> viewRows, int at, int n, CancellationToken ct)
    {
        // 덩어리 안의 이어진 행을 한 번에 읽는다(첫 열만: 페이지를 올리는 게 목적이다).
        var i = at;
        var end = at + n;
        while (i < end)
        {
            var j = i;
            while (j + 1 < end && viewRows[j + 1] == viewRows[j] + 1)
            {
                j++;
            }

            await g.ReadRowsAsync(viewRows[i], viewRows[j], [0], ct).ConfigureAwait(true);
            i = j + 1;
        }
    }

    internal static string DeleteNotice(RowsDeleted r)
    {
        var parts = new List<string>();
        if (r.Marked > 0)
        {
            parts.Add($"{r.Marked}행을 삭제 표시했습니다(반영하면 DELETE {r.Marked}건이 실행됩니다)");
        }

        if (r.Removed > 0)
        {
            parts.Add($"새 행 {r.Removed}개는 바로 없앴습니다");
        }

        if (r.Skipped > 0)
        {
            parts.Add($"이미 지웠거나 지울 수 없는 {r.Skipped}행은 건너뛰었습니다");
        }

        return parts.Count == 0 ? "지울 행이 없습니다." : string.Join(" · ", parts) + ".";
    }

    /// <summary>고른 모든 행의 바꾼 값·삭제 표시·새 행을 되돌린다. 되돌린 행 수를 돌려준다(편집 모드가 아니면 0).</summary>
    public static int ResetSelected(ResultGridModel grid)
    {
        ArgumentNullException.ThrowIfNull(grid);
        if (GridEditBinding.Of(grid) is not { } s || !grid.HasSelection)
        {
            return 0;
        }

        var ranges = grid.SelectedRowRanges();
        var firstRow = ranges[0].First;
        var col = grid.Active.Col;
        var editRows = GridEditBinding.ToEditRows(grid, s, Rows(ranges));
        var hadNew = editRows.Any(r => r < 0);
        s.ResetRows(editRows);
        if (hadNew)
        {
            grid.SelectCell(new GridCell(Math.Min(firstRow, Math.Max(0, grid.RowCount - 1)), col));
        }

        if (editRows.Count > 1)
        {
            grid.ReportNotice($"{editRows.Count}행을 되돌렸습니다.");
        }

        return editRows.Count;
    }
}
