using Oranda.Core.Execution.Providers;
using Oranda.Core.Grid;
using Oranda.Tibero.Results;

namespace Oranda.Tibero.Tests.Results;

public class TiberoResultSetTests
{
    private static readonly ResultColumn[] Cols =
    [
        new(0, "ID", "NUMBER", ColumnDataKind.Number),
        new(1, "NAME", "VARCHAR2", ColumnDataKind.Text),
    ];

    private static FakeDataReader Reader(int rows) =>
        new(["ID", "NAME"], Enumerable.Range(1, rows).Select(i => new object?[] { i, "n" + i }).ToArray());

    private static TiberoResultSet Create(FakeDataReader reader, SessionCommandGate gate, int pageSize, TiberoResultHooks? hooks = null) =>
        new(gate, null, reader, Cols, static (r, i, _) => r.GetValue(i), pageSize, hooks);

    private static int[] Ids(ResultPage page) => page.Columns[0].Select(v => (int)v!).ToArray();

    [Fact]
    public async Task Pages_come_in_order_with_column_major_buffers_and_known_count_at_end()
    {
        var gate = new SessionCommandGate();
        await using var rs = Create(Reader(5), gate, 2);

        var p0 = await rs.GetPageAsync(0, CancellationToken.None);
        var p1 = await rs.GetPageAsync(1, CancellationToken.None);
        var p2 = await rs.GetPageAsync(2, CancellationToken.None);

        Assert.Equal([1, 2], Ids(p0));
        Assert.Equal(2, p1.FirstRowIndex);
        Assert.Equal([3, 4], Ids(p1));
        Assert.Equal([5], Ids(p2));
        Assert.True(p2.IsLast);
        Assert.Equal("n5", p2.Columns[1][0]);
        Assert.True(rs.IsComplete);
        Assert.Equal(5, rs.KnownRowCount);
        Assert.Equal(2, rs.PageSize);
        Assert.False(gate.IsHeld);
    }

    [Fact]
    public async Task Skipping_ahead_reads_the_pages_in_between_and_same_page_is_served_again()
    {
        var gate = new SessionCommandGate();
        await using var rs = Create(Reader(5), gate, 2);

        var p1 = await rs.GetPageAsync(1, CancellationToken.None);
        var again = await rs.GetPageAsync(1, CancellationToken.None);

        Assert.Equal([3, 4], Ids(p1));
        Assert.Same(p1, again);
        await Assert.ThrowsAsync<InvalidOperationException>(() => rs.GetPageAsync(0, CancellationToken.None));
    }

    [Fact]
    public async Task Page_past_the_end_is_empty_and_last()
    {
        var gate = new SessionCommandGate();
        await using var rs = Create(Reader(3), gate, 2);

        var page = await rs.GetPageAsync(4, CancellationToken.None);

        Assert.Equal(0, page.RowCount);
        Assert.True(page.IsLast);
        Assert.Equal(3, rs.KnownRowCount);
    }

    [Fact]
    public async Task User_cancel_between_rows_keeps_the_rows_read_and_the_next_request_continues()
    {
        var gate = new SessionCommandGate();
        using var cts = new CancellationTokenSource();
        var reader = Reader(6);
        reader.OnRead = n =>
        {
            if (n == 3)
            {
                cts.Cancel(); // 3 번째 읽기 뒤 다음 행 사이에서 취소를 받는다
            }
        };
        await using var rs = Create(reader, gate, 4);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rs.GetPageAsync(0, cts.Token));
        Assert.False(rs.IsCut);
        Assert.False(gate.IsHeld);

        var page = await rs.GetPageAsync(0, CancellationToken.None);
        var next = await rs.GetPageAsync(1, CancellationToken.None);

        Assert.Equal([1, 2, 3, 4], Ids(page));
        Assert.Equal([5, 6], Ids(next));
    }

    [Fact]
    public async Task Slow_round_trip_after_cancel_triggers_server_cancel_and_cuts_the_result()
    {
        var gate = new SessionCommandGate();
        using var cts = new CancellationTokenSource();
        using var serverCancelled = new ManualResetEventSlim();
        var reader = Reader(6);
        reader.OnRead = n =>
        {
            if (n == 2)
            {
                cts.Cancel();
                Assert.True(serverCancelled.Wait(TimeSpan.FromSeconds(15)), "하드 취소가 서버 취소를 부르지 않았다");
                throw new IOException("canceled by server");
            }
        };
        var hooks = new TiberoResultHooks
        {
            CancelServer = serverCancelled.Set,
            IsCancelled = static ex => ex is IOException,
        };
        await using var rs = Create(reader, gate, 4, hooks);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rs.GetPageAsync(0, cts.Token));

        Assert.True(rs.IsCut);
        Assert.False(gate.IsHeld);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => rs.GetPageAsync(0, CancellationToken.None));
        Assert.Contains("취소", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Driver_error_is_reported_to_the_session_and_rethrown_and_gate_is_released()
    {
        var gate = new SessionCommandGate();
        var reader = Reader(3);
        reader.OnRead = n =>
        {
            if (n == 2)
            {
                throw new InvalidDataException("boom");
            }
        };
        Exception? reported = null;
        var busy = new List<bool>();
        var hooks = new TiberoResultHooks { ReportFailure = e => reported = e, SetBusy = busy.Add, IsCancelled = static _ => false };
        await using var rs = Create(reader, gate, 2, hooks);

        await Assert.ThrowsAsync<InvalidDataException>(() => rs.GetPageAsync(0, CancellationToken.None));

        Assert.IsType<InvalidDataException>(reported);
        Assert.Equal([true, false], busy);
        Assert.False(gate.IsHeld);
    }

    [Fact]
    public async Task Dispose_closes_the_reader_once_and_blocks_further_pages()
    {
        var gate = new SessionCommandGate();
        var reader = Reader(2);
        var rs = Create(reader, gate, 2);

        await rs.DisposeAsync();
        await rs.DisposeAsync();

        Assert.True(reader.Disposed);
        Assert.False(gate.IsHeld);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => rs.GetPageAsync(0, CancellationToken.None));
    }

    [Fact]
    public async Task Dispose_waits_for_the_page_in_flight()
    {
        var gate = new SessionCommandGate();
        using var inRead = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var reader = Reader(2);
        reader.OnRead = n =>
        {
            if (n == 1)
            {
                inRead.Set();
                release.Wait(TimeSpan.FromSeconds(15));
            }
        };
        var rs = Create(reader, gate, 2);

        var page = rs.GetPageAsync(0, CancellationToken.None);
        Assert.True(inRead.Wait(TimeSpan.FromSeconds(15)));
        var dispose = rs.DisposeAsync().AsTask();
        await Task.Delay(100);
        Assert.False(dispose.IsCompleted);
        Assert.False(reader.Disposed);

        release.Set();
        await page;
        await dispose;

        Assert.True(reader.Disposed);
    }
}
