using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.VisualTree;

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
        // 「벤치」 칸 전체가 체크 자리다(상자 밖 칸을 눌러도 바뀐다). 줄 고르기는 그대로 일어난다.
        RewriteList.AddHandler(PointerPressedEvent, OnBenchCellPressed, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        HintList.AddHandler(PointerPressedEvent, OnBenchCellPressed, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
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

    /// <summary>「벤치」 칸(체크 상자 밖)을 왼쪽 단추로 누르면 그 줄 체크를 뒤집는다. 체크 상자 자신은 스스로 바뀌므로 건너뛴다.</summary>
    private static void OnBenchCellPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is not Visual source || sender is not Visual list || !e.GetCurrentPoint(list).Properties.IsLeftButtonPressed)
        {
            return;
        }

        Panel? cell = null;
        for (var v = source; v is not null && !ReferenceEquals(v, list); v = v.GetVisualParent())
        {
            if (v is CheckBox)
            {
                return;
            }

            if (v is Panel p && p.Classes.Contains("bench-cell"))
            {
                cell = p;
                break;
            }
        }

        switch (cell?.DataContext)
        {
            case RewriteCandidateItem { CanCheck: true } r:
                r.IsChecked = !r.IsChecked;
                break;
            case HintCandidateItem { CanCheck: true } h:
                h.IsChecked = !h.IsChecked;
                break;
        }
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
