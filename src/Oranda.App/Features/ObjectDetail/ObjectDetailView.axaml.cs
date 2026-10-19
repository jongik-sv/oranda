using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input.Platform;

namespace Oranda.App.Features.ObjectDetail;

public partial class ObjectDetailView : UserControl
{
    private ObjectDetailViewModel? _vm;

    public ObjectDetailView()
    {
        InitializeComponent();
        CopyButton.Click += async (_, _) =>
        {
            if (_vm is not null && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(_vm.DdlText);
            }
        };
        Tabs.SelectionChanged += (_, _) =>
        {
            if (_vm is not null)
            {
                _vm.IsDdlSelected = Tabs.SelectedItem == DdlTab;
            }
        };
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null)
            {
                _vm.PropertyChanged -= OnVmChanged;
            }

            _vm = DataContext as ObjectDetailViewModel;
            if (_vm is not null)
            {
                _vm.PropertyChanged += OnVmChanged;
                Sync();
            }
        };
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e) => Sync();

    private void Sync()
    {
        if (_vm is null)
        {
            return;
        }

        var wanted = _vm.IsDdlSelected ? DdlTab : (TabItem)Tabs.Items[0]!;
        if (Tabs.SelectedItem != wanted)
        {
            Tabs.SelectedItem = wanted;
        }
    }
}
