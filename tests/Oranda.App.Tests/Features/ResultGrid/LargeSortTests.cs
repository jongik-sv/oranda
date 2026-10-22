using Oranda.App.Features.ResultGrid;
using Oranda.Core.Grid;

namespace Oranda.App.Tests.Features.ResultGrid;

/// <summary>TSK-13-17 큰 결과 정렬 보호: 문턱 미만은 묻지 않고 정렬, 문턱에 닿으면 묻고, 취소하면 원래 순서와 응답을 유지한다.</summary>
public sealed class LargeSortTests
{
    /// <summary>행을 한 쪽씩 내주는 커서. ID 는 큰 수에서 작은 수로 줄어 오름차순 정렬이 순서를 뒤집는다.</summary>
    private sealed class Cursor(int rows, int pageSize = 500) : IResultSet
    {
        private int _fetched;

        public TaskCompletionSource? Gate { get; set; }

        public IReadOnlyList<ResultColumn> Columns { get; } = [new(0, "ID", "NUMBER", ColumnDataKind.Number)];

        public bool IsComplete => (long)_fetched * pageSize >= rows;

        public long? KnownRowCount => IsComplete ? rows : null;

        public async Task<ResultPage> GetPageAsync(int pageIndex, CancellationToken ct)
        {
            if (Gate is { } g && pageIndex > 0)
            {
                await g.Task.WaitAsync(ct);
            }

            _fetched = Math.Max(_fetched, pageIndex + 1);
            var first = pageIndex * pageSize;
            var count = Math.Clamp(rows - first, 0, pageSize);
            var ids = new object?[count];
            for (var i = 0; i < count; i++)
            {
                ids[i] = (decimal)(rows - (first + i));
            }

            return new ResultPage(pageIndex, first, count, [ids]) { IsLast = first + count >= rows };
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static async Task<ResultGridModel> OpenAsync(Cursor cursor, int threshold)
    {
        var m = new ResultGridModel(cursor) { LocalSortMaxRowsOverride = threshold };
        await m.LoadFirstPageAsync();
        return m;
    }

    private static async Task<decimal> FirstValueAsync(ResultGridModel m)
    {
        await m.ReadRowsAsync(0, 0, [0], default);
        Assert.True(m.TryGetValue(0, 0, out var v));
        return Convert.ToDecimal(v, System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public async Task Below_the_threshold_sorts_without_asking()
    {
        await using var m = await OpenAsync(new Cursor(800), threshold: 1000); // 다 안 가져왔지만 문턱 안에서 끝난다
        var asked = 0;
        m.ConfirmLargeSort = (_, _) => { asked++; return Task.FromResult(LargeSortChoice.Cancel); };

        await m.ToggleSortAsync(0);

        Assert.Equal(0, asked);
        Assert.Equal(0, m.SortColumn);
        Assert.Equal(1m, await FirstValueAsync(m));
        Assert.False(m.IsSorting);
    }

    [Fact]
    public async Task Reaching_the_threshold_asks_and_cancel_keeps_the_original_order()
    {
        await using var m = await OpenAsync(new Cursor(5000), threshold: 1000);
        LargeSortRequest? shown = null;
        m.ConfirmLargeSort = (r, _) => { shown = r; return Task.FromResult(LargeSortChoice.Cancel); };

        await m.ToggleSortAsync(0);

        Assert.NotNull(shown);
        Assert.Equal(1000, shown.Threshold);
        Assert.False(shown.AllLoaded);
        Assert.InRange(m.LoadedRowCount, 1000, 1500); // 문턱까지만 더 가져왔다
        Assert.Equal(-1, m.SortColumn);
        Assert.Equal(5000m, await FirstValueAsync(m)); // 원래 순서
        Assert.False(m.IsSorting);
    }

    [Fact]
    public async Task Local_all_fetches_everything_then_sorts_and_is_not_asked_again()
    {
        await using var m = await OpenAsync(new Cursor(3000), threshold: 1000);
        var asked = 0;
        m.ConfirmLargeSort = (_, _) => { asked++; return Task.FromResult(LargeSortChoice.LocalAll); };

        await m.ToggleSortAsync(0);
        await m.ToggleSortAsync(0); // 내림차순

        Assert.Equal(1, asked);
        Assert.Equal(3000, m.LoadedRowCount);
        Assert.True(m.SortDescending);
        Assert.Equal(3000m, await FirstValueAsync(m));
    }

    [Fact]
    public async Task Server_requery_choice_hands_over_to_the_server_sort_without_fetching_more()
    {
        await using var m = await OpenAsync(new Cursor(5000), threshold: 1000);
        (int Ordinal, bool Desc)? called = null;
        m.ServerSort = (o, d, _) => { called = (o, d); return Task.CompletedTask; };
        m.ConfirmLargeSort = (r, _) =>
        {
            Assert.Null(r.ServerBlockedReason);
            return Task.FromResult(LargeSortChoice.ServerReQuery);
        };

        await m.ToggleSortAsync(0);

        Assert.Equal((0, false), called);
        Assert.Equal(-1, m.SortColumn); // 이 모델은 그대로(결과는 서버 재조회가 새로 만든다)
        Assert.InRange(m.LoadedRowCount, 1000, 1500);
    }

    [Fact]
    public async Task Server_requery_is_offered_as_blocked_when_no_server_sort_is_available()
    {
        await using var m = await OpenAsync(new Cursor(5000), threshold: 1000);
        string? reason = null;
        m.ConfirmLargeSort = (r, _) => { reason = r.ServerBlockedReason; return Task.FromResult(LargeSortChoice.Cancel); };

        await m.ToggleSortAsync(0);

        Assert.False(string.IsNullOrEmpty(reason));
    }

    [Fact]
    public async Task Cancel_while_fetching_stops_without_sorting_and_the_model_stays_usable()
    {
        var cursor = new Cursor(50_000) { Gate = new TaskCompletionSource() };
        await using var m = await OpenAsync(cursor, threshold: 40_000);
        m.ConfirmLargeSort = (_, _) => Task.FromResult(LargeSortChoice.LocalAll);

        var sort = m.ToggleSortAsync(0);
        await WaitAsync(() => m.IsSorting);
        Assert.Equal(SortPhase.Fetching, m.SortingPhase);
        m.CancelSort(); // Esc·취소 단추
        await sort;

        Assert.False(m.IsSorting);
        Assert.Equal(-1, m.SortColumn);
        Assert.Equal(50_000m, await FirstValueAsync(m));

        cursor.Gate.SetResult(); // 이어서 다시 정렬할 수 있다
        m.LocalSortMaxRowsOverride = 100_000;
        await m.ToggleSortAsync(0);
        Assert.Equal(0, m.SortColumn);
    }

    [Fact]
    public void BuildOrder_sorts_numbers_with_nulls_last_and_keeps_ties_in_source_order()
    {
        object?[] keys = [3m, null, 1m, 3m, "2", DBNull.Value];

        var asc = ResultGridModel.BuildOrder(keys, ColumnDataKind.Number, desc: false, default);
        var desc = ResultGridModel.BuildOrder(keys, ColumnDataKind.Number, desc: true, default);

        Assert.Equal([2, 4, 0, 3, 1, 5], asc);
        Assert.Equal([0, 3, 4, 2, 1, 5], desc);
    }

    [Fact]
    public void BuildOrder_stops_when_cancelled()
    {
        var keys = Enumerable.Range(0, 200_000).Select(i => (object?)(decimal)(i * 7919 % 100_003)).ToArray();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => ResultGridModel.BuildOrder(keys, ColumnDataKind.Number, false, cts.Token));
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        for (var i = 0; i < 300 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }
}
