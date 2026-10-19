using Oranda.Core.Grid;
using Oranda.Testing;

namespace Oranda.Core.Tests.Grid;

// 소유: TSK-06-03. 페이지 캐시·메모리 상한·임시 파일.
public sealed class ResultPageCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oranda-cache-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static FakeResultSet Make(int rows, int pageSize)
    {
        var cols = new List<ResultColumn>
        {
            new(0, "ID", "NUMBER", ColumnDataKind.Number),
            new(1, "NAME", "VARCHAR2", ColumnDataKind.Text),
            new(2, "D", "DATE", ColumnDataKind.Date),
            new(3, "L", "CLOB", ColumnDataKind.Clob),
        };
        var data = Enumerable.Range(0, rows)
            .Select(i => (IReadOnlyList<object?>)new object?[]
            {
                (decimal)i,
                i % 7 == 0 ? null : "name-" + i,
                new DateTime(2026, 1, 1).AddMinutes(i),
                new LobValue(LobKind.Clob, 10) { TextPreview = "lob" + i, Locator = "loc" + i },
            })
            .ToList();
        return new FakeResultSet(cols, data, pageSize);
    }

    [Fact]
    public async Task Returns_pages_and_reuses_resident_page_without_refetch()
    {
        await using var rs = Make(100, 10);
        await using var cache = new ResultPageCache(rs);

        var p = await cache.GetPageAsync(2);
        Assert.Equal(20, p.FirstRowIndex);
        Assert.Equal(10, p.RowCount);
        var before = rs.PageRequests;
        var again = await cache.GetPageAsync(2);
        Assert.Same(p, again);
        Assert.Equal(before, rs.PageRequests);
        Assert.True(cache.TryGetResident(2, out _));
        Assert.False(cache.TryGetResident(9, out _));
    }

    [Fact]
    public async Task Concurrent_requests_for_one_page_fetch_once()
    {
        await using var rs = Make(100, 10);
        await using var cache = new ResultPageCache(rs);

        var pages = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => cache.GetPageAsync(0)));
        Assert.All(pages, x => Assert.Same(pages[0], x));
        Assert.Equal(1, rs.PageRequests);
    }

    [Fact]
    public async Task Evicts_beyond_memory_limit_and_spills_to_temp_file_without_refetching_source()
    {
        await using var rs = Make(200, 10);
        await using var cache = new ResultPageCache(rs, new ResultPageCacheOptions { MaxMemoryBytes = 4000, SpillDirectory = _dir });

        for (var i = 0; i < 20; i++)
        {
            await cache.GetPageAsync(i);
        }

        Assert.True(cache.ResidentPageCount < 20);
        Assert.True(cache.SpilledPageCount > 0);
        Assert.True(cache.ResidentBytes <= 4000 || cache.ResidentPageCount == 1);
        Assert.Equal(20, rs.PageRequests);
        Assert.NotEmpty(Directory.GetFiles(_dir));

        // 밀려난 첫 페이지를 다시 부르면 원본이 아니라 임시 파일에서 읽는다.
        var first = await cache.GetPageAsync(0);
        Assert.Equal(20, rs.PageRequests);
        Assert.Equal(10, first.RowCount);
        Assert.Equal(3m, first.Columns[0][3]);
        Assert.Equal("name-3", first.Columns[1][3]);
        Assert.Null(first.Columns[1][0]);
        Assert.Equal(new DateTime(2026, 1, 1).AddMinutes(4), first.Columns[2][4]);
        var lob = Assert.IsType<LobValue>(first.Columns[3][5]);
        Assert.Equal("lob5", lob.TextPreview);
        Assert.Equal("loc5", lob.Locator);
    }

    [Fact]
    public async Task Round_trips_many_value_types_through_spill()
    {
        var cols = new List<ResultColumn> { new(0, "X", "ANY", ColumnDataKind.Other) };
        var values = new object?[]
        {
            null, "s", 1.5m, 7L, 3, (short)2, (byte)9, 2.5d, 1.25f, true, new DateTime(2026, 5, 1, 1, 2, 3, DateTimeKind.Utc),
            new DateTimeOffset(2026, 5, 1, 1, 2, 3, TimeSpan.FromHours(9)), TimeSpan.FromSeconds(90), Guid.NewGuid(),
            new byte[] { 1, 2, 3 }, new float[] { 1f, 2f }, new double[] { 1d }, new sbyte[] { -1 }, new Uri("http://x"),
        };
        var rows = values.Select(v => (IReadOnlyList<object?>)new[] { v }).ToList();
        await using var rs = new FakeResultSet(cols, rows, 1000);
        await using var cache = new ResultPageCache(rs, new ResultPageCacheOptions { MaxMemoryBytes = 1, SpillDirectory = _dir });

        var original = await cache.GetPageAsync(0);
        _ = await cache.GetPageAsync(1); // 첫 페이지를 밀어낸다
        Assert.False(cache.TryGetResident(0, out _));
        var restored = await cache.GetPageAsync(0);

        Assert.NotSame(original, restored);
        for (var i = 0; i < values.Length; i++)
        {
            var a = original.Columns[0][i];
            var b = restored.Columns[0][i];
            if (a is Array arr)
            {
                Assert.Equal(arr, Assert.IsAssignableFrom<Array>(b));
            }
            else
            {
                Assert.Equal(a, b);
            }
        }
    }

    [Fact]
    public async Task Past_end_page_has_zero_rows()
    {
        await using var rs = Make(5, 10);
        await using var cache = new ResultPageCache(rs);
        var p = await cache.GetPageAsync(3);
        Assert.Equal(0, p.RowCount);
    }

    [Fact]
    public async Task Dispose_deletes_spill_file_and_blocks_further_use()
    {
        await using var rs = Make(100, 10);
        var cache = new ResultPageCache(rs, new ResultPageCacheOptions { MaxMemoryBytes = 1, SpillDirectory = _dir });
        await cache.GetPageAsync(0);
        await cache.GetPageAsync(1);
        Assert.NotEmpty(Directory.GetFiles(_dir));

        await cache.DisposeAsync();
        Assert.Empty(Directory.GetFiles(_dir));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => cache.GetPageAsync(0));
        Assert.False(rs.IsDisposed);
    }

    [Fact]
    public async Task Canceled_caller_does_not_break_shared_load()
    {
        await using var rs = Make(100, 10);
        await using var cache = new ResultPageCache(rs);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.GetPageAsync(0, cts.Token));
        var p = await cache.GetPageAsync(0);
        Assert.Equal(10, p.RowCount);
    }
}
