using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Oranda.App.Features.TableEdit.Advisor;

/// <summary>AI 파티션 추천 결과 창(비모달, 탭 하나에 하나). DataContext = <see cref="PartitionAdvisorResultViewModel"/>.</summary>
public partial class PartitionAdvisorResultWindow : Window
{
    public PartitionAdvisorResultWindow()
    {
        InitializeComponent();
        RetryButton.Click += async (_, _) => await (Vm?.RetryAsync() ?? Task.CompletedTask);
        RequestCandidatesButton.Click += async (_, _) => await (Vm?.RequestCandidatesAsync() ?? Task.CompletedTask);
        StopButton.Click += (_, _) => Vm?.Stop();
        SaveReportButton.Click += async (_, _) => await (Vm?.SaveReportAsync() ?? Task.FromResult<string?>(null));
        CloseButton.Click += (_, _) => Close();
        Closing += (_, _) => Vm?.Stop();
    }

    private PartitionAdvisorResultViewModel? Vm => DataContext as PartitionAdvisorResultViewModel;

    private void OnFillClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: AdviceCandidateCard card })
        {
            Vm?.Fill(card);
        }
    }
}
