using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Oranda.App.Features.Localization;
using Oranda.App.Shell.Composition;
using Oranda.App.ViewModels;
using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Sessions;
using Oranda.Core.Shell;

namespace Oranda.App.Features.ConnectionManager;

/// <summary>
/// 상태 표시줄 1번 구간(ui-shell.md 1.2): 환경 칩 + <c>사용자@접속 이름</c> + 서버 버전(<c>19c</c>·<c>26ai</c>).
/// Orange 의 세션 콤보처럼 지금 어디에 붙어 있는지 늘 보이게 한다. 누르면 접속 관리자를 연다.
/// 활성 문서의 접속(<see cref="SessionRouter.ShownSession"/>)을 따른다. 라우터는 접속 서비스와 함께 생기므로 그 전에는
/// 세션 열림·닫힘 이벤트만 듣는다(Oracle 어셈블리를 깨우지 않는다).
/// </summary>
[OrandaStatusBarItem("oranda.status.connection", 1, CollapseRank = 0)]
public sealed class ConnectionStatusViewModel : ViewModelBase, IStatusBarItem
{
    private readonly List<SessionOpened> _open = [];
    private readonly ServiceContainer? _container;
    private readonly Func<SessionRouter?> _router;
    private SessionRouter? _hooked;

    public ConnectionStatusViewModel(IEventBus? events = null, ServiceContainer? container = null, Func<SessionRouter?>? router = null)
    {
        _container = container;
        _router = router ?? (() => SessionRouter.Find(container));
        events?.Subscribe<SessionOpened>(this, e => Post(() => Opened(e)));
        events?.Subscribe<SessionClosed>(this, e => Post(() => Closed(e.SessionId)));
        UiText.WatchLanguage((_, _) => Post(Changed)); // 언어가 바뀌면 글자를 다시 만든다
    }

    public bool IsVisible => true;

    /// <summary>
    /// 보이는 세션: 라우터가 있으면 활성 문서의 접속(접속 안 됨 탭이면 null), 없으면 가장 최근에 연 작업 세션. 없으면 null.
    /// </summary>
    public SessionOpened? Current => Hook() is { } router
        ? router.ShownSession is { } s ? new SessionOpened(s.Id, s.Profile, s.Server, s.Purpose) : null
        : _open.Count == 0 ? null : _open[^1];

    public string Text => Current is { } s
        ? $"{s.Profile.User}@{s.Profile.Name}  {ShortVersion(s.Server.Version)}".TrimEnd() + (ShownBroken ? UiText.Get("Execution.status.brokenSuffix", " (끊김)") : string.Empty)
        : _hooked?.ShownMissing is { } missing
            ? UiText.Format("Execution.status.notConnectedNamed", "접속 안 됨({0})", _hooked.ProfileName(missing) ?? "?")
            : UiText.Get("Execution.status.notConnected", "접속 안 됨");

    /// <summary>보이는 접속이 끊겼다(콤보의 「(끊김)」 과 같다).</summary>
    private bool ShownBroken => _hooked?.ShownSession is { } shown && _hooked.IsBroken(shown);

    public string EnvironmentText => EnvironmentTextOf(Current?.Profile.Environment);

    /// <summary>환경 칩 색 리소스 키(테두리 색과 같다).</summary>
    public string? EnvironmentBrushKey => EnvironmentBrushKeyOf(Current?.Profile.Environment);

    /// <summary>환경 칩 글(개발·테스트·운영). 환경이 없으면 빈 글.</summary>
    public static string EnvironmentTextOf(ConnectionEnvironment? environment) => environment switch
    {
        ConnectionEnvironment.Production => UiText.Get("Execution.status.env.production", "운영"),
        ConnectionEnvironment.Test => UiText.Get("Execution.status.env.test", "테스트"),
        ConnectionEnvironment.Development => UiText.Get("Execution.status.env.development", "개발"),
        _ => string.Empty,
    };

    /// <summary>환경 칩 색 리소스 키(창 테두리 색과 같다). 환경이 없으면 null.</summary>
    public static string? EnvironmentBrushKeyOf(ConnectionEnvironment? environment) => environment switch
    {
        ConnectionEnvironment.Production => "OrandaEnvProductionBrush",
        ConnectionEnvironment.Test => "OrandaEnvTestBrush",
        ConnectionEnvironment.Development => "OrandaEnvDevelopmentBrush",
        _ => null,
    };

    public string ToolTip => Current is { } s
        ? UiText.Format("Execution.status.connTip", "{0}@{1} · Oracle {2} · SID {3} — 눌러서 접속 관리자 열기",
            s.Profile.User, s.Profile.DataSource, s.Server.Version, s.Server.SessionId.ToString(CultureInfo.InvariantCulture))
        : UiText.Get("Execution.status.connTipNone", "접속 관리자 열기");

    /// <summary><c>23.26.0.0.0</c> → <c>26ai</c>, <c>19.3.0.0.0</c> → <c>19c</c>. 모르면 빈 글.</summary>
    public static string ShortVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return string.Empty;
        }

        var parts = version.Split('.');
        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var major))
        {
            return string.Empty;
        }

        // 23ai 이후 릴리스 갱신은 둘째 자리가 연도(23.26 = 26ai)다.
        if (major >= 23 && parts.Length > 1 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minor) && minor >= 23)
        {
            return minor.ToString(CultureInfo.InvariantCulture) + "ai";
        }

        // 11g 는 11.2 만 들어오므로 둘째 자리까지 보인다(TSK-03-07).
        if (major < 12)
        {
            return parts.Length > 1 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minor11)
                ? string.Create(CultureInfo.InvariantCulture, $"{major}.{minor11}")
                : major.ToString(CultureInfo.InvariantCulture) + "g";
        }

        return major >= 23 ? major.ToString(CultureInfo.InvariantCulture) + "ai" : major.ToString(CultureInfo.InvariantCulture) + "c";
    }

    public Task OpenManagerAsync() =>
        _container?.GetService<ICommandRegistry>() is { } registry && _container.GetService<IContextKeys>() is { } keys
            ? registry.ExecuteAsync("oranda.conn.manage", keys)
            : Task.CompletedTask;

    private void Opened(SessionOpened e)
    {
        if (e.Purpose != SessionPurpose.Work)
        {
            return; // 모니터용 전용 세션은 표시하지 않는다.
        }

        _open.RemoveAll(s => s.SessionId == e.SessionId);
        _open.Add(e);
        Changed();
    }

    private void Closed(Guid id)
    {
        if (_open.RemoveAll(s => s.SessionId == id) > 0)
        {
            Changed();
        }
    }

    /// <summary>라우터가 생겼으면 그 바뀜(탭 전환·콤보 선택)을 듣는다.</summary>
    private SessionRouter? Hook()
    {
        var router = _router();
        if (router is not null && !ReferenceEquals(router, _hooked))
        {
            _hooked = router;
            router.Changed += (_, _) => Changed();
        }

        return router;
    }

    private void Changed()
    {
        OnPropertyChanged(nameof(Current));
        OnPropertyChanged(nameof(Text));
        OnPropertyChanged(nameof(EnvironmentText));
        OnPropertyChanged(nameof(EnvironmentBrushKey));
        OnPropertyChanged(nameof(ToolTip));
    }

    private static void Post(Action a)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            a();
        }
        else
        {
            Dispatcher.UIThread.Post(a);
        }
    }
}

/// <summary>1번 구간 View: 환경 칩(색 바탕 글자) + 접속 글. 누르면 접속 관리자.</summary>
public sealed class ConnectionStatusView : UserControl
{
    private readonly Border _chip = new() { CornerRadius = new CornerRadius(3), Padding = new Thickness(5, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _chipText = new() { FontSize = 11, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _text = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private ConnectionStatusViewModel? _vm;

    public ConnectionStatusView()
    {
        _chip.Child = _chipText;
        _chipText.Bind(TextBlock.ForegroundProperty, _chipText.GetResourceObservable("OrandaEnvChipForegroundBrush")); // 색은 리소스 키로만(ui-shell 6장)
        Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { _chip, _text } };
        Cursor = new Cursor(StandardCursorType.Hand);
        Background = Brushes.Transparent; // 글자 사이도 눌리게
        PointerPressed += async (_, e) =>
        {
            if (_vm is not null && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                await _vm.OpenManagerAsync();
            }
        };
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null)
            {
                _vm.PropertyChanged -= OnChanged;
            }

            _vm = DataContext as ConnectionStatusViewModel;
            if (_vm is not null)
            {
                _vm.PropertyChanged += OnChanged;
            }

            Refresh();
        };
    }

    private void OnChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => Refresh();

    private void Refresh()
    {
        _text.Text = _vm?.Text ?? string.Empty;
        _text.Opacity = _vm?.Current is null ? 0.65 : 1;
        ToolTip.SetTip(this, _vm?.ToolTip);
        Avalonia.Automation.AutomationProperties.SetName(this, _vm?.Text ?? string.Empty);
        _chipText.Text = _vm?.EnvironmentText ?? string.Empty;
        _chip.IsVisible = _vm?.EnvironmentBrushKey is not null;
        if (_vm?.EnvironmentBrushKey is { } key)
        {
            _chip.Bind(Border.BackgroundProperty, _chip.GetResourceObservable(key));
        }
    }
}
