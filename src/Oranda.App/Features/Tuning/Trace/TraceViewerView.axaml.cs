using System;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;

namespace Oranda.App.Features.Tuning.Trace;

/// <summary>트레이스 보기 화면. 단추는 <see cref="TraceViewerViewModel"/> 동작을 부르고, 복사·저장·경로 복사는 여기서 한다.</summary>
public partial class TraceViewerView : UserControl
{
    private TraceViewerViewModel? _vm;

    public TraceViewerView()
    {
        InitializeComponent();
        // 숫자 칸은 오른쪽 맞춤(값을 글자로 미리 서식한 표).
        CallTable.RightAlignedColumns = [1, 2, 3, 4, 5, 6, 7];
        FileList.RightAlignedColumns = [1];
        FileList.MonospaceColumns = [0];
        RefreshButton.Click += async (_, _) => await ReloadAsync();
        RetryButton.Click += async (_, _) => await ReloadAsync();
        OptimizerTraceButton.Click += async (_, _) =>
        {
            if (DataContext is TraceViewerViewModel vm)
            {
                await vm.RunOptimizerTraceAsync();
            }
        };
        ReadAllButton.Click += (_, _) => (DataContext as TraceViewerViewModel)?.ReadAll();
        CopyButton.Click += async (_, _) => await CopyAsync((DataContext as TraceViewerViewModel)?.ExportText());
        CopyPathButton.Click += async (_, _) => await CopyAsync((DataContext as TraceViewerViewModel)?.ServerPath);
        CopyGrantButton.Click += async (_, _) => await CopyAsync(TraceViewerViewModel.GrantSql);
        SaveButton.Click += async (_, _) => await SaveAsync();
        FileList.CurrentRowChanged += (_, _) =>
        {
            if (DataContext is TraceViewerViewModel vm && FileList.CurrentRowIndex is var i and >= 0 && i < vm.Files.Count && !ReferenceEquals(vm.SelectedFile, vm.Files[i]))
            {
                vm.SelectedFile = vm.Files[i];
            }
        };
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.F5 && e.KeyModifiers == KeyModifiers.None)
        {
            _ = ReloadAsync();
            e.Handled = true;
        }
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnVmPropertyChanged;
        }

        _vm = DataContext as TraceViewerViewModel;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnVmPropertyChanged;
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 10053 로 연 파일: 표시 줄이 보이게 옮긴다.
        if (e.PropertyName == nameof(TraceViewerViewModel.ScrollTarget) && _vm?.ScrollTarget is { } line)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                LineList.ScrollIntoView(line);
                LineList.SelectedItem = line;
            }, Avalonia.Threading.DispatcherPriority.Loaded);
        }
    }

    private async Task ReloadAsync()
    {
        if (DataContext is TraceViewerViewModel { Session: { } session } vm)
        {
            await vm.LoadAsync(session, vm.Target, vm.TargetText);
        }
    }

    private async Task CopyAsync(string? text)
    {
        if (!string.IsNullOrEmpty(text) && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
        }
    }

    private async Task SaveAsync()
    {
        if (DataContext is not TraceViewerViewModel vm || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return;
        }

        var body = vm.ViewIndex == 1;
        var name = Path.GetFileNameWithoutExtension(vm.SelectedFile?.FileName ?? "trace") + (body ? ".trc" : "_summary.txt");
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = body ? "트레이스 본문 저장" : "트레이스 요약 저장",
            SuggestedFileName = name,
            DefaultExtension = body ? "trc" : "txt",
        });
        if (file?.TryGetLocalPath() is { } path)
        {
            await File.WriteAllTextAsync(path, vm.ExportText());
            vm.StatusText = "저장했습니다: " + path;
        }
    }
}
