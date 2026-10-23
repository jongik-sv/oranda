using System.Globalization;
using Oranda.Core.Grid;

namespace Oranda.App.Features.ResultGrid;

// ResultGridModel 의 일부: 앱 안 거르기(ResultGridModel.cs 에서 나눔, 동작 불변).
public sealed partial class ResultGridModel
{
    // ---- 앱 안 거르기 (TSK-13-26) ----

    /// <summary>대기 중인 편집이 있어 거르기를 바꾸지 않을 때 상태 줄에 보이는 안내.</summary>
    public const string FilterBlockedNotice = "반영하지 않은 변경이 있어 거르기를 바꿀 수 없습니다. 반영하거나 되돌린 뒤 거를 수 있습니다.";

    /// <summary>지금 걸린 앱 안 거르기 조건(없으면 <see cref="LocalFilterSpec.Empty"/>). 서버에 다시 조회하지 않고 가져온 행만 거른다.</summary>
    public LocalFilterSpec LocalFilter { get; private set; } = LocalFilterSpec.Empty;

    /// <summary>거르기가 걸려 일부 행만 보이는 중이면 true.</summary>
    public bool IsLocalFiltered => _match is not null;

    /// <summary>거르기를 계산하는 중(큰 결과에서 입력이 이어지는 동안).</summary>
    public bool IsFilterRunning { get; private set; }

    /// <summary>거르기를 통과한 질의 행 수. 거르기가 없으면 <see cref="BaseRowCount"/>.</summary>
    public long LocalFilterVisibleRows => BaseRowCount;

    /// <summary>
    /// 앱 안 거르기를 바꾼다. 가져온 행 전체를 UI 스레드 밖에서 훑고(페이지마다 양보), 끝나야 보이는 행이 바뀐다.
    /// 새 호출이 오면 앞선 계산은 버려진다. 정렬은 걸러진 행에 적용되고, 편집 모드의 새 행은 거르기와 무관하게 끝에 보인다.
    /// 반영하지 않은 편집이 있으면 바꾸지 않고 알린다(편집 세션이 화면 행 번호로 변경을 기억한다). 바꿨으면 true.
    /// </summary>
    public async Task<bool> SetLocalFilterAsync(LocalFilterSpec? spec, CancellationToken ct = default)
    {
        spec ??= LocalFilterSpec.Empty;
        if (_disposed || (spec.IsEmpty && _match is null && !IsFilterRunning))
        {
            LocalFilter = spec;
            return true;
        }

        if (EditOverlay is { HasPendingChanges: true })
        {
            ReportNotice(FilterBlockedNotice);
            return false;
        }

        var generation = ++_filterGeneration;
        _filterCts?.Cancel();
        LocalFilter = spec;
        if (spec.IsEmpty)
        {
            IsFilterRunning = false;
            ApplyMatch(null, 0);
            return true;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        _filterCts = cts;
        IsFilterRunning = true;
        RaiseChanged();
        try
        {
            var (match, rows) = await EvaluateFilterAsync(spec, null, 0, cts.Token).ConfigureAwait(true);
            if (generation != _filterGeneration)
            {
                return false;
            }

            IsFilterRunning = false;
            ApplyMatch(match, rows);
            return true;
        }
        catch (OperationCanceledException)
        {
            if (generation == _filterGeneration)
            {
                IsFilterRunning = false;
                RaiseChanged();
            }

            return false;
        }
        finally
        {
            if (ReferenceEquals(_filterCts, cts))
            {
                _filterCts = null;
            }
        }
    }

    /// <summary>모든 앱 안 거르기(빠른 거르기 글·열 필터)를 지운다.</summary>
    public Task<bool> ClearLocalFilterAsync(CancellationToken ct = default) => SetLocalFilterAsync(LocalFilterSpec.Empty, ct);

    /// <summary>
    /// 거르기가 걸린 뒤 더 가져온 행(「모두 가져오기」·정렬 가져오기)을 거르기에 넣는다. 이미 계산한 앞부분은 다시 훑지 않는다.
    /// 거르기가 없거나 이미 다 반영했으면 아무것도 하지 않는다.
    /// </summary>
    private async Task ExtendLocalFilterAsync(CancellationToken ct)
    {
        var n = (int)Math.Min(int.MaxValue, TotalRowCount ?? _loadedRows);
        if (_match is not { } old || LocalFilter.IsEmpty || n <= _matchedRows)
        {
            return;
        }

        var generation = _filterGeneration;
        var (match, rows) = await EvaluateFilterAsync(LocalFilter, old, _matchedRows, ct).ConfigureAwait(true);
        if (generation == _filterGeneration)
        {
            ApplyMatch(match, rows);
        }
    }

    /// <summary>자동 필터 값 목록에 올리는 서로 다른 값의 최대 개수.</summary>
    public const int MaxFilterValues = 1000;

    /// <summary>
    /// 열 자동 필터의 값 목록: 가져온 모든 행에서 이 열의 서로 다른 화면 표시 값과 그 개수(NULL 은 따로).
    /// 페이지를 UI 스레드에서 모으고 세는 일은 <see cref="Task.Run(Action)"/> 안에서 돈다. 서로 다른 값이 <paramref name="maxValues"/> 개를 넘으면 먼저 만난 값만 싣고 <see cref="ColumnValueList.Truncated"/> 를 켠다.
    /// </summary>
    public async Task<ColumnValueList> CollectColumnValuesAsync(int ordinal, int maxValues = MaxFilterValues, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ordinal);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(ordinal, Columns.Count);
        var n = (int)Math.Min(int.MaxValue, TotalRowCount ?? _loadedRows);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        long nulls = 0;
        var truncated = false;
        var column = Columns[ordinal];
        var display = Display;
        for (var p = 0; (long)p * PageSize < n; p++)
        {
            ct.ThrowIfCancellationRequested();
            var page = await _cache.GetPageAsync(p, ct).ConfigureAwait(true);
            var (pageNulls, pageTruncated) = await Task.Run(() =>
            {
                long nullCount = 0;
                var over = false;
                var values = page.Columns[ordinal];
                for (var i = 0; i < page.RowCount; i++)
                {
                    var v = values[i];
                    if (v is null or DBNull)
                    {
                        nullCount++;
                        continue;
                    }

                    var text = CellValueFormatter.Format(column, v, display);
                    if (counts.TryGetValue(text, out var c))
                    {
                        counts[text] = c + 1;
                    }
                    else if (counts.Count < maxValues)
                    {
                        counts[text] = 1;
                    }
                    else
                    {
                        over = true;
                    }
                }

                return (nullCount, over);
            }, ct).ConfigureAwait(true);
            nulls += pageNulls;
            truncated |= pageTruncated;
        }

        var list = counts.Select(kv => new ColumnValueCount(kv.Key, kv.Value)).ToList();
        if (column.Kind is ColumnDataKind.Number or ColumnDataKind.BinaryFloat
            && list.All(v => decimal.TryParse(v.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out _)))
        {
            list.Sort((a, b) => decimal.Parse(a.Text, NumberStyles.Float, CultureInfo.CurrentCulture).CompareTo(decimal.Parse(b.Text, NumberStyles.Float, CultureInfo.CurrentCulture)));
        }
        else
        {
            list.Sort((a, b) => string.Compare(a.Text, b.Text, StringComparison.OrdinalIgnoreCase));
        }

        return new ColumnValueList(list, nulls, truncated);
    }

    private void RefreshQuickFilterColumns()
    {
        if (LocalFilter is { HasQuick: true, QuickColumns: null } spec)
        {
            _ = SetLocalFilterAsync(spec); // 보이는 열이 바뀌었으니 「모든 보이는 열」 대상을 다시 계산한다
        }
    }

    /// <summary>
    /// 가져온 행을 <paramref name="from"/> 부터 훑어 통과 표를 만든다. 페이지는 UI 스레드에서 모으고(페이지마다 양보) 글 만들기·비교는 <see cref="Task.Run(Action)"/> 안에서 돈다.
    /// <paramref name="reuse"/> 가 있으면 그 앞부분을 이어받아 <paramref name="from"/> 이후만 계산한다.
    /// </summary>
    private async Task<(bool[] Match, int Rows)> EvaluateFilterAsync(LocalFilterSpec spec, bool[]? reuse, int from, CancellationToken ct)
    {
        var n = (int)Math.Min(int.MaxValue, TotalRowCount ?? _loadedRows);
        var result = new bool[n];
        if (reuse is not null)
        {
            Array.Copy(reuse, result, Math.Min(reuse.Length, Math.Min(from, n)));
        }

        var matcher = new LocalRowFilter(spec, Columns, [.. _visible], Display);
        for (var p = from / PageSize; (long)p * PageSize < n; p++)
        {
            ct.ThrowIfCancellationRequested();
            var page = await _cache.GetPageAsync(p, ct).ConfigureAwait(true);
            await Task.Run(() => matcher.Evaluate(page, result, ct), ct).ConfigureAwait(true);
            if ((p & 7) == 7)
            {
                await Task.Yield();
            }
        }

        return (result, n);
    }

    private void ApplyMatch(bool[]? match, int rows)
    {
        _match = match;
        _matchedRows = rows;
        _flashRows = null;
        RebuildView();
        if (RowCount == 0)
        {
            HasSelection = false;
        }
        else
        {
            ClampSelection();
        }

        RaiseChanged();
    }

    /// <summary>정렬 순열(<c>_order</c>)과 거르기 통과 표를 합쳐 화면 순서(<c>_view</c>)를 만든다. 정렬·거르기가 바뀔 때마다 이 한 곳을 거친다.</summary>
    private void RebuildView()
    {
        if (_match is not { } match)
        {
            _view = _order;
            return;
        }

        var n = Math.Min(match.Length, _matchedRows);
        var view = new List<int>(Math.Min(n, 1 << 16));
        var sorted = _order;
        if (sorted is not null)
        {
            foreach (var src in sorted)
            {
                if (src < n && match[src])
                {
                    view.Add(src);
                }
            }
        }

        // 정렬 순열이 없거나, 정렬한 뒤에 더 가져온 행은 가져온 순서대로 뒤에 붙인다.
        for (var src = sorted?.Length ?? 0; src < n; src++)
        {
            if (match[src])
            {
                view.Add(src);
            }
        }

        _view = [.. view];
    }
}
