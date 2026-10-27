using Avalonia.Controls;
using Oranda.Core.Grid;
using Oranda.App.Shell.Menus;

namespace Oranda.App.Features.SessionMonitor;

/// <summary>세션 모니터 문서. 단추는 <see cref="SessionMonitorViewModel"/> 의 동작을 부른다(명령 ID 와 같은 동작).</summary>
public partial class SessionMonitorView : UserControl
{
    private readonly MonitorGridBinder<SessionItem> _grid;
    private SessionMonitorViewModel? _bound;

    public SessionMonitorView()
    {
        InitializeComponent();
        SessionList.ExtraMenuItems = () => ShellContextMenus.Factory?.Invoke("monitor") ?? [];
        _grid = new MonitorGridBinder<SessionItem>(SessionList, Columns, s => s.Row.Key)
        {
            CellEmphasis = (_, c) => c == BlockerColumn, // 블로커 칸에 값이 있으면 위험색 글자
        };
        DataContextChanged += (_, _) =>
        {
            if (DataContext is SessionMonitorViewModel vm && !ReferenceEquals(_bound, vm))
            {
                _bound = vm;
                _grid.AttachStatusLine(StatusLine, vm, () => vm.StatusText);
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
        SessionList.RowDoubleTapped += (_, _) => Run(vm => vm.LoadDetailAsync());
        OtherAccountButton.Click += (_, _) => Run(vm => vm.OpenWithOtherAccountAsync());
        AccessBanner.OtherAccountRequested += (_, _) => Run(vm => vm.OpenWithOtherAccountAsync());
    }

    /// <summary>블로커 열 번호(<see cref="Columns"/> 안).</summary>
    private const int BlockerColumn = 4;

    /// <summary>세션 목록 열. 경과·블로커는 숫자라 오른쪽 맞춤·수로 정렬된다.</summary>
    internal static readonly MonitorColumn<SessionItem>[] Columns =
    [
        new("SID,SERIAL#", ColumnDataKind.Text, s => s.KeyText),
        new("사용자", ColumnDataKind.Text, s => s.Username),
        new("상태", ColumnDataKind.Text, s => s.Status),
        new("경과(초)", ColumnDataKind.Number, s => s.Row.LastCallElapsedSeconds),
        new("블로커", ColumnDataKind.Number, s => s.Row.BlockingSession?.Sid),
        new("대기 이벤트", ColumnDataKind.Text, s => s.Event),
        new("SQL ID", ColumnDataKind.Text, s => s.SqlId),
        new("프로그램", ColumnDataKind.Text, s => s.Program),
        new("델타", ColumnDataKind.Text, s => s.DeltaText),
    ];

    private void Run(System.Func<SessionMonitorViewModel, System.Threading.Tasks.Task> action)
    {
        if (DataContext is SessionMonitorViewModel vm)
        {
            _ = action(vm);
        }
    }
}
