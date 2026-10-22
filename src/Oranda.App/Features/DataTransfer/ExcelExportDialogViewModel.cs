using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Oranda.App.ViewModels;

namespace Oranda.App.Features.DataTransfer;

/// <summary>「Excel 로 내보내기」 작은 대화상자: 범위 세 가지와 「내보낸 뒤 열기」를 고르고 「저장」으로 닫는다. 저장 위치는 그 뒤에 고른다.</summary>
public sealed partial class ExcelExportDialogViewModel : ViewModelBase
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAllRows), nameof(IsLoadedRows), nameof(IsFetchedAll), nameof(IsSelection))]
    private ExcelExportScope _scope;

    [ObservableProperty]
    private bool _openAfter = true;

    /// <param name="canAllRows">질의를 서버에서 다시 실행할 수 있는지(실행할 문장과 접속을 아는지).</param>
    /// <param name="loadedRows">그리드에 불러온 행 수. 앱 안 거르기가 걸려 있으면 보이는 행 수.</param>
    /// <param name="selectionRows">선택한 행 수(선택이 없으면 0).</param>
    /// <param name="selectionColumns">선택한 열 수(선택이 없으면 0).</param>
    /// <param name="fetchedRows">앱 안 거르기가 걸려 있을 때 거르기와 무관하게 가져온 행 수. 거르기가 없으면 null(「가져온 전체」 선택지가 보이지 않는다).</param>
    public ExcelExportDialogViewModel(bool canAllRows, long loadedRows, long selectionRows, int selectionColumns, long? fetchedRows = null)
    {
        HasFetchedAll = fetchedRows is not null;
        FetchedLabel = $"가져온 전체({fetchedRows ?? 0:N0}행, 거르기 무시)";
        CanAllRows = canAllRows;
        CanSelection = selectionRows > 0 && selectionColumns > 0;
        LoadedLabel = HasFetchedAll
            ? $"보이는 행만({loadedRows:N0}행, 거르기 적용)"
            : $"그리드에 불러온 행({loadedRows:N0}행, 다시 실행하지 않음)";
        SelectionLabel = CanSelection ? $"선택한 셀({selectionRows:N0}행 × {selectionColumns:N0}열)" : "선택한 셀(선택 없음)";
        _scope = canAllRows ? ExcelExportScope.AllRows : ExcelExportScope.LoadedRows;
    }

    public bool CanAllRows { get; }

    public bool CanSelection { get; }

    public string LoadedLabel { get; }

    public string SelectionLabel { get; }

    /// <summary>앱 안 거르기가 걸려 「가져온 전체」를 고를 수 있으면 true(그때만 화면에 보인다).</summary>
    public bool HasFetchedAll { get; }

    public string FetchedLabel { get; }

    public bool IsAllRows
    {
        get => Scope == ExcelExportScope.AllRows;
        set => Pick(value, ExcelExportScope.AllRows);
    }

    public bool IsLoadedRows
    {
        get => Scope == ExcelExportScope.LoadedRows;
        set => Pick(value, ExcelExportScope.LoadedRows);
    }

    public bool IsFetchedAll
    {
        get => Scope == ExcelExportScope.FetchedAll;
        set => Pick(value, ExcelExportScope.FetchedAll);
    }

    public bool IsSelection
    {
        get => Scope == ExcelExportScope.Selection;
        set => Pick(value, ExcelExportScope.Selection);
    }

    /// <summary>「저장」으로 닫았으면 true. 취소·닫기는 false.</summary>
    public bool Accepted { get; private set; }

    /// <summary>창이 닫아야 할 때 발생.</summary>
    public event EventHandler? CloseRequested;

    [RelayCommand]
    public void Save()
    {
        Accepted = true;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void Pick(bool selected, ExcelExportScope scope)
    {
        if (selected)
        {
            Scope = scope;
        }
    }
}
