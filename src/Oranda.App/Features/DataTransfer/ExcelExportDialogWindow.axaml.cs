using Avalonia.Controls;

namespace Oranda.App.Features.DataTransfer;

/// <summary>「Excel 로 내보내기」 범위 선택 대화상자(도킹 안 함). 닫는 일만 화면 쪽에서 맡는다.</summary>
public partial class ExcelExportDialogWindow : Window
{
    public ExcelExportDialogWindow()
    {
        InitializeComponent();
        CloseButton.Click += (_, _) => Close();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is ExcelExportDialogViewModel vm)
            {
                vm.CloseRequested += (_, _) => Close();
            }
        };
    }
}
