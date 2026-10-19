using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Connection;
using Oranda.Oracle.Connection;

namespace Oranda.Oracle.IntegrationTests.Connection;

// DB 없이 도는 단위 시험이다(Category 표시 없음). 실제 DB 시험은 [Trait("Category", "Integration")] 를 붙인다.

public class ConnectionProfileTests
{
    [Fact]
    public void Builds_connection_string_for_easy_connect_plus()
    {
        var profile = new ConnectionProfile("dev", "scott", "tcps://db.example.com:1522/svc?retry_count=3");

        var parsed = new OracleConnectionStringBuilder(profile.BuildConnectionString("tiger"));

        Assert.Equal("tcps://db.example.com:1522/svc?retry_count=3", parsed.DataSource);
        Assert.Equal("scott", parsed.UserID);
        Assert.Equal("tiger", parsed.Password);
        Assert.False(parsed.Pooling);
    }

    [Theory]
    [InlineData(AuthMode.SysDba, "SYSDBA")]
    [InlineData(AuthMode.SysOper, "SYSOPER")]
    public void Sets_dba_privilege_only_for_sys_modes(AuthMode mode, string expected)
    {
        var profile = new ConnectionProfile("sys", "sys", "host:1521/svc", mode);

        var parsed = new OracleConnectionStringBuilder(profile.BuildConnectionString("pw"));

        Assert.Equal(expected, parsed.DBAPrivilege);
    }

    [Fact]
    public void Default_mode_has_no_dba_privilege()
    {
        var profile = new ConnectionProfile("dev", "scott", "host:1521/svc");

        var parsed = new OracleConnectionStringBuilder(profile.BuildConnectionString("tiger"));

        Assert.True(string.IsNullOrEmpty(parsed.DBAPrivilege));
    }
}
