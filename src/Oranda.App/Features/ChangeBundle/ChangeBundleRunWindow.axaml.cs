using Avalonia.Controls;

namespace Oranda.App.Features.ChangeBundle;

/// <summary>묶음 「실행…」 창. 실행은 한 번 — 끝나면 상태 줄에 결과를 두고 「닫기」만 남는다.</summary>
public partial class ChangeBundleRunWindow : Window
{
    public ChangeBundleRunWindow()
    {
        InitializeComponent();
        RunButton.Click += async (_, _) =>
        {
            if (DataContext is ChangeBundleRunViewModel vm)
            {
                await vm.RunAsync();
            }
        };
        // 실행 중에는 「중지」(지금 문장에 취소를 보내고 남은 문장은 보내지 않는다), 아니면 닫기.
        CloseButton.Click += (_, _) =>
        {
            if (DataContext is ChangeBundleRunViewModel { IsRunning: true } vm)
            {
                vm.Cancel();
                return;
            }

            Close();
        };
        Closing += (_, e) =>
        {
            // 실행 중에는 창을 닫지 않고 멈추기만 한다(멈춘 뒤 닫는다).
            if (DataContext is ChangeBundleRunViewModel { IsRunning: true } vm)
            {
                vm.Cancel();
                e.Cancel = true;
            }
        };
    }
}
