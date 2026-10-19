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
        // 파라미터 구역에서 행을 누르면 변경 미리보기에 이름·현재 값을 채운다.
        Table.RowActivated += (_, row) =>
        {
            if (DataContext is DbInfoViewModel { SelectedSection.Title: var title } vm && title.Contains("파라미터", StringComparison.Ordinal) && row.Count >= 2
                && row[0]?.ToString() is { Length: > 0 } name)
            {
                vm.PickParameter(name, row[1]?.ToString());
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
