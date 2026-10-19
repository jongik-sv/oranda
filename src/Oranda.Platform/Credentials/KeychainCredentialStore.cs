using Oranda.Core.Composition;
using Oranda.Core.Security;

namespace Oranda.Platform.Credentials;

/// <summary>macOS 키체인(Security.framework P/Invoke) 저장소. 소유: TSK-03-02. 빈 틀 — 기능 Task 가 구현한다.</summary>
[OrandaService(typeof(ICredentialStore), Platform = ServicePlatform.MacOS)]
internal sealed class KeychainCredentialStore : ICredentialStore
{
    public Task SaveAsync(string key, ReadOnlyMemory<byte> secret)
        => throw new NotImplementedException("TSK-03-02");

    public Task<byte[]?> LoadAsync(string key)
        => throw new NotImplementedException("TSK-03-02");

    public Task DeleteAsync(string key)
        => throw new NotImplementedException("TSK-03-02");
}
