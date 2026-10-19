using System.Linq;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

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
}
