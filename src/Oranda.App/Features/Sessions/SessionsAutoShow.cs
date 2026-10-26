using Avalonia.Threading;
using Oranda.App.Features.ConnectionManager;
using Oranda.App.Shell.Docking;
using Oranda.App.Shell.Layout;
using Oranda.Core.Composition;
using Oranda.Core.Events;
using Oranda.Core.Shell;

namespace Oranda.App.Features.Sessions;

/// <summary>
/// 「세션」 도구 창을 보이고 숨기는 규칙(TSK-14-04, D-034). 순수 논리라 도킹을 모른다(보이는지·보이기·숨기기를 대리자로 받는다).
/// - 열린 작업 세션이 1개 이하에서 2개 이상이 되는 순간 한 번 보인다.
/// - 사용자가 그 창을 닫으면(세션이 2개 이상인 채로) 앱을 다시 켜기 전까지 몰래 다시 열지 않는다.
/// - 세션이 다시 1개 이하가 되면, 우리가 보였고 아직 보이는 창만 숨긴다(사용자가 직접 연 창은 그대로 둔다).
/// </summary>
public sealed class SessionsVisibility(Func<bool> isVisible, Action show, Action hide)
{
    /// <summary>창을 보이는 세션 수 기준.</summary>
    public const int Threshold = 2;

    private int _count;
    private bool _autoShown;
    private bool _dismissed;

    /// <summary>우리가 보인 창이 아직 우리 것으로 남아 있는지(시험용).</summary>
    public bool AutoShown => _autoShown;

    public bool Dismissed => _dismissed;

    /// <summary>열린 작업 세션 수가 바뀌었다.</summary>
    public void Update(int count)
    {
        var previous = _count;
        _count = count;
        if (count >= Threshold)
        {
            if (previous < Threshold && !_dismissed && !isVisible())
            {
                show();
                _autoShown = isVisible();
            }

            return;
        }

        if (_autoShown && isVisible())
        {
            hide();
        }

        _autoShown = false;
    }

    /// <summary>배치가 바뀌었다: 우리가 보인 창이 세션이 2개 이상인 채로 사라졌으면 사용자가 닫은 것이다.</summary>
    public void LayoutChanged()
    {
        if (_autoShown && _count >= Threshold && !isVisible())
        {
            _autoShown = false;
            _dismissed = true;
        }
    }
}

/// <summary>
/// 시작 뒤 열린 작업 세션 수를 지켜보며 「세션」 도구 창을 보이고 숨긴다(<see cref="SessionsVisibility"/>). 접속 서비스를 만들지 않고
/// 이미 만들어진 라우터만 본다(<see cref="SessionRouter.Find"/>, 시작 때 Oracle 어셈블리를 깨우지 않는다). 세션이 열리고 닫히는 이벤트와
/// 라우터의 바뀜을 받아 한 박자 뒤(접속 서비스 목록이 갱신된 뒤)에 센다.
/// </summary>
[OrandaService(typeof(IStartupTask))]
public sealed class SessionsAutoShow(DockWorkspace docking, IEventBus bus, IServiceProvider services) : IStartupTask
{
    public const string PanelId = "oranda.panel.sessions";

    private SessionRouter? _hooked;
    private SessionsVisibility? _visibility;
    private bool _queued;

    public StartupPhase Phase => StartupPhase.AfterFirstFrame;

    public Task RunAsync(CancellationToken ct)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _visibility = new SessionsVisibility(() => docking.IsToolVisible(PanelId), Show, () => docking.HideTool(PanelId));
            bus.Subscribe<SessionOpened>(this, _ => QueueRecount());
            bus.Subscribe<SessionClosed>(this, _ => QueueRecount());
            docking.LayoutChanged += (_, _) => _visibility.LayoutChanged();
            QueueRecount();
        });
        return Task.CompletedTask;
    }

    /// <summary>
    /// 보이되 왼쪽 묶음에서 보던 탭(스키마 브라우저 등)을 가로채지 않는다: 새 탭만 더하고 원래 활성 탭을 되돌려 놓는다.
    /// </summary>
    internal void Show()
    {
        var previous = docking.Capture().Root is { } root ? ActiveLeftTool(root) : null;
        docking.ShowTool(PanelId);
        if (previous is not null && previous != PanelId)
        {
            docking.ActivateTool(previous);
        }
    }

    private static string? ActiveLeftTool(DockNode root)
    {
        string? found = null;
        void Walk(DockNode node)
        {
            if (found is null && node.Kind == DockNodeKind.ToolGroup && LayoutPlanner.LocationOf(root, node) == DockLocation.Left)
            {
                found = node.ActiveTool;
            }

            foreach (var child in node.Children ?? [])
            {
                Walk(child);
            }
        }

        Walk(root);
        return found;
    }

    private void QueueRecount()
    {
        if (_queued)
        {
            return;
        }

        _queued = true;
        Dispatcher.UIThread.Post(
            () =>
            {
                _queued = false;
                Hook();
                _visibility?.Update(_hooked?.AllSessions.Count ?? 0);
            },
            DispatcherPriority.Background);
    }

    /// <summary>라우터가 생겼으면 그 바뀜을 듣는다(접속 서비스와 함께 생긴다).</summary>
    private void Hook()
    {
        if (_hooked is null && SessionRouter.Find(services) is { } router)
        {
            _hooked = router;
            router.Changed += (_, _) => QueueRecount();
        }
    }
}
