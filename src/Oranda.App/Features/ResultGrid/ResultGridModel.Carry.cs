using Oranda.Core.Grid;

namespace Oranda.App.Features.ResultGrid;

// ResultGridModel 의 일부: 같은 모양의 새 결과로 화면 상태 이어받기(모니터 자동 새로 고침, TSK-14-15).
public sealed partial class ResultGridModel
{
    /// <summary>
    /// (화면 행, 열 ordinal)을 받아 참이면 그 칸 글자를 위험색으로 그린다(모니터의 블로커 칸 등). NULL 칸은 무시. 기본 없음.
    /// </summary>
    public Func<long, int, bool>? CellEmphasis { get; set; }

    /// <summary>이 칸을 위험색으로 그리는가(<see cref="CellEmphasis"/>).</summary>
    public bool IsEmphasized(long viewRow, int ordinal) => CellEmphasis?.Invoke(viewRow, ordinal) == true;

    /// <summary>(화면 행, 열 ordinal)을 받아 참이면 그 칸 글자를 주의색으로 그린다(튜닝 표의 느린 plan_hash·미승인 baseline 등). 위험색이 먼저. 기본 없음.</summary>
    public Func<long, int, bool>? CellWarning { get; set; }

    /// <summary>이 칸을 주의색으로 그리는가(<see cref="CellWarning"/>).</summary>
    public bool IsWarned(long viewRow, int ordinal) => CellWarning?.Invoke(viewRow, ordinal) == true;

    /// <summary>화면 행 → 결과 집합의 원래 행 번호(정렬·거르기를 거친 뒤). 새 행이거나 범위 밖이면 -1.</summary>
    public long SourceRowOf(long viewRow)
    {
        var baseRow = BaseRowOf(viewRow);
        return baseRow < 0 || baseRow >= BaseRowCount ? -1 : SourceRow(baseRow);
    }

    /// <summary>
    /// 같은 열(이름·순서)의 이전 결과에서 화면 상태를 이어받는다: 열 너비, 보이는 열 순서·숨김, 고정 열 수, 텍스트 모드, 같은 값 숨김.
    /// 정렬·앱 안 거르기는 행을 다시 계산해야 하므로 <see cref="CarrySortAndFilterFromAsync"/> 가 맡는다. 열이 다르면 아무것도 하지 않는다.
    /// </summary>
    public bool CarryViewStateFrom(ResultGridModel old)
    {
        ArgumentNullException.ThrowIfNull(old);
        if (!SameColumns(old))
        {
            return false;
        }

        CarryLayoutFrom(old);
        _visible.Clear();
        _visible.AddRange(old._visible.Where(o => o >= 0 && o < Columns.Count));
        FrozenCount = Math.Min(old.FrozenCount, Math.Max(0, _visible.Count - 1));
        IsTextMode = old.IsTextMode;
        HideRepeatedValues = old.HideRepeatedValues;
        RefreshQuickFilterColumns();
        return true;
    }

    /// <summary>이전 결과의 정렬(열·방향)과 앱 안 거르기를 이 결과에 다시 건다. 이 결과의 행을 모두 가져온 뒤 계산한다.</summary>
    public async Task CarrySortAndFilterFromAsync(ResultGridModel old, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(old);
        if (!SameColumns(old))
        {
            return;
        }

        if (old.SortColumn >= 0 && old.SortColumn < Columns.Count)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
            try
            {
                await FetchAllCancellableAsync(cts.Token).ConfigureAwait(true);
                await SortLocallyAsync(old.SortColumn, old.SortDescending, cts.Token).ConfigureAwait(true);
            }
            finally
            {
                SetSortPhase(SortPhase.None);
            }
        }

        if (!old.LocalFilter.IsEmpty)
        {
            await SetLocalFilterAsync(old.LocalFilter, ct).ConfigureAwait(true);
        }
    }

    private bool SameColumns(ResultGridModel old)
    {
        if (old.Columns.Count != Columns.Count)
        {
            return false;
        }

        for (var c = 0; c < Columns.Count; c++)
        {
            if (!string.Equals(old.Columns[c].Name, Columns[c].Name, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// 메모리에 있는 행으로 만든 결과 집합(모니터 표처럼 이미 읽은 목록을 결과 그리드로 보일 때). 모든 행을 한 페이지로 준다.
/// <paramref name="rows"/> 는 행마다 열 값(열 수와 같은 길이).
/// </summary>
public sealed class MemoryResultSet(IReadOnlyList<ResultColumn> columns, IReadOnlyList<IReadOnlyList<object?>> rows) : IResultSet
{
    public IReadOnlyList<ResultColumn> Columns { get; } = columns ?? throw new ArgumentNullException(nameof(columns));

    public bool IsComplete => true;

    public long? KnownRowCount => rows.Count;

    /// <summary>한 페이지 = 전체 행(빈 결과도 1).</summary>
    public int? PageSize => Math.Max(1, rows.Count);

    public Task<ResultPage> GetPageAsync(int pageIndex, CancellationToken ct)
    {
        if (pageIndex > 0 || rows.Count == 0)
        {
            return Task.FromResult(new ResultPage(pageIndex, rows.Count, 0, [.. Columns.Select(_ => (IReadOnlyList<object?>)[])]) { IsLast = true });
        }

        var cols = new IReadOnlyList<object?>[Columns.Count];
        for (var c = 0; c < cols.Length; c++)
        {
            var values = new object?[rows.Count];
            for (var r = 0; r < rows.Count; r++)
            {
                values[r] = c < rows[r].Count ? rows[r][c] : null;
            }

            cols[c] = values;
        }

        return Task.FromResult(new ResultPage(0, 0, rows.Count, cols) { IsLast = true });
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
