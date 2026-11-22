using Oranda.App.Features.ConnectionManager;
using Oranda.Core.Connection;
using Oranda.Core.Sessions;
using Oranda.Core.Storage;

namespace Oranda.App.Tests.Features.AiSetup.Access;

/// <summary>접속 서비스 가짜: 목록 읽기·저장(비밀번호 인자 기록)만 한다.</summary>
internal sealed class FakeConnections : IConnectionService
{
    public List<(string? Password, bool SavePassword)> Calls { get; } = [];

    public List<ConnectionProfile> Profiles { get; } = [];

    /// <summary>저장이 실패한다(저장 실패 경로).</summary>
    public bool FailSave { get; set; }

    public Task<ConnectionProfile> SaveProfileAsync(ConnectionProfile profile, string? password, bool savePassword, CancellationToken ct)
    {
        Calls.Add((password, savePassword));
        if (FailSave)
        {
            throw new IOException("disk full (test)");
        }

        var index = Profiles.FindIndex(p => p.Id == profile.Id);
        if (index >= 0)
        {
            Profiles[index] = profile;
        }

        return Task.FromResult(profile);
    }

    public ISession? ActiveSession => null;

    public IReadOnlyList<ISession> Sessions => [];

    public ConnectionProfile? LastProfile => null;

    public bool IsBroken => false;

    public event EventHandler? ActiveSessionChanged { add { } remove { } }

    public event EventHandler<ISession>? Connected { add { } remove { } }

    public ISession? SessionOf(Guid profileId) => null; // 시험은 세션을 만들지 않는다

    public bool IsSessionBroken(Guid sessionId) => false;

    public bool Switch(ISession session) => false;

    public Task<StoredDocument<ConnectionsDocument>> LoadAsync(CancellationToken ct) =>
        Task.FromResult(new StoredDocument<ConnectionsDocument>(new ConnectionsDocument { Profiles = [.. Profiles] }));

    public Task DeleteProfileAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();

    public Task<ConnectOutcome> ConnectAsync(ConnectionProfile profile, string? password, CancellationToken ct) => throw new NotSupportedException();

    public Task DisconnectAsync(CancellationToken ct) => throw new NotSupportedException();

    public Task DisconnectAsync(ISession session, CancellationToken ct) => throw new NotSupportedException();

    public Task<ConnectOutcome> ReconnectAsync(string? password, CancellationToken ct) => throw new NotSupportedException();

    public Task<PasswordChangeOutcome> ChangePasswordAsync(ConnectionProfile profile, string? oldPassword, string newPassword, CancellationToken ct) => throw new NotSupportedException();

    public Task<bool> HasStoredPasswordAsync(ConnectionProfile profile, CancellationToken ct) => throw new NotSupportedException();

    public Task<ConnectionProfile> ImportWalletAsync(string zipPath, string? walletAlias, CancellationToken ct) => throw new NotSupportedException();

    public Task<ConnectionTestOutcome> TestConnectionAsync(ConnectionProfile profile, string? password, CancellationToken ct) => throw new NotSupportedException();

    public Task<ConnectionProfile> DuplicateProfileAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();

    public Task UpdateFoldersAsync(Func<ConnectionsDocument, ConnectionsDocument> edit, CancellationToken ct) => throw new NotSupportedException();
}
