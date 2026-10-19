using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Oranda.Core.Transfer;

namespace Oranda.App.Features.DataTransfer;

/// <summary>내보내기 대화상자(도킹 안 함, ui-shell.md 2장). 형식 목록과 파일 선택만 화면 쪽에서 맡는다.</summary>
public partial class ExportDialogWindow : Window
{
    public ExportDialogWindow()
    {
        InitializeComponent();
        FormatBox.ItemsSource = new[] { TransferFormat.Csv, TransferFormat.Tsv, TransferFormat.Xlsx, TransferFormat.Json, TransferFormat.Insert }
            .Select(ExportDialogViewModel.Label).ToList();
        FormatBox.SelectedIndex = 0;
        FormatBox.SelectionChanged += (_, _) =>
        {
            if (DataContext is ExportDialogViewModel vm && FormatBox.SelectedIndex >= 0)
            {
                vm.Format = vm.Formats[FormatBox.SelectedIndex];
            }
        };
        BrowseButton.Click += OnBrowse;
        CloseButton.Click += (_, _) => Close();
        Closed += (_, _) => (DataContext as System.IDisposable)?.Dispose();
    }

    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ExportDialogViewModel vm)
        {
            return;
        }

        var ext = vm.SuggestedExtension;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "내보낼 파일",
            SuggestedFileName = vm.SuggestedFileName,
            DefaultExtension = ext.TrimStart('.'),
            FileTypeChoices = [new FilePickerFileType(ExportDialogViewModel.Label(vm.Format)) { Patterns = ["*" + ext] }],
        }).ConfigureAwait(true);
        if (file?.TryGetLocalPath() is { } path)
        {
            vm.FilePath = path;
        }
    }
}
