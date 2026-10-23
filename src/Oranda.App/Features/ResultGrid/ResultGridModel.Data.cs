using System.Globalization;
using Oranda.Core.Grid;

namespace Oranda.App.Features.ResultGrid;

// ResultGridModel 의 일부: 데이터 가져오기·셀 읽기(ResultGridModel.cs 에서 나눔, 동작 불변).
public sealed partial class ResultGridModel
{
    // ---- 데이터 가져오기 ----

    /// <summary>첫 페이지를 가져온다. 열이 없는 결과(DML 등)면 아무것도 하지 않는다.</summary>
    public Task LoadFirstPageAsync(CancellationToken ct = default) => HasResult ? FetchPageAsync(0, ct) : Task.CompletedTask;

    /// <summary>보이는 행 범위가 쓸 페이지를 백그라운드로 요청한다(Render 에서 불러도 싸다).</summary>
    public void Prefetch(long firstViewRow, long lastViewRow)
    {
        if (_disposed || RowCount == 0 && _loadedRows > 0)
        {
            return;
        }

        var first = (int)(SourceRow(Math.Max(0, Math.Min(firstViewRow, Math.Max(0, BaseRowCount - 1)))) / PageSize);
        var lastSrc = SourceRow(Math.Max(0, Math.Min(lastViewRow, Math.Max(0, BaseRowCount - 1))));
        var last = (int)(lastSrc / PageSize);
        for (var p = first; p <= last; p++)
        {
            if (!_cache.TryGetResident(p, out _) && (TotalRowCount is null || (long)p * PageSize < TotalRowCount))
            {
                RequestPage(p);
            }
        }

        // 끝에 가까우면 다음 페이지를 미리 받는다(화면 끝 근처에서 다음 페이지 요청, TRD 4.4).
        if (HasMoreRows && !IsSorted && _match is null && lastViewRow >= _loadedRows - PageSize / 2)
        {
            RequestPage((int)(_loadedRows / PageSize));
        }
    }

    /// <summary>커서 끝까지 모두 가져온다(PRD 「모두 가져오기」).</summary>
    public async Task FetchAllAsync(CancellationToken ct = default)
    {
        var p = (int)(_loadedRows / PageSize);
        while (HasMoreRows)
        {
            var page = await FetchPageAsync(p, ct).ConfigureAwait(true);
            if (page.IsLast || page.RowCount == 0)
            {
                break;
            }

            p++;
        }

        await ExtendLocalFilterAsync(ct).ConfigureAwait(true);
    }

    private async Task<ResultPage> FetchPageAsync(int pageIndex, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        var page = await _cache.GetPageAsync(pageIndex, linked.Token).ConfigureAwait(true);
        OnPage(page);
        return page;
    }

    private void RequestPage(int pageIndex)
    {
        if (_disposed || !_pending.Add(pageIndex))
        {
            return;
        }

        _ = LoadInBackgroundAsync(pageIndex);
    }

    private async Task LoadInBackgroundAsync(int pageIndex)
    {
        try
        {
            await FetchPageAsync(pageIndex, _cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // 닫는 중
        }
        catch (Exception ex)
        {
            Error = ex;
            RaiseChanged();
        }
        finally
        {
            _pending.Remove(pageIndex);
        }
    }

    private void OnPage(ResultPage page)
    {
        var end = page.FirstRowIndex + page.RowCount;
        var changed = false;
        if (end > _loadedRows)
        {
            _loadedRows = end;
            changed = true;
        }

        if (page.IsLast || page.RowCount == 0)
        {
            _knownRows = _loadedRows;
            changed = true;
        }

        if (changed || page.RowCount > 0)
        {
            RaiseChanged();
        }
    }

    // ---- 셀 읽기 ----

    // 화면 행 → 질의 결과 행. 정렬 순열·거르기 결과를 합친 _view 를 거친다(없으면 그대로). 거르기 중에는 범위 밖을 -1 로 돌려 숨긴 행을 읽지 않게 한다.
    private long SourceRow(long viewRow)
    {
        if (_view is not { } v)
        {
            return viewRow;
        }

        return viewRow >= 0 && viewRow < v.Length ? v[viewRow] : _match is null ? viewRow : -1;
    }

    /// <summary>화면 행 → 질의 결과 행(정렬 순서표를 거치기 전의 순번). 편집 모드에서 새로 더한 행이면 -1.</summary>
    public long BaseRowOf(long viewRow) => EditOverlay is { } edit ? edit.BaseRowOf(viewRow) : viewRow;

    /// <summary>원시 값. 페이지가 아직 없으면 <c>false</c>(백그라운드 요청은 <see cref="Prefetch"/> 몫).</summary>
    public bool TryGetValue(long viewRow, int ordinal, out object? value)
    {
        // 편집 모드: 바꾼 값이 원본보다 앞서고, 질의 결과 뒤의 행은 새로 더한 행이다(페이지가 없다).
        if (EditOverlay is { } edit)
        {
            if (edit.TryGetValue(viewRow, ordinal, out value))
            {
                return true;
            }

            viewRow = edit.BaseRowOf(viewRow);
            if (viewRow < 0)
            {
                value = null;
                return true;
            }
        }

        return TryGetBaseValue(viewRow, ordinal, out value);
    }

    /// <summary>
    /// 질의 결과의 원래 값(편집 모드에서 바꾼 값을 거치지 않는다). <paramref name="baseRow"/> 는 질의 결과 행(<see cref="BaseRowOf"/>, 편집 세션의 행 번호와 같다).
    /// 편집 세션이 UPDATE·DELETE 의 WHERE 키, 낙관적 잠금의 원래 값, 「원래 값과 같으면 변경 지움」에 쓴다(<c>GridEditBinding.OriginalValues</c>).
    /// 새 행(질의 결과 밖)이거나 페이지가 메모리에 없으면 false.
    /// </summary>
    public bool TryGetBaseValue(long baseRow, int ordinal, out object? value)
    {
        if (baseRow >= 0 && baseRow < BaseRowCount)
        {
            var src = SourceRow(baseRow);
            if (_cache.TryGetResident((int)(src / PageSize), out var page))
            {
                var i = (int)(src - page.FirstRowIndex);
                if (i >= 0 && i < page.RowCount)
                {
                    value = page.Columns[ordinal][i];
                    return true;
                }
            }
        }

        value = null;
        return false;
    }

    /// <summary>셀 표시 글자. 페이지가 아직 없으면 빈 글자(그리기 쪽이 곧 다시 그린다). 같은 값 숨김이 켜져 있으면 앞 행과 같을 때 빈 글자.</summary>
    public string GetCellText(long viewRow, int ordinal)
    {
        if (!TryGetValue(viewRow, ordinal, out var v))
        {
            RequestPage((int)(SourceRow(BaseRowOf(viewRow)) / PageSize));
            return string.Empty;
        }

        var text = CellValueFormatter.Format(Columns[ordinal], v, Display);
        if (HideRepeatedValues && viewRow > 0 && TryGetValue(viewRow - 1, ordinal, out var prev)
            && CellValueFormatter.Format(Columns[ordinal], prev, Display) == text)
        {
            return string.Empty;
        }

        return text;
    }

    /// <summary>값이 NULL 이면 true(NULL 표시 색 구분용). 페이지가 없으면 false.</summary>
    public bool IsNull(long viewRow, int ordinal) => TryGetValue(viewRow, ordinal, out var v) && v is null or DBNull;

    /// <summary>숫자·날짜·간격 열은 오른쪽 맞춤.</summary>
    public bool IsRightAligned(int ordinal) => Columns[ordinal].Kind is ColumnDataKind.Number or ColumnDataKind.BinaryFloat;
}
