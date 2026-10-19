using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace Oranda.App.Features.DataTransfer;

/// <summary>가져오기 대화상자(도킹 안 함). 열 매핑 줄의 대상 열 목록을 연결하고 파일 선택을 맡는다.</summary>
public partial class ImportDialogWindow : Window
{
    public ImportDialogWindow()
    {
        InitializeComponent();
        BrowseButton.Click += OnBrowse;
        CloseButton.Click += (_, _) => Close();
        Closed += (_, _) => (DataContext as System.IDisposable)?.Dispose();
        AddHandler(Control.LoadedEvent, OnRowLoaded, RoutingStrategies.Bubble);
    }

    /// <summary>매핑 줄이 만들어질 때 그 줄의 대상 열 선택 상자를 현재 대상 열 목록과 양방향으로 잇는다.</summary>
    private void OnRowLoaded(object? sender, RoutedEventArgs e)
    {
        if (e.Source is ComboBox { Name: "TargetBox", DataContext: ImportMappingRow row } box && DataContext is ImportDialogViewModel vm && box.ItemsSource is null)
        {
            var items = new List<string?> { null };
            items.AddRange(vm.TargetColumns);
            box.ItemsSource = items;
            box.PlaceholderText = "(건너뜀)";
            box.SelectedItem = row.Target;
            box.SelectionChanged += (_, _) => row.Target = box.SelectedItem as string;
        }
    }

    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ImportDialogViewModel vm)
        {
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "가져올 파일",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("CSV·TSV·Excel") { Patterns = ["*.csv", "*.tsv", "*.txt", "*.xlsx"] }],
        }).ConfigureAwait(true);
        if (files.Count == 1 && files[0].TryGetLocalPath() is { } path)
        {
            vm.FilePath = path;
        }
    }
}
