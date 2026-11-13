using Avalonia.Controls;

namespace Oranda.App.Features.PointCompare;

/// <summary>화면 3 「이 실행 전과 비교」 대화상자. 일은 모두 <see cref="PointCompareViewModel"/> 이 한다.</summary>
public partial class PointCompareWindow : Window
{
    public PointCompareWindow()
    {
        InitializeComponent();
        CompareButton.Click += async (_, _) =>
        {
            if (Vm is { } vm)
            {
                await vm.LoadAsync();
            }
        };
        ApplyButton.Click += async (_, _) =>
        {
            if (Vm is { } vm && await vm.ApplyAsync() is { Failed: 0, Conflicts: 0 })
            {
                Close(); // 결과 안내는 메시지 창·결과 요약 줄에 남는다
            }
        };
        WidenButton.Click += async (_, _) =>
        {
            if (Vm is { } vm)
            {
                await vm.WidenToGroupAsync();
            }
        };
        OpenScriptButton.Click += (_, _) => Vm?.OpenScriptInEditor();
        OpenRollbackButton.Click += (_, _) => Vm?.OpenRollbackScriptOfGroup();
        CloseButton.Click += (_, _) => Close();
    }

    private PointCompareViewModel? Vm => DataContext as PointCompareViewModel;
}
