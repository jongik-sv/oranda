using System.Security.Cryptography;
using Oranda.Core.Composition;
using Oranda.Core.Rollback;
using Oranda.Core.Security;
using Oranda.Platform.Credentials;

namespace Oranda.Platform.Rollback;

/// <summary>
/// 원복 파일 금고(설계 12.2, RD-9). AES-256-GCM, 키는 무작위 32바이트를 <see cref="ICredentialStore"/> 의
/// <c>oranda:rollback:key:v1</c> 에 둔다(Windows DPAPI 파일, macOS 키체인). 키는 처음 암호화할 때 만들고, 푸는 쪽은 만들지 않는다.
/// 결과 바이트 = <c>ORB1</c>(4) · 판 번호(1, 값 1) · nonce(12) · tag(16) · 암호문. 머리·키·AAD·본문이 틀리면 <see cref="CryptographicException"/>.
/// 휘발 저장소(<see cref="VolatileCredentialStore"/>)면 재시작 때 키가 사라지므로 <see cref="IsDurable"/> 이 false 다.
/// 키·평문은 로그·예외 메시지에 쓰지 않는다. 소유: RB-20.
/// </summary>
[OrandaService(typeof(IRollbackVault))]
internal sealed class RollbackVault : IRollbackVault, IDisposable
{
    internal const string KeyName = "oranda:rollback:key:v1";
    internal const byte FormatVersion = 1;

    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private static ReadOnlySpan<byte> Magic => "ORB1"u8;
    private static int HeaderSize => 4 + 1 + NonceSize + TagSize;

    private readonly ICredentialStore _credentials;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private byte[]? _key;

    public RollbackVault(ICredentialStore credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        _credentials = credentials;
    }

    public bool IsDurable => _credentials is not VolatileCredentialStore;

    public void Dispose()
    {
        if (_key is not null)
        {
            CryptographicOperations.ZeroMemory(_key);
            _key = null;
        }

        _gate.Dispose();
    }

    public async Task<byte[]> ProtectAsync(ReadOnlyMemory<byte> plain, ReadOnlyMemory<byte> associated, CancellationToken ct)
    {
        var key = await KeyAsync(create: true, ct).ConfigureAwait(false)
            ?? throw new CryptographicException("원복 키를 만들지 못했습니다.");
        var output = new byte[HeaderSize + plain.Length];
        Magic.CopyTo(output);
        output[4] = FormatVersion;
        var nonce = output.AsSpan(5, NonceSize);
        var tag = output.AsSpan(5 + NonceSize, TagSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plain.Span, output.AsSpan(HeaderSize), tag, associated.Span);
        return output;
    }

    public async Task<byte[]> UnprotectAsync(ReadOnlyMemory<byte> cipher, ReadOnlyMemory<byte> associated, CancellationToken ct)
    {
        if (cipher.Length < HeaderSize || !cipher.Span[..4].SequenceEqual(Magic))
        {
            throw new CryptographicException("원복 파일 형식이 아닙니다.");
        }

        if (cipher.Span[4] != FormatVersion)
        {
            throw new CryptographicException("모르는 원복 파일 판입니다.");
        }

        var key = await KeyAsync(create: false, ct).ConfigureAwait(false)
            ?? throw new CryptographicException("원복 키가 없습니다.");
        var span = cipher.Span;
        var plain = new byte[cipher.Length - HeaderSize];
        using var aes = new AesGcm(key, TagSize);
        // 태그가 맞지 않으면 AuthenticationTagMismatchException(CryptographicException 의 하위 형식)을 던진다.
        aes.Decrypt(span.Slice(5, NonceSize), span[HeaderSize..], span.Slice(5 + NonceSize, TagSize), plain, associated.Span);
        return plain;
    }

    private async Task<byte[]?> KeyAsync(bool create, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_key is not null)
            {
                return _key;
            }

            byte[]? stored;
            try
            {
                stored = await _credentials.LoadAsync(KeyName).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException
                or InvalidOperationException or PlatformNotSupportedException or EntryPointNotFoundException)
            {
                // 다른 사용자·다른 PC 의 DPAPI 파일, 잠긴·거부한 키체인(OSStatus → InvalidOperationException) 등: 기존 키를 읽을 수 없으면 새 키로 덮지 않는다(기존 파일을 영영 잃는다).
                throw new CryptographicException("원복 키를 읽지 못했습니다.", ex);
            }

            if (stored is { Length: KeySize })
            {
                _key = stored;
                return _key;
            }

            if (stored is not null)
            {
                CryptographicOperations.ZeroMemory(stored);
                throw new CryptographicException("원복 키의 길이가 맞지 않습니다.");
            }

            if (!create)
            {
                return null;
            }

            var fresh = RandomNumberGenerator.GetBytes(KeySize);
            await _credentials.SaveAsync(KeyName, fresh).ConfigureAwait(false);
            _key = fresh;
            return _key;
        }
        finally
        {
            _gate.Release();
        }
    }
}
