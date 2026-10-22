using System.Globalization;
using Oranda.Core.Grid;

namespace Oranda.App.Features.ResultGrid;

/// <summary>선택 범위 안의 셀 위치. <see cref="Col"/> 은 <see cref="ResultGridModel.VisibleColumns"/> 의 위치(열 ordinal 이 아님).</summary>
public readonly record struct GridCell(long Row, int Col);

/// <summary>
/// 가상 결과 그리드의 화면 상태(UI 컨트롤과 분리, TSK-06-03). 열 숨김·고정·정렬·선택·텍스트 모드·같은 값 숨김·복사·찾기.
/// 데이터는 <see cref="ResultPageCache"/> 로 받는다. UI 스레드에서 만들고 쓴다(<see cref="Changed"/> 는 만든 문맥에서 올린다).
/// </summary>
public sealed class ResultGridModel : IAsyncDisposable
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

    /// <summary>질의가 돌려준 행 수(편집 모드에서 새로 더한 행은 뺀다).</summary>
    public long BaseRowCount => TotalRowCount ?? _loadedRows;

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
        if (HasMoreRows && !IsSorted && lastViewRow >= _loadedRows - PageSize / 2)
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

    private long SourceRow(long viewRow) => _order is { } o && viewRow >= 0 && viewRow < o.Length ? o[viewRow] : viewRow;

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

    // ---- 텍스트 모드 ----

    /// <summary>텍스트 결과 모드의 머리글 줄.</summary>
    public string GetTextHeader() => string.Join(" | ", _visible.Select(c => PadCell(Columns[c].Name, _textWidths[c], rightAlign: false)))
        + Environment.NewLine + string.Join("-+-", _visible.Select(c => new string('-', _textWidths[c])));

    /// <summary>텍스트 결과 모드의 데이터 줄.</summary>
    public string GetTextLine(long viewRow) =>
        string.Join(" | ", _visible.Select(c => PadCell(GetCellText(viewRow, c), _textWidths[c], IsRightAligned(c))));

    private static string PadCell(string s, int width, bool rightAlign)
    {
        var w = DisplayWidth(s);
        if (w > width)
        {
            var sb = new System.Text.StringBuilder();
            var acc = 0;
            foreach (var ch in s)
            {
                var cw = CharWidth(ch);
                if (acc + cw > width - 1)
                {
                    break;
                }

                sb.Append(ch);
                acc += cw;
            }

            return sb.Append('…').Append(' ', Math.Max(0, width - acc - 1)).ToString();
        }

        var pad = new string(' ', width - w);
        return rightAlign ? pad + s : s + pad;
    }

    /// <summary>동아시아 전각 문자는 2칸으로 센 표시 너비.</summary>
    public static int DisplayWidth(string s)
    {
        var w = 0;
        foreach (var ch in s)
        {
            w += CharWidth(ch);
        }

        return w;
    }

    private static int CharWidth(char ch) =>
        ch is (>= 'ᄀ' and <= 'ᅟ') or (>= '⺀' and <= '꓏') or (>= '가' and <= '힣')
            or (>= '豈' and <= '﫿') or (>= '︰' and <= '﹯') or (>= '＀' and <= '｠') or (>= '￠' and <= '￦') ? 2 : 1;

    public void ToggleTextMode()
    {
        IsTextMode = !IsTextMode;
        RaiseChanged();
    }

    public void ToggleRepeatHide()
    {
        HideRepeatedValues = !HideRepeatedValues;
        RaiseChanged();
    }

    // ---- 열 숨김·고정 ----

    public void HideColumn(int ordinal)
    {
        var pos = _visible.IndexOf(ordinal);
        if (pos < 0 || _visible.Count == 1)
        {
            return; // 마지막 열은 숨기지 않는다
        }

        _visible.RemoveAt(pos);
        if (pos < FrozenCount)
        {
            FrozenCount--;
        }

        ClampSelection();
        RaiseChanged();
    }

    public void ShowAllColumns()
    {
        _visible.Clear();
        _visible.AddRange(Enumerable.Range(0, Columns.Count));
        RaiseChanged();
    }

    /// <summary>
    /// 보이는 열 <paramref name="from"/> 을 <paramref name="insertBefore"/> 앞으로 옮긴다(TSK-13-17, 머리글 끌기). 위치는 둘 다 <see cref="VisibleColumns"/> 의 위치다.
    /// 고정 열은 옮기지도 그 안으로 놓지도 못한다. 이 결과의 화면 순서만 바꾸며 열 정체성(ordinal)·정렬·필터는 그대로다.
    /// 옮겼으면 true, 제자리거나 막혀서 그대로면 false.
    /// </summary>
    public bool MoveColumn(int from, int insertBefore)
    {
        if (from < FrozenCount || from >= _visible.Count || insertBefore < FrozenCount || insertBefore > _visible.Count)
        {
            return false;
        }

        var to = insertBefore > from ? insertBefore - 1 : insertBefore;
        if (to == from)
        {
            return false;
        }

        var ordinal = _visible[from];
        _visible.RemoveAt(from);
        _visible.Insert(to, ordinal);
        if (HasSelection)
        {
            // 선택은 열 위치를 따르므로 어긋난다. 보던 행은 그대로 두고 옮긴 열의 그 칸 하나만 고른다(행 보기 창이 행을 따라가므로 행을 바꾸지 않는다).
            Anchor = Active = Clamp(new GridCell(Active.Row, to));
        }

        RaiseChanged();
        return true;
    }

    /// <summary>보이는 열 <paramref name="count"/> 개를 왼쪽에 고정한다. 이미 같은 수면 해제한다.</summary>
    public void FreezeColumns(int count)
    {
        count = Math.Clamp(count, 0, Math.Max(0, _visible.Count - 1));
        FrozenCount = FrozenCount == count ? 0 : count;
        RaiseChanged();
    }

    /// <summary>활성 셀 열까지(포함) 고정/해제.</summary>
    public void FreezeUpToActive() => FreezeColumns(HasSelection ? Active.Col + 1 : 0);

    // ---- 정렬 ----

    /// <summary>편집 모드에서 반영하지 않은 변경이 있어 정렬하지 않을 때 상태 줄에 보이는 안내.</summary>
    public const string SortBlockedNotice = "반영하지 않은 편집이 있어 정렬하지 않았습니다. 반영하거나 되돌린 뒤 정렬할 수 있습니다.";

    /// <summary>
    /// 열 정렬(TSK-13-17). 같은 열을 다시 부르면 내림차순 → 해제 순. 큰 결과를 보호한다:
    /// 문턱(<see cref="LocalSortMaxRows"/>) 미만이면 묻지 않고 정렬하고, 다 안 가져왔으면 문턱까지만 더 가져와 본 뒤 그 안에 끝나면 정렬한다.
    /// 문턱에 닿으면 <see cref="ConfirmLargeSort"/> 로 묻는다(서버 다시 조회·그래도 이 PC 에서 정렬·취소). 가져오기와 정렬 계산은 UI 스레드를 막지 않고
    /// <see cref="CancelSort"/>(Esc·취소 단추)로 멈출 수 있으며, 멈추면 원래 순서가 그대로다.
    /// 편집 모드에서 반영하지 않은 변경(바꾼 값·새 행·지울 행)이 있으면 정렬하지 않고 알린다 — 편집 세션은 변경을 화면 행 번호로 기억하므로
    /// 순서가 바뀌면 바꾼 값이 다른 행에 보이고, 그 행을 고치면 원래 행이 바뀐다.
    /// </summary>
    public async Task ToggleSortAsync(int ordinal, CancellationToken ct = default)
    {
        if (IsSorting || RefuseSortForEdits())
        {
            return;
        }

        if (SortColumn == ordinal && SortDescending)
        {
            if (_serverSorted && ServerSort is { } reset)
            {
                // 서버가 정렬한 결과의 해제 = 순서표를 지우는 것으로는 원래 순서가 돌아오지 않는다: 정렬 없이 다시 조회한다.
                await RunServerSortAsync(reset, -1, false, ct).ConfigureAwait(true);
                return;
            }

            SortColumn = -1;
            SortDescending = false;
            _order = null;
            ClearSortNotice();
            RaiseChanged();
            return;
        }

        var desc = SortColumn == ordinal;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        _sortCts = cts;
        SetSortPhase(SortPhase.Fetching);
        try
        {
            var max = LocalSortMaxRows;
            await FetchUpToAsync(max, cts.Token).ConfigureAwait(true);
            if (HasMoreRows || _loadedRows >= max)
            {
                if (!await ConfirmLocalSortAsync(ordinal, desc, max, cts.Token).ConfigureAwait(true))
                {
                    return;
                }
            }

            await SortLocallyAsync(ordinal, desc, cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // 취소: 지금까지 가져온 행은 남지만 정렬 순서는 바뀌지 않는다.
        }
        finally
        {
            _sortCts = null;
            SetSortPhase(SortPhase.None);
        }
    }

    /// <summary>정렬 가져오기·계산이 도는 단계. 상태 줄이 진행을 보이는 데 쓴다.</summary>
    public SortPhase SortingPhase { get; private set; }

    /// <summary>정렬하느라 가져오는 중이거나 계산하는 중(묻는 창이 떠 있는 동안은 아니다).</summary>
    public bool IsSorting => SortingPhase is SortPhase.Fetching or SortPhase.Sorting or SortPhase.Querying;

    /// <summary>정렬 중이면 멈춘다(Esc·취소 단추). 묻는 창이 떠 있으면 「취소」로 닫는다.</summary>
    public void CancelSort() => _sortCts?.Cancel();

    /// <summary>
    /// 이 PC 에서 정렬하는 행 수의 문턱. 설정 <c>grid.localSortMaxRows</c>(<see cref="LocalSortMaxRowsProvider"/>)를 따르고,
    /// 시험·특수 결과는 <see cref="LocalSortMaxRowsOverride"/> 로 덮는다.
    /// </summary>
    public int LocalSortMaxRows => Math.Max(1, LocalSortMaxRowsOverride ?? LocalSortMaxRowsProvider?.Invoke() ?? Oranda.Core.Settings.GridSettings.DefaultLocalSortMaxRows);

    public int? LocalSortMaxRowsOverride { get; set; }

    /// <summary>설정의 문턱을 읽는 함수. 시작 때 설정 서비스가 채운다.</summary>
    public static Func<int>? LocalSortMaxRowsProvider { get; set; }

    /// <summary>
    /// 문턱에 닿았을 때 사용자에게 묻는 함수(결과 영역이 대화상자를 채운다). 비어 있으면 묻지 않고 이 PC 에서 끝까지 정렬한다.
    /// </summary>
    public Func<LargeSortRequest, CancellationToken, Task<LargeSortChoice>>? ConfirmLargeSort { get; set; }

    /// <summary>
    /// 서버에서 ORDER BY 로 다시 조회하는 함수(결과 영역이 채운다). 인자 = 정렬 열 ordinal, 내림차순 여부.
    /// 다시 조회할 수 없는 결과면 비워 두거나 <see cref="ServerSortBlockedReason"/> 이 이유를 돌려준다.
    /// </summary>
    public Func<int, bool, CancellationToken, Task>? ServerSort { get; set; }

    /// <summary>서버 다시 조회를 쓸 수 없는 이유(묻는 창의 첫째 선택지를 끄고 보인다). 쓸 수 있으면 null.</summary>
    public Func<int, bool, string?>? ServerSortBlockedReason { get; set; }

    private CancellationTokenSource? _sortCts;
    private bool _localSortApproved;
    private bool _serverSorted;

    private void SetSortPhase(SortPhase phase)
    {
        SortingPhase = phase;
        RaiseChanged();
    }

    /// <summary>문턱까지만 가져온다. 이미 그만큼 있거나 끝까지 왔으면 바로 돌아온다.</summary>
    private async Task FetchUpToAsync(long maxRows, CancellationToken ct)
    {
        var p = (int)(_loadedRows / PageSize);
        while (HasMoreRows && _loadedRows < maxRows)
        {
            ct.ThrowIfCancellationRequested();
            var page = await FetchPageAsync(p, ct).ConfigureAwait(true);
            if (page.IsLast || page.RowCount == 0)
            {
                break;
            }

            p++;
        }
    }

    /// <summary>
    /// 문턱에 닿았을 때 묻고, 이 PC 에서 정렬하기로 하면 true. 서버 다시 조회를 고르면 그 일을 시키고 false, 취소면 false.
    /// 한 번 이 PC 에서 정렬하기로 한 결과는 같은 결과에서 다시 묻지 않는다.
    /// </summary>
    private async Task<bool> ConfirmLocalSortAsync(int ordinal, bool desc, int max, CancellationToken ct)
    {
        var choice = LargeSortChoice.LocalAll;
        if (!_localSortApproved && ConfirmLargeSort is { } ask)
        {
            var blocked = ServerSort is null ? "서버에서 다시 조회할 수 없는 결과입니다." : ServerSortBlockedReason?.Invoke(ordinal, desc);
            SetSortPhase(SortPhase.Asking);
            choice = await ask(new LargeSortRequest(ordinal, Columns[ordinal].Name, desc, _loadedRows, max, !HasMoreRows, blocked), ct).ConfigureAwait(true);
            SetSortPhase(SortPhase.Fetching);
        }

        switch (choice)
        {
            case LargeSortChoice.ServerReQuery when ServerSort is { } server:
                SetSortPhase(SortPhase.Querying);
                await server(ordinal, desc, ct).ConfigureAwait(true);
                return false;
            case LargeSortChoice.LocalAll:
                _localSortApproved = true;
                await FetchAllCancellableAsync(ct).ConfigureAwait(true);
                return true;
            default:
                return false;
        }
    }

    private async Task FetchAllCancellableAsync(CancellationToken ct)
    {
        var p = (int)(_loadedRows / PageSize);
        while (HasMoreRows)
        {
            ct.ThrowIfCancellationRequested();
            var page = await FetchPageAsync(p, ct).ConfigureAwait(true);
            if (page.IsLast || page.RowCount == 0)
            {
                break;
            }

            p++;
        }
    }

    /// <summary>
    /// 순서표를 만든다. 키 읽기는 페이지마다 UI 스레드를 양보하고, 비교·정렬은 UI 스레드 밖(<see cref="Task.Run(Action)"/>)에서 돈다.
    /// 새 순서표는 정렬이 끝나야 바뀌므로 도중에 취소하면 원래 순서가 그대로다.
    /// </summary>
    private async Task SortLocallyAsync(int ordinal, bool desc, CancellationToken ct)
    {
        SetSortPhase(SortPhase.Sorting);
        var n = (int)(TotalRowCount ?? _loadedRows);
        var keys = new object?[n];
        for (var p = 0; (long)p * PageSize < n; p++)
        {
            ct.ThrowIfCancellationRequested();
            var page = await _cache.GetPageAsync(p, ct).ConfigureAwait(true);
            for (var i = 0; i < page.RowCount; i++)
            {
                keys[page.FirstRowIndex + i] = page.Columns[ordinal][i];
            }

            if ((p & 7) == 7)
            {
                await Task.Yield();
            }
        }

        var kind = Columns[ordinal].Kind;
        var order = await Task.Run(() => BuildOrder(keys, kind, desc, ct), ct).ConfigureAwait(true);
        if (RefuseSortForEdits())
        {
            return; // 가져오는 사이에 셀을 고쳤다
        }

        _order = order;
        _serverSorted = false;
        SortColumn = ordinal;
        SortDescending = desc;
        ClearSortNotice();
        RaiseChanged();
    }

    /// <summary>
    /// 서버가 ORDER BY 로 정렬해 돌려준 새 결과에 정렬 표시만 한다(행은 이미 그 순서라 순서표는 만들지 않는다).
    /// 같은 열을 다시 누르면 내림차순으로 다시 조회하고, 내림차순에서 누르면 정렬 없이 다시 조회한다.
    /// </summary>
    public void MarkServerSorted(int ordinal, bool desc)
    {
        _order = null;
        _serverSorted = true;
        SortColumn = ordinal;
        SortDescending = desc;
        RaiseChanged();
    }

    private async Task RunServerSortAsync(Func<int, bool, CancellationToken, Task> server, int ordinal, bool desc, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        _sortCts = cts;
        SetSortPhase(SortPhase.Querying);
        try
        {
            await server(ordinal, desc, cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // 취소: 지금 결과를 그대로 둔다.
        }
        finally
        {
            _sortCts = null;
            SetSortPhase(SortPhase.None);
        }
    }

    /// <summary>키 배열을 정렬한 행 순서표(같으면 원래 순서). 숫자 열은 한 번만 수로 바꿔 비교한다(비교마다 글자로 바꾸면 100만 행에서 매우 느리다).</summary>
    internal static int[] BuildOrder(object?[] keys, ColumnDataKind kind, bool desc, CancellationToken ct)
    {
        var n = keys.Length;
        var order = Enumerable.Range(0, n).ToArray();
        var calls = 0;
        void Tick()
        {
            if ((++calls & 0xFFF) == 0)
            {
                ct.ThrowIfCancellationRequested();
            }
        }

        try
        {
            if (kind == ColumnDataKind.Number && TryParseNumbers(keys, out var numbers, out var isNull))
            {
                Array.Sort(order, (a, b) =>
                {
                    Tick();
                    var an = isNull[a];
                    var bn = isNull[b];
                    var c = an || bn ? (an && bn ? 0 : an ? 1 : -1) : desc ? numbers[b].CompareTo(numbers[a]) : numbers[a].CompareTo(numbers[b]);
                    return c != 0 ? c : a.CompareTo(b);
                });
            }
            else
            {
                Array.Sort(order, (a, b) =>
                {
                    Tick();
                    var c = CompareValues(keys[a], keys[b], kind, desc);
                    return c != 0 ? c : a.CompareTo(b);
                });
            }
        }
        catch (InvalidOperationException ex) when (ex.InnerException is OperationCanceledException)
        {
            throw new OperationCanceledException(ct); // Array.Sort 는 비교 함수의 예외를 감싼다
        }

        return order;
    }

    private static bool TryParseNumbers(object?[] keys, out decimal[] numbers, out bool[] isNull)
    {
        numbers = new decimal[keys.Length];
        isNull = new bool[keys.Length];
        for (var i = 0; i < keys.Length; i++)
        {
            var k = keys[i];
            if (k is null or DBNull)
            {
                isNull[i] = true;
            }
            else if (k is decimal d)
            {
                numbers[i] = d;
            }
            else if (!decimal.TryParse(Convert.ToString(k, System.Globalization.CultureInfo.InvariantCulture), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out numbers[i]))
            {
                return false; // 수로 읽을 수 없는 값이 있으면 일반 비교로 돌아간다
            }
        }

        return true;
    }

    /// <summary>반영하지 않은 편집이 있으면 정렬하지 않는다고 알리고 true.</summary>
    private bool RefuseSortForEdits()
    {
        if (EditOverlay is not { HasPendingChanges: true })
        {
            return false;
        }

        _sortRefused = true;
        ReportNotice(SortBlockedNotice);
        return true;
    }

    /// <summary>정렬했으니 앞서 보인 「정렬하지 않았습니다」 안내를 지운다.</summary>
    private void ClearSortNotice()
    {
        if (_sortRefused)
        {
            _sortRefused = false;
            ReportNotice(string.Empty);
        }
    }

    private static int CompareValues(object? a, object? b, ColumnDataKind kind, bool desc)
    {
        var an = a is null or DBNull;
        var bn = b is null or DBNull;
        if (an || bn)
        {
            return an && bn ? 0 : an ? 1 : -1; // NULL 은 항상 뒤
        }

        var c = CompareNonNull(a!, b!, kind);
        return desc ? -c : c;
    }

    private static int CompareNonNull(object a, object b, ColumnDataKind kind)
    {
        if (kind == ColumnDataKind.Number
            && decimal.TryParse(Convert.ToString(a, System.Globalization.CultureInfo.InvariantCulture), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var da)
            && decimal.TryParse(Convert.ToString(b, System.Globalization.CultureInfo.InvariantCulture), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var db))
        {
            return da.CompareTo(db);
        }

        if (a is LobValue la && b is LobValue lb)
        {
            return string.CompareOrdinal(la.TextPreview, lb.TextPreview);
        }

        if (a is byte[] ba && b is byte[] bb)
        {
            return ba.AsSpan().SequenceCompareTo(bb);
        }

        if (a.GetType() == b.GetType() && a is IComparable ca)
        {
            return ca.CompareTo(b);
        }

        return string.CompareOrdinal(
            CellValueFormatter.Format(new ResultColumn(0, "", "", kind), a, GridDisplayOptions.Plain),
            CellValueFormatter.Format(new ResultColumn(0, "", "", kind), b, GridDisplayOptions.Plain));
    }

    // ---- 선택 ----

    public void SelectCell(GridCell cell)
    {
        cell = Clamp(cell);
        Anchor = Active = cell;
        HasSelection = RowCount > 0 && _visible.Count > 0;
        RaiseChanged();
    }

    public void ExtendTo(GridCell cell)
    {
        if (!HasSelection)
        {
            SelectCell(cell);
            return;
        }

        Active = Clamp(cell);
        RaiseChanged();
    }

    public void SelectAll()
    {
        if (RowCount == 0 || _visible.Count == 0)
        {
            return;
        }

        Anchor = new GridCell(0, 0);
        Active = new GridCell(RowCount - 1, _visible.Count - 1);
        HasSelection = true;
        RaiseChanged();
    }

    public void SelectRow(long row, bool extend = false)
    {
        var last = _visible.Count - 1;
        if (extend && HasSelection)
        {
            Active = Clamp(new GridCell(row, last));
        }
        else
        {
            Anchor = Clamp(new GridCell(row, 0));
            Active = Clamp(new GridCell(row, last));
            HasSelection = RowCount > 0;
        }

        RaiseChanged();
    }

    public void SelectColumn(int visibleCol)
    {
        if (RowCount == 0)
        {
            return;
        }

        Anchor = Clamp(new GridCell(0, visibleCol));
        Active = Clamp(new GridCell(RowCount - 1, visibleCol));
        HasSelection = true;
        RaiseChanged();
    }

    public (GridCell From, GridCell To) SelectionRect() => (
        new GridCell(Math.Min(Anchor.Row, Active.Row), Math.Min(Anchor.Col, Active.Col)),
        new GridCell(Math.Max(Anchor.Row, Active.Row), Math.Max(Anchor.Col, Active.Col)));

    public bool IsSelected(long row, int visibleCol)
    {
        if (!HasSelection)
        {
            return false;
        }

        var (f, t) = SelectionRect();
        return row >= f.Row && row <= t.Row && visibleCol >= f.Col && visibleCol <= t.Col;
    }

    private GridCell Clamp(GridCell c) =>
        new(Math.Clamp(c.Row, 0, Math.Max(0, RowCount - 1)), Math.Clamp(c.Col, 0, Math.Max(0, _visible.Count - 1)));

    private void ClampSelection()
    {
        if (HasSelection)
        {
            Anchor = Clamp(Anchor);
            Active = Clamp(Active);
        }
    }

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

        var (from, to) = SelectionRect();
        return await ReadRowsAsync(from.Row, to.Row, ordinals, ct).ConfigureAwait(true);
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
        for (var r = firstRow; r <= lastRow; r++)
        {
            var values = new object?[ordinals.Count];
            if (BaseRowOf(r) is var baseRow && baseRow >= 0)
            {
                var src = SourceRow(baseRow);
                var page = await _cache.GetPageAsync((int)(src / PageSize), ct).ConfigureAwait(true);
                OnPage(page);
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

    /// <summary>INSERT 문 복사에서 빼는 의사 열: ROWID 종류 열과 이름이 ROWID·ROWNUM·ORA_ROWSCN 인 열.</summary>
    public static bool IsPseudoColumn(ResultColumn column)
    {
        ArgumentNullException.ThrowIfNull(column);
        return column.Kind == ColumnDataKind.RowId
            || column.Name.Equals("ROWID", StringComparison.OrdinalIgnoreCase)
            || column.Name.Equals("ROWNUM", StringComparison.OrdinalIgnoreCase)
            || column.Name.Equals("ORA_ROWSCN", StringComparison.OrdinalIgnoreCase);
    }

    // ---- 찾기 ----

    /// <summary>
    /// 활성 셀 다음부터 <paramref name="text"/>(대소문자 무시)가 든 셀을 찾아 선택한다. 끝까지 가도 없으면 처음부터 한 바퀴.
    /// 아직 안 받은 행은 가져오면서 찾는다.
    /// </summary>
    public async Task<bool> FindNextAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(text) || RowCount == 0)
        {
            return false;
        }

        var cols = _visible.Count;
        var start = HasSelection ? Active.Row * cols + Active.Col + 1 : 0;
        for (var pass = 0; pass < 2; pass++)
        {
            var from = pass == 0 ? start : 0;
            var r = from / cols;
            var c = (int)(from % cols);
            while (true)
            {
                var total = TotalRowCount;
                if (total is { } t && r >= t)
                {
                    break;
                }

                var src = SourceRow(r);
                var page = await _cache.GetPageAsync((int)(src / PageSize), ct).ConfigureAwait(true);

                OnPage(page);
                var i = (int)(src - page.FirstRowIndex);
                if (i < 0 || i >= page.RowCount)
                {
                    break;
                }

                for (; c < cols; c++)
                {
                    var o = _visible[c];
                    var s = CellValueFormatter.Format(Columns[o], page.Columns[o][i], GridDisplayOptions.Plain);
                    if (s.Contains(text, StringComparison.OrdinalIgnoreCase))
                    {
                        SelectCell(new GridCell(r, c));
                        return true;
                    }
                }

                c = 0;
                r++;
            }
        }

        return false;
    }

    /// <summary>「결과에서 찾기」 명령이 찾기 줄을 열라고 알린다.</summary>
    public event EventHandler? FindRequested;

    /// <summary>「결과 탭 닫기」 명령이 이 결과를 닫아 달라고 알린다(탭 묶음은 편집기 문서 쪽).</summary>
    public event EventHandler? CloseRequested;

    /// <summary>「전체 행 수 세기」 명령이 이 결과를 만든 질의의 전체 행 수를 세어 달라고 알린다(문장은 결과 영역이 안다).</summary>
    public event EventHandler? CountRowsRequested;

    /// <summary>「거르기 줄 보이기/숨기기」 명령이 결과 위 WHERE·ORDER BY 줄을 열거나 닫으라고 알린다(GRID-10).</summary>
    public event EventHandler? FilterBarRequested;

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
