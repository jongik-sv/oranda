using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Oranda.App.Shell;
using Oranda.App.Shell.Composition;
using Oranda.App.Views;

namespace Oranda.App;

public partial class App : Application
{
    /// <summary>앱 조립 결과. 시험이 <see cref="Start"/> 로 직접 조립한 값을 넣을 수도 있다.</summary>
    public ServiceContainer? Services { get; private set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            PerfMarks.Mark("app-init");
            var services = AppComposition.Create();
            PerfMarks.Mark("composed");

            // 설정·레이아웃 파일 읽기는 스레드 풀에서 하고, 그동안 UI 스레드는 창 XAML 을 읽는다(둘이 겹친다).
            var shell = services.GetRequiredService<ShellController>();
            var preload = Task.Run(() => shell.LoadAsync());
            var window = new MainWindow();
            Start(services, window, preload);
            desktop.MainWindow = window;
            desktop.Exit += (_, _) => Services?.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// 설정·레이아웃을 읽고 테마·도킹을 짓고 창을 셸에 묶는다. 읽기는 스레드 풀에서 마치고(UI 스레드가 막혀도 교착하지 않게)
    /// 화면을 건드리는 일은 UI 스레드에서 한다.
    /// </summary>
    public void Start(ServiceContainer services, MainWindow window, Task<ShellLoadResult>? preload = null)
    {
        Services = services;
        var shell = services.GetRequiredService<ShellController>();
        var loaded = (preload ?? Task.Run(() => shell.LoadAsync())).GetAwaiter().GetResult();
        PerfMarks.Mark("settings-loaded");
        shell.Initialize(loaded);
        window.Attach(services);
        PerfMarks.Mark("shell-attached");
    }
}
