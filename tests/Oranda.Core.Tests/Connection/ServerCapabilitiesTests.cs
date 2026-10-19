using Oranda.Core.Connection;

namespace Oranda.Core.Tests.Connection;

public class ServerCapabilitiesTests
{
    private static HashSet<string> Set(params string[] values) => new(values, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Dba_role_can_query_dictionary()
    {
        var caps = ServerCapabilities.From(Set(), Set("DBA"), canReadVSession: true, canExecuteDbmsMonitor: true);

        Assert.True(caps.IsDba);
        Assert.True(caps.CanQueryDictionary);
    }

    [Fact]
    public void Select_catalog_role_is_enough_for_dictionary_but_not_kill()
    {
        var caps = ServerCapabilities.From(Set(), Set("SELECT_CATALOG_ROLE"), canReadVSession: true, canExecuteDbmsMonitor: false);

        Assert.False(caps.IsDba);
        Assert.True(caps.CanQueryDictionary);
        Assert.False(caps.CanKillSession);
        Assert.False(caps.CanTraceSessions);
    }

    [Fact]
    public void Kill_needs_alter_system()
    {
        var caps = ServerCapabilities.From(Set("ALTER SYSTEM"), Set(), canReadVSession: false, canExecuteDbmsMonitor: false);

        Assert.True(caps.CanKillSession);
        Assert.False(caps.CanQueryPerformanceViews);
    }

    [Fact]
    public void Plain_user_gets_nothing()
    {
        var caps = ServerCapabilities.From(Set("CREATE SESSION"), Set("CONNECT"), canReadVSession: false, canExecuteDbmsMonitor: false);

        Assert.Equal(new ServerCapabilities(false, false, false, false, false), caps);
    }

    [Theory]
    [InlineData("19.0.0.0.0", 19, true)]
    [InlineData("11.2.0.4.0", 11, false)]
    public void Detects_major_version(string version, int major, bool atLeast12c)
    {
        var info = new ServerInfo(version, null, null, null, null, null, false, 0,
            new ServerCapabilities(false, false, false, false, false));

        Assert.Equal(major, info.MajorVersion);
        Assert.Equal(atLeast12c, info.IsAtLeast12c);
    }
}
