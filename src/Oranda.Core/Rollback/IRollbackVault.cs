namespace Oranda.Core.Rollback;

/// <summary>
/// 원복 파일 암호화(설계 12.2, RD-9): AES-256-GCM, 키는 <c>ICredentialStore</c> 의 <c>oranda:rollback:key:v1</c>.
/// 구현: Oranda.Platform <c>RollbackVault</c>(RB-20).
/// </summary>
public interface IRollbackVault
{
    /// <summary>재시작해도 키가 남는가. 휘발 저장소(<c>VolatileCredentialStore</c>)면 false 이고 원복 스크립트를 만들지 않는다.</summary>
    bool IsDurable { get; }

    Task<byte[]> ProtectAsync(ReadOnlyMemory<byte> plain, ReadOnlyMemory<byte> associated, CancellationToken ct);

    /// <summary>키가 없거나 변조되면 System.Security.Cryptography.CryptographicException.</summary>
    Task<byte[]> UnprotectAsync(ReadOnlyMemory<byte> cipher, ReadOnlyMemory<byte> associated, CancellationToken ct);
}
