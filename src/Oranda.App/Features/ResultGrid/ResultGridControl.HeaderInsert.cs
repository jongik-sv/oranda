using Avalonia;
using Avalonia.Input;
using Avalonia.Threading;
using Oranda.Core.Grid;

namespace Oranda.App.Features.ResultGrid;

// ResultGridControl 의 일부: 머리글·값 더블클릭으로 편집기에 넣기. 켜는 곳은 편집기 결과 탭뿐이고, 모니터 표·객체 상세는 이 파일의 동작이 모두 꺼져 있다.
public sealed partial class ResultGridControl
{
    /// <summary>
    /// 머리글을 더블클릭했다(열 ordinal). 받는 쪽(결과 영역)이 컬럼 이름을 편집기에 넣는다.
    /// 구독만으로는 켜지지 않고 <see cref="HeaderDoubleClickInserts"/> 가 켜져 있을 때만 낸다.
    /// </summary>
    public event EventHandler<int>? HeaderInsertRequested;

    /// <summary>
    /// 값 칸을 더블클릭했다(칸 위치). 받는 쪽(결과 영역)이 편집 모드면 셀 편집을, 아니면 값을 편집기에 넣는다.
    /// 구독이 없으면 예전처럼 <see cref="EditRequested"/> 를 낸다(모니터 표 등).
    /// </summary>
    public event EventHandler<GridCell>? CellDoubleTapped;

    /// <summary>머리글 클릭 정렬이 행 순서를 바꾸기 직전(지연 정렬이 늦게 실행될 때 셀 편집기가 열려 있을 수 있다). 결과 영역 화면이 열린 셀 편집기를 먼저 확정한다.</summary>
    public event EventHandler? RowOrderChanging;

    /// <summary>
    /// 머리글 더블클릭을 편집기에 넣는 요청으로 받는다. 켜면 열 필터가 켜진 상태의 첫 클릭 정렬이 더블클릭 시간만큼 미뤄진다
    /// (더블클릭이면 정렬하지 않는다). 꺼져 있으면 머리글 클릭은 놓는 즉시 정렬한다.
    /// </summary>
    public bool HeaderDoubleClickInserts { get; set; }

    /// <summary>시험이 정렬 지연을 줄이는 자리. null 이면 플랫폼의 더블클릭 시간.</summary>
    internal TimeSpan? HeaderSortDelayOverride { get; set; }

    private DispatcherTimer? _sortTimer;
    private int _pendingSortOrdinal = -1;

    private TimeSpan HeaderSortDelay =>
        HeaderSortDelayOverride ?? Application.Current?.PlatformSettings?.GetDoubleTapTime(PointerType.Mouse) ?? TimeSpan.FromMilliseconds(500);

    /// <summary>머리글 클릭 정렬을 바로 하거나(더블클릭 요청을 받지 않을 때) 더블클릭 시간만큼 미룬다.</summary>
    private void SortFromHeaderClick(int ordinal)
    {
        if (!HeaderDoubleClickInserts)
        {
            _ = SortAsync(ordinal);
            return;
        }

        FlushPendingSort(); // 다른 열을 연달아 눌렀으면 앞의 정렬을 먼저 한다
        _pendingSortOrdinal = ordinal;
        var timer = new DispatcherTimer { Interval = HeaderSortDelay };
        _sortTimer = timer;
        timer.Tick += (_, _) =>
        {
            if (ReferenceEquals(_sortTimer, timer))
            {
                FlushPendingSort();
            }
        };
        timer.Start();
    }

    /// <summary>미룬 정렬이 있으면 지금 한다.</summary>
    private void FlushPendingSort()
    {
        var ordinal = _pendingSortOrdinal;
        CancelPendingSort();
        if (ordinal >= 0 && _model is { } m && ordinal < m.Columns.Count)
        {
            _ = SortAsync(ordinal);
        }
    }

    /// <summary>미룬 정렬을 버린다(더블클릭·Esc·결과 바꿈·화면에서 떨어짐).</summary>
    private void CancelPendingSort()
    {
        _sortTimer?.Stop();
        _sortTimer = null;
        _pendingSortOrdinal = -1;
    }

    /// <summary>머리글 왼쪽 버튼 누르기가 더블클릭의 두 번째 누르기면 정렬을 버리고 이름 넣기를 요청한다. 처리했으면 true.</summary>
    private bool TryHandleHeaderDoubleClick(PointerPressedEventArgs e, int visibleCol)
    {
        if (!HeaderDoubleClickInserts || e.ClickCount != 2 || _model is not { } m || visibleCol < 0 || visibleCol >= m.VisibleColumns.Count)
        {
            return false;
        }

        CancelPendingSort();
        HeaderInsertRequested?.Invoke(this, m.VisibleColumns[visibleCol]);
        return true;
    }
}
