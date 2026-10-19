using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Security;
using Oranda.Core.Sessions;

namespace Oranda.Oracle.Sessions;

/// <summary>ODP.NET 세션 관리자(작업·전용 세션, Pooling=false, 지원 버전 관문, Keep Alive, 끊김 감지). 소유: TSK-03-03. 빈 틀 — 기능 Task 가 구현한다.</summary>
[OrandaService(typeof(ISessionManager))]
internal sealed class OracleSessionManager : ISessionManager
{
    public IReadOnlyList<ISession> Sessions
        => throw new NotImplementedException("TSK-03-03");

    public Task<ISession> OpenAsync(ConnectionProfile profile, SecureCredential credential, SessionPurpose purpose, CancellationToken ct)
        => throw new NotImplementedException("TSK-03-03");
}
