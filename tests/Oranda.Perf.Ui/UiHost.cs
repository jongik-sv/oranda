using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using OrandaApp = Oranda.App.App;
using Oranda.App.Views;
using Oranda.App.Shell.Composition;
using Oranda.Core.Composition;
using Oranda.Perf.Common;

namespace Oranda.Perf.Ui;

/// <summary>시나리오가 쓰는 실행 환경: 진짜 앱(Oranda.App) 한 벌과 그 주 창.</summary>
internal sealed class UiContext(UiOptions options, OrandaApp app, MainWindow window, IClassicDesktopStyleApplicationLifetime lifetime)
{
    public UiOptions Options { get; } = options;

    public OrandaApp App { get; } = app;

    public MainWindow Window { get; } = window;

    public IClassicDesktopStyleApplicationLifetime Lifetime { get; } = lifetime;

    public ServiceContainer Services => App.Services ?? throw new InvalidOperationException("앱이 아직 조립되지 않았다.");

    public Report Report { get; } = new("Oranda 창 측정: " + options.Scenario, EnvText.Describe());

    public Compositor Compositor => ElementComposition.GetElementVisual(Window)!.Compositor;

    /// <summary>다음 렌더 틱까지 기다린 뒤 그 배치가 실제로 그려질 때까지의 시각(Stopwatch 틱)을 돌려준다.</summary>
    public async Task<(long Processed, long Rendered)> RenderBatchAsync()
    {
        var batch = Compositor.RequestCompositionBatchCommitAsync();
        await batch.Processed;
        var processed = Stopwatch.GetTimestamp();
        await batch.Rendered;
        return (processed, Stopwatch.GetTimestamp());
    }

    public static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string what)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > timeout)
            {
                throw new TimeoutException($"{what}: {timeout.TotalSeconds:F0}초 안에 되지 않았다.");
            }

            await Task.Delay(5);
        }
    }
}

internal static class EnvText
{
    public static string Describe() =>
        $"{RuntimeInformation.OSDescription} / {RuntimeInformation.OSArchitecture} / {Environment.ProcessorCount} 논리 코어 / .NET {Environment.Version} / " +
        (typeof(UiHost).Assembly.GetCustomAttributes(typeof(DebuggableAttribute), false).Length > 0 && IsOptimized() ? "Release" : "Debug(JIT 최적화 꺼짐)");

    private static bool IsOptimized() =>
        typeof(UiHost).Assembly.GetCustomAttributes(typeof(DebuggableAttribute), false).OfType<DebuggableAttribute>().All(a => !a.IsJITOptimizerDisabled);
}

internal static class UiHost
{
    /// <summary>
    /// 앱을 진짜 창으로 띄우고(실제 플랫폼 백엔드), 첫 프레임과 시작 작업이 끝나기를 기다린 뒤 시나리오를 UI 스레드에서 돌린다.
    /// 시나리오가 끝나면(또는 예외) 앱을 끝낸다. 데이터 폴더는 실행마다 빈 임시 폴더라 이전 실행 상태가 섞이지 않는다.
    /// </summary>
    public static int Run(UiOptions options, Func<UiContext, Task> scenario)
    {
        KeepAwake.Enable();
        var dataDir = Path.Combine(Path.GetTempPath(), "oranda-perfui-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("ORANDA_DATA_DIR", dataDir);
        if (options.Features is not null)
        {
            FeaturePreset.Parse(options.Features).WriteSettings(dataDir); // 임시 데이터 폴더에만 쓴다(사용자 설정은 건드리지 않는다)
        }

        var exit = 0;
        try
        {
            AppBuilder.Configure<OrandaApp>()
                .UsePlatformDetect()
                .WithInterFont()
                .AfterSetup(_ => Dispatcher.UIThread.Post(async () => exit = await RunScenarioAsync(options, scenario), DispatcherPriority.Background))
                .StartWithClassicDesktopLifetime([]);
        }
        finally
        {
            try
            {
                Directory.Delete(dataDir, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return exit;
    }

    /// <summary>
    /// 창이 실제로 그려지고 있는지(화면 잠금·가림이 아닌지) 확인한다: 렌더 배치가 그려지기까지 걸리는 시간의 중앙값이 200 ms 를 넘으면 측정을 중단한다.
    /// (잠기면 애니메이션 프레임은 오지만 렌더 스레드가 그리지 않아 한 배치가 약 1초 걸린다.)
    /// </summary>
    private static async Task EnsureDisplayIsLiveAsync(UiContext ctx)
    {
        var latencies = new List<double>();
        for (var i = 0; i < 6; i++)
        {
            var t0 = Stopwatch.GetTimestamp();
            var (_, rendered) = await ctx.RenderBatchAsync();
            latencies.Add(Stopwatch.GetElapsedTime(t0, rendered).TotalMilliseconds);
            await Task.Delay(30);
        }

        var median = Stats.Median(latencies);
        Console.WriteLine("렌더 배치 지연(시작 점검): " + string.Join(", ", latencies.Select(l => l.ToString("F0", System.Globalization.CultureInfo.InvariantCulture))) + " ms");
        if (median > 200)
        {
            throw new InvalidOperationException("화면이 잠겼거나 창이 가려져 렌더가 느리다(렌더 배치 중앙값 "
                + median.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) + " ms). 화면 잠금을 풀고(자리 비움 잠금 시간을 늘리고) 다시 돌린다.");
        }
    }

    private static async Task<int> RunScenarioAsync(UiOptions options, Func<UiContext, Task> scenario)
    {
        var lifetime = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        var code = 0;
        try
        {
            await UiContext.WaitUntilAsync(() => lifetime.MainWindow is { IsLoaded: true }, TimeSpan.FromSeconds(60), "주 창 열림");
            var window = (MainWindow)lifetime.MainWindow!;
            window.Topmost = true; // 다른 창이 가리면 프레임 간격이 달라진다
            window.Activate();
            await Task.Delay(3500); // 첫 프레임 뒤 시작 작업(2초 뒤 유휴 작업까지)이 끝나기를 기다린다
            var ctx = new UiContext(options, (OrandaApp)Application.Current, window, lifetime);
            if (options.NeedsDisplay)
            {
                await EnsureDisplayIsLiveAsync(ctx);
            }

            await scenario(ctx);
            ctx.Report.WriteTo(options.Out, options.BaseName);
            Console.WriteLine(ctx.Report.ToMarkdown());
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("시나리오 실패: " + ex);
            code = 1;
        }
        finally
        {
            lifetime.Shutdown();
        }

        return code;
    }
}
