namespace Oranda.Core.Security;

/// <summary>
/// 비밀 저장소(TRD 4.12, data-model 5). 키 = <c>ConnectionProfile.CredentialRef</c>(<c>oranda:conn:{guid}</c>).
/// 구현(Oranda.Platform): Windows DPAPI 파일 / macOS 키체인(TSK-03-02) / Linux 데스크톱 키링(libsecret, <c>KeyringPasswordStore</c> —
/// 키링이 없으면 앱이 도는 동안만 메모리에 둔다) / 그 밖의 OS 는 메모리 휘발(<c>VolatileCredentialStore</c>).
/// </summary>
public interface ICredentialStore
{
    Task SaveAsync(string key, ReadOnlyMemory<byte> secret);

    /// <summary>없으면 null.</summary>
    Task<byte[]?> LoadAsync(string key);

    /// <summary>없는 키를 지워도 오류가 아니다.</summary>
    Task DeleteAsync(string key);
}
