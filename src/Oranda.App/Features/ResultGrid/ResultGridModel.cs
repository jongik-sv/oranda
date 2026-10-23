using System.Globalization;
using Oranda.Core.Grid;

namespace Oranda.App.Features.ResultGrid;

/// <summary>선택 범위 안의 셀 위치. <see cref="Col"/> 은 <see cref="ResultGridModel.VisibleColumns"/> 의 위치(열 ordinal 이 아님).</summary>
public readonly record struct GridCell(long Row, int Col);

/// <summary>
/// 가상 결과 그리드의 화면 상태(UI 컨트롤과 분리, TSK-06-03). 열 숨김·고정·정렬·선택·텍스트 모드·같은 값 숨김·복사·찾기.
/// 데이터는 <see cref="ResultPageCache"/> 로 받는다. UI 스레드에서 만들고 쓴다(<see cref="Changed"/> 는 만든 문맥에서 올린다).
/// </summary>
public sealed partial class ResultGridModel : IAsyncDisposable
{
    public const int DefaultPageSize = 500;

    private readonly ResultPageCache _cache;
    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;
    private readonly HashSet<int> _pending = [];
    private readonly CancellationTokenSource _cts = new();
    private readonly List<int> _visible;
    private readonly double[] _widths;
    private readonly int[] _textWidths;
    private int[]? _order;
    private int[]? _view;
    private bool[]? _match;
    private int _matchedRows;
    private int _filterGeneration;
    private CancellationTokenSource? _filterCts;
    private long _loadedRows;
    private long? _knownRows;
    private bool _sortRefused;
    private bool _disposed;

    public ResultGridModel(IResultSet source, ResultPageCacheOptions? cacheOptions = null, int pageSize = DefaultPageSize)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        Source = source;
        PageSize = pageSize;
        _cache = new ResultPageCache(source, cacheOptions);
        Columns = source.Columns;
        _visible = [.. Enumerable.Range(0, Columns.Count)];
        _widths = new double[Columns.Count];
        _textWidths = new int[Columns.Count];
        Array.Fill(_widths, 100d);
        Array.Fill(_textWidths, 4);
    }

    public IResultSet Source { get; }

    public IReadOnlyList<ResultColumn> Columns { get; }

    public int PageSize { get; }

    public ResultPageCache Cache => _cache;

    /// <summary>화면에 보이는 열 ordinal(표시 순서).</summary>
    public IReadOnlyList<int> VisibleColumns => _visible;

    /// <summary>
    /// 보이는 열을 화면 순서로 늘어놓고, 숨긴 열을 원래 순서로 그 뒤에 붙인 열 ordinal 목록.
    /// 행 보기 창·서버 재조회 내보내기처럼 숨긴 열도 다뤄야 하되 화면 순서는 따라야 하는 곳이 쓴다.
    /// </summary>
    public IReadOnlyList<int> ColumnsInDisplayOrder() =>
        [.. _visible, .. Enumerable.Range(0, Columns.Count).Where(o => !_visible.Contains(o))];

    /// <summary>왼쪽에서부터 고정된 보이는 열 수.</summary>
    public int FrozenCount { get; private set; }

    public GridDisplayOptions Display { get; set; } = GridDisplayOptions.Default;

    public bool IsTextMode { get; private set; }

    public bool HideRepeatedValues { get; private set; }

    /// <summary>정렬 중인 열 ordinal. 없으면 -1.</summary>
    public int SortColumn { get; private set; } = -1;

    public bool SortDescending { get; private set; }

    public Exception? Error { get; private set; }

    /// <summary>복사 결과를 클립보드에 쓰는 함수. View 가 채운다.</summary>
    public Func<string, Task>? ClipboardWriter { get; set; }

    /// <summary>클립보드 글을 읽는 함수(「행 붙여넣기」). View 가 채운다. 글이 없으면 null.</summary>
    public Func<Task<string?>>? ClipboardReader { get; set; }

    /// <summary>데이터·열 상태·선택이 바뀌어 다시 그려야 할 때.</summary>
    public event EventHandler? Changed;

    /// <summary>지금까지 받은 행 수(끝까지 받았으면 전체 행 수).</summary>
    public long LoadedRowCount => _loadedRows;

    /// <summary>끝까지 받은 뒤의 전체 행 수. 그 전에는 null.</summary>
    public long? TotalRowCount => _knownRows ?? Source.KnownRowCount;

    /// <summary>스크롤 범위로 쓰는 행 수.</summary>
    public long RowCount => BaseRowCount + (EditOverlay?.ExtraRows ?? 0);

    /// <summary>화면에 보이는 질의 행 수(편집 모드에서 새로 더한 행은 뺀다). 앱 안 거르기(TSK-13-26)가 걸려 있으면 통과한 행만 센다.</summary>
    public long BaseRowCount => _match is null ? TotalRowCount ?? _loadedRows : _view!.Length;

    /// <summary>편집 모드에서 그릴 대기 값·행 상태·새 행(그리드 편집이 채운다). 없으면 읽기 전용 그리드.</summary>
    public IGridEditOverlay? EditOverlay { get; set; }

    /// <summary>편집 중인 셀이면 true(바뀐 칸 색칠용).</summary>
    public bool IsEdited(long viewRow, int ordinal) => EditOverlay?.IsEdited(viewRow, ordinal) ?? false;

    /// <summary>행 표시(바뀜·새 행·지울 행).</summary>
    public GridRowMark RowMarkOf(long viewRow) => EditOverlay?.RowMark(viewRow) ?? GridRowMark.None;

    /// <summary>복제한 새 행의 키 열처럼 반영 전에 값을 바꿔야 하는 칸이면 true(경고 색).</summary>
    public bool NeedsChange(long viewRow, int ordinal) => EditOverlay?.NeedsChange(viewRow, ordinal) ?? false;

    /// <summary>편집 상태가 바뀌었으니 다시 그린다.</summary>
    public void NotifyEditChanged() => RaiseChanged();

    private HashSet<long>? _flashRows;

    /// <summary>저장이 끝나 잠깐 강조 중인 행이면 true(TSK-13-18). 강조는 그리기에만 쓰고 입력은 막지 않는다.</summary>
    public bool IsFlashing(long viewRow) => _flashRows?.Contains(viewRow) ?? false;

    /// <summary>강조 중인 행이 있으면 true. 컨트롤이 강조 뒤에 붙었을 때 지울 시각을 세려고 읽는다.</summary>
    public bool HasFlash => _flashRows is not null;

    /// <summary>강조를 시작했다는 알림. 그리드 컨트롤이 받아 잠시 뒤 <see cref="ClearFlash"/> 를 부른다.</summary>
    public event EventHandler? FlashStarted;

    /// <summary>저장된 행을 잠깐 강조한다. 빈 목록이면 아무것도 하지 않는다.</summary>
    public void FlashSavedRows(IEnumerable<long> viewRows)
    {
        ArgumentNullException.ThrowIfNull(viewRows);
        var set = new HashSet<long>(viewRows);
        if (set.Count == 0)
        {
            return;
        }

        _flashRows = set;
        RaiseChanged();
        FlashStarted?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 반영한 행의 키(PK·UK 열 또는 ROWID)로 다시 읽은 결과에서 행을 찾아 강조한다(TSK-13-23). 표시 위치·정렬·열 순서와 무관하다.
    /// 메모리에 있는 페이지만 훑으므로 아직 가져오지 않은 행은 강조하지 않는다(더 가져오지 않는다).
    /// </summary>
    public void FlashRowsByKey(IEnumerable<IReadOnlyDictionary<string, object?>> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var wanted = keys.Where(k => k.Count > 0).ToList();
        if (wanted.Count == 0)
        {
            return;
        }

        var found = new List<long>();
        foreach (var key in wanted)
        {
            var ordinals = new List<(int Ordinal, object? Value)>();
            foreach (var (name, value) in key)
            {
                var col = Columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
                if (col is null)
                {
                    ordinals.Clear();
                    break;
                }

                ordinals.Add((col.Ordinal, value));
            }

            if (ordinals.Count == 0)
            {
                continue;
            }

            for (var r = 0L; r < BaseRowCount; r++)
            {
                if (ordinals.All(o => TryGetBaseValue(r, o.Ordinal, out var v) && SameKeyValue(v, o.Value)))
                {
                    found.Add(r);
                    break;
                }
            }
        }

        FlashSavedRows(found);
    }

    private static bool SameKeyValue(object? a, object? b)
    {
        a = a is DBNull ? null : a;
        b = b is DBNull ? null : b;
        if (a is null || b is null)
        {
            return a is null && b is null;
        }

        if (a is IConvertible && b is IConvertible && a is not string && b is not string
            && a is not DateTime && b is not DateTime)
        {
            try
            {
                return Convert.ToDecimal(a, CultureInfo.InvariantCulture) == Convert.ToDecimal(b, CultureInfo.InvariantCulture);
            }
            catch (Exception e) when (e is FormatException or InvalidCastException or OverflowException)
            {
                // 숫자가 아니면 아래 비교로 넘어간다.
            }
        }

        return a.Equals(b) || string.Equals(Convert.ToString(a, CultureInfo.InvariantCulture), Convert.ToString(b, CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    /// <summary>강조를 지운다.</summary>
    public void ClearFlash()
    {
        if (_flashRows is null)
        {
            return;
        }

        _flashRows = null;
        RaiseChanged();
    }

    public bool HasMoreRows => TotalRowCount is null;

    public bool HasResult => Columns.Count > 0;

    public bool HasSelection { get; private set; }

    public GridCell Anchor { get; private set; }

    public GridCell Active { get; private set; }

    public bool IsSorted => SortColumn >= 0;

    public double GetColumnWidth(int ordinal) => _widths[ordinal];

    public void SetColumnWidth(int ordinal, double width)
    {
        _widths[ordinal] = Math.Clamp(width, 30, 2000);
        RaiseChanged();
    }

    /// <summary>머리글·첫 페이지 값 길이로 열 너비를 정한다. <paramref name="charWidth"/> = 글자 1칸 DIP 너비.</summary>
    public void AutoSizeColumns(double charWidth, double padding = 16, double min = 56, double max = 360)
    {
        if (!_cache.TryGetResident(0, out var page))
        {
            return;
        }

        for (var c = 0; c < Columns.Count; c++)
        {
            var chars = Math.Max(DisplayWidth(Columns[c].Name) + 2, 4);
            var n = Math.Min(page.RowCount, 200);
            for (var r = 0; r < n; r++)
            {
                chars = Math.Max(chars, DisplayWidth(CellValueFormatter.Format(Columns[c], page.Columns[c][r], Display)));
            }

            _widths[c] = Math.Clamp(chars * charWidth + padding, min, max);
            _textWidths[c] = Math.Min(chars, 60);
        }

        RaiseChanged();
    }

    /// <summary>「결과에서 찾기」 명령이 찾기 줄을 열라고 알린다.</summary>
    public event EventHandler? FindRequested;

    /// <summary>「결과 탭 닫기」 명령이 이 결과를 닫아 달라고 알린다(탭 묶음은 편집기 문서 쪽).</summary>
    public event EventHandler? CloseRequested;

    /// <summary>「전체 행 수 세기」 명령이 이 결과를 만든 질의의 전체 행 수를 세어 달라고 알린다(문장은 결과 영역이 안다).</summary>
    public event EventHandler? CountRowsRequested;

    /// <summary>「거르기 줄 보이기/숨기기」 명령이 결과 위 WHERE·ORDER BY 줄을 열거나 닫으라고 알린다(GRID-10).</summary>
    public event EventHandler? FilterBarRequested;

    /// <summary>「결과 안에서 거르기」 칸을 열라고 알린다(<c>oranda.grid.toggleQuickFilter</c>, TSK-13-26).</summary>
    public event EventHandler? QuickFilterRequested;

    /// <summary>「모든 거르기 지우기」 명령이 빠른 거르기 글까지 지우라고 알린다(칸의 글은 결과 영역이 안다).</summary>
    public event EventHandler? ClearLocalFiltersRequested;

    /// <summary>셀 값 거르기 명령이 활성 칸의 값으로 거르라고 알린다(문장·바인드는 결과 영역이 안다).</summary>
    public event EventHandler<CellFilterKind>? CellFilterRequested;

    /// <summary>「결과 탭 고정」 명령이 이 결과의 탭을 고정하거나 풀라고 알린다(탭 묶음은 결과 탭 쪽).</summary>
    public event EventHandler? PinRequested;

    /// <summary>외래키 따라가기 명령이 활성 행의 부모·자식 행을 새 결과 탭에 열라고 알린다(문장은 결과 영역이 안다, GRID-11).</summary>
    public event EventHandler<RelatedRowsKind>? RelatedRowsRequested;

    /// <summary>읽기 전용 결과에 행 붙여넣기·복제를 하려 했다. 결과 영역이 「편집 모드로 다시 실행」 안내 막대를 띄운다.</summary>
    public event EventHandler? EditHintRequested;

    /// <summary>명령 결과 알림(행 붙여넣기의 열 수 불일치, 정렬하지 않은 이유 등). 결과 영역이 상태 줄에 보인다. 빈 글이면 알림을 지운다.</summary>
    public event EventHandler<string>? NoticeRequested;

    /// <summary>이 칸이 보이게 스크롤해 달라는 요청(새로 더한 행으로 옮길 때). 그리드 컨트롤이 받는다.</summary>
    public event EventHandler<GridCell>? RevealRequested;

    public void RequestEditHint() => EditHintRequested?.Invoke(this, EventArgs.Empty);

    public void ReportNotice(string message) => NoticeRequested?.Invoke(this, message);

    /// <summary>칸을 골라 보이게 한다(셀 커서와 행 머리 ▶ 가 그 행으로 간다).</summary>
    public void SelectAndReveal(GridCell cell)
    {
        SelectCell(cell);
        RevealRequested?.Invoke(this, Active);
    }

    public void RequestFind() => FindRequested?.Invoke(this, EventArgs.Empty);

    public void RequestClose() => CloseRequested?.Invoke(this, EventArgs.Empty);

    public void RequestCountRows() => CountRowsRequested?.Invoke(this, EventArgs.Empty);

    public void RequestQuickFilter() => QuickFilterRequested?.Invoke(this, EventArgs.Empty);

    public void RequestClearLocalFilters() => ClearLocalFiltersRequested?.Invoke(this, EventArgs.Empty);

    public void RequestFilterBar() => FilterBarRequested?.Invoke(this, EventArgs.Empty);

    public void RequestCellFilter(CellFilterKind kind) => CellFilterRequested?.Invoke(this, kind);

    public void RequestPin() => PinRequested?.Invoke(this, EventArgs.Empty);

    public void RequestRelatedRows(RelatedRowsKind kind) => RelatedRowsRequested?.Invoke(this, kind);

    private void RaiseChanged()
    {
        if (_disposed)
        {
            return;
        }

        if (_ui is null || SynchronizationContext.Current == _ui)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            _ui.Post(_ => Changed?.Invoke(this, EventArgs.Empty), null);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _cts.CancelAsync().ConfigureAwait(false);
        await _cache.DisposeAsync().ConfigureAwait(false);
        await Source.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
    }
}

/// <summary>
/// 「행 복사」가 앱 안에 남기는 사본. <see cref="Rows"/> 는 원시 값(NULL·날짜·숫자 형식 그대로), <see cref="Text"/> 는 클립보드에 쓴 TSV.
/// 붙여 넣을 때 클립보드 글이 <see cref="Text"/> 와 같을 때만 이 사본을 쓴다(그 사이 다른 곳에서 복사했으면 클립보드 글을 쓴다).
/// </summary>
public sealed record GridRowsCopy(IReadOnlyList<ResultColumn> Columns, IReadOnlyList<IReadOnlyList<object?>> Rows, string Text)
{
    /// <summary>클립보드 글이 이 사본이 쓴 글과 같다(줄 끝 차이는 무시한다: 운영체제가 LF 를 CRLF 로 바꿔 둘 수 있다).</summary>
    public bool Matches(string? clipboard) =>
        clipboard is not null && string.Equals(clipboard.ReplaceLineEndings("\n").TrimEnd('\n'), Text.ReplaceLineEndings("\n").TrimEnd('\n'), StringComparison.Ordinal);
}

/// <summary>편집 행 표시.</summary>
public enum GridRowMark
{
    None,
    Modified,
    Inserted,
    Deleted,
    Failed,
}

/// <summary>그리드가 편집 모드에서 읽는 덮어쓰기(대기 값·행 상태·새 행). 그리드 편집(TSK-06-04)이 구현한다.</summary>
public interface IGridEditOverlay
{
    /// <summary>질의 결과 뒤에 붙여 보일 새 행 수.</summary>
    int ExtraRows { get; }

    bool TryGetValue(long viewRow, int ordinal, out object? value);

    bool IsEdited(long viewRow, int ordinal);

    GridRowMark RowMark(long viewRow);

    /// <summary>화면 행 → 질의 결과 행(<see cref="ResultGridModel.BaseRowOf"/>). 새 행이면 -1.</summary>
    long BaseRowOf(long viewRow);

    /// <summary>반영 전에 값을 바꿔야 하는 칸(복제한 새 행의 키 열)이면 true.</summary>
    bool NeedsChange(long viewRow, int ordinal);

    /// <summary>반영하지 않은 변경(바꾼 값·새 행·지울 행)이 있다. 있으면 정렬하지 않는다(<see cref="ResultGridModel.ToggleSortAsync"/>).</summary>
    bool HasPendingChanges { get; }
}

/// <summary>정렬 가져오기·계산 단계(상태 줄·취소 단추용).</summary>
public enum SortPhase
{
    None,

    /// <summary>문턱까지(또는 끝까지) 행을 가져오는 중.</summary>
    Fetching,

    /// <summary>사용자에게 묻는 창이 떠 있다.</summary>
    Asking,

    /// <summary>키를 읽고 순서표를 계산하는 중.</summary>
    Sorting,

    /// <summary>서버에서 ORDER BY 로 다시 조회하는 중.</summary>
    Querying,
}

/// <summary>큰 결과 정렬을 묻는 창의 선택.</summary>
public enum LargeSortChoice
{
    Cancel,

    /// <summary>서버에서 ORDER BY 로 다시 조회한다(기본).</summary>
    ServerReQuery,

    /// <summary>전부 가져와 이 PC 에서 정렬한다(시간·메모리를 많이 쓴다).</summary>
    LocalAll,
}

/// <summary>큰 결과 정렬을 묻는 창에 보일 내용. <paramref name="ServerBlockedReason"/> 이 있으면 서버 다시 조회를 고를 수 없다.</summary>
public sealed record LargeSortRequest(int Ordinal, string ColumnName, bool Descending, long LoadedRows, int Threshold, bool AllLoaded, string? ServerBlockedReason);

/// <summary>자동 필터 값 목록의 한 항목: 화면 표시 값과 가져온 행에서의 개수.</summary>
public readonly record struct ColumnValueCount(string Text, int Count);

/// <summary>열 자동 필터의 값 목록. <paramref name="Truncated"/> 면 서로 다른 값이 너무 많아 일부만 실렸다.</summary>
public sealed record ColumnValueList(IReadOnlyList<ColumnValueCount> Values, long NullCount, bool Truncated);
