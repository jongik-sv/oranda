using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Oranda.App.Shell.Icons;

namespace Oranda.App.Features.HistoryScripts;

public partial class ScriptsView : UserControl
{
    public ScriptsView()
    {
        InitializeComponent();
        AddButton.Content = ShellIcons.Create("folderAdd");
        RemoveButton.Content = ShellIcons.Create("folderRemove");
        RefreshButton.Content = ShellIcons.Create("refresh");
        RunButton.Content = ShellIcons.Create("run");
        AddButton.Click += async (_, _) => await AddFolderAsync();
        RemoveButton.Click += (_, _) =>
        {
            if (DataContext is ScriptsViewModel { SelectedNode: { IsRoot: true } root } vm)
            {
                vm.RemoveFolder(root);
            }
        };
        RefreshButton.Click += (_, _) =>
        {
            if (DataContext is ScriptsViewModel vm)
            {
                vm.Refresh(vm.SelectedNode);
            }
        };
        RunButton.Click += async (_, _) =>
        {
            if (DataContext is ScriptsViewModel { SelectedNode: { IsDirectory: false } node } vm)
            {
                await vm.RunAsync(node);
            }
        };
        Tree.DoubleTapped += async (_, _) =>
        {
            if (DataContext is ScriptsViewModel { SelectedNode: { } node } vm && !node.IsDirectory)
            {
                await vm.OpenAsync(node);
            }
        };
        Tree.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter && DataContext is ScriptsViewModel { SelectedNode: { } node } vm)
            {
                await vm.OpenAsync(node);
                e.Handled = true;
            }
        };
        AttachedToVisualTree += (_, _) =>
        {
            if (DataContext is ScriptsViewModel { Roots.Count: 0 } vm)
            {
                vm.Reload();
            }
        };
    }

    private async System.Threading.Tasks.Task AddFolderAsync()
    {
        if (DataContext is not ScriptsViewModel vm || TopLevel.GetTopLevel(this) is not { } top)
        {
            return;
        }

        var picked = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "스크립트 폴더 연결", AllowMultiple = false });
        if (picked.Count > 0 && picked[0].TryGetLocalPath() is { } path)
        {
            vm.AddFolder(path);
        }
    }
}
