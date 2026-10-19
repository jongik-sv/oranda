using Oranda.Core.Connection;
using Oranda.Core.Sessions;

namespace Oranda.Testing;

/// <summary>DB 없는 <see cref="ISession"/>. 상태·트랜잭션 여부를 시험이 직접 바꾼다.</summary>
public sealed class FakeSession : ISession
{
    public FakeSession(ConnectionProfile? profile = null, ServerInfo? server = null, SessionPurpose purpose = SessionPurpose.Work)
    {
        Profile = profile ?? TestData.Profile();
        Server = server ?? TestData.Server();
        Purpose = purpose;
    }

    public Guid Id { get; } = Guid.NewGuid();

    public ConnectionProfile Profile { get; }

    public ServerInfo Server { get; }

    public SessionPurpose Purpose { get; }

    public string DbKey { get; set; } = "FREE/FREEPDB1/SCOTT";

    public SessionState State { get; set; } = SessionState.Idle;

    public bool HasPendingTransaction { get; set; }

    public int CommitCount { get; private set; }

    public int RollbackCount { get; private set; }

    public Task CommitAsync(CancellationToken ct)
    {
        CommitCount++;
        HasPendingTransaction = false;
        return Task.CompletedTask;
    }

    public Task RollbackAsync(CancellationToken ct)
    {
        RollbackCount++;
        HasPendingTransaction = false;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        State = SessionState.Closed;
        return ValueTask.CompletedTask;
    }
}
