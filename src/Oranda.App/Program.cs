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
        // 시작 때 내려받아 둔 업데이트를 자동 적용하는 기본값(켜짐)은 끈다: 적용은 사용자가 고른 시점(종료할 때·지금 다시 시작)에만 하고,
        // 못 끝낸 업데이트는 상태 표시줄 「업데이트 x.y.z 준비됨」으로 보인다(Velopack.xml 1590행 SetAutoApplyOnStartup).
        VelopackApp.Build().SetAutoApplyOnStartup(false).Run();

        var builder = BuildAvaloniaApp();
        PerfMarks.Mark("appbuilder");
        builder.StartWithClassicDesktopLifetime(args);

        // 업데이트 적용은 앱이 완전히 끝난 뒤에 예약한다(종료 확인을 통과했을 때만 값이 있다). 갱신기는 60초만 기다린다.
        Features.Update.UpdateService.Current?.CompleteExit();
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
