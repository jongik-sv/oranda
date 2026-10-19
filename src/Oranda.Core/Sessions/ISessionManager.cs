using Oranda.Core.Connection;
using Oranda.Core.Security;

namespace Oranda.Core.Sessions;

/// <summary>세션 열기·목록(TRD 4.1, 6장). 구현: Oranda.Oracle(TSK-03-03).</summary>
public interface ISessionManager
{
    /// <summary>
    /// 물리 연결 1개를 연다(Pooling=false). 지원하지 않는 서버면 <see cref="UnsupportedServerException"/>.
    /// 성공하면 <c>SessionOpened</c> 이벤트를 낸다.
    /// </summary>
    Task<ISession> OpenAsync(ConnectionProfile profile, SecureCredential credential,
                             SessionPurpose purpose, CancellationToken ct);

    IReadOnlyList<ISession> Sessions { get; }
}
