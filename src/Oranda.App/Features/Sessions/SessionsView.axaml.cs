using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Oranda.App.Features.Sessions;

public partial class SessionsView : UserControl
{
    private SessionsViewModel? _wired;

    public SessionsView()
    {
        InitializeComponent();
        SessionList.AddHandler(PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        SessionList.DoubleTapped += async (_, e) => await OnDoubleTappedAsync(e);
        SessionList.AddHandler(KeyDownEvent, async (_, e) => await OnKeyDownAsync(e), RoutingStrategies.Tunnel);
        SessionMenu.Opening += (_, _) => SyncMenu();
        SessionMenu.Opened += (_, _) => SyncMenu();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is SessionsViewModel vm && !ReferenceEquals(_wired, vm))
            {
                _wired = vm;
                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(SessionsViewModel.Selected))
                    {
                        SyncMenu(); // 고른 줄이 바뀌면 메뉴 항목도 맞춘다(메뉴가 열려 있는 동안도)
                    }
                };
                SyncMenu();
            }
        };
        UseMenu.Click += async (_, _) => await RunAsync(vm => vm.UseAsync(vm.Selected));
        CommitMenu.Click += async (_, _) => await RunAsync(vm => vm.CommitAsync(vm.Selected));
        RollbackMenu.Click += async (_, _) => await RunAsync(vm => vm.RollbackAsync(vm.Selected));
        CloseMenu.Click += async (_, _) => await RunAsync(vm => vm.CloseAsync(vm.Selected));
    }

    /// <summary>오른쪽 단추는 누른 줄을 먼저 고른다(메뉴가 그 줄에 대해 열린다).</summary>
    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is SessionsViewModel vm
            && e.GetCurrentPoint(SessionList).Properties.IsRightButtonPressed
            && (e.Source as Control)?.DataContext is SessionRow row)
        {
            vm.Selected = row;
        }
    }

    /// <summary>줄을 두 번 누르면 그 줄의 세션을 활성 작업 공간에 묶는다(「이 세션 쓰기」 와 같다).</summary>
    private async System.Threading.Tasks.Task OnDoubleTappedAsync(TappedEventArgs e)
    {
        if (DataContext is SessionsViewModel vm && (e.Source as Control)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is SessionRow row)
        {
            e.Handled = true;
            vm.Selected = row;
            await vm.ActivateAsync(row);
        }
    }

    /// <summary>Enter = 고른 줄의 세션을 활성 작업 공간에 묶는다(두 번 누르기와 같다).</summary>
    private async System.Threading.Tasks.Task OnKeyDownAsync(KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None && DataContext is SessionsViewModel { Selected: { } row } vm)
        {
            e.Handled = true;
            await vm.ActivateAsync(row);
        }
    }

    /// <summary>메뉴는 열리기 전에는 데이터 문맥을 받지 못해 바인딩 대신 코드로 맞춘다.</summary>
    private void SyncMenu()
    {
        if (DataContext is not SessionsViewModel vm)
        {
            return;
        }

        var row = vm.Selected;
        UseMenu.IsEnabled = vm.CanUse(row);
        CommitMenu.IsEnabled = vm.CanCommit(row);
        RollbackMenu.IsEnabled = vm.CanRollback(row);
        CloseMenu.IsEnabled = vm.CanClose(row);
    }

    private async System.Threading.Tasks.Task RunAsync(Func<SessionsViewModel, System.Threading.Tasks.Task> action)
    {
        if (DataContext is SessionsViewModel vm && vm.Selected is not null)
        {
            await action(vm);
        }
    }
}
