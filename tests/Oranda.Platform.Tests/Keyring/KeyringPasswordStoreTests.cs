using System.Text;
using Oranda.Core.Composition;
using Oranda.Core.Security;
using Oranda.Platform.Keyring;
using Oranda.Platform.Rollback;

namespace Oranda.Platform.Tests.Keyring;

// Linux 키링(Secret Service, libsecret) 저장소. 키링을 못 쓰면 메모리 저장소로 대신한다.
public sealed class KeyringPasswordStoreTests
{
    private const string MissingLibrary = "liboranda-no-such-library.so.0";
    private const string AnyBus = "unix:path=/run/user/0/bus";

    [Fact]
    public void Registered_for_linux_only()
    {
        var attribute = typeof(KeyringPasswordStore)
            .GetCustomAttributes(typeof(OrandaServiceAttribute), false).Cast<OrandaServiceAttribute>().Single();

        Assert.Equal(typeof(ICredentialStore), attribute.ServiceType);
        Assert.Equal(ServicePlatform.Linux, attribute.Platform);
    }

    [Fact]
    public async Task Missing_libsecret_falls_back_to_memory_without_throwing()
    {
        var store = new KeyringPasswordStore(MissingLibrary, () => AnyBus);
        string key = NewKey();

        Assert.False(store.IsPersistent);
        await store.SaveAsync(key, new byte[] { 1, 2, 3 });
        Assert.Equal(new byte[] { 1, 2, 3 }, await store.LoadAsync(key));
        await store.DeleteAsync(key);
        Assert.Null(await store.LoadAsync(key));
    }

    [Fact]
    public async Task No_session_bus_falls_back_to_memory()
    {
        var store = new KeyringPasswordStore(KeyringPasswordStore.LibraryName, () => null);

        Assert.False(store.IsPersistent);
        await store.SaveAsync(NewKey(), new byte[] { 7 });
    }

    [Fact]
    public async Task Fallback_store_still_rejects_blank_keys()
    {
        var store = new KeyringPasswordStore(MissingLibrary, () => AnyBus);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.SaveAsync(" ", new byte[] { 1 }));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.LoadAsync(""));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.DeleteAsync(" "));
    }

    [Fact]
    public void Rollback_vault_is_not_durable_when_the_keyring_is_unavailable()
    {
        using var fallback = new RollbackVault(new KeyringPasswordStore(MissingLibrary, () => AnyBus));

        Assert.False(fallback.IsDurable);
    }

    // 실제 키링(gnome-keyring 등)이 열린 Linux 에서만 돈다. CI 는 Category=Keyring 을 뺀다.
    [Fact]
    [Trait("Category", "Keyring")]
    public async Task Keyring_round_trips_overwrites_and_deletes()
    {
        if (!TryRealStore(out var store))
        {
            return;
        }

        string key = NewKey();
        byte[] value = Encoding.UTF8.GetBytes("S3cret-p@ss-유니코드-zq9X");
        try
        {
            Assert.Null(await store.LoadAsync(key));
            await store.SaveAsync(key, value);
            Assert.Equal(value, await store.LoadAsync(key));

            // 새 인스턴스(= 앱 재시작)에서도 읽힌다.
            Assert.Equal(value, await new KeyringPasswordStore().LoadAsync(key));

            await store.SaveAsync(key, new byte[] { 9, 8 });
            Assert.Equal(new byte[] { 9, 8 }, await store.LoadAsync(key));

            await store.SaveAsync(key, ReadOnlyMemory<byte>.Empty);
            Assert.Empty((await store.LoadAsync(key))!);

            await store.DeleteAsync(key);
            Assert.Null(await store.LoadAsync(key));
            await store.DeleteAsync(key); // 없는 키 지우기는 오류가 아니다
        }
        finally
        {
            await store.DeleteAsync(key);
        }
    }

    [Fact]
    [Trait("Category", "Keyring")]
    public async Task Keyring_keys_are_independent()
    {
        if (!TryRealStore(out var store))
        {
            return;
        }

        string a = NewKey();
        string b = NewKey();
        try
        {
            await store.SaveAsync(a, new byte[] { 1 });
            await store.SaveAsync(b, new byte[] { 2 });
            await store.DeleteAsync(a);

            Assert.Null(await store.LoadAsync(a));
            Assert.Equal(new byte[] { 2 }, await store.LoadAsync(b));
        }
        finally
        {
            await store.DeleteAsync(a);
            await store.DeleteAsync(b);
        }
    }

    [Fact]
    [Trait("Category", "Keyring")]
    public void Rollback_vault_on_the_keyring_is_durable()
    {
        if (!TryRealStore(out var store))
        {
            return;
        }

        // 암호화까지 하면 실제 원복 키(oranda:rollback:key:v1)를 키링에 만들므로 여기서는 판단만 본다.
        using var vault = new RollbackVault(store);
        Assert.True(vault.IsDurable);
    }

    // ORANDA_KEYRING_REQUIRE=1 이면 키링을 못 쓸 때 건너뛰지 않고 실패한다(키링이 열린 PC 에서 확인할 때).
    private static bool TryRealStore(out KeyringPasswordStore store)
    {
        store = new KeyringPasswordStore();
        bool usable = OperatingSystem.IsLinux() && store.IsPersistent;
        if (!usable && Environment.GetEnvironmentVariable("ORANDA_KEYRING_REQUIRE") == "1")
        {
            Assert.Fail("ORANDA_KEYRING_REQUIRE=1 인데 키링을 쓸 수 없다(libsecret·세션 버스·키링 데몬 확인).");
        }

        return usable;
    }

    private static string NewKey() => "oranda:test:" + Guid.NewGuid().ToString("D");
}
