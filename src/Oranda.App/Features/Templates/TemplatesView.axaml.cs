using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Oranda.App.Features.Templates;

public partial class TemplatesView : UserControl
{
    public TemplatesView()
    {
        InitializeComponent();
        DataContextChanged += async (_, _) =>
        {
            if (DataContext is TemplatesViewModel vm)
            {
                await vm.LoadUserAsync().ConfigureAwait(true);
            }
        };
    }

    private TemplatesViewModel? Vm => DataContext as TemplatesViewModel;

    private void OnInsert(object? sender, RoutedEventArgs e) => Vm?.InsertSelected();

    private async void OnAdd(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm)
        {
            await vm.AddUserSnippetAsync().ConfigureAwait(true);
        }
    }

    private async void OnDelete(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm)
        {
            await vm.DeleteUserSnippetAsync().ConfigureAwait(true);
        }
    }
}
