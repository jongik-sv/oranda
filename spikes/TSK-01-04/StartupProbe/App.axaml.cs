using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Oranda.App.ViewModels;
using Oranda.App.Views;
using Oranda.Core.Connection;
using Oranda.Oracle.Connection;

namespace Oranda.App;

public partial class App : Application
{
    public override void Initialize()
    {
        Perf.Mark("app-init"); // 여기까지 = 플랫폼(Win32·Skia·렌더러) 초기화
        AvaloniaXamlLoader.Load(this);
        Perf.Mark("app-xaml"); // App.axaml(FluentTheme) 적재 끝
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (Perf.EagerOracle)
            {
                // 지연 로딩 비교용: 시작 경로에서 ODP.NET 을 미리 올리는 경우를 흉내 낸다.
                _ = new ConnectionProfile("probe", "scott", "localhost:1521/FREEPDB1").BuildConnectionString("x");
                Perf.Mark("oracle-loaded");
            }

            var window = new MainWindow
            {
                DataContext = new MainViewModel(),
            };
            Perf.Mark("window-created");

            window.Opened += (_, _) =>
            {
                Perf.Mark("window-opened");
                // first-render = 첫 렌더 틱 콜백(App 훅 71ce5ee 와 같은 시점 — 그 틱의 그리기 *전*).
                // first-frame = 두 번째 틱 콜백 = 첫 프레임이 한 번 그려진 뒤.
                window.RequestAnimationFrame(_ =>
                {
                    Perf.Mark("first-render");
                    window.RequestAnimationFrame(_ =>
                    {
                        Perf.Mark("first-frame");
                        // 우선순위가 입력보다 낮은 작업이 돈다 = 디스패처가 비어 입력을 바로 받을 수 있다.
                        Dispatcher.UIThread.Post(() =>
                        {
                            Perf.Mark("input-ready");
                            Perf.Memory();
                            if (Perf.ExitWhenReady)
                            {
                                desktop.Shutdown();
                            }
                        }, DispatcherPriority.ApplicationIdle);
                    });
                });
            };

            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
