using Oranda.Core.Connection;
using Oranda.Core.Sessions;
using Oranda.Tibero.Connection;

namespace Oranda.Tibero.Tests.Integration;

/// <summary>세션을 열 때 관리자가 채운 ServerInfo·DbKey 를 실제 서버로 확인한다(읽기 문장만). 서버·드라이버가 없으면 아무것도 하지 않는다.</summary>
[Trait("Category", "Integration")]
public class TiberoServerProbeIt
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
}
