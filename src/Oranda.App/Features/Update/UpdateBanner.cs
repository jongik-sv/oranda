using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
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

/// <summary>알림 띠 View: 글 하나 + 버튼(적용/내려받기 · 자세히 · 나중에). 사라지면 자리도 차지하지 않는다.</summary>
public sealed class UpdateBanner : UserControl
{
    private readonly TextBlock _text = new() { VerticalAlignment = VerticalAlignment.Center, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly Button _primary = new() { Name = "UpdateBannerPrimaryButton", Classes = { "accent" }, MinHeight = 26, Padding = new Thickness(10, 2) };
    private readonly Button _details = new() { Name = "UpdateBannerDetailsButton", MinHeight = 26, Padding = new Thickness(10, 2) };
    private readonly Button _later = new() { Name = "UpdateBannerLaterButton", MinHeight = 26, Padding = new Thickness(10, 2) };
    private UpdateBannerViewModel? _vm;

    public UpdateBanner()
    {
        Name = "UpdateBanner";
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Children = { _primary, _details, _later } };
        DockPanel.SetDock(buttons, Avalonia.Controls.Dock.Right);
        var strip = new Border
        {
            Margin = new Thickness(4, 4, 4, 0),
            Padding = new Thickness(10, 6),
            CornerRadius = new CornerRadius(3),
            BorderThickness = new Thickness(1),
            Child = new DockPanel { Children = { buttons, _text } },
        };
        strip.Bind(Border.BackgroundProperty, strip.GetResourceObservable("OrandaToolbarBackgroundBrush"));
        strip.Bind(Border.BorderBrushProperty, strip.GetResourceObservable("OrandaButtonAccentBrush"));
        Content = strip;
        IsVisible = false;

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

    private void Refresh()
    {
        var kind = _vm?.Kind ?? UpdateBannerKind.None;
        IsVisible = kind != UpdateBannerKind.None;
        _text.Text = _vm?.Text ?? string.Empty;
        _primary.Content = UpdateBannerViewModel.PrimaryText(kind);
        _details.Content = UpdateBannerViewModel.DetailsText;
        _later.Content = UpdateBannerViewModel.LaterText;
        AutomationProperties.SetName(this, _text.Text);
    }
}
