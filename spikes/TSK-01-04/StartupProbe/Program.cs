using Avalonia;
using System;

namespace Oranda.App;

sealed class Program
{
    // 진입 어셈블리만 올라온 시점(Avalonia 어셈블리 로드 전). main 과의 차이 = Main 준비에 드는 어셈블리 로드.
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void ModuleInit() => Perf.Mark("module-init");

#if !NOSTA
    [STAThread] // NOSTA 변형: STA 초기화 비용(이 PC 에서 ~430 ms) 확인용. 클립보드·끌어놓기(OLE)에 STA 가 필요하므로 실제 App 에는 쓰지 않는다.
#endif
    public static void Main(string[] args)
    {
        Perf.Mark("main");
        if (Environment.GetEnvironmentVariable("ORANDA_PROBE_EXIT_AT_MAIN") == "1")
        {
            return; // 런타임 시작 바닥값만 잴 때
        }

        var builder = BuildAvaloniaApp();
        Perf.Mark("appbuilder");
        builder.StartWithClassicDesktopLifetime(args);
    }

    // src/Oranda.App 의 BuildAvaloniaApp 과 같다. 글꼴·렌더링·합성 변형만 환경 변수로 바꾼다.
    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .AfterSetup(_ => Perf.Mark("setup"))
            .LogToTrace();
        var software = Environment.GetEnvironmentVariable("ORANDA_PROBE_RENDER") == "software";
        var redirection = Environment.GetEnvironmentVariable("ORANDA_PROBE_COMPOSITION") == "redirection";
        if (software || redirection)
        {
            var options = new Win32PlatformOptions();
            if (software)
            {
                // GPU(ANGLE/D3D) 초기화 비용 비교용
                options.RenderingMode = [Win32RenderingMode.Software];
            }

            if (redirection)
            {
                // WinUI 합성(WinUiCompositorConnection) 대신 리디렉션 표면 — 첫 프레임 비용 비교용
                options.CompositionMode = [Win32CompositionMode.RedirectionSurface];
            }

            builder = builder.With(options);
        }

        if (!Perf.NoInterFont)
        {
            builder = builder.WithInterFont();
        }

        return builder;
    }
}
