using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Oranda.App.Features.DataTransfer;
using Oranda.App.Features.Localization;
using Oranda.App.ViewModels;
using Oranda.Core.Shell;

namespace Oranda.App.Features.GridEdit;

/// <summary>
/// 상태 표시줄 9번 구간(TSK-13-18): 저장이 끝난 순간 「N행 저장됨」 같은 알림을 잠깐 보인다.
/// 편집 반영은 문구만, 파일 내보내기는 「열기」·「폴더 열기」 를 함께 보인다. 알림이 없으면 구간이 접힌다.
/// </summary>
[OrandaStatusBarItem("oranda.status.saveNotice", 9, CollapseRank = 1)]
public sealed class SaveStatusViewModel : ViewModelBase, IStatusBarItem, IDisposable
{
    private SaveNotice? _notice = SaveFeedback.Current;

    public SaveStatusViewModel() => SaveFeedback.Changed += OnFeedbackChanged;

    /// <summary>앱이 끝날 때(컨테이너가 부른다) 정적 알림 구독을 푼다.</summary>
    public void Dispose() => SaveFeedback.Changed -= OnFeedbackChanged;

    public bool IsVisible => _notice is not null;

    public string Text => _notice?.Text ?? string.Empty;

    public SaveFeedbackKind Kind => _notice?.Kind ?? SaveFeedbackKind.Success;

    /// <summary>내보낸 파일 경로. 파일이 없는 알림이면 null(열기 동작을 보이지 않는다).</summary>
    public string? FilePath => _notice?.FilePath;

    /// <summary>파일을 기본 앱으로 연다. 열지 못하면 false.</summary>
    public bool OpenFile() => FilePath is { } p && FileLauncher.Open(p);

    /// <summary>파일이 든 폴더를 연다. 열지 못하면 false.</summary>
    public bool OpenFolder() => FilePath is { } p && Path.GetDirectoryName(Path.GetFullPath(p)) is { Length: > 0 } dir && FileLauncher.Open(dir);

    private void OnFeedbackChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            Raise();
        }
        else
        {
            Dispatcher.UIThread.Post(Raise);
        }
    }

    private void Raise()
    {
        _notice = SaveFeedback.Current;
        OnPropertyChanged(nameof(IsVisible));
        OnPropertyChanged(nameof(Text));
        OnPropertyChanged(nameof(Kind));
        OnPropertyChanged(nameof(FilePath));
    }
}

/// <summary>
/// 9번 구간 View: 성공은 성공 글자색, 실패는 오류 색 칩. 내보내기면 칩 오른쪽에 「열기」·「폴더 열기」 링크.
/// 색은 기존 테마 리소스 키만 쓴다(Light·Dark·고대비가 같이 따라온다).
/// </summary>
public sealed class SaveStatusView : UserControl
{
    private readonly Border _chip = new() { CornerRadius = new Avalonia.CornerRadius(3), Padding = new Avalonia.Thickness(6, 0) };
    private readonly TextBlock _text = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _open = Link("SaveFeedback.open", "열기");
    private readonly TextBlock _folder = Link("SaveFeedback.openFolder", "폴더 열기");
    private SaveStatusViewModel? _vm;

    public SaveStatusView()
    {
        _chip.Child = _text;
        Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _chip, _open, _folder } };
        _open.PointerPressed += (_, e) => Click(e, vm => vm.OpenFile());
        _folder.PointerPressed += (_, e) => Click(e, vm => vm.OpenFolder());
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null)
            {
                _vm.PropertyChanged -= OnChanged;
            }

            _vm = DataContext as SaveStatusViewModel;
            if (_vm is not null)
            {
                _vm.PropertyChanged += OnChanged;
            }

            Refresh();
        };
    }

    private static TextBlock Link(string key, string fallback)
    {
        var link = new TextBlock
        {
            Text = UiText.Get(key, fallback),
            VerticalAlignment = VerticalAlignment.Center,
            TextDecorations = TextDecorations.Underline,
            Cursor = new Cursor(StandardCursorType.Hand),
            IsVisible = false,
        };
        return link;
    }

    private void Click(PointerPressedEventArgs e, Func<SaveStatusViewModel, bool> action)
    {
        if (_vm is not null && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            action(_vm);
        }
    }

    private void OnChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // 알림은 어느 스레드에서든 올 수 있다. 컨트롤을 만든 스레드에서만 만진다.
        if (CheckAccess())
        {
            Refresh();
        }
        else
        {
            Dispatcher.Post(Refresh);
        }
    }

    private void Refresh()
    {
        _text.Text = _vm?.Text ?? string.Empty;
        Avalonia.Automation.AutomationProperties.SetName(this, _vm?.Text ?? string.Empty);
        var hasFile = _vm?.FilePath is not null;
        _open.IsVisible = hasFile;
        _folder.IsVisible = hasFile;
        var failure = _vm?.Kind == SaveFeedbackKind.Failure;
        if (failure)
        {
            _chip.Bind(Border.BackgroundProperty, _chip.GetResourceObservable("OrandaEnvProductionBrush"));
            _text.Bind(TextBlock.ForegroundProperty, _text.GetResourceObservable("OrandaEnvProductionForegroundBrush"));
        }
        else
        {
            _chip.ClearValue(Border.BackgroundProperty);
            _text.Bind(TextBlock.ForegroundProperty, _text.GetResourceObservable("OrandaSuccessForegroundBrush"));
        }

        _text.FontWeight = FontWeight.SemiBold;
    }
}
