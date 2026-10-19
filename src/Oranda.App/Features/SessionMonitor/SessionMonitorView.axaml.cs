using Avalonia.Controls;

namespace Oranda.App.Features.SessionMonitor;

/// <summary>세션 모니터 문서. 단추는 <see cref="SessionMonitorViewModel"/> 의 동작을 부른다(명령 ID 와 같은 동작).</summary>
public partial class SessionMonitorView : UserControl
{
    public SessionMonitorView()
    {
        InitializeComponent();
        RefreshButton.Click += (_, _) => Run(vm => vm.RefreshAsync());
        KillButton.Click += (_, _) => Run(vm => vm.KillAsync(immediate: true));
        DisconnectButton.Click += (_, _) => Run(vm => vm.DisconnectAsync(immediate: true));
        TraceOnButton.Click += (_, _) => Run(vm => vm.SetTraceAsync(true));
        TraceOffButton.Click += (_, _) => Run(vm => vm.SetTraceAsync(false));
        DetailButton.Click += (_, _) => Run(vm => vm.LoadDetailAsync());
        SessionList.DoubleTapped += (_, _) => Run(vm => vm.LoadDetailAsync());
    }

    private void Run(System.Func<SessionMonitorViewModel, System.Threading.Tasks.Task> action)
    {
        if (DataContext is SessionMonitorViewModel vm)
        {
            _ = action(vm);
        }
    }
}
