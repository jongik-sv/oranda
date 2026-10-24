using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Oranda.App.Features.Localization;
using Oranda.App.Features.SessionMonitor;
using Oranda.App.Shell;
using Oranda.App.Shell.Composition;
using Oranda.App.ViewModels;
using Oranda.Core.Events;
using Oranda.Core.Monitoring;
using Oranda.Core.Shell;

namespace Oranda.App.Features.BlockerAlert;

/// <summary>
/// 상태 표시줄 7번 구간(TSK-13-02): 「N 세션 막음」(빨강) / 「미커밋 N분」(주황, 10분 넘음) / 「감시 꺼짐」(회색, 권한 없음).
/// Lock 모니터 임계치 알림(oranda1 TSK-05-04)은 「⚠ N 세션 막음 · M 초」(빨강).
/// 누르면 Lock 모니터를 연다(LK-09). 생성자는 이벤트 버스·컨테이너만 받고 모니터 서비스·세션 관리자는 첫 조회 때 꺼낸다
/// (시작 경로에서 Oracle 어셈블리를 올리지 않는다).
/// </summary>
[OrandaStatusBarItem("oranda.status.blocker", 7, CollapseRank = 0)]
public sealed class BlockerStatusViewModel : ViewModelBase, IStatusBarItem, IDisposable
{
    private readonly BlockerWatcher? _watcher;
    private readonly ServiceContainer? _container;
    private MonitorSessionOpener? _backgroundOpener;
    private MonitorSessionOpener? _interactiveOpener;

    public BlockerStatusViewModel(IEventBus? events = null, ServiceContainer? container = null, TimeProvider? time = null)
    {
        _container = container;
        if (events is not null)
        {
            _watcher = new BlockerWatcher(
                events,
                (profile, ct) => OpenMonitorAsync(container, profile, ct),
                (session, sid, ct) => QueryAsync(container, session, sid, ct),
                time);
            Attach();
        }
    }

    /// <summary>시험용: 만들어 둔 감시기를 쓴다.</summary>
    internal BlockerStatusViewModel(BlockerWatcher watcher)
    {
        _watcher = watcher;
        Attach();
    }

    /// <summary>앱이 끝날 때(컨테이너가 부른다) 감시와 모니터 세션을 정리한다.</summary>
    public void Dispose() => _watcher?.Dispose();

    public BlockerAlertState State => _watcher?.State ?? BlockerAlertState.Hidden;

    public bool IsVisible => State.Kind != BlockerAlertKind.None;

    public string Text
    {
        get
        {
            var s = State;
            return s.Kind switch
            {
                BlockerAlertKind.Blocking when s.FromLockMonitor => Format("Blocker.chip.lockAlert", "⚠ {0} 세션 막음 · {1:N0} 초", s.BlockedCount, s.MaxWaitSeconds),
                BlockerAlertKind.Blocking => Join(Format("Blocker.chip.blocking", "{0} 세션 막음", s.BlockedCount),
                    s.MinutesUntilKilled is { } m ? Format("Blocker.chip.killIn", "{0}분 뒤 서버가 이 세션을 끊음", m) : null),
                BlockerAlertKind.LongTransaction => Format("Blocker.chip.longTxn", "미커밋 {0}분", s.PendingMinutes),
                BlockerAlertKind.Disabled => UiText.Get("Blocker.chip.disabled", "감시 꺼짐"),
                _ => string.Empty,
            };
        }
    }

    public string ToolTip
    {
        get
        {
            var s = State;
            return s.Kind switch
            {
                BlockerAlertKind.Blocking when s.FromLockMonitor => Format("Blocker.tip.lockAlert",
                    "Lock 모니터: 잠금 대기가 알림 기준을 넘었습니다({0} 세션 대기, 가장 오래 {1:N0} 초). 눌러서 Lock 모니터 열기", s.BlockedCount, s.MaxWaitSeconds),
                BlockerAlertKind.Blocking => Join(Format("Blocker.tip.blocking", "이 세션이 {0}개 세션을 막고 있습니다. 눌러서 Lock 모니터 열기", s.BlockedCount),
                    s.MinutesUntilKilled is { } m ? Format("Blocker.tip.killIn", "서버 설정(MAX_IDLE_BLOCKER_TIME)에 따라 {0}분 뒤 서버가 이 세션을 끊습니다.", m) : null),
                BlockerAlertKind.LongTransaction => Join(Format("Blocker.tip.longTxn", "미커밋 상태가 {0}분 넘게 이어지고 있습니다. 커밋하거나 롤백하세요.", s.PendingMinutes),
                    DisabledReason(s.Reason)),
                BlockerAlertKind.Disabled => DisabledReason(s.Reason) ?? string.Empty,
                _ => string.Empty,
            };
        }
    }

    /// <summary>칩 색 계열. View 가 리소스 키로 바꾼다.</summary>
    public BlockerAlertKind Kind => State.Kind;

    /// <summary>눌렀을 때: Lock 모니터를 연다(대기 트리·보유자 영향 순위).</summary>
    public async Task OnClickAsync()
    {
        if (_watcher is { } w && State is { Kind: BlockerAlertKind.Disabled, Reason: BlockerDisabledReason.NoCredential })
        {
            // 사용자 동작일 때만 비밀번호를 묻는다. 취소하면 회색 칩이 그대로 남는다.
            if (_container is not null)
            {
                _interactiveOpener ??= SessionMonitorCommands.CreateOpener(_container, "blockerAlert");
                await w.EnableAsync(_interactiveOpener.OpenAsync);
            }

            return;
        }

        await OpenLockMonitorAsync();
    }

    /// <summary>Lock 모니터를 연다(이미 열려 있으면 앞으로 내고 다시 읽는다).</summary>
    public static Task OpenLockMonitorAsync() => ShellCommandRunner.RunAsync(LockMonitor.LockMonitorCommands.Open);

    private void Attach()
    {
        _watcher!.Changed += (_, _) => OnWatcherChanged();
        if (UiText.Current is { } text)
        {
            text.LanguageChanged += (_, _) => OnWatcherChanged();
        }
    }

    private static string? DisabledReason(BlockerDisabledReason reason) => reason switch
    {
        BlockerDisabledReason.NoPrivilege => UiText.Get("Blocker.tip.noPrivilege", "권한이 없어 감시 꺼짐(V$ 조회 권한 필요)"),
        BlockerDisabledReason.NoCredential => UiText.Get("Blocker.tip.noCredential", "비밀번호가 저장되지 않아 감시 꺼짐. 누르면 비밀번호를 넣고 켭니다."),
        _ => null,
    };

    private static string Join(string first, string? second) => second is null ? first : first + " · " + second;

    private static string Format(string key, string fallback, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, UiText.Get(key, fallback), args);

    /// <summary>모니터 세션은 작업 세션의 프로필로 열고, 비밀번호를 묻는 창은 띄우지 않는다(백그라운드 경로).</summary>
    private Task<Oranda.Core.Sessions.ISession?> OpenMonitorAsync(ServiceContainer? container, Oranda.Core.Connection.ConnectionProfile profile, CancellationToken ct)
    {
        if (container is null)
        {
            return Task.FromResult<Oranda.Core.Sessions.ISession?>(null);
        }

        _backgroundOpener ??= SessionMonitorCommands.CreateOpener(container, "blockerAlert");
        _backgroundOpener.AskPassword = null;
        _backgroundOpener.PickProfile = null;
        return _backgroundOpener.OpenAsync(profile, ct);
    }

    private static Task<BlockerSnapshot> QueryAsync(ServiceContainer? container, Oranda.Core.Sessions.ISession session, long sid, CancellationToken ct) =>
        (container?.GetService<IMonitorService>() ?? throw new NotSupportedException()).GetMyBlockingAsync(session, sid, ct);

    private void OnWatcherChanged()
    {
        void Raise()
        {
            OnPropertyChanged(nameof(State));
            OnPropertyChanged(nameof(Kind));
            OnPropertyChanged(nameof(Text));
            OnPropertyChanged(nameof(ToolTip));
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

/// <summary>
/// 7번 구간 View: 빨강·주황은 채운 칩(글자색은 환경 칩과 같은 토큰), 회색은 점 + 글. 눌러서 블로킹 트리를 연다.
/// 색은 기존 상태 색 리소스 키만 쓴다(Light·Dark·고대비가 같이 따라온다).
/// </summary>
public sealed class BlockerStatusView : UserControl
{
    private readonly Border _chip = new() { CornerRadius = new CornerRadius(3), Padding = new Thickness(6, 0) };
    private readonly TextBlock _text = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private BlockerStatusViewModel? _vm;

    public BlockerStatusView()
    {
        _chip.Child = _text;
        Content = _chip;
        Cursor = new Cursor(StandardCursorType.Hand);
        Background = Brushes.Transparent;
        PointerPressed += async (_, e) =>
        {
            if (_vm is not null && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                await _vm.OnClickAsync();
            }
        };
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null)
            {
                _vm.PropertyChanged -= OnChanged;
            }

            _vm = DataContext as BlockerStatusViewModel;
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
        ToolTip.SetTip(this, _vm?.ToolTip);
        Avalonia.Automation.AutomationProperties.SetName(this, _vm?.Text ?? string.Empty);
        var (background, foreground) = _vm?.Kind switch
        {
            BlockerAlertKind.Blocking => ("OrandaEnvProductionBrush", "OrandaEnvProductionForegroundBrush"),
            BlockerAlertKind.LongTransaction => ("OrandaUncommittedBrush", "OrandaEnvProductionForegroundBrush"),
            _ => (null, "OrandaButtonDisabledForegroundBrush"),
        };
        if (background is null)
        {
            _chip.ClearValue(Border.BackgroundProperty);
        }
        else
        {
            _chip.Bind(Border.BackgroundProperty, _chip.GetResourceObservable(background));
        }

        _text.Bind(TextBlock.ForegroundProperty, _text.GetResourceObservable(foreground));
        _text.FontWeight = background is null ? FontWeight.Normal : FontWeight.SemiBold;
    }
}
