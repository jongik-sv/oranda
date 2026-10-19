using Avalonia.Controls;

namespace Oranda.App.Features.LobRowView;

public partial class RowViewerView : UserControl
{
    public RowViewerView()
    {
        InitializeComponent();
        PrevButton.Click += (_, _) => (DataContext as RowViewerViewModel)?.MoveRow(-1);
        NextButton.Click += (_, _) => (DataContext as RowViewerViewModel)?.MoveRow(1);
    }
}
