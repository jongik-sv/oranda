using System.Linq;
using Avalonia.Controls;

namespace Oranda.App.Features.ChangeJournal;

public partial class ChangeJournalView : UserControl
{
    public ChangeJournalView()
    {
        InitializeComponent();
        List.SelectionChanged += async (_, _) =>
        {
            if (DataContext is ChangeJournalViewModel vm)
            {
                await vm.SetSelectionAsync([.. List.SelectedItems?.OfType<JournalItem>() ?? []]);
            }
        };
        List.DoubleTapped += (_, _) => OpenSelected();
        OpenButton.Click += (_, _) => OpenSelected();
        RefreshButton.Click += async (_, _) =>
        {
            if (DataContext is ChangeJournalViewModel vm)
            {
                await vm.ReloadAsync();
            }
        };
        ClearFilterButton.Click += (_, _) => (DataContext as ChangeJournalViewModel)?.ClearObjectFilter();
        AttachedToVisualTree += async (_, _) =>
        {
            if (DataContext is ChangeJournalViewModel vm)
            {
                await vm.ReloadAsync();
            }
        };
    }

    private void OpenSelected()
    {
        if (DataContext is ChangeJournalViewModel vm && List.SelectedItems?.OfType<JournalItem>().FirstOrDefault() is { } item)
        {
            vm.OpenInEditor(item);
        }
    }
}
