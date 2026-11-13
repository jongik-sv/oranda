using System.Data;
using System.Data.Common;
using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Execution;
using Oranda.Core.Rollback;
using Oranda.Core.Sessions;
using Oranda.Tibero.Rollback;
using Oranda.Tibero.Sessions;
using Oranda.Tibero.Tests.Driver;

namespace Oranda.Tibero.Tests.Rollback;

/// <summary>Tibero 되돌리기 통로(가짜 드라이버). 명령 잠금 두 방식, 행 상한, 오류·끊김 감싸기.</summary>
[Collection("FakeDriver")]
public sealed class TiberoRollbackChannelTests : IDisposable
{
    private static readonly ServerInfo Server = new("6", "i", "h", null, "D", null, false, 7,
        new ServerCapabilities(false, false, true, false, false, Dbms.Tibero), DbUniqueName: "D", SessionUser: "U", Dbms: Dbms.Tibero);

    private static readonly IReadOnlyDictionary<string, BindValue> NoBinds = new Dictionary<string, BindValue>();

    private readonly EventBus _bus = new();
    private readonly List<DbCommand> _sent = [];

    public TiberoRollbackChannelTests() => FakeDriverHost.Reset();

    public void Dispose() => FakeDriverHost.Reset();

    [Fact]
    public async Task Gated_query_takes_and_releases_the_command_gate_reads_up_to_max_rows_and_sends_binds()
    {
        Serve(_ => Rows(("1", 1.5), ("2", null), ("3", 3.5)));
        await using var session = NewSession();

        var result = await TiberoRollbackChannel.Gated(session).QueryAsync("SELECT A, B FROM T WHERE K = :k",
            new Dictionary<string, BindValue> { ["k"] = new(BindDataType.Varchar2, "x") }, 2, CancellationToken.None);

        Assert.True(result.Truncated);
        Assert.Equal(2, result.Rows.Count);
        Assert.Equal(["1", 1.5], result.Rows[0]);
        Assert.Equal(["2", null], result.Rows[1]);
        Assert.Equal("k", Assert.Single(_sent[0].Parameters.Cast<DbParameter>()).ParameterName.TrimStart(':'));
        Assert.False(session.CommandGate.IsHeld);
    }

    [Fact]
    public async Task Held_channel_runs_while_the_caller_holds_the_gate_and_gated_waits_for_it()
    {
        Serve(_ => -1);
        await using var session = NewSession();
        await session.CommandGate.EnterAsync(CancellationToken.None);
        try
        {
            await TiberoRollbackChannel.Locked(session).ExecuteAsync("SAVEPOINT ORANDA$RB", CancellationToken.None);
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => TiberoRollbackChannel.Gated(session).ExecuteAsync("SAVEPOINT ORANDA$RB", cts.Token));
        }
        finally
        {
            session.CommandGate.Release();
        }

        Assert.Equal(["SAVEPOINT ORANDA$RB"], _sent.Select(c => c.CommandText));
    }

    [Fact]
    public async Task Server_errors_become_classified_channel_exceptions_and_a_broken_session_is_reported_as_disconnected()
    {
        Serve(_ => throw FakeDriverHost.ServerError(-8053, "TBR-8053: Not enough privilege."));
        await using var session = NewSession();
        var channel = TiberoRollbackChannel.Locked(session);

        var denied = await Assert.ThrowsAsync<RollbackChannelException>(() => channel.QueryAsync("SELECT 1 FROM T FOR UPDATE", NoBinds, 10, CancellationToken.None));
        Assert.Equal(DbErrorKind.InsufficientPrivileges, denied.Error.Kind);
        Assert.Equal("TBR-8053", denied.Error.Code);

        Serve(_ => throw FakeDriverHost.ServerError(-12001, "TBR-12001: session killed"));
        var killed = await Assert.ThrowsAsync<RollbackChannelException>(() => channel.ExecuteAsync("ROLLBACK TO SAVEPOINT ORANDA$RB", CancellationToken.None));
        Assert.Equal(DbErrorKind.Disconnected, killed.Error.Kind);
        Assert.Equal(SessionState.Broken, session.State);

        var after = await Assert.ThrowsAsync<RollbackChannelException>(() => channel.ExecuteAsync("SAVEPOINT ORANDA$RB", CancellationToken.None));
        Assert.Equal(DbErrorKind.Disconnected, after.Error.Kind);
    }

    private static DataTable Rows(params (string A, double? B)[] rows)
    {
        var table = new DataTable();
        table.Columns.Add("A", typeof(string));
        table.Columns.Add("B", typeof(double));
        foreach (var (a, b) in rows)
        {
            table.Rows.Add(a, b is null ? DBNull.Value : b.Value);
        }

        return table;
    }

    private TiberoSession NewSession()
    {
        var driver = FakeDriverHost.Driver;
        var connection = driver.CreateConnection("Data Source=x");
        connection.Open();
        var profile = new ConnectionProfile("t", "u", "h:1/d", Dbms: Dbms.Tibero);
        return new TiberoSession(Guid.NewGuid(), profile, Server, SessionPurpose.Work, "D/-/U", connection, null, _bus, driver: driver);
    }

    private void Serve(Func<DbCommand, object?> respond) =>
        FakeDriverHost.SetHandler(cmd =>
        {
            lock (_sent)
            {
                _sent.Add(cmd);
            }

            return respond(cmd);
        });
}
