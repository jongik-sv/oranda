using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.Localization;
using Oranda.App.Shell;
using Oranda.App.ViewModels;
using Oranda.Core.Shell;

namespace Oranda.App.Features.Update;

/// <summary>
/// 상태 표시줄 8번 구간: 「업데이트 x.y.z 있음」(주황 배지) / 받는 중(파랑 배지, 진행 막대) / 「업데이트 x.y.z 준비됨」(초록 배지). 누르면 업데이트 대화상자를 연다.
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

    /// <summary>받는 중 진행률(0~100).</summary>
    public int Progress => _service.Progress;

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
            OnPropertyChanged(nameof(Progress));
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

/// <summary>
/// 8번 구간 View: 꽉 찬 색 배지(아이콘 + 글, 받는 중이면 진행 막대). 상태 표시줄의 다른 칸은 바탕이 없는 글자라 색 칸 하나가 곁눈으로도 보인다.
/// 배지 색은 테마 리소스(OrandaUpdate*Brush)이고 배지 안 글자는 두 테마 모두 4.5:1 이상이다.
/// 운영 연결이면 상태 표시줄 바탕이 빨강(OrandaEnvProductionBrush)이라 배지가 섞인다. 그 바탕의 글자색(흰/검정)으로 테두리를 두른다.
/// </summary>
public sealed class UpdateStatusView : UserControl
{
    private const double TrackWidth = 34;

    // 16 단위 칸에 그린 선 아이콘: 내려받기(화살표 + 바닥선), 다시 시작(되돌이 화살표).
    private static readonly Geometry DownloadIcon = Geometry.Parse("M8,2 L8,10 M4.5,6.5 L8,10 L11.5,6.5 M3,13.5 L13,13.5");
    private static readonly Geometry RestartIcon = Geometry.Parse("M13,8 A5,5 0 1 1 11.4,4.3 M13,2.5 L13,5.5 L10,5.5");

    private readonly Avalonia.Controls.Shapes.Path _icon = new()
    {
        Width = 12,
        Height = 12,
        Stretch = Stretch.Uniform,
        StrokeThickness = 1.6,
        StrokeLineCap = PenLineCap.Round,
        StrokeJoin = PenLineJoin.Round,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private readonly TextBlock _text = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = FontWeight.SemiBold };
    private readonly Border _fill = new() { HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(2) };
    private readonly Border _track;
    private readonly Border _badge;
    private UpdateStatusViewModel? _vm;
    private Border? _bar;

    public UpdateStatusView()
    {
        _track = new Border { Name = "UpdateStatusTrack", Width = TrackWidth, Height = 4, CornerRadius = new CornerRadius(2), VerticalAlignment = VerticalAlignment.Center, ClipToBounds = true, Child = _fill };
        _badge = new Border
        {
            Name = "UpdateStatusBadge",
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(7, 1, 9, 1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Children = { _icon, _text, _track } },
        };
        AttachedToVisualTree += (_, _) => WatchStatusBar();
        DetachedFromVisualTree += (_, _) => UnwatchStatusBar();
        ActualThemeVariantChanged += (_, _) => ApplyBrushes();
        Content = _badge;
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

    private void WatchStatusBar()
    {
        UnwatchStatusBar();
        _bar = this.GetVisualAncestors().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("statusbar"));
        if (_bar is not null)
        {
            _bar.Classes.CollectionChanged += OnBarClassesChanged;
        }

        ApplyBrushes();
    }

    private void UnwatchStatusBar()
    {
        if (_bar is not null)
        {
            _bar.Classes.CollectionChanged -= OnBarClassesChanged;
            _bar = null;
        }
    }

    private void OnBarClassesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => ApplyBrushes();

    private IBrush? Find(string key) => this.TryFindResource(key, ActualThemeVariant, out var brush) ? brush as IBrush : null;

    private void ApplyBrushes()
    {
        var kind = _vm?.Kind ?? UpdateStatusKind.None;
        _badge.Background = Find(kind switch
        {
            UpdateStatusKind.Ready => "OrandaUpdateReadyBrush",
            UpdateStatusKind.Downloading => "OrandaUpdateDownloadingBrush",
            _ => "OrandaUpdateAvailableBrush",
        });
        var fg = Find("OrandaUpdateBadgeForegroundBrush");
        _text.Foreground = fg;
        _icon.Stroke = fg;
        _fill.Background = fg;
        _track.Background = Find("OrandaUpdateBadgeTrackBrush");
        var production = _bar?.Classes.Contains("env-production") == true;
        _badge.BorderThickness = new Thickness(production ? 1.5 : 0);
        _badge.BorderBrush = production ? Find("OrandaEnvProductionForegroundBrush") : null;
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

    private void Refresh()
    {
        var kind = _vm?.Kind ?? UpdateStatusKind.None;
        _text.Text = _vm?.Text ?? string.Empty;
        _icon.Data = kind == UpdateStatusKind.Ready ? RestartIcon : DownloadIcon;
        _icon.IsVisible = kind != UpdateStatusKind.Downloading;
        _track.IsVisible = kind == UpdateStatusKind.Downloading;
        _fill.Width = TrackWidth * Math.Clamp(_vm?.Progress ?? 0, 0, 100) / 100.0;
        ApplyBrushes();
        ToolTip.SetTip(this, UpdateStatusViewModel.ToolTip);
        Avalonia.Automation.AutomationProperties.SetName(this, _vm?.Text ?? string.Empty);
    }
}
