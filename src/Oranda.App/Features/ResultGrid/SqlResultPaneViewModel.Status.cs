using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.Features.GridEdit;
using Oranda.App.Features.Localization;
using Oranda.App.Shell;
using Oranda.Core.Execution;
using Oranda.Core.Grid;

namespace Oranda.App.Features.ResultGrid;

// SqlResultPaneViewModel 의 일부: 상태 줄·선택 집계(SqlResultPaneViewModel.cs 에서 나눔, 동작 불변).
public sealed partial class SqlResultPaneViewModel
{
    /// <summary>
    /// 결과를 바꾸거나 닫을 때: 거르는 중이면 취소하고(늦게 온 결과는 버린다) 「거른 결과」 표시를 지운다.
    /// 거르기 줄의 글·최근 조건·셀 값 바인드는 남긴다(같은 결과 영역에서 이어 쓴다).
    /// </summary>
    private void ResetFilter()
    {
        _resultGeneration++;
        _cancelFilter?.Invoke();
        _cancelFilter = null;
        if (_activeFilter is not null)
        {
            _activeFilter = null;
            OnPropertyChanged(nameof(IsFiltered));
        }
    }

    /// <summary>결과를 바꾸거나 닫을 때: 세는 중이면 취소하고 센 값·안내를 비운다.</summary>
    private void ResetCount()
    {
        _countGeneration++;
        _cancelCount?.Invoke();
        _cancelCount = null;
        _totalRows = null;
        _countMessage = null;
        IsCountingRows = false;
    }

    private void UpdateStatus()
    {
        OnPropertyChanged(nameof(IsSorting));
        OnPropertyChanged(nameof(SortStatusText));
        OnPropertyChanged(nameof(HasLocalFilter));
        OnPropertyChanged(nameof(CanFetchAllForFilter));
        RefreshBusy();
        if (_model is null)
        {
            StatusText = string.Empty;
            SelectionSummary = null;
            return;
        }

        if (_model.Error is { } err)
        {
            StatusText = "오류: " + err.Message + (_filterMessage is { } failed ? " · " + failed : string.Empty)
                         + (_relatedMessage is { } related ? " · " + related : string.Empty);
            SelectionSummary = null;
            OnPropertyChanged(nameof(CanCountRows));
            return;
        }

        var culture = CultureInfo.CurrentCulture;
        var loaded = _model.LoadedRowCount.ToString("N0", culture);
        var text = _totalRows is { } total
            ? $"가져온 {loaded}행 / 전체 {total.ToString("N0", culture)}행"
            : _model.HasMoreRows ? $"{loaded} 행 (더 있음)" : $"{loaded} 행";
        if (_model.IsLocalFiltered)
        {
            var visible = _model.LocalFilterVisibleRows.ToString("N0", culture);
            text = $"거름: 보이는 {visible} / 가져온 {loaded}행" + (_model.HasMoreRows ? " (전체 아님)" : string.Empty);
        }
        else if (_model.IsFilterRunning)
        {
            text += " · 거르는 중…";
        }
        if (_model.EditOverlay?.ExtraRows is > 0 and var added)
        {
            text += $" · 새 행 {added}"; // 편집 모드에서 아직 반영하지 않은 새 행
        }

        if (_activeFilter is not null)
        {
            text += " · 거른 결과";
        }

        if (_countMessage is { } message)
        {
            text += " · " + message;
        }

        if (_filterMessage is { } filterMessage)
        {
            text += " · " + filterMessage;
        }

        if (_relatedMessage is { } relatedMessage)
        {
            text += " · " + relatedMessage;
        }

        if (_gridNotice is { } gridNotice)
        {
            text += " · " + gridNotice; // 행 붙여넣기·복제 결과(열 수 불일치 등)
        }

        StatusText = text;
        UpdateSelectionSummary(_model);
    }

    /// <summary>
    /// 선택 집계를 다시 센다. 칸 범위·열·정렬이 그대로면 건너뛴다(페이지가 올 때마다 Changed 가 오므로).
    /// 편집 모드에서는 값이 바뀌어도 범위가 같을 수 있어 매번 센다.
    /// </summary>
    private void UpdateSelectionSummary(ResultGridModel m)
    {
        if (SelectionAggregate.ScopeOf(m) is not { } scope)
        {
            _summaryScope = null;
            SelectionSummary = null;
            return;
        }

        if (m.EditOverlay is null && _summaryScope == scope)
        {
            return;
        }

        _summaryScope = scope;
        SelectionSummary = SelectionAggregate.Compute(m)?.ToText(CultureInfo.CurrentCulture);
    }
}
