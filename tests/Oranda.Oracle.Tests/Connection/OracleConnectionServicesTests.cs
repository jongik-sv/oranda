using Oranda.Core.Connection;
using Oranda.Core.Schema;
using Oranda.Oracle.Connection;
using Oranda.Oracle.Schema;
using Oranda.Oracle.Sessions;

namespace Oranda.Oracle.Tests.Connection;

// 소유: Tibero T1 O4. C4 인터페이스의 Oracle 구현이 기존 정적 도우미와 같은 결과를 내는지 본다. DB 없음.
public class OracleConnectionServicesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oranda-o4-" + Guid.NewGuid().ToString("N"));

    public OracleConnectionServicesTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Connection_string_factory_matches_the_profile_extension()
    {
        var profile = new ConnectionProfile("o", "scott", "db:1521/XE") { Auth = AuthMode.SysDba };
        var sut = new OracleConnectionStringFactory();

        Assert.Equal(Dbms.Oracle, sut.Dbms);
        Assert.Equal(profile.BuildConnectionString("tiger"), sut.Build(profile, "tiger"));
    }

    [Fact]
    public void Driver_profile_writes_proxy_user_in_driver_form_and_keeps_the_original_untouched()
    {
        var profile = new ConnectionProfile("p", "scott", "db:1521/XE", ProxyUser: "proxy");

        var driver = OracleDriverProfile.From(profile);

        Assert.Equal("proxy[scott]", driver.User);
        Assert.Null(driver.ProxyUser);
        Assert.Equal("scott", profile.User);
        Assert.Equal("proxy", profile.ProxyUser);
    }

    [Fact]
    public void Driver_profile_expands_a_tns_alias_into_its_descriptor()
    {
        File.WriteAllText(Path.Combine(_dir, "tnsnames.ora"), "ORCL =\n  (DESCRIPTION = (ADDRESS = (PROTOCOL = TCP)(HOST = h)(PORT = 1521)) (CONNECT_DATA = (SERVICE_NAME = s)))\n");
        var profile = new ConnectionProfile("t", "scott", "ORCL", TnsAdmin: _dir);

        var driver = OracleDriverProfile.From(profile);

        Assert.StartsWith("(DESCRIPTION=", driver.DataSource, StringComparison.Ordinal);
        Assert.Null(driver.TnsAdmin);
    }

    [Fact]
    public void Driver_profile_passes_only_the_first_wallet_folder_to_the_driver_and_leaves_the_rest_to_the_descriptor()
    {
        // 접속 시험과 세션 열기가 같은 변환·같은 지갑 상태를 쓴다(A2). 번갈아 다른 지갑을 써도 드라이버 전역 지갑 위치는 한 폴더뿐이다.
        File.WriteAllText(Path.Combine(_dir, "tnsnames.ora"), "ORCL =\n  (DESCRIPTION = (ADDRESS = (PROTOCOL = TCP)(HOST = h)(PORT = 1521)) (CONNECT_DATA = (SERVICE_NAME = s)))\n");
        var walletA = Path.Combine(_dir, "wallet-a-" + Guid.NewGuid().ToString("N"));
        var walletB = Path.Combine(_dir, "wallet-b-" + Guid.NewGuid().ToString("N"));
        var a = new ConnectionProfile("a", "scott", "ORCL", TnsAdmin: _dir, WalletLocation: walletA);
        var b = a with { Name = "b", WalletLocation = walletB };

        var first = OracleDriverProfile.From(a);
        var second = OracleDriverProfile.From(b);
        var again = OracleDriverProfile.From(a);

        Assert.Contains("MY_WALLET_DIRECTORY=" + walletA, first.DataSource, StringComparison.Ordinal);
        Assert.Contains("MY_WALLET_DIRECTORY=" + walletB, second.DataSource, StringComparison.Ordinal);
        Assert.True(first.WalletLocation is null || string.Equals(first.WalletLocation, walletA, StringComparison.Ordinal));
        Assert.Null(second.WalletLocation);
        Assert.Equal(first.WalletLocation, again.WalletLocation);
    }

    [Fact]
    public void Driver_profile_leaves_a_plain_profile_unchanged()
    {
        var profile = new ConnectionProfile("o", "scott", "db:1521/XE");

        Assert.Equal(profile, OracleDriverProfile.From(profile));
    }

    [Theory]
    [InlineData("11.2.0.4.0")]
    [InlineData("19.0.0.0.0")]
    [InlineData("11.1.0.7.0")]
    [InlineData("")]
    [InlineData("abc")]
    public void Version_policy_matches_the_static_policy(string version)
    {
        var sut = new OracleServerVersionPolicy();

        Assert.Equal(SupportedServerPolicy.IsSupported(version, out var expectedReason), sut.IsSupported(version, out var reason));
        Assert.Equal(expectedReason, reason);
        Assert.Equal(Dbms.Oracle, sut.Dbms);
        Assert.Equal(SupportedServerPolicy.MinimumText, sut.MinimumText);
        Assert.Equal(SupportedServerPolicy.TestedVersions, sut.TestedVersions);
    }

    [Fact]
    public void Schema_statements_wrapper_delegates_to_the_static_helper()
    {
        var sut = new OracleSchemaStatements();

        Assert.Equal(Dbms.Oracle, sut.Dbms);
        Assert.Equal(SchemaStatements.ParameterChangeDdl("open_cursors", "300", ParameterScope.Both), sut.ParameterChangeDdl("open_cursors", "300", ParameterScope.Both));
        Assert.Equal(SchemaStatements.PurgeAll().Text, sut.PurgeAll().Text);
    }

    [Fact]
    public void Column_reorder_and_ddl_exporter_wrappers_delegate()
    {
        var reorder = new OracleColumnReorderScripts();
        var exporter = new OracleDdlBulkExporter();

        Assert.Equal(Dbms.Oracle, reorder.Dbms);
        Assert.Equal(ColumnReorderScripts.FirstDifference(["A", "B", "C"], ["A", "C", "B"]), reorder.FirstDifference(["A", "B", "C"], ["A", "C", "B"]));
        Assert.Equal(Dbms.Oracle, exporter.Dbms);
        Assert.Equal(DdlBulkExporter.DefaultTypes, exporter.DefaultTypes);
    }
}
