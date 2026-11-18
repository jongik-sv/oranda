using Oranda.Core.Composition;
using Oranda.Core.Security;
using Oranda.Platform.Credentials;

namespace Oranda.Platform.Keyring;

/// <summary>
/// Linux 비밀번호 저장소(data-model 5.1): 데스크톱 키링(Secret Service — GNOME Keyring·KWallet)에 libsecret 으로 둔다.
/// 처음 쓸 때 한 번 키링을 확인하고, libsecret·세션 버스·키링이 없으면(헤드리스, CI) 예외 없이
/// <see cref="VolatileCredentialStore"/> 로 대신한다(앱이 도는 동안만, <see cref="IsPersistent"/> = false).
/// 비밀·키는 로그·예외 메시지에 쓰지 않는다. 키링이 잠겨 있으면 첫 저장·읽기 때 데스크톱이 잠금 해제 창을 띄울 수 있다.
/// </summary>
[OrandaService(typeof(ICredentialStore), Platform = ServicePlatform.Linux)]
public sealed class KeyringPasswordStore : ICredentialStore, IPersistenceProbe
{
    internal const string LibraryName = "libsecret-1.so.0";

    private readonly Lazy<KeyringNative?> _keyring;
    private readonly VolatileCredentialStore _memory = new();

    public KeyringPasswordStore()
        : this(LibraryName, () => Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS"))
    {
    }

    /// <summary>시험용: 라이브러리 이름과 세션 버스 주소를 바꿔 「키링 없음」 경로를 만든다.</summary>
    internal KeyringPasswordStore(string libraryName, Func<string?> sessionBus)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryName);
        ArgumentNullException.ThrowIfNull(sessionBus);
        _keyring = new Lazy<KeyringNative?>(() => KeyringNative.TryOpen(libraryName, sessionBus()), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>키링에 저장하면 true, 메모리로 대신하면 false.</summary>
    public bool IsPersistent => _keyring.Value is not null;

    public Task SaveAsync(string key, ReadOnlyMemory<byte> secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (_keyring.Value is not { } keyring)
        {
            return _memory.SaveAsync(key, secret);
        }

        return Task.Run(() => keyring.Store(key, secret.Span));
    }

    public Task<byte[]?> LoadAsync(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (_keyring.Value is not { } keyring)
        {
            return _memory.LoadAsync(key);
        }

        return Task.Run(() => keyring.Lookup(key));
    }

    public Task DeleteAsync(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (_keyring.Value is not { } keyring)
        {
            return _memory.DeleteAsync(key);
        }

        return Task.Run(() => keyring.Clear(key));
    }
}
