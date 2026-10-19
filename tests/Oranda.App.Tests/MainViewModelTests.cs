using Oranda.App.ViewModels;

namespace Oranda.App.Tests;

// 뼈대. Avalonia Headless UI 시험이 여기에 들어온다(TRD 11).
public class MainViewModelTests
{
    [Fact]
    public void Greeting_raises_property_changed()
    {
        var vm = new MainViewModel();
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.Greeting = "Oranda";

        Assert.Equal("Oranda", vm.Greeting);
        Assert.Contains(nameof(MainViewModel.Greeting), changed);
    }
}
