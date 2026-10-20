using Avalonia;
using System;
using Velopack;

namespace Oranda.App;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // 위 규칙의 예외: Velopack 은 Main 첫 줄이어야 한다(TSK-10-06). 설치·제거 훅으로 실행되면 처리하고 프로세스를 끝내며,
        // 설치하지 않은 실행에서는 훅·업데이트 없이 돌아온다(Velopack 진단 로그 몇 줄은 남긴다). vpk pack 은 이 호출이 없으면 패키징을 멈춘다.
        VelopackApp.Build().Run();

        var builder = BuildAvaloniaApp();
        PerfMarks.Mark("appbuilder");
        builder.StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
#if DEBUG
        builder = builder.WithDeveloperTools();
#endif
        return builder;
    }
}
