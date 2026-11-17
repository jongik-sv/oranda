using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;

namespace Oranda.App.Features.ChangeBundle;

/// <summary>반영 묶음 창. 단추는 ViewModel 의 비동기 메서드를 부르고, 표 두 번 누르기는 전문 보기·고치기.</summary>
public partial class ChangeBundleView : UserControl
{
    public ChangeBundleView()
    {
        InitializeComponent();
        Wire("NewBundleButton", vm => vm.NewBundleAsync());
        Wire("RenameBundleButton", vm => vm.RenameBundleAsync());
        Wire("DeleteBundleButton", vm => vm.DeleteBundleAsync());
        Wire("OwnersButton", vm => vm.EditTargetOwnersAsync());
        Wire("UpButton", vm => vm.MoveUpAsync());
        Wire("DownButton", vm => vm.MoveDownAsync());
        Wire("RemoveButton", vm => vm.RemoveSelectedAsync());
        Wire("RunButton", vm => vm.RunAsync());
        Wire("SaveButton", vm => vm.SaveScriptAsync());
        if (this.FindControl<ListBox>("List") is { } list)
        {
            list.DoubleTapped += async (_, _) =>
            {
                if (Vm is { } vm)
                {
                    await vm.OpenSelectedAsync();
                }
            };
            list.KeyDown += async (_, e) =>
            {
                if (Vm is not { } vm)
                {
                    return;
                }

                if (e.Key == Key.Delete)
                {
                    e.Handled = true;
                    await vm.RemoveSelectedAsync();
                }
                else if (e.Key == Key.Enter)
                {
                    e.Handled = true;
                    await vm.OpenSelectedAsync();
                }
            };
        }
    }

    private ChangeBundleViewModel? Vm => DataContext as ChangeBundleViewModel;

    private void Wire(string name, Func<ChangeBundleViewModel, Task> action)
    {
        if (this.FindControl<Button>(name) is { } button)
        {
            button.Click += async (_, _) =>
            {
                if (Vm is { } vm)
                {
                    await action(vm);
                }
            };
        }
    }
}
