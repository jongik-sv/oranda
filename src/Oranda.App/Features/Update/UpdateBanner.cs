using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Oranda.App.Features.Localization;
using Oranda.App.Shell;
using Oranda.App.ViewModels;

namespace Oranda.App.Features.Update;

/// <summary>알림 띠가 지금 알리는 것.</summary>
public enum UpdateBannerKind
{
    None,

    /// <summary>새 버전을 찾았다(자동 내려받기가 꺼져 있어 직접 받아야 한다).</summary>
    Available,

    /// <summary>내려받기를 마쳐 적용만 남았다.</summary>
    Ready,
}

/// <summary>
/// 주 창 위쪽 업데이트 알림 띠의 모델(비모달). 새 버전을 찾거나 내려받기를 마치면 버전마다 한 번 띄우고,
/// 「나중에」로 닫은 버전은 이 실행에서 다시 띄우지 않는다. 상태 표시줄 항목과는 별개로 둔다.
/// </summary>
public sealed class UpdateBannerViewModel : ViewModelBase
{
    private readonly UpdateService _service;
    private readonly HashSet<string> _dismissed = [];

    public UpdateBannerViewModel(UpdateService service)
    {
        _service = service;
        service.Changed += OnServiceChanged;
        if (UiText.Current is { } text)
        {
            text.LanguageChanged += (_, _) => OnServiceChanged(null, EventArgs.Empty);
        }
    }

    public bool IsVisible => Kind != UpdateBannerKind.None;

    /// <summary>알리는 버전(받을 것 또는 받은 것).</summary>
    public string? Version => _service.Phase == UpdatePhase.Ready ? _service.ReadyVersion : _service.Offer?.Version;

    public UpdateBannerKind Kind
    {
        get
        {
            var kind = _service.Phase switch
            {
                UpdatePhase.Ready => UpdateBannerKind.Ready,
                UpdatePhase.Available when !_service.IsSkipped && !_service.AutoDownload => UpdateBannerKind.Available,
                _ => UpdateBannerKind.None,
            };
            return kind != UpdateBannerKind.None && Version is { } v && !_dismissed.Contains(v) ? kind : UpdateBannerKind.None;
        }
    }

    public string Text => Kind switch
    {
        UpdateBannerKind.Ready => Format("Update.banner.ready", "Oranda {0} 이 준비되었습니다", Version),
        UpdateBannerKind.Available => Format("Update.banner.available", "Oranda {0} 을 내려받을 수 있습니다", Version),
        _ => string.Empty,
    };

    public static string PrimaryText(UpdateBannerKind kind) => kind == UpdateBannerKind.Available
        ? UiText.Get("Update.banner.download", "내려받기")
        : UiText.Get("Update.banner.restart", "다시 시작해 적용");

    public static string DetailsText => UiText.Get("Update.banner.details", "자세히");

    public static string LaterText => UiText.Get("Update.banner.later", "나중에");

    /// <summary>「다시 시작해 적용」(준비됨) 또는 「내려받기」(찾음).</summary>
    public async Task PrimaryAsync()
    {
        if (Kind == UpdateBannerKind.Ready)
        {
            await _service.ApplyAndRestartAsync();
        }
        else if (Kind == UpdateBannerKind.Available)
        {
            await _service.DownloadAsync();
        }
    }

    /// <summary>「자세히」: 업데이트 창을 연다.</summary>
    public static Task OpenDetailsAsync() => ShellCommandRunner.RunAsync("oranda.help.checkUpdate");

    /// <summary>「나중에」: 이 버전은 이 실행에서 다시 띄우지 않는다.</summary>
    public void Dismiss()
    {
        if (Version is { } version)
        {
            _dismissed.Add(version);
        }

        RaiseAll();
    }

    private static string Format(string key, string fallback, params object?[] args) =>
        string.Format(System.Globalization.CultureInfo.CurrentCulture, UiText.Get(key, fallback), args);

    private void RaiseAll()
    {
        OnPropertyChanged(nameof(Kind));
        OnPropertyChanged(nameof(Text));
        OnPropertyChanged(nameof(IsVisible));
    }

    private void OnServiceChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            RaiseAll();
        }
        else
        {
            Dispatcher.UIThread.Post(RaiseAll);
        }
    }
}

/// <summary>
/// 알림 띠 View: 종류별 꽉 찬 색 바탕(있음 주황 · 준비됨 초록, 상태 표시줄 배지와 같은 OrandaUpdate*Brush) 위에 아이콘 + 글(버전은 굵게) + 버튼.
/// 주 버튼(내려받기 · 다시 시작해 적용)은 글자색을 바탕으로 쓴 채움 버튼이고, 「자세히」 「나중에」는 테두리만 있는 보조 버튼이다.
/// 사라지면 자리도 차지하지 않는다. 글이 길어져 줄바꿈되어도 버튼은 오른쪽에 그대로 남는다.
/// </summary>
public sealed class UpdateBanner : UserControl
{
    // 16 단위 칸에 그린 선 아이콘(상태 표시줄 배지와 같은 모양): 내려받기(화살표 + 바닥선), 다시 시작(되돌이 화살표).
    private static readonly Geometry DownloadIcon = Geometry.Parse("M8,2 L8,10 M4.5,6.5 L8,10 L11.5,6.5 M3,13.5 L13,13.5");
    private static readonly Geometry RestartIcon = Geometry.Parse("M13,8 A5,5 0 1 1 11.4,4.3 M13,2.5 L13,5.5 L10,5.5");

    private readonly Border _strip;
    private readonly Avalonia.Controls.Shapes.Path _icon = new()
    {
        Width = 16,
        Height = 16,
        Stretch = Stretch.Uniform,
        StrokeThickness = 1.6,
        StrokeLineCap = PenLineCap.Round,
        StrokeJoin = PenLineJoin.Round,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 0, 8, 0),
    };

    private readonly Run _prefix = new();
    private readonly Run _version = new() { FontWeight = FontWeight.Bold };
    private readonly Run _suffix = new();
    private readonly TextBlock _text = new() { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MinWidth = 160 };
    private readonly Button _primary = new() { Name = "UpdateBannerPrimaryButton", MinHeight = 26, Padding = new Thickness(10, 2), FontWeight = FontWeight.SemiBold };
    private readonly Button _details = new() { Name = "UpdateBannerDetailsButton", MinHeight = 26, Padding = new Thickness(10, 2) };
    private readonly Button _later = new() { Name = "UpdateBannerLaterButton", MinHeight = 26, Padding = new Thickness(10, 2) };
    private UpdateBannerViewModel? _vm;

    public UpdateBanner()
    {
        Name = "UpdateBanner";
        _text.Inlines = [_prefix, _version, _suffix];
        _text.FontWeight = FontWeight.Medium;

        // 좁은 창: 글이 먼저 줄바꿈되고 버튼 묶음은 자기 폭을 지킨다(DockPanel 오른쪽 도킹).
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Children = { _primary, _details, _later } };
        DockPanel.SetDock(buttons, Avalonia.Controls.Dock.Right);
        DockPanel.SetDock(_icon, Avalonia.Controls.Dock.Left);
        _strip = new Border
        {
            Margin = new Thickness(4, 4, 4, 0),
            Padding = new Thickness(10, 5),
            CornerRadius = new CornerRadius(3),
            Child = new DockPanel { Children = { _icon, buttons, _text } },
        };
        Content = _strip;
        IsVisible = false;
        ApplyPalette(UpdateBannerKind.Available);

        // 단추는 비활성화하지 않는다(지역 색이 테마의 비활성 모양을 덮는다). 단추 모양(채움/테두리)은 지역 값으로 정해 테마 기본 스타일에 가려지지 않게 하고, 마우스를 올린 표시는 투명도로 준다.
        Styles.Add(new Style(x => x.OfType<Button>().Class("banner-button").Class(":pointerover")) { Setters = { new Setter(OpacityProperty, 0.92) } });
        Styles.Add(new Style(x => x.OfType<Button>().Class("banner-button").Class(":pressed")) { Setters = { new Setter(OpacityProperty, 0.85) } });
        foreach (var button in new[] { _primary, _details, _later })
        {
            button.Classes.Add("banner-button");
        }

        _primary.Click += async (_, _) =>
        {
            if (_vm is not null)
            {
                await _vm.PrimaryAsync();
            }
        };
        _details.Click += async (_, _) => await UpdateBannerViewModel.OpenDetailsAsync();
        _later.Click += (_, _) => _vm?.Dismiss();
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null)
            {
                _vm.PropertyChanged -= OnChanged;
            }

            _vm = DataContext as UpdateBannerViewModel;
            if (_vm is not null)
            {
                _vm.PropertyChanged += OnChanged;
            }

            Refresh();
        };
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

    /// <summary>색은 전부 테마 리소스 키로 묶는다(라이트/다크 전환을 따라간다).</summary>
    private void ApplyPalette(UpdateBannerKind kind)
    {
        var fill = kind == UpdateBannerKind.Ready ? "OrandaUpdateReadyBrush" : "OrandaUpdateAvailableBrush";
        const string Fg = "OrandaUpdateBadgeForegroundBrush";
        _strip.Bind(Border.BackgroundProperty, _strip.GetResourceObservable(fill));
        _text.Bind(TextBlock.ForegroundProperty, _text.GetResourceObservable(Fg));
        _icon.Bind(Shape.StrokeProperty, _icon.GetResourceObservable(Fg));

        // 주 버튼: 글자색 바탕에 종류 색 글자(흰/검정 바탕 위 4.5:1 이상).
        _primary.Bind(BackgroundProperty, _primary.GetResourceObservable(Fg));
        _primary.Bind(ForegroundProperty, _primary.GetResourceObservable(fill));
        _primary.Bind(BorderBrushProperty, _primary.GetResourceObservable(Fg));
        _primary.BorderThickness = new Thickness(1);

        // 보조 버튼: 바탕 없이 글자색 테두리·글자.
        foreach (var button in new[] { _details, _later })
        {
            button.Background = Brushes.Transparent;
            button.Bind(ForegroundProperty, button.GetResourceObservable(Fg));
            button.Bind(BorderBrushProperty, button.GetResourceObservable(Fg));
            button.BorderThickness = new Thickness(1);
        }
    }

    private void Refresh()
    {
        var kind = _vm?.Kind ?? UpdateBannerKind.None;
        IsVisible = kind != UpdateBannerKind.None;
        if (kind != UpdateBannerKind.None)
        {
            ApplyPalette(kind);
        }

        _icon.Data = kind == UpdateBannerKind.Ready ? RestartIcon : DownloadIcon;
        var label = _vm?.Text ?? string.Empty;
        SetText(label, _vm?.Version);
        AutomationProperties.SetName(_text, label); // Inlines 를 쓰면 TextBlock.Text 가 비므로 이름을 직접 준다
        _primary.Content = UpdateBannerViewModel.PrimaryText(kind);
        _details.Content = UpdateBannerViewModel.DetailsText;
        _later.Content = UpdateBannerViewModel.LaterText;
        AutomationProperties.SetName(this, label);
    }

    /// <summary>버전 번호만 굵게 나눈다. 번역문에서 버전을 못 찾으면 통째로 보통 글자로 둔다.</summary>
    private void SetText(string text, string? version)
    {
        var at = string.IsNullOrEmpty(version) ? -1 : text.IndexOf(version, StringComparison.Ordinal);
        if (at < 0)
        {
            _prefix.Text = text;
            _version.Text = string.Empty;
            _suffix.Text = string.Empty;
            return;
        }

        _prefix.Text = text[..at];
        _version.Text = version;
        _suffix.Text = text[(at + version!.Length)..];
    }
}
