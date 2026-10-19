using Avalonia.Controls;
using Avalonia.Threading;
using Oranda.App;

namespace Oranda.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        PerfMarks.Mark("window-created");
        Opened += OnOpenedForPerf;
    }

    private void OnOpenedForPerf(object? sender, EventArgs e)
    {
        Opened -= OnOpenedForPerf;

        // 첫 rAF 콜백은 첫 프레임을 그리기 *전* 이다(R2R 에서 실제 프레임보다 약 0.5 s 이름, TSK-01-04).
        // 두 번째 콜백 = 첫 프레임이 한 번 그려진 뒤. 그 뒤 입력보다 낮은 우선순위 작업이 돌면 입력을 바로 받을 수 있다.
        RequestAnimationFrame(_ => RequestAnimationFrame(_ =>
        {
            PerfMarks.Mark("first-render");
            Dispatcher.UIThread.Post(() => PerfMarks.Mark("input-ready"), DispatcherPriority.ApplicationIdle);
        }));
    }
}