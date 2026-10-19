using CommunityToolkit.Mvvm.ComponentModel;

namespace Oranda.App.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _greeting = "Welcome to Avalonia!";
}
