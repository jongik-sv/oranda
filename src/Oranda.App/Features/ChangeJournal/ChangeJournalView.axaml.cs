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
                await vm.SetRowSelectionAsync([.. List.SelectedItems?.OfType<IJournalRow>() ?? []]);
            }
        };
        List.DoubleTapped += (_, e) => Activate((e.Source as Avalonia.StyledElement)?.DataContext as IJournalRow);
        OpenButton.Click += (_, _) => OpenSelected();
        OpenScriptButton.Click += async (_, _) =>
        {
            if (DataContext is ChangeJournalViewModel vm)
            {
                await vm.OpenScriptAsync();
            }
        };
        CompareButton.Click += async (_, _) =>
        {
            if (DataContext is ChangeJournalViewModel vm)
            {
                await vm.CompareAsync();
            }
        };
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

    /// <summary>두 번 누른 그 줄: 묶음 줄은 접고 펴고, DDL 보관본은 새 편집기에 연다. 문장 줄은 아무것도 하지 않는다.</summary>
    private void Activate(IJournalRow? row)
    {
        if (DataContext is not ChangeJournalViewModel vm)
        {
            return;
        }

        switch (row)
        {
            case RollbackGroupItem group:
                vm.ToggleExpand(group);
                break;
            case JournalItem item:
                vm.OpenInEditor(item);
                break;
        }
    }

    private void OpenSelected()
    {
        if (DataContext is ChangeJournalViewModel vm && List.SelectedItems?.OfType<JournalItem>().FirstOrDefault() is { } item)
        {
            vm.OpenInEditor(item);
        }
    }
}
