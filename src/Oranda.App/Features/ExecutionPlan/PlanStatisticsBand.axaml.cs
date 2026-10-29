using Avalonia.Controls;

namespace Oranda.App.Features.ExecutionPlan;

/// <summary>A-Rows 없음 띠. 데이터 문맥 = <see cref="PlanViewModel"/>. 두 단추는 <see cref="PlanViewModel.RerunAsync"/> 를 부른다.</summary>
public partial class PlanStatisticsBand : UserControl
{
    public PlanStatisticsBand()
    {
        InitializeComponent();
        HintCopyButton.Click += async (_, _) =>
        {
            if (DataContext is PlanViewModel vm)
            {
                await vm.RerunAsync(PlanRerunMode.Hint);
            }
        };
        CancelRerunButton.Click += (_, _) => (DataContext as PlanViewModel)?.CancelRerun();
        StatisticsLevelButton.Click += async (_, _) =>
        {
            if (DataContext is PlanViewModel vm)
            {
                await vm.RerunAsync(PlanRerunMode.StatisticsLevel);
            }
        };
    }
}
