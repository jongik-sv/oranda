using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.Features.GridEdit;
using Oranda.App.Features.Localization;
using Oranda.App.Shell;
using Oranda.Core.Execution;
using Oranda.Core.Grid;

namespace Oranda.App.Features.ResultGrid;

// SqlResultPaneViewModel 의 일부: 복사·찾기·이벤트 처리·정리(SqlResultPaneViewModel.cs 에서 나눔, 동작 불변).
public sealed partial class SqlResultPaneViewModel
{
    /// <summary>상태 줄의 선택 집계 글을 클립보드에 쓴다(그리드의 복사와 같은 창구).</summary>
    public Task CopySelectionSummaryAsync() =>
        _selectionSummary is { } text && _model?.ClipboardWriter is { } write ? write(text) : Task.CompletedTask;

    public async Task FindNextAsync()
    {
        if (_model is null)
        {
            return;
        }

        FindMessage = await _model.FindNextAsync(FindText).ConfigureAwait(true) ? string.Empty : "찾지 못했습니다.";
    }

    private void OnEditHintRequested(object? sender, EventArgs e) => ShowEditHint();

    private void OnNoticeRequested(object? sender, string message)
    {
        _gridNotice = message.Length == 0 ? null : message;
        UpdateStatus();
    }

    private void OnEditChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(IsEditMode));
        OnPropertyChanged(nameof(IsEditDirty));
    }

    private void DetachEditSession()
    {
        if (_editSession is not null)
        {
            _editSession.Changed -= OnEditChanged;
            _editSession = null;
            OnEditChanged(this, EventArgs.Empty);
        }

        EditHint = null;
    }

    private void Detach()
    {
        DetachEditSession();
        ResetCount();
        ResetFilter();
        _relatedMessage = null;
        _gridNotice = null;
        _summaryScope = null;
        if (_model is null)
        {
            return;
        }

        _model.Changed -= OnModelChanged;
        _model.FindRequested -= OnFindRequested;
        _model.CloseRequested -= OnCloseRequested;
        _model.CountRowsRequested -= OnCountRowsRequested;
        _model.FilterBarRequested -= OnFilterBarRequested;
        _model.QuickFilterRequested -= OnQuickFilterRequested;
        _model.ClearLocalFiltersRequested -= OnClearLocalFiltersRequested;
        _model.CellFilterRequested -= OnCellFilterRequested;
        _model.PinRequested -= OnPinRequested;
        _model.RelatedRowsRequested -= OnRelatedRowsRequested;
        _model.EditHintRequested -= OnEditHintRequested;
        _model.NoticeRequested -= OnNoticeRequested;
        if (ReferenceEquals(ResultGridCommands.Active, _model))
        {
            ResultGridCommands.Active = null;
        }

        _ = _model.DisposeAsync().AsTask();
        _model = null;
    }

    private void OnModelChanged(object? sender, EventArgs e) => UpdateStatus();

    private void OnFindRequested(object? sender, EventArgs e) => IsFindVisible = true;

    private void OnCloseRequested(object? sender, EventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void OnCountRowsRequested(object? sender, EventArgs e) => _ = CountAllRowsAsync();

    private void OnFilterBarRequested(object? sender, EventArgs e) => ToggleFilterBar();

    private void OnQuickFilterRequested(object? sender, EventArgs e)
    {
        IsQuickFilterVisible = !IsQuickFilterVisible;
        if (IsQuickFilterVisible)
        {
            QuickFilterFocusRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnClearLocalFiltersRequested(object? sender, EventArgs e) => _ = ClearLocalFiltersAsync();

    private void OnCellFilterRequested(object? sender, CellFilterKind kind) => _ = FilterByCellAsync(kind);

    private void OnPinRequested(object? sender, EventArgs e) => PinRequested?.Invoke(this, EventArgs.Empty);

    private void OnRelatedRowsRequested(object? sender, RelatedRowsKind kind) => _ = OpenRelatedRowsAsync(kind);
}
