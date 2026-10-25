using System.Runtime.Versioning;
using System.Text;
using Oranda.Core.Composition;
using Oranda.Core.Security;
using Oranda.Core.Storage;
using Oranda.Platform.Credentials;

namespace Oranda.Platform.Tests.Credentials;

// 소유: TSK-03-02. DPAPI·키체인.
public sealed class CredentialStoreTests : IDisposable
{
    private const string Key = "oranda:conn:0b6f3c1e-1111-4222-8333-444455556666";
    private const string Password = "S3cret-p@ss-유니코드-zq9X";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "oranda-cred-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task Windows_save_then_load_returns_same_bytes()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        DpapiCredentialStore store = NewDpapi();
        byte[] secret = Encoding.UTF8.GetBytes(Password);

        await store.SaveAsync(Key, secret);

        Assert.Equal(secret, await store.LoadAsync(Key));
    }

    [Fact]
    public async Task Windows_no_plaintext_anywhere_under_data_folder()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        DpapiCredentialStore store = NewDpapi();
        await store.SaveAsync(Key, Encoding.UTF8.GetBytes(Password));

        byte[][] needles =
        [
            Encoding.UTF8.GetBytes(Password),
            Encoding.Unicode.GetBytes(Password),
            Encoding.UTF8.GetBytes(Convert.ToBase64String(Encoding.UTF8.GetBytes(Password))),
        ];
        string[] files = Directory.GetFiles(_root, "*", SearchOption.AllDirectories);
        Assert.NotEmpty(files);
        foreach (string file in files)
        {
            byte[] content = await File.ReadAllBytesAsync(file);
            foreach (byte[] needle in needles)
            {
                Assert.False(content.AsSpan().IndexOf(needle) >= 0, "평문이 파일에 있다: " + Path.GetFileName(file));
            }

            Assert.DoesNotContain("0b6f3c1e", Path.GetFileName(file));
        }
    }

    [Fact]
    public async Task Windows_overwrite_replaces_value()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        DpapiCredentialStore store = NewDpapi();
        await store.SaveAsync(Key, new byte[] { 1, 2, 3 });
        await store.SaveAsync(Key, new byte[] { 9, 8 });

        Assert.Equal(new byte[] { 9, 8 }, await store.LoadAsync(Key));
        Assert.Single(Directory.GetFiles(_root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Windows_missing_key_loads_null_and_delete_is_idempotent()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        DpapiCredentialStore store = NewDpapi();

        Assert.Null(await store.LoadAsync(Key));
        await store.DeleteAsync(Key);

        await store.SaveAsync(Key, new byte[] { 1 });
        await store.DeleteAsync(Key);
        Assert.Null(await store.LoadAsync(Key));
        await store.DeleteAsync(Key);
    }

    [Fact]
    public async Task Windows_empty_secret_round_trips_and_keys_are_independent()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        DpapiCredentialStore store = NewDpapi();
        await store.SaveAsync(Key, ReadOnlyMemory<byte>.Empty);
        await store.SaveAsync(Key + "2", new byte[] { 7 });

        Assert.Empty((await store.LoadAsync(Key))!);
        Assert.Equal(new byte[] { 7 }, await store.LoadAsync(Key + "2"));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task Blank_key_is_rejected()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        DpapiCredentialStore store = NewDpapi();

        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.SaveAsync(" ", new byte[] { 1 }));
    }

    [Fact]
    public async Task Keychain_store_off_macos_throws_platform_not_supported()
    {
        if (OperatingSystem.IsMacOS())
        {
            return;
        }

        var store = new KeychainCredentialStore();

        await Assert.ThrowsAsync<PlatformNotSupportedException>(() => store.LoadAsync(Key));
        await Assert.ThrowsAsync<PlatformNotSupportedException>(() => store.SaveAsync(Key, new byte[] { 1 }));
        await Assert.ThrowsAsync<PlatformNotSupportedException>(() => store.DeleteAsync(Key));
    }

    [Fact]
    public void Registered_for_the_matching_platform_only()
    {
        OrandaServiceAttribute dpapi = Attribute(typeof(DpapiCredentialStore));
        OrandaServiceAttribute keychain = Attribute(typeof(KeychainCredentialStore));
        OrandaServiceAttribute volatileStore = Attribute(typeof(VolatileCredentialStore));

        Assert.Equal(typeof(ICredentialStore), dpapi.ServiceType);
        Assert.Equal(ServicePlatform.Windows, dpapi.Platform);
        Assert.Equal(typeof(ICredentialStore), keychain.ServiceType);
        Assert.Equal(ServicePlatform.MacOS, keychain.Platform);
        Assert.Equal(typeof(ICredentialStore), volatileStore.ServiceType);
        Assert.Equal(ServicePlatform.Other, volatileStore.Platform);
    }

    // 대체 저장소(Linux 등): 메모리에만, 앱이 도는 동안만.
    [Fact]
    public async Task Volatile_store_round_trips_overwrites_and_deletes()
    {
        var store = new VolatileCredentialStore();
        byte[] secret = Encoding.UTF8.GetBytes(Password);

        Assert.Null(await store.LoadAsync(Key));
        await store.SaveAsync(Key, secret);
        Assert.Equal(secret, await store.LoadAsync(Key));

        await store.SaveAsync(Key, new byte[] { 9, 8 });
        await store.SaveAsync(Key + "2", ReadOnlyMemory<byte>.Empty);
        Assert.Equal(new byte[] { 9, 8 }, await store.LoadAsync(Key));
        Assert.Empty((await store.LoadAsync(Key + "2"))!);

        await store.DeleteAsync(Key);
        Assert.Null(await store.LoadAsync(Key));
        await store.DeleteAsync(Key); // 없는 키 지우기는 오류가 아니다
        Assert.NotNull(await store.LoadAsync(Key + "2"));
    }

    [Fact]
    public async Task Volatile_store_hands_out_copies_and_rejects_blank_keys()
    {
        var store = new VolatileCredentialStore();
        byte[] secret = [1, 2, 3];
        await store.SaveAsync(Key, secret);
        secret[0] = 7; // 넘긴 배열을 바꿔도 저장값은 그대로
        byte[] loaded = (await store.LoadAsync(Key))!;
        loaded[1] = 7; // 꺼낸 배열을 바꿔도 저장값은 그대로

        Assert.Equal(new byte[] { 1, 2, 3 }, await store.LoadAsync(Key));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.SaveAsync(" ", new byte[] { 1 }));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.LoadAsync(""));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.DeleteAsync(" "));
    }

    private static OrandaServiceAttribute Attribute(Type type)
        => type.GetCustomAttributes(typeof(OrandaServiceAttribute), false).Cast<OrandaServiceAttribute>().Single();

    [SupportedOSPlatform("windows")]
    private DpapiCredentialStore NewDpapi()
        => new(new FakePaths(Path.Combine(_root, "credentials")));

    private sealed class FakePaths(string credentials) : IAppDataPaths
    {
        public string DataDirectory => Path.GetDirectoryName(credentials)!;

        public string SettingsFile => Path.Combine(DataDirectory, "settings.json");

        public string ConnectionsFile => Path.Combine(DataDirectory, "connections.json");

        public string LayoutFile => Path.Combine(DataDirectory, "layout.json");

        public string FormatterDirectory => Path.Combine(DataDirectory, "formatter");

        public string KeymapDirectory => Path.Combine(DataDirectory, "keymaps");

        public string WorkspacesDirectory => Path.Combine(DataDirectory, "workspaces");

        public string AutosaveDirectory => Path.Combine(DataDirectory, "autosave");

        public string DatabaseFile => Path.Combine(DataDirectory, "oranda.db");

        public string CredentialsDirectory => credentials;

        public string LogsDirectory => Path.Combine(DataDirectory, "logs");

        public string BackupDirectory => Path.Combine(DataDirectory, "backup");
    }
}
