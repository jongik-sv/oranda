using Oranda.Core.Connection;
using Oranda.Tibero.Connection;

namespace Oranda.Tibero.Tests.Connection;

public class TiberoServerProbeTests
{
    // 6.2.15 실측 모양(docs/design/tibero-driver-session.md 3·4장). 이름은 가짜.
    private static TiberoProbeRows Measured() => new()
    {
        Sid = "532",
        Identity = ["TBDB", "dbhost1", "TBDB", "SCOTT"],
        Version = [("PRODUCT_MAJOR", "6"), ("PRODUCT_MINOR", " "), ("BUILD_NUMBER", "302172"), ("STABLE_VERSION", "FS06_CS_1902")],
        DriverServerVersion = "6",
        Privileges = new HashSet<string>(["CREATE SESSION", "CREATE TABLE", "SELECT ANY TABLE"], StringComparer.OrdinalIgnoreCase),
        Roles = new HashSet<string>(["CONNECT", "RESOURCE"], StringComparer.OrdinalIgnoreCase),
        CanReadVSession = true,
        ClusterDatabase = "NO",
    };

    [Fact]
    public void Assemble_builds_the_server_info_from_the_measured_rows()
    {
        var info = TiberoServerProbe.Assemble(Measured());

        Assert.Equal(Dbms.Tibero, info.Dbms);
        Assert.Equal("6", info.Version);
        Assert.Equal(6, info.MajorVersion);
        Assert.Equal(532, info.SessionId);
        Assert.Equal("TBDB", info.InstanceName);
        Assert.Equal("dbhost1", info.HostName);
        Assert.Equal("TBDB", info.DatabaseName);
        Assert.Equal("TBDB", info.DbUniqueName); // DB_UNIQUE_NAME 자리에 DB_NAME
        Assert.Equal("SCOTT", info.SessionUser);
        Assert.Null(info.ServiceName);
        Assert.Null(info.ContainerName);
        Assert.False(info.IsMultitenant);
        Assert.False(info.IsRac);
        Assert.False(info.IsAtLeast12c);
        Assert.Equal(Dbms.Tibero, info.Capabilities.Dbms);
        Assert.False(info.Capabilities.IsDba);
        Assert.True(info.Capabilities.CanQueryPerformanceViews);
        Assert.False(info.Capabilities.CanKillSession);
        Assert.False(info.Capabilities.CanTraceSessions);
    }

    [Fact]
    public void Assemble_reads_dba_roles_and_cluster()
    {
        var rows = Measured() with
        {
            Roles = new HashSet<string>(["DBA"], StringComparer.OrdinalIgnoreCase),
            Privileges = new HashSet<string>(["ALTER SYSTEM"], StringComparer.OrdinalIgnoreCase),
            ClusterDatabase = "YES",
        };

        var info = TiberoServerProbe.Assemble(rows);

        Assert.True(info.Capabilities.IsDba);
        Assert.True(info.Capabilities.CanQueryDictionary);
        Assert.True(info.Capabilities.CanKillSession);
        Assert.False(info.Capabilities.CanTraceSessions); // DBMS_MONITOR 없음
        Assert.True(info.IsRac);
    }

    [Fact]
    public void Assemble_tolerates_missing_rows()
    {
        var info = TiberoServerProbe.Assemble(new TiberoProbeRows { DriverServerVersion = "7" });

        Assert.Equal("7", info.Version);
        Assert.Equal(0, info.SessionId);
        Assert.Null(info.DatabaseName);
        Assert.Null(info.SessionUser);
        Assert.False(info.Capabilities.CanQueryPerformanceViews);
        Assert.Equal("0", TiberoServerProbe.Assemble(new TiberoProbeRows()).Version);
    }

    [Fact]
    public void Version_rows_win_over_the_driver_version()
    {
        var info = TiberoServerProbe.Assemble(Measured() with { Version = [("PRODUCT_MAJOR", "7"), ("PRODUCT_MINOR", "2")] });
        Assert.Equal("7.2", info.Version);
    }

    [Fact]
    public void DbKey_uses_db_name_dash_and_user_in_upper_case()
    {
        Assert.Equal("TBDB/-/SCOTT", TiberoServerProbe.BuildDbKey(TiberoServerProbe.Assemble(Measured() with { Identity = ["i", "h", "tbdb", "scott"] })));
        Assert.Equal("?/-/?", TiberoServerProbe.BuildDbKey(TiberoServerProbe.Assemble(new TiberoProbeRows())));
    }
}
