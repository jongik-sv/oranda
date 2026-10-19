using System;
using Avalonia.Controls;
using Avalonia.Input.Platform;

namespace Oranda.App.Features.DbTools;

public partial class DbInfoView : UserControl
{
    public DbInfoView()
    {
        InitializeComponent();
        ScopeBox.ItemsSource = Enum.GetValues<DbParameterScope>();
        ScopeBox.SelectedItem = DbParameterScope.Both;
        ScopeBox.SelectionChanged += (_, _) =>
        {
            if (DataContext is DbInfoViewModel vm && ScopeBox.SelectedItem is DbParameterScope scope)
            {
                vm.ParameterScope = scope;
            }
        };
        RefreshButton.Click += (_, _) =>
        {
            if (DataContext is DbInfoViewModel { Session: { } session } vm)
            {
                _ = vm.LoadAsync(session);
            }
        };
        CopyDdlButton.Click += async (_, _) =>
        {
            if (DataContext is DbInfoViewModel vm && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(vm.PreviewDdl);
            }
        };
    }
}
