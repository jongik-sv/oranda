using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;

namespace Oranda.App.Features.LobRowView;

public partial class LobEditorView : UserControl
{
    public LobEditorView()
    {
        InitializeComponent();
        EncodingBox.ItemsSource = LobContent.EncodingNames;
        EncodingBox.SelectedIndex = 0;
        EncodingBox.SelectionChanged += (_, _) =>
        {
            if (DataContext is LobEditorViewModel vm && EncodingBox.SelectedItem is string name)
            {
                vm.FileEncodingName = name;
            }
        };
        SaveFileButton.Click += async (_, _) =>
        {
            if (DataContext is LobEditorViewModel vm && await PickSaveAsync(vm) is { } path)
            {
                await vm.SaveToFileAsync(path);
            }
        };
        LoadFileButton.Click += async (_, _) =>
        {
            if (DataContext is LobEditorViewModel vm && await PickOpenAsync() is { } path)
            {
                await vm.LoadFromFileAsync(path);
            }
        };
        SaveDbButton.Click += async (_, _) =>
        {
            if (DataContext is LobEditorViewModel vm)
            {
                await vm.SaveToDatabaseAsync(commit: false);
            }
        };
        SaveCommitButton.Click += async (_, _) =>
        {
            if (DataContext is LobEditorViewModel vm)
            {
                await vm.SaveToDatabaseAsync(commit: true);
            }
        };
        ReloadButton.Click += async (_, _) =>
        {
            if (DataContext is LobEditorViewModel vm)
            {
                await vm.LoadAsync();
            }
        };
        DataContextChanged += (_, _) =>
        {
            if (DataContext is LobEditorViewModel vm)
            {
                vm.PropertyChanged += OnViewModelChanged;
                UpdateButtons(vm);
                UpdatePreview(vm);
            }
        };
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not LobEditorViewModel vm)
        {
            return;
        }

        if (e.PropertyName == nameof(LobEditorViewModel.ImageBytes))
        {
            UpdatePreview(vm);
        }
        else if (e.PropertyName is nameof(LobEditorViewModel.IsBinary) or nameof(LobEditorViewModel.CanSaveToDatabase) or nameof(LobEditorViewModel.IsText))
        {
            UpdateButtons(vm);
        }
    }

    // 「파일에서 불러오기」는 저장할 수 있는 LOB 에서만 보인다(BLOB 는 이것으로만 바꾼다).
    private void UpdateButtons(LobEditorViewModel vm)
    {
        LoadFileButton.IsVisible = vm.Source is { Lob: not null, Target: not null, Session.Profile.ReadOnly: false };
    }

    private void UpdatePreview(LobEditorViewModel vm)
    {
        Preview.Source = null;
        if (vm.ImageBytes is { } bytes)
        {
            try
            {
                Preview.Source = new Bitmap(new MemoryStream(bytes));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException)
            {
                vm.StatusText = "이미지로 열지 못했습니다: " + ex.Message;
            }
        }
    }

    private async Task<string?> PickSaveAsync(LobEditorViewModel vm)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return null;
        }

        var name = vm.IsText ? "lob.txt" : "lob.bin";
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions { Title = "LOB 를 파일로 저장", SuggestedFileName = name });
        return file?.TryGetLocalPath();
    }

    private async Task<string?> PickOpenAsync()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return null;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "파일에서 불러오기", AllowMultiple = false });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }
}
