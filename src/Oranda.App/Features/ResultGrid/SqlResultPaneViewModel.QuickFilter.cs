using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.Features.GridEdit;
using Oranda.App.Features.Localization;
using Oranda.App.Shell;
using Oranda.Core.Execution;
using Oranda.Core.Grid;

namespace Oranda.App.Features.ResultGrid;

// SqlResultPaneViewModel 의 일부: 빠른 거르기(SqlResultPaneViewModel.cs 에서 나눔, 동작 불변).
public sealed partial class SqlResultPaneViewModel
{
    /// <summary>빠른 거르기 입력이 멈춘 뒤 거르기를 시작할 때까지 기다리는 시간(TSK-13-26).</summary>
    public TimeSpan QuickFilterDebounce { get; set; } = TimeSpan.FromMilliseconds(150);

    /// <summary>「결과 안에서 거르기」 칸을 보인다(<c>oranda.grid.toggleQuickFilter</c>). 숨겨도 걸어 둔 거르기는 그대로다.</summary>
    public bool IsQuickFilterVisible
    {
        get => _isQuickFilterVisible;
        set => SetProperty(ref _isQuickFilterVisible, value);
    }

    /// <summary>빠른 거르기 글. 바꾸면 디바운스 뒤 가져온 행만 거른다(서버에 다시 조회하지 않는다).</summary>
    public string QuickFilterText
    {
        get => _quickFilterText;
        set
        {
            value ??= string.Empty;
            if (SetProperty(ref _quickFilterText, value))
            {
                ScheduleQuickFilter(QuickFilterDebounce);
            }
        }
    }

    /// <summary>빠른 거르기가 볼 열 목록(맨 앞은 「모든 열」). 결과가 바뀌면 다시 채운다.</summary>
    public ObservableCollection<QuickFilterColumn> QuickFilterColumns { get; } = [];

    /// <summary>빠른 거르기가 보는 열. 「모든 열」이면 그때 보이는 모든 열.</summary>
    public QuickFilterColumn? SelectedQuickFilterColumn
    {
        get => _quickFilterColumn;
        set
        {
            if (SetProperty(ref _quickFilterColumn, value))
            {
                ScheduleQuickFilter(TimeSpan.Zero);
            }
        }
    }

    /// <summary>빠른 거르기 칸에 포커스를 옮겨 달라는 요청(칸을 막 열었을 때).</summary>
    public event EventHandler? QuickFilterFocusRequested;

    /// <summary>앱 안 거르기가 걸려 있다(빠른 거르기 글·열 필터).</summary>
    public bool HasLocalFilter => _model is { IsLocalFiltered: true } or { IsFilterRunning: true };

    /// <summary>거르고 남은 행이 가져온 일부뿐일 때 「모두 가져오기」 단추를 보인다.</summary>
    public bool CanFetchAllForFilter => _model is { IsLocalFiltered: true, HasMoreRows: true };

    /// <summary>
    /// 빠른 거르기 칸의 Esc: 글이 있으면 글을 지우고(true), 이미 비어 있으면 칸을 닫는다(false). 다 안 닫은 거르기는 칸을 닫아도 남는다.
    /// </summary>
    public bool ClearOrCloseQuickFilter()
    {
        if (_quickFilterText.Length > 0)
        {
            QuickFilterText = string.Empty;
            ScheduleQuickFilter(TimeSpan.Zero);
            return true;
        }

        IsQuickFilterVisible = false;
        return false;
    }

    /// <summary>한 열의 자동 필터를 바꾼다(비우면 그 열 필터를 지운다). 다른 열 필터·빠른 거르기 글은 그대로 두고 AND 로 묶는다.</summary>
    public Task<bool> SetColumnFilterAsync(int ordinal, ColumnFilter? filter) =>
        _model is { } m ? m.SetLocalFilterAsync(m.LocalFilter.WithColumn(ordinal, filter)) : Task.FromResult(false);

    /// <summary>모든 앱 안 거르기(빠른 거르기 글·열 필터)를 지운다(<c>oranda.grid.clearLocalFilters</c>).</summary>
    public Task ClearLocalFiltersAsync()
    {
        _quickCts?.Cancel();
        SetQuickTextSilently(string.Empty);
        return _model is { } m ? m.ClearLocalFilterAsync() : Task.CompletedTask;
    }

    private void SetQuickTextSilently(string text)
    {
        if (_quickFilterText != text)
        {
            _quickFilterText = text;
            OnPropertyChanged(nameof(QuickFilterText));
        }
    }

    private void ScheduleQuickFilter(TimeSpan delay)
    {
        _quickCts?.Cancel();
        var cts = new CancellationTokenSource();
        _quickCts = cts;
        _ = ApplyQuickFilterAsync(delay, cts);
    }

    private async Task ApplyQuickFilterAsync(TimeSpan delay, CancellationTokenSource cts)
    {
        var ct = cts.Token;
        try
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, ct).ConfigureAwait(true);
            }

            if (_model is not { } m)
            {
                return;
            }

            var columns = _quickFilterColumn is { Ordinal: >= 0 } c ? new[] { c.Ordinal } : null;
            var spec = m.LocalFilter with { QuickText = _quickFilterText, QuickColumns = columns };
            if (!await m.SetLocalFilterAsync(spec, ct).ConfigureAwait(true) && !ct.IsCancellationRequested)
            {
                SetQuickTextSilently(m.LocalFilter.QuickText); // 반영하지 않은 편집이 있어 거부했다: 칸을 실제 거르기에 맞춘다
            }
        }
        catch (OperationCanceledException)
        {
            // 새 입력이 와서 이 거르기는 버린다.
        }
        finally
        {
            if (ReferenceEquals(_quickCts, cts))
            {
                _quickCts = null;
            }

            cts.Dispose();
        }
    }

    private void FillQuickFilterColumns(ResultGridModel m)
    {
        QuickFilterColumns.Clear();
        QuickFilterColumns.Add(new QuickFilterColumn(-1, "모든 열"));
        foreach (var col in m.Columns)
        {
            QuickFilterColumns.Add(new QuickFilterColumn(col.Ordinal, col.Name));
        }

        var keep = m.LocalFilter.QuickColumns is { Count: 1 } one ? QuickFilterColumns.FirstOrDefault(q => q.Ordinal == one[0]) : null;
        _quickFilterColumn = keep ?? QuickFilterColumns[0];
        OnPropertyChanged(nameof(SelectedQuickFilterColumn));
    }
}
