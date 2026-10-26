using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.Features.GridEdit;
using Oranda.App.Features.Localization;
using Oranda.App.Shell;
using Oranda.Core.Execution;
using Oranda.Core.Grid;

namespace Oranda.App.Features.ResultGrid;

// SqlResultPaneViewModel 의 일부: 결과 거르기·셀 값 거르기(SqlResultPaneViewModel.cs 에서 나눔, 동작 불변).
public sealed partial class SqlResultPaneViewModel
{
    // ---- 결과 거르기(GRID-10) ---------------------------------------------------------------

    /// <summary>거르기 줄 보이기/숨기기(<c>oranda.grid.toggleFilterBar</c>). 질의 결과가 아니면 열지 않는다.</summary>
    public void ToggleFilterBar()
    {
        if (!_isFilterBarVisible && Statement is not { Kind: StatementKind.Query })
        {
            return;
        }

        IsFilterBarVisible = !_isFilterBarVisible;
        if (_isFilterBarVisible)
        {
            FilterFocusRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// 거르기 줄의 조건·정렬로 결과를 만든 질의를 감싸 다시 실행하고, 이 결과 영역의 결과를 바꾼다(줄의 글은 남는다).
    /// 편집 모드였으면 편집 없이 다시 연다(감싼 질의는 키·ROWID 를 알아낼 수 없다). 반영하지 않은 편집이 있으면 거르지 않는다.
    /// 실패·취소는 상태 줄에 보이고 지금 결과를 그대로 둔다. 밖으로 던지지 않는다.
    /// </summary>
    public async Task ApplyFilterAsync(CancellationToken ct = default)
    {
        if (_isFiltering || Statement is not { Kind: StatementKind.Query } query)
        {
            return;
        }

        var request = new ResultFilterRequest(query, WhereText.Trim(), OrderByText.Trim())
        {
            Binds = new Dictionary<string, BindValue>(_filterBinds, StringComparer.OrdinalIgnoreCase),
            RunBinds = RunBinds,
        };
        if (request.IsEmpty && _activeFilter is null)
        {
            ShowFilterMessage(null); // 거른 적이 없고 조건도 없다: 다시 실행할 까닭이 없다
            return;
        }

        if (IsEditDirty)
        {
            ShowFilterMessage("반영하지 않은 편집이 있습니다. 반영하거나 되돌린 뒤 거르세요.");
            return;
        }

        if ((Runner?.Filter ?? ResultGridCommands.Filter) is not { } run)
        {
            ShowFilterMessage("거를 수 없습니다(실행 엔진이 연결되지 않았습니다).");
            return;
        }

        var generation = _resultGeneration;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _cancelFilter = cts.Cancel;
        IsFiltering = true;
        ShowFilterMessage(request.IsEmpty ? "거르기를 지우는 중…" : "거르는 중…");

        IResultSet? result = null;
        string? message = null;
        try
        {
            result = await run(request, cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            message = "거르기를 취소했습니다.";
        }
        catch (Exception e)
        {
            message = "거르지 못했습니다: " + e.Message.ReplaceLineEndings(" ");
        }

        _cancelFilter = null;
        IsFiltering = false;
        if (generation != _resultGeneration)
        {
            // 그새 결과 영역이 비었거나 다른 결과로 바뀌었다: 늦게 온 결과는 버린다.
            if (result is not null)
            {
                await result.DisposeAsync().ConfigureAwait(true);
            }

            return;
        }

        if (result is null)
        {
            ShowFilterMessage(message);
            return;
        }

        if (_editSession is { } editing && ReferenceEquals(GridEditCommands.Active, editing))
        {
            GridEditCommands.Active = null; // 편집 명령이 버린 편집 상태를 가리키지 않게
        }

        SetResultCore(result, _model?.Display, keepLocalFilter: true);
        _activeFilter = request.IsEmpty ? null : request;
        _filterMessage = null;
        Remember(request.Where);
        OnPropertyChanged(nameof(IsFiltered));
        UpdateStatus();
        FilterApplied?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 거르기 지우기: 조건·정렬 칸을 비우고, 거른 결과였으면 원래 질의를 그대로 다시 실행한다(편집 모드는 F9 로 다시).
    /// 셀 값 바인드는 남긴다(최근 조건 목록의 조건이 그 이름을 쓴다).
    /// </summary>
    public Task ClearFilterAsync(CancellationToken ct = default)
    {
        WhereText = string.Empty;
        OrderByText = string.Empty;
        return ApplyFilterAsync(ct);
    }

    /// <summary>거르는 중인 질의를 취소한다(서버 중단은 실행 엔진 몫).</summary>
    public void CancelFilter() => _cancelFilter?.Invoke();

    /// <summary>
    /// 셀 값으로 거르기(「이 값으로 거르기」·「이 값 빼고 거르기」·「NULL 인 행만」): 활성 칸의 열과 값으로 조건을 만들어
    /// WHERE 칸 뒤에 <c>AND</c> 로 잇고 바로 거른다. 값은 바인드(<c>:oranda_fN</c>)로만 넘기고 본문에 이어 붙이지 않는다.
    /// </summary>
    public Task FilterByCellAsync(CellFilterKind kind, CancellationToken ct = default)
    {
        if (!CanApplyFilter || Statement is not { } query || _model is not { HasResult: true, HasSelection: true } m)
        {
            return Task.CompletedTask;
        }

        if (IsEditDirty)
        {
            ShowFilterMessage("반영하지 않은 편집이 있습니다. 반영하거나 되돌린 뒤 거르세요.");
            return Task.CompletedTask;
        }

        var cell = m.Active;
        if (cell.Col < 0 || cell.Col >= m.VisibleColumns.Count)
        {
            return Task.CompletedTask;
        }

        var ordinal = m.VisibleColumns[cell.Col];
        if (!m.TryGetValue(cell.Row, ordinal, out var value))
        {
            ShowFilterMessage("칸 값을 아직 가져오지 않았습니다. 잠시 뒤 다시 거르세요.");
            return Task.CompletedTask;
        }

        var name = ResultFilter.NextBindName(query.BindNames.Concat(_filterBinds.Keys), ref _filterBindCounter);
        if (!ResultFilter.TryCellCondition(m.Columns[ordinal], value, kind, name, out var condition, out var bind, out var error))
        {
            ShowFilterMessage(error);
            return Task.CompletedTask;
        }

        if (bind is not null)
        {
            _filterBinds[name] = bind;
        }

        WhereText = ResultFilter.Combine(WhereText, condition);
        IsFilterBarVisible = true;
        return ApplyFilterAsync(ct);
    }

    private void ShowFilterMessage(string? message)
    {
        _filterMessage = message;
        UpdateStatus();
    }

    /// <summary>적용한 조건을 최근 목록 맨 앞에 둔다(같은 글은 하나만, 최대 <see cref="ResultFilter.HistorySize"/>개).</summary>
    private void Remember(string condition)
    {
        if (condition.Length == 0)
        {
            return;
        }

        var at = FilterHistory.IndexOf(condition);
        if (at == 0)
        {
            return;
        }

        if (at > 0)
        {
            FilterHistory.RemoveAt(at);
        }

        FilterHistory.Insert(0, condition);
        while (FilterHistory.Count > ResultFilter.HistorySize)
        {
            FilterHistory.RemoveAt(FilterHistory.Count - 1);
        }

        OnPropertyChanged(nameof(HasFilterHistory));
    }
}
