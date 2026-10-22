using Avalonia.Controls;
using Oranda.App.Shell.Menus;

namespace Oranda.App.Features.SessionMonitor;

/// <summary>세션 모니터 문서. 단추는 <see cref="SessionMonitorViewModel"/> 의 동작을 부른다(명령 ID 와 같은 동작).</summary>
public partial class SessionMonitorView : UserControl
{
    private readonly MonitorTableBinder<SessionItem> _grid;
    private SessionMonitorViewModel? _bound;

    public SessionMonitorView()
    {
        InitializeComponent();
        SessionList.ExtraMenuItems = () => ShellContextMenus.Factory?.Invoke("monitor") ?? [];
        SessionList.RightAlignedColumns = [3, 4];
        _grid = new MonitorTableBinder<SessionItem>(SessionList,
            ["SID,SERIAL#", "사용자", "상태", "경과", "블로커", "대기 이벤트", "SQL ID", "프로그램", "델타"],
            s => [s.KeyText, s.Username, s.Status, s.Elapsed, s.BlockedBy, s.Event, s.SqlId, s.Program, s.DeltaText],
            s => s.Row.Key);
        DataContextChanged += (_, _) =>
        {
            if (DataContext is SessionMonitorViewModel vm && !ReferenceEquals(_bound, vm))
            {
                _bound = vm;
                _grid.Attach(vm.Sessions, vm, nameof(SessionMonitorViewModel.SelectedSession), () => vm.SelectedSession, s => vm.SelectedSession = s);
                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(SessionMonitorViewModel.DeltaMode))
                    {
                        _grid.Refresh();
                    }
                };
            }
        };
        RefreshButton.Click += (_, _) => Run(vm => vm.RefreshAsync());
        KillButton.Click += (_, _) => Run(vm => vm.KillAsync(immediate: true));
        DisconnectButton.Click += (_, _) => Run(vm => vm.DisconnectAsync(immediate: true));
        TraceOnButton.Click += (_, _) => Run(vm => vm.SetTraceAsync(true));
        TraceOffButton.Click += (_, _) => Run(vm => vm.SetTraceAsync(false));
        DetailButton.Click += (_, _) => Run(vm => vm.LoadDetailAsync());
        SessionList.DoubleTapped += (_, _) => Run(vm => vm.LoadDetailAsync());
        OtherAccountButton.Click += (_, _) => Run(vm => vm.OpenWithOtherAccountAsync());
        AccessBanner.OtherAccountRequested += (_, _) => Run(vm => vm.OpenWithOtherAccountAsync());
    }

    private void Run(System.Func<SessionMonitorViewModel, System.Threading.Tasks.Task> action)
    {
        if (DataContext is SessionMonitorViewModel vm)
        {
            _ = action(vm);
        }
    }
}
