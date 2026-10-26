using System.Globalization;
using Oranda.Core.Grid;

namespace Oranda.App.Features.ResultGrid;

// ResultGridModel 의 일부: 복사(ResultGridModel.cs 에서 나눔, 동작 불변).
public sealed partial class ResultGridModel
{
    // ---- 복사 ----

    /// <summary>
    /// 선택 범위를 복사 글자로 만든다. 선택이 없으면 빈 글자. 아직 못 받은 행은 가져온다.
    /// INSERT 문은 고른 칸의 열이 아니라 고른 칸이 걸친 행의 모든 열로 만든다(숨긴 열·ROWID 같은 의사 열은 빼고 결과 열 순서대로, 오렌지·Toad 처럼).
    /// </summary>
    public async Task<string> BuildCopyTextAsync(CopyFormat format, bool includeHeaders = false, CancellationToken ct = default)
    {
        if (!HasSelection)
        {
            return string.Empty;
        }

        var (from, to) = SelectionRect();
        var ordinals = format == CopyFormat.Insert
            ? _visible.Where(o => !IsPseudoColumn(Columns[o])).ToList()
            : _visible.Skip(from.Col).Take(to.Col - from.Col + 1).ToList();
        if (ordinals.Count == 0)
        {
            return string.Empty;
        }

        var cols = ordinals.Select(o => Columns[o]).ToList();
        var rows = await ReadSelectedRowsAsync(ordinals, ct).ConfigureAwait(true);
        var first = cols[0];
        var table = first.BaseTable is { } t
            ? (first.BaseOwner is { } owner ? $"{CellValueFormatter.QuoteIdentifier(owner)}." : string.Empty) + CellValueFormatter.QuoteIdentifier(t)
            : "TABLE_NAME";
        return SelectionCopyFormatter.Format(format, cols, rows, new CopyOptions { IncludeHeaders = includeHeaders, TableName = table });
    }

    /// <summary>선택 범위를 복사해 <see cref="ClipboardWriter"/> 로 쓴다.</summary>
    public async Task CopyAsync(CopyFormat format, bool includeHeaders = false, CancellationToken ct = default)
    {
        var text = await BuildCopyTextAsync(format, includeHeaders, ct).ConfigureAwait(true);
        if (text.Length > 0 && ClipboardWriter is { } w)
        {
            await w(text).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// 「행 복사」(<c>oranda.grid.copyRows</c>): 고른 칸이 걸친 행 전체(보이는 열 모두, 숨긴 열은 빼고)를 TSV 로 클립보드에 쓴다.
    /// 앱 안에서 붙여 넣을 때 NULL·날짜·숫자 형식을 잃지 않게 원시 값 사본을 <see cref="ResultGridCommands.CopiedRows"/> 에 함께 둔다.
    /// 선택이 없으면 아무것도 하지 않고 null.
    /// </summary>
    public async Task<GridRowsCopy?> CopyRowsAsync(bool includeHeaders = false, CancellationToken ct = default)
    {
        if (!HasSelection || _visible.Count == 0)
        {
            return null;
        }

        var ordinals = _visible.ToList();
        var cols = ordinals.Select(o => Columns[o]).ToList();
        var rows = await ReadSelectedRowsAsync(ordinals, ct).ConfigureAwait(true);
        var text = SelectionCopyFormatter.Format(CopyFormat.Tsv, cols, rows, new CopyOptions { IncludeHeaders = includeHeaders });
        var copy = new GridRowsCopy(cols, rows, text);
        ResultGridCommands.CopiedRows = copy;
        if (ClipboardWriter is { } w)
        {
            await w(text).ConfigureAwait(true);
        }

        return copy;
    }

    /// <summary>
    /// 선택 범위가 걸친 행(화면 순서)의 값을 <paramref name="ordinals"/> 열 순서로 읽는다. 아직 못 받은 페이지는 가져온다.
    /// 편집 모드에서는 화면에 보이는 그대로 읽는다(바꾼 값이 원본보다 앞서고, 새 행도 읽는다).
    /// </summary>
    public async Task<List<IReadOnlyList<object?>>> ReadSelectedRowsAsync(IReadOnlyList<int> ordinals, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ordinals);
        if (!HasSelection)
        {
            return [];
        }

        // 고른 행 구간을 화면 순서로 이어 읽는다(칸 범위가 걸친 행 + Ctrl 로 더한 떨어진 행).
        var rows = new List<IReadOnlyList<object?>>();
        foreach (var (first, last) in SelectedRowRanges())
        {
            rows.AddRange(await ReadRowsAsync(first, last, ordinals, ct).ConfigureAwait(true));
        }

        return rows;
    }

    /// <summary>
    /// 화면 행 <paramref name="firstRow"/>~<paramref name="lastRow"/>(양끝 포함)의 값을 <paramref name="ordinals"/> 열 순서로 읽는다.
    /// 아직 못 받은 페이지는 가져온다. 편집 모드에서는 화면에 보이는 그대로 읽는다.
    /// </summary>
    public async Task<List<IReadOnlyList<object?>>> ReadRowsAsync(long firstRow, long lastRow, IReadOnlyList<int> ordinals, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ordinals);
        var rows = new List<IReadOnlyList<object?>>();
        rows.Capacity = (int)Math.Clamp(lastRow - firstRow + 1, 0, 100_000);
        ResultPage? page = null; // 같은 페이지의 행은 한 번만 가져오고 알린다(행마다 Changed 를 내면 큰 선택에서 선택 집계가 행 수만큼 다시 돈다)
        for (var r = firstRow; r <= lastRow; r++)
        {
            var values = new object?[ordinals.Count];
            if (BaseRowOf(r) is var baseRow && baseRow >= 0)
            {
                var src = SourceRow(baseRow);
                if (src < 0)
                {
                    break;
                }

                if (page is null || page.PageIndex != (int)(src / PageSize))
                {
                    page = await _cache.GetPageAsync((int)(src / PageSize), ct).ConfigureAwait(true);
                    OnPage(page);
                }

                var i = (int)(src - page.FirstRowIndex);
                if (i < 0 || i >= page.RowCount)
                {
                    break;
                }

                for (var k = 0; k < ordinals.Count; k++)
                {
                    values[k] = page.Columns[ordinals[k]][i];
                }
            }

            if (EditOverlay is { } edit)
            {
                for (var k = 0; k < ordinals.Count; k++)
                {
                    if (edit.TryGetValue(r, ordinals[k], out var pending))
                    {
                        values[k] = pending;
                    }
                }
            }

            rows.Add(values);
        }

        return rows;
    }

    /// <summary>
    /// 앱 안 거르기와 상관없이 지금까지 가져온 모든 행(정렬 순서)의 값을 <paramref name="ordinals"/> 열 순서로 읽는다(내보내기 「가져온 전체」).
    /// 거르기를 통과한 행은 화면에 보이는 값(편집 중인 값 포함)을, 가려진 행은 가져온 원본 값을 쓴다. 새로 더한 행은 가져온 행이 아니므로 뺀다.
    /// </summary>
    public async IAsyncEnumerable<IReadOnlyList<object?>> ReadAllFetchedRowsAsync(IReadOnlyList<int> ordinals,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ordinals);
        var count = _loadedRows;
        var viewOf = new Dictionary<int, long>();
        if (_match is not null && _view is { } v)
        {
            for (var i = 0; i < v.Length; i++)
            {
                viewOf[v[i]] = i;
            }
        }

        // 정렬 순열이 있으면 그 순서로, 정렬한 뒤 더 가져온 행은 가져온 순서대로 뒤에 붙인다(RebuildView 와 같은 규칙).
        var sorted = _order;
        var sortedLength = Math.Min(sorted?.Length ?? 0, (int)Math.Min(count, int.MaxValue));
        for (long k = 0; k < count; k++)
        {
            ct.ThrowIfCancellationRequested();
            var src = k < sortedLength ? sorted![k] : (int)k;
            if (viewOf.TryGetValue(src, out var viewRow))
            {
                var visible = await ReadRowsAsync(viewRow, viewRow, ordinals, ct).ConfigureAwait(true);
                if (visible.Count == 1)
                {
                    yield return visible[0];
                }

                continue;
            }

            var page = await _cache.GetPageAsync(src / PageSize, ct).ConfigureAwait(true);
            OnPage(page);
            var i = (int)(src - page.FirstRowIndex);
            if (i < 0 || i >= page.RowCount)
            {
                yield break;
            }

            var values = new object?[ordinals.Count];
            for (var c = 0; c < ordinals.Count; c++)
            {
                values[c] = page.Columns[ordinals[c]][i];
            }

            yield return values;
        }
    }

    /// <summary>INSERT 문 복사에서 빼는 의사 열: ROWID 종류 열과 이름이 ROWID·ROWNUM·ORA_ROWSCN 인 열.</summary>
    public static bool IsPseudoColumn(ResultColumn column)
    {
        ArgumentNullException.ThrowIfNull(column);
        return column.Kind == ColumnDataKind.RowId
            || column.Name.Equals("ROWID", StringComparison.OrdinalIgnoreCase)
            || column.Name.Equals("ROWNUM", StringComparison.OrdinalIgnoreCase)
            || column.Name.Equals("ORA_ROWSCN", StringComparison.OrdinalIgnoreCase);
    }
}
