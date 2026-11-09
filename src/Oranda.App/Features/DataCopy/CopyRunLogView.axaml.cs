using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace Oranda.App.Features.DataCopy;

/// <summary>
/// 실행·검증 단계의 「작업 로그」 칸. 줄이 더해지면 끝으로 스크롤하고, 「로그 복사」·「로그 저장…」은 남은 줄 전체 글을 쓴다.
/// 판단(상한·글 모양)은 <see cref="CopyRunLogViewModel"/> 이 한다.
/// </summary>
public partial class CopyRunLogView : UserControl
{
    private CopyRunLogViewModel? _vm;
    private bool _scrollQueued;

    public CopyRunLogView()
    {
        InitializeComponent();
        CopyLogButton.Click += OnCopy;
        SaveLogButton.Click += OnSave;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.Lines.CollectionChanged -= OnLinesChanged;
        }

        _vm = DataContext as CopyRunLogViewModel;
        if (_vm is not null)
        {
            _vm.Lines.CollectionChanged += OnLinesChanged;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_vm is not null)
        {
            _vm.Lines.CollectionChanged -= OnLinesChanged;
            _vm = null;
        }

        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_vm is null && DataContext is CopyRunLogViewModel vm)
        {
            _vm = vm;
            _vm.Lines.CollectionChanged += OnLinesChanged;
        }

        // 탭이 가려진 동안 쌓인 줄: 다시 보일 때 끝으로 간다.
        QueueScrollToEnd();
    }

    private void OnLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add)
        {
            QueueScrollToEnd();
        }
    }

    // 줄이 몰려 들어와도 한 번만 스크롤한다(배치 진행 줄이 연달아 온다).
    private void QueueScrollToEnd()
    {
        if (_scrollQueued)
        {
            return;
        }

        _scrollQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _scrollQueued = false;
            if (_vm is { Lines.Count: > 0 } vm)
            {
                LogList.ScrollIntoView(vm.Lines.Count - 1);
            }
        }, DispatcherPriority.Background);
    }

    private async void OnCopy(object? sender, RoutedEventArgs e)
    {
        if (_vm is { HasLines: true } vm && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(vm.AllText()).ConfigureAwait(true);
        }
    }

    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        if (_vm is not { HasLines: true } vm || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return;
        }

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "작업 로그 저장",
            SuggestedFileName = "data-copy-log.txt",
            DefaultExtension = "txt",
            FileTypeChoices = [new FilePickerFileType("텍스트") { Patterns = ["*.txt", "*.log"] }],
        }).ConfigureAwait(true);
        if (file is null)
        {
            return;
        }

        try
        {
            await using var stream = await file.OpenWriteAsync().ConfigureAwait(true);
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync(vm.AllText()).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 쓰지 못함(잠긴 파일·권한): 로그는 화면에 그대로 남고, 이유를 끝줄에 적는다.
            vm.Append("저장", "로그를 저장하지 못했습니다: " + ex.Message, Core.Transfer.Copy.CopyLogKind.Error);
        }
    }
}
