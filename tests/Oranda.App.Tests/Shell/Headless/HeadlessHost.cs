using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using Oranda.App.Shell.Composition;
using Oranda.App.Views;

namespace Oranda.App.Tests.Shell.Headless;

/// <summary>Headless 시험용 앱 구성(TRD 11). 실제 <see cref="Oranda.App.App"/> 의 스타일·리소스를 그대로 쓴다.</summary>
public static class HeadlessAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<Oranda.App.App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true });
}

/// <summary>한 시험 프로세스에서 Avalonia UI 스레드 하나를 공유한다. 본문은 모두 UI 스레드에서 돈다.</summary>
public static class HeadlessHost
{
    // 기본 격리(PerTest)는 Dispatch 마다 Application·Dispatcher 를 새로 만든다. 그 설정 틈에 병렬로 도는 다른 시험이 Dispatcher.UIThread 를
    // 먼저 건드리면 새 디스패처를 그 스레드가 소유해 VerifyAccess 가 간헐로 실패한다. 앱 하나를 계속 쓰면 설정이 한 번뿐이다.
    private static readonly Lazy<HeadlessUnitTestSession> Session = new(
        () => HeadlessUnitTestSession.StartNew(typeof(HeadlessAppBuilder), AvaloniaTestIsolationLevel.PerAssembly));

    /// <summary>세션을 띄우고 첫 Dispatch 까지 기다린다(시험 프레임워크가 시험 전에 한 번 부른다).</summary>
    internal static void WarmUp() => Session.Value.Dispatch(() => { }, CancellationToken.None).GetAwaiter().GetResult();

    public static Task Run(Func<Task> body) =>
        Session.Value.Dispatch(
            async () =>
            {
                await body();
                return true;
            },
            CancellationToken.None);

    public static Task Run(Action body) => Session.Value.Dispatch(body, CancellationToken.None);

    /// <summary>
    /// 조건이 될 때까지 기다린다. UI 스레드에서 불렀을 때만 UI 작업을 직접 처리한다.
    /// 시험 본문이 <see cref="Run(Func{Task})"/> 밖(xunit 스레드)이면 RunJobs 가 소유 스레드 예외를 내므로 조건만 기다린다.
    /// </summary>
    public static async Task WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > until)
            {
                throw new TimeoutException("조건이 시간 안에 참이 되지 않았다.");
            }

            if (Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.RunJobs();
            }

            await Task.Delay(10);
        }
    }
}

/// <summary>
/// 임시 데이터 폴더로 앱 전체(조립·메인 창)를 띄우는 도구. 같은 폴더로 다시 만들면 「재시작」이다.
/// </summary>
public sealed class ShellHarness : IDisposable
{
    private readonly bool _ownsDirectory;

    private readonly Oranda.App.Features.SqlEditor.SqlEditorHost _previousHost;

    public ShellHarness(string? dataDirectory = null, bool show = true, bool nativeMenu = false)
    {
        _ownsDirectory = dataDirectory is null;
        DataDirectory = dataDirectory ?? Path.Combine(Path.GetTempPath(), "oranda-shell-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DataDirectory);
        Services = AppComposition.Create(new AppCompositionOptions { DataDirectory = DataDirectory, DevBuild = true });

        // 편집기 호스트는 프로세스 정적이다. 앞 시험이 남긴 탭·결과가 새 셸(조회 결과 창)에 보이지 않게 셸마다 새로 둔다.
        _previousHost = Oranda.App.Features.SqlEditor.SqlEditorHost.Current;
        Oranda.App.Features.SqlEditor.SqlEditorHost.Current = new Oranda.App.Features.SqlEditor.SqlEditorHost();
        Window = new MainWindow { UseNativeMenu = nativeMenu };
        ((Oranda.App.App)Application.Current!).Start(Services, Window);
        if (show)
        {
            Window.Show();
            Dispatcher.UIThread.RunJobs();
        }
    }

    public string DataDirectory { get; }

    public ServiceContainer Services { get; }

    public MainWindow Window { get; }

    public T Get<T>()
        where T : class => Services.GetRequiredService<T>();

    public void Dispose()
    {
        Window.CloseWithoutSaving();
        Dispatcher.UIThread.RunJobs();
        (Application.Current as Oranda.App.App)?.ReleaseServices(Services);
        Services.Dispose();
        Oranda.App.Features.SqlEditor.SqlEditorHost.Current = _previousHost;
        if (_ownsDirectory)
        {
            try
            {
                Directory.Delete(DataDirectory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
