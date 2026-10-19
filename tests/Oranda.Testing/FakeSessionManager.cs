using Oranda.Core.Connection;
using Oranda.Core.Security;
using Oranda.Core.Sessions;

namespace Oranda.Testing;

/// <summary>열 때마다 <see cref="FakeSession"/> 을 만드는 세션 관리자.</summary>
public sealed class FakeSessionManager : ISessionManager
{
    private readonly List<ISession> _sessions = [];

    public IReadOnlyList<ISession> Sessions => _sessions;

    /// <summary>설정하면 열 때 이 예외를 던진다(예: <see cref="UnsupportedServerException"/>).</summary>
    public Exception? OpenFailure { get; set; }

    public ServerInfo Server { get; set; } = TestData.Server();

    public Task<ISession> OpenAsync(ConnectionProfile profile, SecureCredential credential, SessionPurpose purpose, CancellationToken ct)
    {
        if (OpenFailure is not null)
        {
            return Task.FromException<ISession>(OpenFailure);
        }

        var session = new FakeSession(profile, Server, purpose);
        _sessions.Add(session);
        return Task.FromResult<ISession>(session);
    }
}
