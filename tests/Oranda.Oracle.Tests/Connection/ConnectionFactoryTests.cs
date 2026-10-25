using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Connection;
using Oranda.Oracle.Connection;

namespace Oranda.Oracle.Tests.Connection;

/// <summary>제품 연결은 서버가 모르는 PC 시간대 지역 이름 대신 시차를 보내도록 만든다(11.2 XE 의 ORA-01882 접속 거부).</summary>
public sealed class ConnectionFactoryTests
{
    [Fact]
    public void Profile_connection_uses_hour_offset_for_an_unsupported_time_zone_and_keeps_the_profile_settings()
    {
        var profile = new ConnectionProfile("o", "scott", "db:1521/XE") { Auth = AuthMode.SysDba };

        using var conn = profile.CreateConnection("tiger");

        Assert.True(conn.UseHourOffsetForUnsupportedTimezone);
        Assert.Equal(System.Data.ConnectionState.Closed, conn.State);
        var cs = new OracleConnectionStringBuilder(profile.BuildConnectionString("tiger"));
        Assert.Equal("db:1521/XE", cs.DataSource);
        Assert.Equal("SYSDBA", cs.DBAPrivilege);
        Assert.False(cs.Pooling);
    }

    [Fact]
    public void Raw_connection_string_gets_the_same_time_zone_rule()
    {
        using var conn = ConnectionProfileExtensions.CreateConnection("Data Source=db:1521/XE;User Id=system;Password=x");

        Assert.True(conn.UseHourOffsetForUnsupportedTimezone);
        Assert.Contains("db:1521/XE", conn.ConnectionString, StringComparison.Ordinal);
    }

    [Fact]
    public void Null_profile_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => ((ConnectionProfile)null!).CreateConnection("x"));
    }
}
