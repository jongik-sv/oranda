using Avalonia.Controls;
using Oranda.App.Shell.Menus;

namespace Oranda.App.Features.ExecutionPlan;

/// <summary>실행 계획 창. 고정·비교 단추는 <see cref="PlanViewModel"/> 의 동작을 부른다(명령 ID 와 같은 동작).</summary>
public partial class PlanView : UserControl
{
    public PlanView()
    {
        InitializeComponent();
        ShellContextMenus.Attach(PlanList, "plan");
        PinButton.Click += (_, _) => Run(vm => vm.PinCurrent());
        CompareButton.Click += (_, _) => Run(vm => vm.CompareWithPinned());
        UnpinButton.Click += (_, _) => Run(vm => vm.Unpin());
    }

    private void Run(System.Action<PlanViewModel> action)
    {
        if (DataContext is PlanViewModel vm)
        {
            action(vm);
        }
    }
}
