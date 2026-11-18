using Avalonia.Controls;

namespace Oranda.App.Features.TableEdit.Advisor;

/// <summary>「분포 보기」 근거 창(비모달, 탭 하나에 하나). DataContext = <see cref="PartitionEvidenceViewModel"/>.</summary>
public partial class PartitionEvidenceWindow : Window
{
    public PartitionEvidenceWindow()
    {
        InitializeComponent();
        SaveReportButton.Click += async (_, _) => await (Vm?.SaveReportAsync() ?? Task.FromResult<string?>(null));
        CloseButton.Click += (_, _) => Close();
    }

    private PartitionEvidenceViewModel? Vm => DataContext as PartitionEvidenceViewModel;
}
