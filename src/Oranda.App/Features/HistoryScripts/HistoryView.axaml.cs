using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Oranda.App.Features.HistoryScripts;

public partial class HistoryView : UserControl
{
    public HistoryView()
    {
        InitializeComponent();
        RerunButton.Click += async (_, _) =>
        {
            if (DataContext is HistoryViewModel { Selected: { } item } vm)
            {
                await vm.RerunAsync(item);
            }
        };
        ToEditorButton.Click += async (_, _) =>
        {
            if (DataContext is HistoryViewModel { Selected: { } item } vm)
            {
                await vm.SendToEditorAsync(item);
            }
        };
        DeleteButton.Click += OnDelete;
        ClearButton.Click += async (_, _) =>
        {
            if (DataContext is HistoryViewModel vm)
            {
                await vm.ClearAsync(vm.CurrentConnectionOnly);
            }
        };
        MoreButton.Click += async (_, _) =>
        {
            if (DataContext is HistoryViewModel vm)
            {
                await vm.LoadMoreAsync();
            }
        };
        List.DoubleTapped += async (_, _) =>
        {
            if (DataContext is HistoryViewModel { Selected: { } item } vm)
            {
                await vm.SendToEditorAsync(item);
            }
        };
        List.KeyDown += OnListKeyDown;
        AttachedToVisualTree += async (_, _) =>
        {
            if (DataContext is HistoryViewModel vm)
            {
                await vm.ReloadAsync();
            }
        };
    }

    private async void OnDelete(object? sender, RoutedEventArgs e)
    {
        if (DataContext is HistoryViewModel vm)
        {
            await vm.DeleteAsync([.. List.SelectedItems?.OfType<HistoryItem>() ?? []]);
        }
    }

    private async void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && DataContext is HistoryViewModel vm)
        {
            await vm.DeleteAsync([.. List.SelectedItems?.OfType<HistoryItem>() ?? []]);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && DataContext is HistoryViewModel { Selected: { } item } vm2)
        {
            await vm2.RerunAsync(item);
            e.Handled = true;
        }
    }
}
