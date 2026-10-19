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
    private static readonly Lazy<HeadlessUnitTestSession> Session = new(() => HeadlessUnitTestSession.StartNew(typeof(HeadlessAppBuilder)));

    public static Task Run(Func<Task> body) =>
        Session.Value.Dispatch(
            async () =>
            {
                await body();
                return true;
            },
            CancellationToken.None);

    public static Task Run(Action body) => Session.Value.Dispatch(body, CancellationToken.None);

    /// <summary>조건이 될 때까지 UI 스레드 작업을 처리하며 기다린다.</summary>
    public static async Task WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > until)
            {
                throw new TimeoutException("조건이 시간 안에 참이 되지 않았다.");
            }

            Dispatcher.UIThread.RunJobs();
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

    public ShellHarness(string? dataDirectory = null, bool show = true)
    {
        _ownsDirectory = dataDirectory is null;
        DataDirectory = dataDirectory ?? Path.Combine(Path.GetTempPath(), "oranda-shell-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DataDirectory);
        Services = AppComposition.Create(new AppCompositionOptions { DataDirectory = DataDirectory, DevBuild = true });
        Window = new MainWindow();
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
        Services.Dispose();
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
