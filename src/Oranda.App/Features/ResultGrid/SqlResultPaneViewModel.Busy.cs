using System.ComponentModel;

namespace Oranda.App.Features.ResultGrid;

// SqlResultPaneViewModel 의 일부: 결과 위쪽 가장자리의 얇은 진행 막대와 상태 줄 「모두 가져오기」·「취소」 단추.
public sealed partial class SqlResultPaneViewModel
{
    private readonly DelayedBusyFlag _busyBar = new();
    private int _serverSorting;

    public SqlResultPaneViewModel()
    {
        _busyBar.PropertyChanged += OnBusyBarChanged;
    }

    private void OnBusyBarChanged(object? sender, PropertyChangedEventArgs e) => OnPropertyChanged(nameof(IsBusyBarVisible));

    /// <summary>서버와 오가는 중: 페이지 가져오기(미리 읽기·모두 가져오기)·머리글 정렬·거르기·서버 정렬 다시 조회.</summary>
    public bool IsBusy => (_model?.IsFetching ?? false) || IsSorting || IsFiltering || _serverSorting > 0;

    /// <summary>진행 막대를 보인다: <see cref="IsBusy"/> 가 지연(기본 150ms) 넘게 이어질 때.</summary>
    public bool IsBusyBarVisible => _busyBar.Visible;

    /// <summary>진행 막대가 보이기까지의 지연(시험이 0 으로 줄인다).</summary>
    public TimeSpan BusyBarDelay
    {
        get => _busyBar.Delay;
        set => _busyBar.Delay = value;
    }

    /// <summary>「모두 가져오기」 단추: 더 가져올 행이 있고 오류가 없으며 가져오는 중이 아닐 때.</summary>
    public bool CanFetchAll => _model is { HasMoreRows: true, Error: null, HasResult: true, IsFetchingAll: false };

    /// <summary>모두 가져오는 중: 같은 자리 단추가 「취소」가 된다.</summary>
    public bool IsFetchingAll => _model?.IsFetchingAll ?? false;

    /// <summary>모두 가져오기를 멈춘다(가져온 행은 남는다).</summary>
    public void CancelFetchAll() => _model?.CancelFetchAll();

    private void RefreshBusy()
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(CanFetchAll));
        OnPropertyChanged(nameof(IsFetchingAll));
        _busyBar.Set(IsBusy);
    }

    private void BeginServerSort()
    {
        _serverSorting++;
        RefreshBusy();
    }

    private void EndServerSort()
    {
        _serverSorting--;
        RefreshBusy();
    }
}
