using System.Security.Cryptography;
using Oranda.Core.Composition;
using Oranda.Core.Security;

namespace Oranda.Platform.Credentials;

/// <summary>
/// OS 비밀 저장소가 없는 플랫폼(Linux 등, 정식 지원 밖 — PRD 2)의 대체: 앱이 도는 동안 메모리에만 둔다.
/// 디스크에 평문을 쓰지 않으므로 「비밀번호 저장」 은 재시작하면 사라지고 다시 묻는다. 이 저장소가 없으면 접속 명령이 서비스 없음으로 죽는다.
/// </summary>
[OrandaService(typeof(ICredentialStore), Platform = ServicePlatform.Other)]
public sealed class VolatileCredentialStore : ICredentialStore
{
    private readonly Dictionary<string, byte[]> _items = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public Task SaveAsync(string key, ReadOnlyMemory<byte> secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        lock (_gate)
        {
            if (_items.Remove(key, out var old))
            {
                CryptographicOperations.ZeroMemory(old);
            }

            _items[key] = secret.ToArray();
        }

        return Task.CompletedTask;
    }

    public Task<byte[]?> LoadAsync(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        lock (_gate)
        {
            return Task.FromResult(_items.TryGetValue(key, out var value) ? (byte[]?)value.ToArray() : null);
        }
    }

    public Task DeleteAsync(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        lock (_gate)
        {
            if (_items.Remove(key, out var old))
            {
                CryptographicOperations.ZeroMemory(old);
            }
        }

        return Task.CompletedTask;
    }
}
