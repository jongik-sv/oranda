using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Oranda.App.Features.Templates;

/// <summary>단축키 설정 창(<c>oranda.tools.keymap</c>). 논리는 <see cref="KeymapEditorViewModel"/>.</summary>
public partial class KeymapEditorWindow : Window
{
    public KeymapEditorWindow()
    {
        InitializeComponent();
    }

    private KeymapEditorViewModel? Vm => DataContext as KeymapEditorViewModel;

    private void OnAssign(object? sender, RoutedEventArgs e) => Vm?.Assign();

    private void OnClear(object? sender, RoutedEventArgs e) => Vm?.Clear();

    private void OnResetRow(object? sender, RoutedEventArgs e) => Vm?.ResetRow();

    private void OnResetAll(object? sender, RoutedEventArgs e) => Vm?.ResetAll();

    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm)
        {
            await vm.SaveAsync().ConfigureAwait(true);
        }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
