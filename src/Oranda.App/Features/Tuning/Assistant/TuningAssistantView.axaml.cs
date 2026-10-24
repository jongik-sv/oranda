using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;

namespace Oranda.App.Features.Tuning.Assistant;

/// <summary>튜닝 도우미 문서. 단추는 <see cref="TuningAssistantViewModel"/> 의 동작을 부른다(명령 ID 와 같은 동작). F5 = 다시 분석.</summary>
public partial class TuningAssistantView : UserControl
{
    public TuningAssistantView()
    {
        InitializeComponent();
        ReanalyzeButton.Click += (_, _) => Run(vm => vm.ReanalyzeAsync());
        RunButton.Click += (_, _) => Run(vm => vm.RunBenchmarkAsync());
        CancelButton.Click += (_, _) => Vm?.CancelBenchmark();
        ActualPlanButton.Click += (_, _) => Run(vm => vm.FetchActualPlanAsync());
        EstimatedPlanButton.Click += (_, _) => Run(vm => vm.FetchEstimatedPlanAsync());
        InsertDdlButton.Click += (_, _) => Vm?.InsertIndexDdl();
        InsertRewriteButton.Click += (_, _) => Vm?.InsertRewrite();
        CopyDdlButton.Click += (_, _) => _ = CopyAsync(Vm?.IndexDdl);
        CopyRewriteButton.Click += (_, _) => _ = CopyAsync(Vm?.RewriteSql);
        ReportButton.Click += (_, _) => _ = Oranda.App.Shell.ShellCommandRunner.RunAsync(Report.TuningReportNavigation.CommandId);
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.F5 && e.KeyModifiers == KeyModifiers.None)
            {
                Run(vm => vm.ReanalyzeAsync());
                e.Handled = true;
            }
        };
    }

    private TuningAssistantViewModel? Vm => DataContext as TuningAssistantViewModel;

    private void Run(Func<TuningAssistantViewModel, Task> action)
    {
        if (Vm is { } vm)
        {
            _ = action(vm);
        }
    }

    private async Task CopyAsync(string? text)
    {
        if (string.IsNullOrEmpty(text) || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return;
        }

        await clipboard.SetTextAsync(text).ConfigureAwait(true);
    }
}
