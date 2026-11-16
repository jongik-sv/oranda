using Oranda.Testing;

namespace Oranda.Platform.Tests.Testing;

/// <summary>Oranda.Testing 공용 도구(TempDirectory·ServiceBag·InMemorySettingsStore)의 계약.</summary>
public sealed class TestingToolsTests
{
    [Fact]
    public void TempDirectory_does_not_create_the_folder_and_dispose_removes_it_with_its_files()
    {
        var temp = new TempDirectory("oranda-tools-");
        Assert.False(Directory.Exists(temp));
        Assert.StartsWith("oranda-tools-", Path.GetFileName(temp.Path));

        Directory.CreateDirectory(Path.Combine(temp, "sub"));
        File.WriteAllText(Path.Combine(temp, "sub", "a.txt"), "x");
        temp.Dispose();

        Assert.False(Directory.Exists(temp.Path));
        temp.Dispose();
    }

    [Fact]
    public void ServiceBag_returns_what_was_added_and_null_for_the_rest()
    {
        var fake = new MemoryStream();
        var bag = new ServiceBag().Add<Stream>(fake).Add<IDisposable>(null);

        Assert.Same(fake, bag.GetService(typeof(Stream)));
        Assert.Null(bag.GetService(typeof(IDisposable)));
        Assert.Null(bag.GetService(typeof(string)));
    }

    [Fact]
    public async Task InMemorySettingsStore_loads_what_was_saved()
    {
        var store = new InMemorySettingsStore();
        var settings = new Oranda.Core.Settings.AppSettings();

        await store.SaveAsync(settings, default);

        Assert.Same(settings, store.Current);
        Assert.Same(settings, (await store.LoadAsync(default)).Value);
    }
}
