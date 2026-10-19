using Oranda.App.Shell;
using Oranda.App.Shell.Composition;
using Oranda.App.Tests.Shell.Headless;
using Oranda.App.ViewModels;
using Oranda.Core.Connection;
using Oranda.Core.Shell;

namespace Oranda.App.Tests;

// TSK-03-01: 메인 창 ViewModel(창 제목·환경 테두리·상태 표시줄 구간). 화면 시험은 Shell/Headless.
public class MainViewModelTests
{
    private static MainViewModel Create(out ShellController shell, out ServiceContainer services)
    {
        var dir = Path.Combine(Path.GetTempPath(), "oranda-vm-" + Guid.NewGuid().ToString("N"));
        services = AppComposition.Create(new AppCompositionOptions { DataDirectory = dir });
        shell = services.GetRequiredService<ShellController>();
        return (MainViewModel)services.Create(typeof(MainViewModel));
    }

    [Fact]
    public void Title_and_border_class_follow_the_active_environment()
    {
        var vm = Create(out var shell, out var services);
        using (services)
        {
            var changed = new List<string?>();
            vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

            Assert.Equal("Oranda", vm.Title);
            Assert.Null(vm.EnvironmentClass);

            shell.ActiveEnvironment = ConnectionEnvironment.Production;
            Assert.Equal("Oranda [운영]", vm.Title);
            Assert.Equal("env-production", vm.EnvironmentClass);
            Assert.Contains(nameof(MainViewModel.Title), changed);

            shell.ActiveEnvironment = ConnectionEnvironment.Test;
            Assert.Equal("Oranda", vm.Title);
            Assert.Equal("env-test", vm.EnvironmentClass);

            shell.ActiveEnvironment = ConnectionEnvironment.Development;
            Assert.Equal("env-development", vm.EnvironmentClass);
        }
    }

    [Fact]
    public void Status_bar_entry_collapses_without_hiding_the_item_itself()
    {
        var entry = new StatusBarEntry(new StatusBarItemDefinition("oranda.status.t", 1, 3, typeof(Item)), new Item());
        var changed = new List<string?>();
        entry.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Assert.True(entry.IsShown);
        entry.Collapsed = true;

        Assert.False(entry.IsShown);
        Assert.Contains(nameof(StatusBarEntry.IsShown), changed);
    }

    [Fact]
    public void Status_bar_entry_hides_when_the_item_has_nothing_to_show()
    {
        var entry = new StatusBarEntry(new StatusBarItemDefinition("oranda.status.t", 1, 0, typeof(Item)), new Item { Visible = false });

        Assert.False(entry.IsShown);
    }

    private sealed class Item : IStatusBarItem
    {
        public bool Visible { get; init; } = true;

        public bool IsVisible => Visible;
    }
}
