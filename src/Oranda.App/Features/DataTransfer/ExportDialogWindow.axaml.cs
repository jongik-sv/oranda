using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace Oranda.App.Features.DataTransfer;

/// <summary>내보내기 대화상자(도킹 안 함, ui-shell.md 2장). 파일 선택·닫기·첫 포커스만 화면 쪽에서 맡는다.</summary>
public partial class ExportDialogWindow : Window
{
    public ExportDialogWindow()
    {
        InitializeComponent();
        BrowseButton.Click += OnBrowse;
        CloseButton.Click += (_, _) => Close();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is ExportDialogViewModel vm)
            {
                vm.Finished += (_, _) => Close();
            }
        };

        // 저장 위치가 채워져 있으므로 바로 Enter(또는 Space)로 내보낼 수 있게 한다.
        // 긴 경로는 끝(파일 이름)이 보이게 한다.
        Opened += (_, _) =>
        {
            ShowPathEnd();
            StartButton.Focus();
        };
        Closed += (_, _) => (DataContext as IDisposable)?.Dispose();
    }

    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ExportDialogViewModel vm)
        {
            return;
        }

        var ext = vm.SuggestedExtension;
        var current = vm.FilePath.Trim();
        var folder = ExportDialogViewModel.ValidatePath(current, Directory.Exists).Folder is { } f && Directory.Exists(f) ? f : ExcelExportPlan.DefaultFolder();
        var name = Path.GetFileName(current);
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = ExportText.PickerTitle,
            SuggestedFileName = string.IsNullOrEmpty(name) ? vm.Title + ext : name,
            DefaultExtension = ext.TrimStart('.'),
            SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(folder).ConfigureAwait(true),
            FileTypeChoices = [new FilePickerFileType(ExportDialogViewModel.Label(vm.Format)) { Patterns = ["*" + ext] }],
        }).ConfigureAwait(true);
        if (file?.TryGetLocalPath() is { } path)
        {
            vm.FilePath = path;
            ShowPathEnd();
        }
    }

    private void ShowPathEnd() => PathBox.CaretIndex = PathBox.Text?.Length ?? 0;
}
