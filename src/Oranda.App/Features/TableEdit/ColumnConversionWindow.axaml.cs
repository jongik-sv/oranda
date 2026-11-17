using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Oranda.App.Features.TableEdit;

/// <summary>「변환해서 바꾸기…」 창(설계 18.9, 시안 ⑧). DataContext = <see cref="ColumnConversionViewModel"/>. 열리면 바로 검사한다.</summary>
public partial class ColumnConversionWindow : Window
{
    public ColumnConversionWindow()
    {
        InitializeComponent();
        Opened += async (_, _) =>
        {
            if (DataContext is ColumnConversionViewModel vm)
            {
                await vm.CheckAsync();
            }
        };
        CheckButton.Click += async (_, _) => await WithVm(vm => vm.CheckAsync());
        RunButton.Click += async (_, _) => await WithVm(async vm =>
        {
            await vm.RunAsync();
            if (vm.Succeeded)
            {
                Close();
            }
        });
        SaveScriptButton.Click += async (_, _) => await WithVm(vm => vm.SaveScriptAsync());
        AddToBundleButton.Click += async (_, _) => await WithVm(vm => vm.AddToBundleAsync());
        RemainingButton.Click += (_, _) => (DataContext as ColumnConversionViewModel)?.OpenRemainingInEditor();
        OpenRebuildButton.Click += (_, _) => (DataContext as ColumnConversionViewModel)?.OpenRebuildInEditor();
        CloseButton.Click += (_, _) => Close();
        Closing += (_, e) =>
        {
            // 실행 중에 닫으면 서버는 뒤에서 바뀌는데 편집기는 다시 읽지 않는다 — 끝날 때까지 닫지 않는다.
            if (DataContext is ColumnConversionViewModel { IsBusy: true } vm)
            {
                e.Cancel = true;
                vm.StatusText = TableEditText.T("실행·검사가 끝난 뒤에 닫을 수 있습니다", "Wait until the run or check finishes before closing");
            }
        };
    }

    private async Task WithVm(Func<ColumnConversionViewModel, Task> action)
    {
        if (DataContext is ColumnConversionViewModel vm)
        {
            await action(vm);
        }
    }

    private void OnViewRowsClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string sql })
        {
            (DataContext as ColumnConversionViewModel)?.OpenViewRows(sql);
        }
    }
}
