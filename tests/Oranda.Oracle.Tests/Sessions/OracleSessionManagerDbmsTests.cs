using Oranda.Core.Connection;
using Oranda.Core.Security;
using Oranda.Core.Sessions;
using Oranda.Oracle.Sessions;

namespace Oranda.Oracle.Tests.Sessions;

// 소유: Tibero T1 O5. OracleSessionManager 는 Oracle 프로필만 연다 — 다른 DBMS 프로필은 연결을 만들기 전에 거부한다. DB 없음.
public class OracleSessionManagerDbmsTests
{
    [Fact]
    public async Task OpenAsync_rejects_a_tibero_profile_before_touching_the_connection()
    {
        var manager = new OracleSessionManager(null!);
        var profile = new ConnectionProfile("t", "scott", "h:1521/s") { Dbms = Dbms.Tibero };
        using var credential = new SecureCredential("tiger");

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => manager.OpenAsync(profile, credential, SessionPurpose.Work, CancellationToken.None));

        Assert.Equal("profile", ex.ParamName);
        Assert.Empty(manager.Sessions);
    }
}
