using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Oranda.Core.Composition;
using Oranda.Core.Security;
using Oranda.Core.Storage;

namespace Oranda.Platform.Credentials;

/// <summary>
/// Windows DPAPI(CurrentUser) 파일 저장소(data-model 5.1). 소유: TSK-03-02.
/// 파일 = <c>{CredentialsDirectory}/{SHA-256(key) 소문자 hex}.bin</c>, 내용 = DPAPI 암호문(엔트로피 상수 포함).
/// 비밀·키는 로그·예외 메시지에 쓰지 않는다.
/// </summary>
[OrandaService(typeof(ICredentialStore), Platform = ServicePlatform.Windows)]
[SupportedOSPlatform("windows")]
public sealed class DpapiCredentialStore : ICredentialStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Oranda.Credential.v1");

    private readonly string _directory;

    public DpapiCredentialStore(IAppDataPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _directory = paths.CredentialsDirectory;
    }

    public Task SaveAsync(string key, ReadOnlyMemory<byte> secret)
    {
        EnsureWindows();
        string path = PathFor(key);
        byte[] plain = secret.ToArray();
        byte[] cipher;
        try
        {
            cipher = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }

        Directory.CreateDirectory(_directory);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temp, cipher);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }

        return Task.CompletedTask;
    }

    public Task<byte[]?> LoadAsync(string key)
    {
        EnsureWindows();
        string path = PathFor(key);
        if (!File.Exists(path))
        {
            return Task.FromResult<byte[]?>(null);
        }

        byte[] cipher = File.ReadAllBytes(path);
        byte[] plain = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);
        return Task.FromResult<byte[]?>(plain);
    }

    public Task DeleteAsync(string key)
    {
        EnsureWindows();
        string path = PathFor(key);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("DPAPI 는 Windows 에서만 쓸 수 있다.");
        }
    }

    private string PathFor(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        string hex = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        return Path.Combine(_directory, hex + ".bin");
    }
}
