namespace Oranda.Core.Security;

/// <summary>
/// 비밀 저장소(TRD 4.12, data-model 5). 키 = <c>ConnectionProfile.CredentialRef</c>(<c>oranda:conn:{guid}</c>).
/// 구현: Windows DPAPI 파일 / macOS 키체인(Oranda.Platform, TSK-03-02).
/// </summary>
public interface ICredentialStore
{
    Task SaveAsync(string key, ReadOnlyMemory<byte> secret);

    /// <summary>없으면 null.</summary>
    Task<byte[]?> LoadAsync(string key);

    /// <summary>없는 키를 지워도 오류가 아니다.</summary>
    Task DeleteAsync(string key);
}
