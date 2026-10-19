using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Security;
using Oranda.Core.Sessions;
using Oranda.Oracle.Connection;
using Oranda.Oracle.IntegrationTests.Integration;
using Oranda.Oracle.Sessions;

namespace Oranda.Oracle.IntegrationTests.Sessions;

// 소유: TSK-03-03. 지원 버전 관문(단위)과 세션 관리자(통합).

public class SupportedServerPolicyTests
{
    [Theory]
    [InlineData("19.0.0.0.0")]
    [InlineData("19.25.0.0.0")]
    [InlineData("23.26.0.0.0")]
    [InlineData("23.4")]
    public void Supported_versions_pass(string version)
    {
        Assert.True(SupportedServerPolicy.IsSupported(version, out var reason));
        Assert.Equal(string.Empty, reason);
        SupportedServerPolicy.EnsureSupported(version);
    }

    [Theory]
    [InlineData("18.0.0.0.0", "18")]
    [InlineData("21.3.0.0.0", "21")]
    [InlineData("12.2.0.1.0", "12")]
    [InlineData("11.2.0.4.0", "11")]
    [InlineData("26.1.0.0.0", "26")]
    public void Unsupported_versions_are_rejected_with_version_and_reason(string version, string major)
    {
        Assert.False(SupportedServerPolicy.IsSupported(version, out var reason));
        Assert.Contains(major, reason, StringComparison.Ordinal);
        Assert.Contains("19.x", reason, StringComparison.Ordinal);

        var ex = Assert.Throws<UnsupportedServerException>(() => SupportedServerPolicy.EnsureSupported(version));
        Assert.Equal(version, ex.ServerVersion);
        Assert.Equal(reason, ex.Reason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("abc")]
    [InlineData("-19.0")]
    public void Unreadable_versions_are_rejected(string? version)
    {
        Assert.False(SupportedServerPolicy.IsSupported(version, out var reason));
        Assert.NotEmpty(reason);
    }
}

public class SessionLogicTests
{
    [Theory]
    [InlineData(3113, true)]
    [InlineData(3114, true)]
    [InlineData(12541, true)]
    [InlineData(942, false)]
    [InlineData(1, false)]
    public void Broken_codes(int number, bool expected)
        => Assert.Equal(expected, OracleSession.IsBrokenError(number));

    [Theory]
    [InlineData("Oracle Database 19c Enterprise Edition Release 19.0.0.0.0 - Production", "Enterprise")]
    [InlineData("Oracle Database 23ai Free Release 23.0.0.0.0 - Develop, Learn, and Run for Free", "Free")]
    [InlineData("Oracle Database 19c Standard Edition 2 Release 19.0.0.0.0", "Standard")]
    [InlineData("something else", null)]
    [InlineData(null, null)]
    public void Edition_is_parsed_from_banner(string? banner, string? expected)
        => Assert.Equal(expected, ServerProbe.ParseEdition(banner));

    [Theory]
    [InlineData("OLTP", true)]
    [InlineData("DWCS", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void Autonomous_detected_by_cloud_service(string? cloud, bool expected)
        => Assert.Equal(expected, ServerProbe.IsAutonomousService(cloud));

    [Fact]
    public void Db_key_is_upper_case_triple()
    {
        var info = new ServerInfo("23.0", "i", "h", "s", "db", "freepdb1", true, 1,
            new ServerCapabilities(false, false, false, false, false),
            DbUniqueName: "Free", SessionUser: "scott");

        Assert.Equal("FREE/FREEPDB1/SCOTT", OracleSessionManager.BuildDbKey(info));
    }

    [Fact]
    public void Connection_string_disables_pooling()
    {
        var cs = new ConnectionProfile("p", "u", "h:1521/s").BuildConnectionString("pw");

        Assert.Contains("Pooling=False", cs, StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed class RecordingBus : IEventBus
{
    public List<OrandaEvent> Events { get; } = [];

    public IDisposable Subscribe<TEvent>(object owner, Action<TEvent> handler)
        where TEvent : OrandaEvent
        => throw new NotSupportedException();

    public void Publish<TEvent>(TEvent e)
        where TEvent : OrandaEvent
    {
        lock (Events)
        {
            Events.Add(e);
        }
    }
}

[Trait("Category", "Integration")]
public sealed class SessionManagerIntegrationTests : IClassFixture<OracleFreeFixture>
{
    private readonly OracleFreeFixture _db;

    public SessionManagerIntegrationTests(OracleFreeFixture db) => _db = db;

    [Fact]
    public async Task Open_sets_module_action_client_identifier_and_probes()
    {
        var bus = new RecordingBus();
        var manager = new OracleSessionManager(bus);
        var profile = new ConnectionProfile("free-system", "system", _db.DataSource);
        using var cred = new SecureCredential(_db.SystemPassword);

        await using var session = await manager.OpenAsync(profile, cred, SessionPurpose.Monitor, default);

        Assert.Single(manager.Sessions);
        Assert.Equal(23, session.Server.MajorVersion);
        Assert.False(session.Server.IsAutonomous);
        Assert.False(session.Server.IsRac);
        Assert.NotNull(session.Server.Edition);
        Assert.Equal("SYSTEM", session.Server.SessionUser);
        Assert.EndsWith("/FREEPDB1/SYSTEM", session.DbKey, StringComparison.Ordinal);
        Assert.Contains(bus.Events, e => e is SessionOpened o && o.SessionId == session.Id);

        // 다른 연결에서 v$session 으로 본다.
        await using var other = new OracleConnection(profile.BuildConnectionString(_db.SystemPassword));
        await other.OpenAsync();
        await using var cmd = other.CreateCommand();
        cmd.CommandText = "SELECT module, action, client_identifier FROM v$session WHERE sid = :sid";
        cmd.Parameters.Add("sid", session.Server.SessionId);
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("Oranda", reader.GetString(0));
        Assert.Equal("Monitor", reader.GetString(1));
        Assert.StartsWith("oranda:", reader.GetString(2), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Killed_session_is_detected_as_broken_with_pending_loss()
    {
        var bus = new RecordingBus();
        var manager = new OracleSessionManager(bus);
        var profile = new ConnectionProfile("free-system", "system", _db.DataSource, KeepAliveSeconds: 1);
        using var cred = new SecureCredential(_db.SystemPassword);
        await using var session = (OracleSession)await manager.OpenAsync(profile, cred, SessionPurpose.Work, default);
        session.SetPending(true);

        await using var admin = new OracleConnection(profile.BuildConnectionString(_db.SystemPassword));
        await admin.OpenAsync();
        await using var cmd = admin.CreateCommand();
        cmd.CommandText = "SELECT sid || ',' || serial# FROM v$session WHERE sid = " + session.Server.SessionId;
        var key = (string)(await cmd.ExecuteScalarAsync())!;
        cmd.CommandText = $"ALTER SYSTEM KILL SESSION '{key}' IMMEDIATE";
        await cmd.ExecuteNonQueryAsync();

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (session.State != SessionState.Broken && DateTime.UtcNow < deadline)
        {
            await Task.Delay(250);
        }

        Assert.Equal(SessionState.Broken, session.State);
        var broken = Assert.Single(bus.Events.OfType<SessionBroken>());
        Assert.True(broken.HadPendingTransaction);
    }

    [Fact]
    public async Task Dispose_removes_session_and_publishes_closed()
    {
        var bus = new RecordingBus();
        var manager = new OracleSessionManager(bus);
        var profile = new ConnectionProfile("free-system", "system", _db.DataSource);
        using var cred = new SecureCredential(_db.SystemPassword);
        var session = await manager.OpenAsync(profile, cred, SessionPurpose.Work, default);

        await session.DisposeAsync();

        Assert.Empty(manager.Sessions);
        Assert.Equal(SessionState.Closed, session.State);
        Assert.Contains(bus.Events, e => e is SessionClosed c && c.SessionId == session.Id);
    }
}
