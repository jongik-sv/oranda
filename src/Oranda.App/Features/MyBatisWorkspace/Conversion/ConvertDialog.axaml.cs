using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Oranda.App.Features.Localization;

namespace Oranda.App.Features.MyBatisWorkspace.Conversion;

/// <summary>
/// 「MyBatis 작업 공간으로 바꾸기」 모달 창(TSK-03-05). 확인이면 <see cref="ConvertDialogResult"/>, 취소·닫기면 null 을 돌려준다.
/// 찾아보기는 새 파일이면 저장 파일 고르기, 기존 매퍼면 열기 고르기다(.xml).
/// </summary>
public partial class ConvertDialog : Window
{
    public ConvertDialog()
    {
        InitializeComponent();
        BrowseButton.Click += OnBrowse;
        CancelButton.Click += (_, _) => Close(null);
        ConfirmButton.Click += (_, _) =>
        {
            // 바꾸다 실패하면 창을 열어 둔 채 바닥 안내에 오류가 보인다.
            if ((DataContext as ConvertDialogViewModel)?.TryBuildResult() is { } result)
            {
                Close(result);
            }
        };
    }

    /// <summary><paramref name="owner"/> 위에 모달로 띄운다. 취소하면 null.</summary>
    public static Task<ConvertDialogResult?> ShowAsync(Window owner, ConvertDialogViewModel model)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(model);
        return new ConvertDialog { DataContext = model }.ShowDialog<ConvertDialogResult?>(owner);
    }

    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ConvertDialogViewModel vm)
        {
            return;
        }

        FilePickerFileType[] xml = [new FilePickerFileType(UiText.Get("MbConvert.browse.xmlType", "매퍼 xml")) { Patterns = ["*.xml"] }];
        string? picked;
        if (vm.Target == ConvertTargetKind.NewFile)
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = UiText.Get("MbConvert.browse.save", "저장할 매퍼 파일"),
                SuggestedFileName = System.IO.Path.GetFileName(vm.Path),
                DefaultExtension = "xml",
                FileTypeChoices = xml,
            }).ConfigureAwait(true);
            picked = file?.TryGetLocalPath();
        }
        else
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = UiText.Get("MbConvert.browse.open", "문장을 추가할 매퍼 파일"),
                AllowMultiple = false,
                FileTypeFilter = xml,
            }).ConfigureAwait(true);
            picked = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        }

        if (picked is not null)
        {
            vm.Path = picked;
        }
    }
}
