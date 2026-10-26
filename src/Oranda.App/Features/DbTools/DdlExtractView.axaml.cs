using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;

namespace Oranda.App.Features.DbTools;

public partial class DdlExtractView : UserControl
{
    public DdlExtractView()
    {
        InitializeComponent();
        RunButton.Click += (_, _) =>
        {
            if (DataContext is DdlExtractViewModel vm)
            {
                _ = vm.RunAsync();
            }
        };
        RefreshTargetsButton.Click += (_, _) => RefreshTargets();

        // F5 = 대상 다시 조회, 목록에서 Space = 고른 줄 체크 바꾸기, 줄(체크 칸 밖)을 누르면 체크 바꾸기.
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.F5 && e.KeyModifiers == KeyModifiers.None)
            {
                RefreshTargets();
                e.Handled = true;
            }
        };
        TargetList.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Space && e.KeyModifiers == KeyModifiers.None && TargetList.SelectedItem is DdlTargetItem item)
            {
                item.IsChecked = !item.IsChecked;
                e.Handled = true;
            }
        };
        TargetList.AddHandler(TappedEvent, (_, e) =>
        {
            if (e.Source is Visual v && v.FindAncestorOfType<CheckBox>(includeSelf: true) is null
                && v.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is DdlTargetItem item)
            {
                item.IsChecked = !item.IsChecked;
            }
        });
        BrowseButton.Click += async (_, _) =>
        {
            if (DataContext is DdlExtractViewModel vm && TopLevel.GetTopLevel(this)?.StorageProvider is { } storage)
            {
                var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "DDL 출력 폴더" });
                if ((folders.Count > 0 ? folders[0] : null)?.TryGetLocalPath() is { } path)
                {
                    vm.OutputDirectory = path;
                }
            }
        };
    }

    private void RefreshTargets()
    {
        if (DataContext is DdlExtractViewModel vm)
        {
            _ = vm.RefreshTargetsAsync();
        }
    }
}
