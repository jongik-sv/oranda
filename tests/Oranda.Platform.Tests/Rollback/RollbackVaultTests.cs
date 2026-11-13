using System.Security.Cryptography;
using System.Text;
using Oranda.Platform.Credentials;
using Oranda.Platform.Rollback;
using Oranda.Platform.Storage;
using Oranda.Testing;

namespace Oranda.Platform.Tests.Rollback;

// 소유: RB-20. 금고(AES-256-GCM, 설계 12.2·RD-9).
public sealed class RollbackVaultTests : IDisposable
{
    private static readonly byte[] Plain = Encoding.UTF8.GetBytes("{\"schemaVersion\":1,\"text\":\"UPDATE emp SET sal = 1 -- 값 ünï\"}");
    private static readonly byte[] Aad = Encoding.UTF8.GetBytes("orb1:7:FREE/FREEPDB1/APP");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "oranda-vault-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task Protect_then_unprotect_round_trips_with_ORB1_header_and_stores_32_byte_key()
    {
        var credentials = new InMemoryCredentialStore();
        using var vault = new RollbackVault(credentials);

        var cipher = await vault.ProtectAsync(Plain, Aad, default);

        Assert.Equal("ORB1"u8.ToArray(), cipher[..4]);
        Assert.Equal(RollbackVault.FormatVersion, cipher[4]);
        Assert.Equal(4 + 1 + 12 + 16 + Plain.Length, cipher.Length);
        Assert.Equal(32, (await credentials.LoadAsync(RollbackVault.KeyName))!.Length);
        Assert.Equal(Plain, await vault.UnprotectAsync(cipher, Aad, default));
        Assert.True(vault.IsDurable);

        // 다른 금고 인스턴스(재시작)도 저장소의 같은 키로 푼다.
        using var again = new RollbackVault(credentials);
        Assert.Equal(Plain, await again.UnprotectAsync(cipher, Aad, default));
    }

    [Fact]
    public async Task Different_associated_data_or_tampered_bytes_fail()
    {
        using var vault = new RollbackVault(new InMemoryCredentialStore());
        var cipher = await vault.ProtectAsync(Plain, Aad, default);

        await Assert.ThrowsAnyAsync<CryptographicException>(
            () => vault.UnprotectAsync(cipher, Encoding.UTF8.GetBytes("orb1:8:FREE/FREEPDB1/APP"), default));

        var tampered = (byte[])cipher.Clone();
        tampered[^1] ^= 0x01;
        await Assert.ThrowsAnyAsync<CryptographicException>(() => vault.UnprotectAsync(tampered, Aad, default));

        var badHeader = (byte[])cipher.Clone();
        badHeader[0] = (byte)'X';
        await Assert.ThrowsAnyAsync<CryptographicException>(() => vault.UnprotectAsync(badHeader, Aad, default));
    }

    [Fact]
    public async Task Missing_key_fails_to_unprotect_and_does_not_create_a_new_key()
    {
        using var writer = new RollbackVault(new InMemoryCredentialStore());
        var cipher = await writer.ProtectAsync(Plain, Aad, default);

        var empty = new InMemoryCredentialStore();
        using var reader = new RollbackVault(empty);

        await Assert.ThrowsAnyAsync<CryptographicException>(() => reader.UnprotectAsync(cipher, Aad, default));
        Assert.Null(await empty.LoadAsync(RollbackVault.KeyName));
    }

    [Fact]
    public async Task Unreadable_existing_key_fails_without_overwriting_it()
    {
        var store = new UnreadableStore();
        using var vault = new RollbackVault(store);

        await Assert.ThrowsAnyAsync<CryptographicException>(() => vault.ProtectAsync(Plain, Aad, default));
        Assert.Equal(0, store.Saves);
    }

    [Fact]
    public void Volatile_credential_store_is_not_durable()
    {
        using var vault = new RollbackVault(new VolatileCredentialStore());

        Assert.False(vault.IsDurable);
    }

    [Fact]
    public async Task Windows_DPAPI_keeps_the_key_across_vault_instances()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var paths = new AppDataPaths(_root);
        byte[] cipher;
        using (var first = new RollbackVault(new DpapiCredentialStore(paths)))
        {
            cipher = await first.ProtectAsync(Plain, Aad, default);
            Assert.True(first.IsDurable);
        }

        // 키 파일은 DPAPI 암호문이다(평문 키가 디스크에 없다).
        var keyFile = Assert.Single(Directory.GetFiles(paths.CredentialsDirectory, "*.bin"));
        Assert.NotEqual(32, new FileInfo(keyFile).Length);

        using var second = new RollbackVault(new DpapiCredentialStore(paths));
        Assert.Equal(Plain, await second.UnprotectAsync(cipher, Aad, default));
    }

    /// <summary>잠긴 키체인처럼 읽기가 InvalidOperationException 을 던지는 저장소.</summary>
    private sealed class UnreadableStore : Oranda.Core.Security.ICredentialStore
    {
        public int Saves { get; private set; }

        public Task SaveAsync(string key, ReadOnlyMemory<byte> secret)
        {
            Saves++;
            return Task.CompletedTask;
        }

        public Task<byte[]?> LoadAsync(string key) => throw new InvalidOperationException("OSStatus -25308");

        public Task DeleteAsync(string key) => Task.CompletedTask;
    }
}
