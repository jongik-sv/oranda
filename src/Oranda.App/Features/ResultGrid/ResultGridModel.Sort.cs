using System.Globalization;
using Oranda.Core.Grid;

namespace Oranda.App.Features.ResultGrid;

// ResultGridModel 의 일부: 정렬(ResultGridModel.cs 에서 나눔, 동작 불변).
public sealed partial class ResultGridModel
{
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
            RebuildView();
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

        await ExtendLocalFilterAsync(ct).ConfigureAwait(true);
        _order = order;
        RebuildView();
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
        RebuildView();
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
}
