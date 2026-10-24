using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Oranda.App.Shell.Menus;

namespace Oranda.App.Features.ExecutionPlan;

/// <summary>
/// 실행 계획 창. 고정·비교 단추는 <see cref="PlanViewModel"/> 의 동작을 부른다(명령 ID 와 같은 동작).
/// 그래프 탭(TSK-06-04): 노드 누르기 = 트리와 같은 단계 고르기, 맞춤·확대·축소. 보고서 탭: 새로 고침·브라우저·복사.
/// 「통계 보기」「자식 커서」 「튜닝 도우미」 는 다른 기능의 명령을 ID 로 부른다.
/// </summary>
public partial class PlanView : UserControl
{
    private PlanViewModel? _vm;

    public PlanView()
    {
        InitializeComponent();
        ShellContextMenus.Attach(PlanList, "plan");
        PinButton.Click += (_, _) => Run(vm => vm.PinCurrent());
        CompareButton.Click += (_, _) => Run(vm => vm.CompareWithPinned());
        UnpinButton.Click += (_, _) => Run(vm => vm.Unpin());

        GraphCanvas.NodeClicked += (_, id) => Run(vm => vm.SelectGraphStep(id));
        FitButton.Click += (_, _) => GraphCanvas.Fit();
        ZoomInButton.Click += (_, _) => GraphCanvas.ZoomBy(1.2);
        ZoomOutButton.Click += (_, _) => GraphCanvas.ZoomBy(1 / 1.2);
        ExpandAllButton.Click += (_, _) => Run(vm => vm.ExpandAllGraph());

        StatsButton.Click += async (_, _) => await RunAsync(vm => vm.ShowTableStatsAsync());
        ChildCursorsButton.Click += async (_, _) => await RunAsync(vm => vm.ShowChildCursorsAsync());
        TuningReportButton.Click += (_, _) => _ = _vm?.LastCursor is { } cursor
            ? Tuning.Report.TuningReportNavigation.OpenAsync(cursor, _vm.LastSession)
            : Shell.ShellCommandRunner.RunAsync(Tuning.Report.TuningReportNavigation.CommandId);
        GraphStatsButton.Click += async (_, _) => await RunAsync(vm => vm.ShowTableStatsAsync());
        GraphAssistantButton.Click += (_, _) => RunCommand(PlanViewModel.AssistantCommandId);
        ConnectionManagerButton.Click += (_, _) => RunCommand(PlanViewModel.ConnectionManagerCommandId);
        ShowTreeButton.Click += (_, _) => Run(vm => vm.SelectedTabIndex = 0);

        ReportRefreshButton.Click += async (_, _) => await RunAsync(vm => vm.LoadReportAsync());
        ReportRetryButton.Click += async (_, _) => await RunAsync(vm => vm.LoadReportAsync());
        BrowserButton.Click += async (_, _) => await RunAsync(vm => vm.OpenReportInBrowserAsync());
        ReportCopyButton.Click += async (_, _) =>
        {
            if (DataContext is PlanViewModel { ReportText.Length: > 0 } vm && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(vm.ReportText);
            }
        };
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnVmPropertyChanged;
        }

        _vm = DataContext as PlanViewModel;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnVmPropertyChanged;
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 트리에서 고른 단계가 그래프에서도 보이게 옮긴다. 그래프 탭을 처음 열면 맞춘다.
        if (e.PropertyName == nameof(PlanViewModel.GraphSelectedId) && _vm?.GraphSelectedId is { } id)
        {
            GraphCanvas.BringIntoView(id);
        }
        else if (e.PropertyName == nameof(PlanViewModel.SelectedTabIndex) && _vm?.SelectedTabIndex == PlanViewModel.GraphTabIndex)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => GraphCanvas.Fit(), Avalonia.Threading.DispatcherPriority.Loaded);
        }
    }

    private void Run(Action<PlanViewModel> action)
    {
        if (DataContext is PlanViewModel vm)
        {
            action(vm);
        }
    }

    private async Task RunAsync(Func<PlanViewModel, Task> action)
    {
        if (DataContext is PlanViewModel vm)
        {
            await action(vm);
        }
    }

    private void RunCommand(string id)
    {
        if (DataContext is PlanViewModel vm)
        {
            _ = vm.RunCommand(id);
        }
    }
}
