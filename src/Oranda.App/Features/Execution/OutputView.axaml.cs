using Avalonia.Controls;

namespace Oranda.App.Features.Execution;

public partial class OutputView : UserControl
{
    public OutputView()
    {
        InitializeComponent();
        // 글이 늘면 맨 아래를 보인다(실행하는 동안 따라 내려간다).
        OutputBox.TextChanged += (_, _) => OutputBox.CaretIndex = OutputBox.Text?.Length ?? 0;
    }
}
