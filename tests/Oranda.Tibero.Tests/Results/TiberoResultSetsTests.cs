using System.Data;
using System.Data.Common;
using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Execution;
using Oranda.Core.Grid;
using Oranda.Core.Security;
using Oranda.Core.Sessions;
using Oranda.Tibero.Results;
using Oranda.Tibero.Sessions;
using Oranda.Tibero.Tests.Driver;

namespace Oranda.Tibero.Tests.Results;

[Collection("FakeDriver")]
public class TiberoResultSetsTests
{
    private const string TypesNs = "Tibero.DataAccess.Types.";

    private static readonly ConnectionProfile Profile = new("t", "u", "h:1/d", Dbms: Dbms.Tibero);

    private static readonly ServerInfo Server = new("6", "i", "h", null, "D", null, false, 7,
        new ServerCapabilities(false, false, true, false, false, Dbms.Tibero), DbUniqueName: "D", SessionUser: "U", Dbms: Dbms.Tibero);

    private static TiberoSession NewSession() =>
        new(Guid.NewGuid(), Profile, Server, SessionPurpose.Work, "D/-/U", FakeDriverHost.Driver.CreateConnection("Data Source=x"), null,
            new EventBus(), driver: FakeDriverHost.Driver);

    private static DataTable Schema(params int[] providerTypes)
    {
        var t = new DataTable();
        t.Columns.Add("ProviderType", typeof(int));
        foreach (var pt in providerTypes)
        {
            t.Rows.Add(pt);
        }

        return t;
    }

    private static FakeDataReader Reader(int rows) =>
        new(["ID", "NAME"], Enumerable.Range(1, rows).Select(i => new object?[] { i, "n" + i }).ToArray(),
            Schema(TiberoColumnTypes.PtInt32, TiberoColumnTypes.PtVarchar2));

    [Fact]
    public async Task Create_reads_columns_from_the_schema_and_cells_through_the_driver_values()
    {
        var session = NewSession();
        await using var rs = TiberoResultSets.Create(session, null, Reader(3), 2);

        var page = await rs.GetPageAsync(0, CancellationToken.None);

        Assert.Equal([ColumnDataKind.Number, ColumnDataKind.Text], rs.Columns.Select(c => c.Kind));
        Assert.Equal([1, 2], page.Columns[0].Select(v => (int)v!));
        Assert.Equal(["n1", "n2"], page.Columns[1].Select(v => (string)v!));
        Assert.False(session.CommandGate.IsHeld);
        Assert.Equal(SessionState.Idle, session.State);
    }

    [Fact]
    public async Task Session_is_busy_while_a_page_is_read_and_idle_after()
    {
        var session = NewSession();
        var states = new List<SessionState>();
        var reader = Reader(2);
        reader.OnRead = _ => states.Add(session.State);
        await using var rs = TiberoResultSets.Create(session, null, reader, 5);

        await rs.GetPageAsync(0, CancellationToken.None);

        Assert.All(states, s => Assert.Equal(SessionState.Busy, s));
        Assert.Equal(SessionState.Idle, session.State);
    }

    [Fact]
    public async Task Page_reads_wait_for_the_session_command_gate()
    {
        var session = NewSession();
        await using var rs = TiberoResultSets.Create(session, null, Reader(2), 5);
        await session.CommandGate.EnterAsync(CancellationToken.None);

        var page = rs.GetPageAsync(0, CancellationToken.None);
        await Task.Delay(100);
        Assert.False(page.IsCompleted);

        session.CommandGate.Release();
        Assert.Equal(2, (await page).RowCount);
    }

    [Fact]
    public async Task Hard_cancel_calls_cancel_on_the_command_that_made_the_reader()
    {
        var session = NewSession();
        using var cts = new CancellationTokenSource();
        using var cancelled = new ManualResetEventSlim();
        var command = new CancelProbeCommand(cancelled);
        var reader = Reader(3);
        reader.OnRead = n =>
        {
            if (n == 1)
            {
                cts.Cancel();
                Assert.True(cancelled.Wait(TimeSpan.FromSeconds(15)), "하드 취소가 명령 Cancel 을 부르지 않았다");
                throw new IOException("canceled");
            }
        };
        await using var rs = TiberoResultSets.Create(session, command, reader, 5);

        await Assert.ThrowsAnyAsync<Exception>(() => rs.GetPageAsync(0, cts.Token));

        Assert.True(command.CancelCalls >= 1);
        Assert.False(session.CommandGate.IsHeld);
    }

    [Fact]
    public async Task Disposing_the_result_set_disposes_the_command()
    {
        var command = new CancelProbeCommand(new ManualResetEventSlim());
        var rs = TiberoResultSets.Create(NewSession(), command, Reader(1), 5);

        await rs.DisposeAsync();

        Assert.True(command.WasDisposed);
    }

    [Fact]
    public async Task Session_without_a_driver_cannot_read_results()
    {
        var session = new TiberoSession(Guid.NewGuid(), Profile, Server, SessionPurpose.Work, "D/-/U", FakeDriverHost.Driver.CreateConnection("x"), null, new EventBus());
        await using var reader = Reader(1);

        Assert.Throws<InvalidOperationException>(() => TiberoResultSets.Create(session, null, reader, 5));
    }

    // ---- OUT 값 ----

    private static DbParameter Out(object? value)
    {
        var p = FakeDriverHost.Driver.CreateConnection("x").CreateCommand().CreateParameter();
        p.Value = value;
        return p;
    }

    [Fact]
    public async Task Out_values_are_keyed_by_name_and_ref_cursors_become_result_sets()
    {
        var session = NewSession();
        var table = new DataTable();
        table.Columns.Add("c", typeof(string));
        table.Rows.Add("row1");
        var outs = new List<TiberoOutBind>
        {
            new("TXT", Out(FakeDriverHost.New(TypesNs + "TiberoString", "hello")), new BindValue(BindDataType.Varchar2, null, BindDirection.Out)),
            new("num", Out(FakeDriverHost.New(TypesNs + "TiberoString", "12.5")), new BindValue(BindDataType.Number, null, BindDirection.Out)),
            new("cur", Out(FakeDriverHost.New(TypesNs + "TiberoRefCursor", table)), new BindValue(BindDataType.RefCursor, null, BindDirection.Out)),
            new("nul", Out(FakeDriverHost.Static(TypesNs + "TiberoString", "Null")), new BindValue(BindDataType.Varchar2, null, BindDirection.Out)),
        };

        var values = TiberoResultSets.ReadOutValues(session, outs, 10);

        Assert.Equal("hello", values["txt"]);
        Assert.Equal(12.5m, values["NUM"]);
        Assert.Null(values["nul"]);
        await using var cursor = Assert.IsType<TiberoResultSet>(values["cur"]);
        var page = await cursor.GetPageAsync(0, CancellationToken.None);
        Assert.Equal("row1", page.Columns[0][0]);
    }

    // ---- LOB accessor ----

    private static LobValue ClobCell(string text)
    {
        var table = new DataTable();
        table.Columns.Add("c", typeof(string));
        table.Rows.Add(text);
        using var reader = FakeDriverHost.NewReader(table);
        Assert.True(reader.Read());
        return (LobValue)TiberoValues.ReadCell(FakeDriverHost.Driver, reader, 0, ColumnDataKind.Clob, TiberoColumnTypes.PtClob, 2)!;
    }

    [Fact]
    public async Task Lob_accessor_reads_the_whole_clob_and_does_not_hold_the_gate_between_reads()
    {
        var session = NewSession();
        var accessor = new TiberoLobAccessor();

        await using var stream = await accessor.OpenReadAsync(session, ClobCell("가나다abc"), CancellationToken.None);

        Assert.False(session.CommandGate.IsHeld);
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        Assert.Equal("가나다abc", System.Text.Encoding.UTF8.GetString(ms.ToArray()));
        Assert.False(session.CommandGate.IsHeld);
    }

    [Fact]
    public async Task Lob_accessor_waits_for_the_gate_and_honours_cancellation()
    {
        var session = NewSession();
        var accessor = new TiberoLobAccessor();
        await session.CommandGate.EnterAsync(CancellationToken.None);

        var open = accessor.OpenReadAsync(session, ClobCell("abc"), CancellationToken.None);
        await Task.Delay(100);
        Assert.False(open.IsCompleted);
        using var cts = new CancellationTokenSource();
        var cancelled = accessor.OpenReadAsync(session, ClobCell("abc"), cts.Token);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);

        session.CommandGate.Release();
        await using var stream = await open;
        Assert.NotNull(stream);
    }

    [Fact]
    public async Task Lob_accessor_without_a_locator_asks_for_a_rerun()
    {
        var accessor = new TiberoLobAccessor();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            accessor.OpenReadAsync(NewSession(), new LobValue(LobKind.Clob, 3) { TextPreview = "abc" }, CancellationToken.None));
    }

    /// <summary>Cancel/Dispose 호출을 기록하는 명령(드라이버 명령 대역).</summary>
    private sealed class CancelProbeCommand(ManualResetEventSlim cancelled) : DbCommand
    {
        public int CancelCalls { get; private set; }

        public bool WasDisposed { get; private set; }

#pragma warning disable CS8765
        public override string CommandText { get; set; } = string.Empty;
#pragma warning restore CS8765

        public override int CommandTimeout { get; set; }

        public override CommandType CommandType { get; set; }

        public override bool DesignTimeVisible { get; set; }

        public override UpdateRowSource UpdatedRowSource { get; set; }

        protected override DbConnection? DbConnection { get; set; }

        protected override DbParameterCollection DbParameterCollection => throw new NotSupportedException();

        protected override DbTransaction? DbTransaction { get; set; }

        public override void Cancel()
        {
            CancelCalls++;
            cancelled.Set();
        }

        public override int ExecuteNonQuery() => throw new NotSupportedException();

        public override object? ExecuteScalar() => throw new NotSupportedException();

        public override void Prepare()
        {
        }

        protected override DbParameter CreateDbParameter() => throw new NotSupportedException();

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            base.Dispose(disposing);
        }
    }
}
