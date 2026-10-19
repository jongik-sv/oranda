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

    /// <summary>편집 상태가 바뀌었으니 다시 그린다.</summary>
    public void NotifyEditChanged() => RaiseChanged();

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

            if (viewRow >= BaseRowCount)
            {
                value = null;
                return true;
            }
        }

        var src = SourceRow(viewRow);
        if (_cache.TryGetResident((int)(src / PageSize), out var page))
        {
            var i = (int)(src - page.FirstRowIndex);
            if (i >= 0 && i < page.RowCount)
            {
                value = page.Columns[ordinal][i];
                return true;
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
            RequestPage((int)(SourceRow(viewRow) / PageSize));
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

    /// <summary>
    /// 열 정렬. 커서가 앞으로만 가므로 끝까지 가져온 뒤 행 순서표를 만든다. 같은 열을 다시 부르면 내림차순 → 해제 순.
    /// </summary>
    public async Task ToggleSortAsync(int ordinal, CancellationToken ct = default)
    {
        if (SortColumn == ordinal && SortDescending)
        {
            SortColumn = -1;
            SortDescending = false;
            _order = null;
            RaiseChanged();
            return;
        }

        var desc = SortColumn == ordinal;
        await FetchAllAsync(ct).ConfigureAwait(true);
        var n = (int)(TotalRowCount ?? _loadedRows);
        var keys = new object?[n];
        for (var p = 0; (long)p * PageSize < n; p++)
        {
            var page = await _cache.GetPageAsync(p, ct).ConfigureAwait(true);
            for (var i = 0; i < page.RowCount; i++)
            {
                keys[page.FirstRowIndex + i] = page.Columns[ordinal][i];
            }
        }

        var order = Enumerable.Range(0, n).ToArray();
        var kind = Columns[ordinal].Kind;
        Array.Sort(order, (a, b) =>
        {
            var c = CompareValues(keys[a], keys[b], kind, desc);
            return c != 0 ? c : a.CompareTo(b);
        });
        _order = order;
        SortColumn = ordinal;
        SortDescending = desc;
        RaiseChanged();
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

    /// <summary>선택 범위를 복사 글자로 만든다. 선택이 없으면 빈 글자. 아직 못 받은 행은 가져온다.</summary>
    public async Task<string> BuildCopyTextAsync(CopyFormat format, bool includeHeaders = false, CancellationToken ct = default)
    {
        if (!HasSelection)
        {
            return string.Empty;
        }

        var (from, to) = SelectionRect();
        var ordinals = _visible.Skip(from.Col).Take(to.Col - from.Col + 1).ToList();
        var cols = ordinals.Select(o => Columns[o]).ToList();
        var rows = new List<IReadOnlyList<object?>>((int)Math.Min(to.Row - from.Row + 1, 100_000));
        for (var r = from.Row; r <= to.Row; r++)
        {
            var src = SourceRow(r);
            var page = await _cache.GetPageAsync((int)(src / PageSize), ct).ConfigureAwait(true);
            OnPage(page);
            var i = (int)(src - page.FirstRowIndex);
            if (i < 0 || i >= page.RowCount)
            {
                break;
            }

            rows.Add(ordinals.Select(o => page.Columns[o][i]).ToList());
        }

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

    public void RequestFind() => FindRequested?.Invoke(this, EventArgs.Empty);

    public void RequestClose() => CloseRequested?.Invoke(this, EventArgs.Empty);

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
}
