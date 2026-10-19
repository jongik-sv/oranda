using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace Oranda.App.Features.FormatterProfiles;

public partial class FormatterProfilesView : UserControl
{
    public FormatterProfilesView()
    {
        InitializeComponent();
        CopyButton.Click += (_, _) => Run(vm => vm.CopySelectedAsync());
        DeleteButton.Click += (_, _) => Run(vm => vm.DeleteAsync());
        DefaultButton.Click += (_, _) => Run(vm => vm.SetDefaultAsync());
        SaveButton.Click += (_, _) => Run(vm => vm.SaveAsync());
        ExportButton.Click += async (_, _) =>
        {
            if (DataContext is FormatterProfilesViewModel { Selected: { } sel } vm && TopLevel.GetTopLevel(this)?.StorageProvider is { } storage)
            {
                var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = "포매터 프로필 내보내기",
                    SuggestedFileName = sel.Profile.Id + ".json",
                    DefaultExtension = "json",
                    FileTypeChoices = [new FilePickerFileType("포매터 프로필") { Patterns = ["*.json"] }],
                });
                if (file?.TryGetLocalPath() is { } path)
                {
                    await vm.ExportAsync(path);
                }
            }
        };
        ImportButton.Click += async (_, _) =>
        {
            if (DataContext is FormatterProfilesViewModel vm && TopLevel.GetTopLevel(this)?.StorageProvider is { } storage)
            {
                var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "포매터 프로필 가져오기",
                    AllowMultiple = false,
                    FileTypeFilter = [new FilePickerFileType("포매터 프로필") { Patterns = ["*.json"] }],
                });
                if ((files.Count > 0 ? files[0] : null)?.TryGetLocalPath() is { } path)
                {
                    await vm.ImportAsync(path);
                }
            }
        };
    }

    private void Run(Func<FormatterProfilesViewModel, Task> action)
    {
        if (DataContext is FormatterProfilesViewModel vm)
        {
            _ = action(vm);
        }
    }
}
