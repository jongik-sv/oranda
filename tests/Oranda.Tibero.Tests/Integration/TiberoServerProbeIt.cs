using Oranda.Core.Connection;
using Oranda.Core.Sessions;
using Oranda.Tibero.Connection;
using Xunit.Abstractions;

namespace Oranda.Tibero.Tests.Integration;

/// <summary>세션을 열 때 관리자가 채운 ServerInfo·DbKey 를 실제 서버로 확인한다(읽기 문장만). 서버·드라이버가 없으면 아무것도 하지 않는다.</summary>
[Trait("Category", "Integration")]
public class TiberoServerProbeIt(ITestOutputHelper output)
{
    [TiberoItFact]
    public async Task Open_reads_the_session_identity_and_capabilities()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var session = await it.OpenAsync(SessionPurpose.Monitor);
        var info = session.Server;

        Assert.Equal(Dbms.Tibero, info.Dbms);
        Assert.True(info.MajorVersion >= 6, info.Version);
        Assert.True(info.SessionId > 0);
        // 접속 정보가 시험 로그에 실리지 않게 참·거짓만 본다(Assert.Equal 은 실패 때 두 값을 찍는다).
        Assert.True(TiberoItSessions.Same(it.Env.DbName, info.DatabaseName, ignoreCase: true));
        Assert.True(TiberoItSessions.Same(it.Env.User, info.SessionUser, ignoreCase: true));
        Assert.True(TiberoItSessions.Same(TiberoServerProbe.BuildDbKey(info), session.DbKey));
        Assert.True(info.Capabilities.CanQueryPerformanceViews); // 일반 계정도 V$SESSION 을 읽는다(실측)
    }

    /// <summary>
    /// 두 번째 계정(<c>ORANDA_IT_TIBERO_USER2</c>·<c>PASSWORD2</c>, 없으면 건너뜀)의 접속 시험이 V$VERSION 을 볼 수 있든 없든 통과하는지 본다.
    /// V$VERSION 을 볼 수 있는지·안내가 붙었는지는 단언하지 않고 출력만 한다(계정 권한에 따라 다르다).
    /// </summary>
    [TiberoItFact]
    public async Task Connection_probe_passes_for_the_second_account_whether_or_not_V_VERSION_is_visible()
    {
        var env = new TiberoItEnv();
        var user2 = Environment.GetEnvironmentVariable("ORANDA_IT_TIBERO_USER2");
        var password2 = Environment.GetEnvironmentVariable("ORANDA_IT_TIBERO_PASSWORD2");
        if (env.Skip() || string.IsNullOrEmpty(user2) || string.IsNullOrEmpty(password2))
        {
            return;
        }

        var profile = new ConnectionProfile("it2", user2, $"{env.Host}:{env.Port}/{env.DbName}", Dbms: Dbms.Tibero);
        var result = await new TiberoConnectionProbe().ProbeAsync(profile, password2, CancellationToken.None);
        Assert.True(result.Succeeded, result.Message);

        // 접속 정보가 시험 로그에 실리지 않게 버전·안내 여부만 찍는다.
        output.WriteLine($"probe version={result.ServerVersion ?? "(none)"}, notice={(result.Message is null ? "none" : "V$VERSION not visible")}");
    }
}
