using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Oranda.App.Features.Localization;
using Oranda.App.Shell;
using Oranda.App.ViewModels;
using Oranda.Core.Shell;

namespace Oranda.App.Features.Update;

/// <summary>
/// 상태 표시줄 8번 구간: 「업데이트 x.y.z 준비됨」(강조색) / 「업데이트 x.y.z 있음」 / 받는 중. 누르면 업데이트 대화상자를 연다.
/// 건너뛴 버전과 시작 확인의 조용한 결과(최신·실패)는 보이지 않는다.
/// </summary>
[OrandaStatusBarItem("oranda.status.update", 8, CollapseRank = 0)]
public sealed class UpdateStatusViewModel : ViewModelBase, IStatusBarItem
{
    private readonly UpdateService _service;

    public UpdateStatusViewModel(UpdateService service)
    {
        _service = service;
        service.Changed += OnServiceChanged;
        if (UiText.Current is { } text)
        {
            text.LanguageChanged += (_, _) => OnServiceChanged(null, EventArgs.Empty);
        }
    }

    public bool IsVisible => Kind != UpdateStatusKind.None;

    public UpdateStatusKind Kind => _service.Phase switch
    {
        UpdatePhase.Ready => UpdateStatusKind.Ready,
        UpdatePhase.Downloading => UpdateStatusKind.Downloading,
        UpdatePhase.Available when !_service.IsSkipped => UpdateStatusKind.Available,
        _ => UpdateStatusKind.None,
    };

    public string Text
    {
        get
        {
            var version = _service.Phase == UpdatePhase.Ready ? _service.ReadyVersion : _service.Offer?.Version;
            return Kind switch
            {
                UpdateStatusKind.Ready => Format("Update.status.ready", "업데이트 {0} 준비됨", version),
                UpdateStatusKind.Downloading => Format("Update.status.downloading", "업데이트 {0} 받는 중 {1}%", version, _service.Progress),
                UpdateStatusKind.Available => Format("Update.status.available", "업데이트 {0} 있음", version),
                _ => string.Empty,
            };
        }
    }

    public static string ToolTip => UiText.Get("Update.status.tooltip", "눌러서 업데이트 창 열기");

    public static Task OpenAsync() => ShellCommandRunner.RunAsync("oranda.help.checkUpdate");

    private static string Format(string key, string fallback, params object?[] args) =>
        string.Format(System.Globalization.CultureInfo.CurrentCulture, UiText.Get(key, fallback), args);

    private void OnServiceChanged(object? sender, EventArgs e)
    {
        void Raise()
        {
            OnPropertyChanged(nameof(Kind));
            OnPropertyChanged(nameof(Text));
            OnPropertyChanged(nameof(IsVisible));
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            Raise();
        }
        else
        {
            Dispatcher.UIThread.Post(Raise);
        }
    }
}

public enum UpdateStatusKind
{
    None,
    Available,
    Downloading,
    Ready,
}

/// <summary>8번 구간 View: 점 + 글. 준비됨은 강조색·굵게. 누르면 업데이트 대화상자.</summary>
public sealed class UpdateStatusView : UserControl
{
    private readonly Border _dot = new() { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _text = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private UpdateStatusViewModel? _vm;

    public UpdateStatusView()
    {
        _dot.Bind(Border.BackgroundProperty, _dot.GetResourceObservable("OrandaButtonAccentBrush"));
        Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { _dot, _text } };
        Cursor = new Cursor(StandardCursorType.Hand);
        Background = Brushes.Transparent;
        PointerPressed += async (_, e) =>
        {
            if (_vm is not null && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                await UpdateStatusViewModel.OpenAsync();
            }
        };
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null)
            {
                _vm.PropertyChanged -= OnChanged;
            }

            _vm = DataContext as UpdateStatusViewModel;
            if (_vm is not null)
            {
                _vm.PropertyChanged += OnChanged;
            }

            Refresh();
        };
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

    private void Refresh()
    {
        _text.Text = _vm?.Text ?? string.Empty;
        _text.FontWeight = _vm?.Kind == UpdateStatusKind.Ready ? FontWeight.SemiBold : FontWeight.Normal;
        ToolTip.SetTip(this, UpdateStatusViewModel.ToolTip);
        Avalonia.Automation.AutomationProperties.SetName(this, _vm?.Text ?? string.Empty);
    }
}
