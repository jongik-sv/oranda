using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace Oranda.App.Features.TableSpec;

public partial class TableSpecView : UserControl
{
    public TableSpecView()
    {
        InitializeComponent();
        RunButton.Click += async (_, _) =>
        {
            if (DataContext is not TableSpecViewModel vm || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
            {
                return;
            }

            var ext = TableSpecViewModel.Extension(vm.Format.Format);
            var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "테이블 정의서 저장",
                SuggestedFileName = vm.SuggestedFileName,
                DefaultExtension = ext.TrimStart('.'),
                FileTypeChoices = [new FilePickerFileType(vm.Format.Title) { Patterns = ["*" + ext] }],
            });
            if (file?.TryGetLocalPath() is { } path)
            {
                await vm.GenerateAsync(path);
            }
        };
    }
}
