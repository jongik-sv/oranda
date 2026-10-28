using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Connection;
using Oranda.Oracle.Connection;

namespace Oranda.Oracle.Tests.Connection;

/// <summary>
/// TNS 디렉터리·지갑 위치는 접속마다 연결 문자열로 넘긴다. ODP.NET 의 <see cref="OracleConfiguration.TnsAdmin"/> 은 한 번이라도 접속을 시도한 뒤에는
/// 같은 값이어도 넣는 순간 ORA-50099 를 던지므로(그 예외로 접속 관리자 「시험」 이 앱을 죽였다), 전역 설정에 기대면 두 번째 TNS 접속부터 깨진다.
/// </summary>
public sealed class TnsAdminPerConnectionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "oranda-tns-" + Guid.NewGuid().ToString("N"));

    public TnsAdminPerConnectionTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Connection_string_carries_the_tns_directory_and_wallet_of_the_profile()
    {
        var profile = new ConnectionProfile("t", "scott", "ALIAS", TnsAdmin: "/opt/tns/", WalletLocation: "/opt/wallet");

        var cs = new OracleConnectionStringBuilder(profile.BuildConnectionString("tiger"));

        Assert.Equal("/opt/tns/", cs["TNS_ADMIN"]);
        Assert.Equal("/opt/wallet", cs["WALLET_LOCATION"]);
    }

    [Fact]
    public void Profile_without_tns_directory_adds_nothing()
    {
        var cs = new OracleConnectionStringBuilder(new ConnectionProfile("t", "scott", "db:1521/svc").BuildConnectionString("tiger"));

        Assert.False(cs.ContainsKey("TNS_ADMIN") && !string.IsNullOrEmpty(cs["TNS_ADMIN"] as string));
        Assert.False(cs.ContainsKey("WALLET_LOCATION") && !string.IsNullOrEmpty(cs["WALLET_LOCATION"] as string));
    }

    [Fact]
    public async Task Each_profile_resolves_its_alias_from_its_own_tns_directory_after_another_connection_was_tried()
    {
        // 닫힌 포트(1·2)를 가리키는 별칭: 별칭을 찾으면 서버 연결 단계(ORA-50201)에서, 못 찾으면 ORA-12154 로 실패한다.
        var a = Tns("a", "FAKEA", 1);
        var b = Tns("b", "FAKEB", 2);
        var first = new ConnectionProfile("a", "u", "FAKEA", TnsAdmin: a, ConnectTimeoutSeconds: 3);
        var second = new ConnectionProfile("b", "u", "FAKEB", TnsAdmin: b, ConnectTimeoutSeconds: 3);

        var e1 = await OpenFailureAsync(first);
        var e2 = await OpenFailureAsync(second); // 앞에서 접속을 시도한 뒤라 전역 TnsAdmin 은 더 바꿀 수 없다

        Assert.DoesNotContain("ORA-12154", e1.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("ORA-12154", e2.Message, StringComparison.Ordinal);
    }

    private string Tns(string folder, string alias, int port)
    {
        var dir = Path.Combine(_root, folder);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "tnsnames.ora"), $"{alias} = (DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST=127.0.0.1)(PORT={port}))(CONNECT_DATA=(SID=X)))\n");
        return dir;
    }

    private static async Task<OracleException> OpenFailureAsync(ConnectionProfile profile)
    {
        await using var conn = profile.CreateConnection("pw");
        return await Assert.ThrowsAsync<OracleException>(() => conn.OpenAsync());
    }
}
