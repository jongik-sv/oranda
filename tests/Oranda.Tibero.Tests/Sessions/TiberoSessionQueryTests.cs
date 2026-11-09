using System.Globalization;
using Oranda.Core.Connection;
using Oranda.Core.Sessions;
using Oranda.Tibero.Sessions;
using Oranda.Tibero.Tests.Integration;

namespace Oranda.Tibero.Tests.Sessions;

public class TiberoSessionQueryTests
{
    [Fact]
    public async Task A_session_of_another_provider_is_rejected()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => TiberoSessionQuery.RunAsync(new OtherSession(), "SELECT 1 FROM DUAL"));
    }

    [Fact]
    public void Converters_handle_null_and_numbers()
    {
        Assert.Null(TiberoSessionQuery.Str(null));
        Assert.Equal("12", TiberoSessionQuery.Str(12m));
        Assert.Equal(7L, TiberoSessionQuery.Long(7m));
        Assert.Null(TiberoSessionQuery.Long(null));
        Assert.Equal(new DateTime(2026, 11, 9), TiberoSessionQuery.Date(new DateTime(2026, 11, 9)));
        Assert.Null(TiberoSessionQuery.Date("2026-11-09"));
    }

    [Fact]
    public void View_prefix_follows_the_dictionary_capability()
    {
        Assert.Equal("all", TiberoSessionQuery.ViewPrefix(new OtherSession()));
        Assert.Equal("dba", TiberoSessionQuery.ViewPrefix(new OtherSession(dictionary: true)));
    }

    private sealed class OtherSession(bool dictionary = false) : ISession
    {
        public Guid Id { get; } = Guid.NewGuid();

        public ConnectionProfile Profile { get; } = new("t", "u", "h:1/d", Dbms: Dbms.Tibero);

        public ServerInfo Server { get; } = new("6", null, null, null, null, null, false, 1,
            new ServerCapabilities(false, dictionary, false, false, false, Dbms.Tibero), Dbms: Dbms.Tibero);

        public SessionPurpose Purpose => SessionPurpose.Work;

        public string DbKey => "D/-/U";

        public SessionState State => SessionState.Idle;

        public bool HasPendingTransaction => false;

        public Task CommitAsync(CancellationToken ct) => Task.CompletedTask;

        public Task RollbackAsync(CancellationToken ct) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

[Trait("Category", "Integration")]
public class TiberoSessionQueryIt
{
    [TiberoItFact]
    public async Task Run_reads_rows_with_named_binds_and_releases_the_gate()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var session = await it.OpenAsync();

        var table = await TiberoSessionQuery.RunAsync(session,
            "SELECT SID, :tag AS TAG FROM V$MYSTAT WHERE ROWNUM = 1 AND SID = :sid",
            cmd =>
            {
                TiberoSessionQuery.Bind(cmd, "sid", session.Server.SessionId);
                TiberoSessionQuery.Bind(cmd, "tag", "oranda");
            });

        Assert.Equal(["SID", "TAG"], table.Columns);
        var row = Assert.Single(table.Rows);
        Assert.Equal(session.Server.SessionId, TiberoSessionQuery.Long(row[0]));
        Assert.Equal("oranda", TiberoSessionQuery.Str(row[1]));
        Assert.False(session.CommandGate.IsHeld);

        var count = await TiberoSessionQuery.ScalarAsync(session, "SELECT COUNT(*) FROM DUAL");
        Assert.Equal("1", Convert.ToString(count, CultureInfo.InvariantCulture));

        var limited = await TiberoSessionQuery.RunAsync(session, "SELECT LEVEL FROM DUAL CONNECT BY LEVEL <= 10", maxRows: 3);
        Assert.Equal(3, limited.Rows.Count);

        // 서버 오류는 그대로 던지고 잠금은 놓는다(세션은 살아 있다).
        await Assert.ThrowsAnyAsync<Exception>(() => TiberoSessionQuery.RunAsync(session, "SELECT * FROM ORT_NO_SUCH_TABLE_XYZ"));
        Assert.False(session.CommandGate.IsHeld);
        Assert.Equal(SessionState.Idle, session.State);
    }
}
