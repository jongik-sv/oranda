using Avalonia.Controls;

namespace Oranda.App.Features.TableEdit;

/// <summary>「✎ 새 테이블」 탭 위 칸(소유자·이름·주석·종류). DataContext = 객체 상세 ViewModel.</summary>
public partial class NewTableHeaderView : UserControl
{
    public NewTableHeaderView()
    {
        InitializeComponent();
    }
}
