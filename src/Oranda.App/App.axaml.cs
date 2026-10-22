using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Oranda.App.Shell;
using Oranda.App.Shell.Composition;
using Oranda.App.Shell.Menus;
using Oranda.App.Views;
using Oranda.Core.Composition;

namespace Oranda.App;

public partial class App : Application
{
    /// <summary>앱 조립 결과. 시험이 <see cref="Start"/> 로 직접 조립한 값을 넣을 수도 있다.</summary>
    public ServiceContainer? Services { get; private set; }

    /// <summary>
    /// 시험이 조립한 서비스를 닫기 전에 참조를 거둔다: 앱이 시험 사이에 남는 헤드리스 세션에서 <c>(Application.Current as App)?.Services</c> 가
    /// 닫힌 컨테이너를 집지 않게 한다. 그 사이 다른 조립으로 바뀌었으면 건드리지 않는다.
    /// </summary>
    internal void ReleaseServices(ServiceContainer services)
    {
        if (ReferenceEquals(Services, services))
        {
            Services = null;
        }
    }

    /// <summary>macOS 앱 메뉴(Oranda)의 앞쪽 항목. macOS 가 아니면 null.</summary>
    public MacAppMenu? AppMenu { get; private set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        // Avalonia 는 플랫폼 초기화 직후(Initialize 다음) 응용 프로그램 메뉴를 읽으므로 여기서 붙여야 기본 항목(숨기기·종료)이 이어 붙는다.
        if (OperatingSystem.IsMacOS())
        {
            AppMenu = new MacAppMenu();
            NativeMenu.SetMenu(this, AppMenu.Menu);
        }
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            PerfMarks.Mark("app-init");
            var services = AppComposition.Create();
            PerfMarks.Mark("composed");
            if (services.GetService<Oranda.Core.Storage.IAppDataPaths>() is { } paths)
            {
                TraceLog.Start(paths.LogsDirectory);
            }

            // 설정·레이아웃 파일 읽기는 스레드 풀에서 하고, 그동안 UI 스레드는 창 XAML 을 읽는다(둘이 겹친다).
            var shell = services.GetRequiredService<ShellController>();
            var preload = Task.Run(() => shell.LoadAsync());
            var window = new MainWindow { UseNativeMenu = OperatingSystem.IsMacOS() };
            Start(services, window, preload);
            desktop.MainWindow = window;
            ScheduleStartupTasks(services);
            OpenCommandLineFiles(desktop.Args);
            OpenCommandLineConnect(services, desktop.Args);
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
        AppMenu?.Bind(shell.Text);
        window.Attach(services);
        PerfMarks.Mark("shell-attached");
    }

    /// <summary>명령줄 인자로 받은 Orange 작업 공간(.sqd)을 첫 프레임 뒤에 작업 공간으로 연다.</summary>
    private static void OpenCommandLineFiles(string[]? args)
    {
        if (args is not { Length: > 0 })
        {
            return;
        }

        Dispatcher.UIThread.Post(
            () => _ = Features.SqlEditor.DroppedWorkspaceFiles.OpenAsync(Features.SqlEditor.SqlEditorHost.Current, args, CancellationToken.None),
            DispatcherPriority.Background);
    }

    /// <summary>
    /// 명령줄 <c>oranda user@alias [as sysdba]</c>(TSK-03-05): 첫 프레임 뒤 접속 관리자를 그 프로필을 고른 채로 연다.
    /// 있는 파일 경로(이름에 @ 가 든 .sqd 등)는 접속 대상으로 보지 않는다.
    /// </summary>
    private static void OpenCommandLineConnect(ServiceContainer services, string[]? args)
    {
        var tokens = args?.Where(a => !File.Exists(a)).ToArray() ?? [];
        if (tokens.Length == 0)
        {
            return;
        }

        Dispatcher.UIThread.Post(
            () => _ = Features.ConnectionImport.ConnectionImportCommands.OpenFromCommandLineAsync(services, tokens),
            DispatcherPriority.Background);
    }

    /// <summary>첫 프레임 뒤 유휴로 보고 지연 어셈블리를 훑기까지 기다리는 시간.</summary>
    private static readonly TimeSpan StartupIdleDelay = TimeSpan.FromSeconds(2);

    /// <summary>
    /// 시작 작업(<see cref="IStartupTask"/>)을 예약한다(D-006, 시작 경로에서는 돌지 않는다).
    /// 첫 프레임 직후 <see cref="StartupPhase.AfterFirstFrame"/> 작업을 부르고, 잠시 뒤 유휴 때 지연 어셈블리(Oracle·Language)를 훑어
    /// 아직 돌지 않은 작업(<see cref="StartupPhase.Idle"/> 과 지연 어셈블리의 작업)을 부른다.
    /// </summary>
    private static void ScheduleStartupTasks(ServiceContainer services)
    {
        var ran = new HashSet<IStartupTask>(ReferenceEqualityComparer.Instance);
        Dispatcher.UIThread.Post(() => _ = RunStartupTasksAsync(services, ran, StartupPhase.AfterFirstFrame), DispatcherPriority.Background);
        DispatcherTimer.RunOnce(
            () =>
            {
                services.ScanDeferred();
                _ = RunStartupTasksAsync(services, ran, null);
            },
            StartupIdleDelay);
    }

    /// <summary>등록된 시작 작업 중 아직 돌지 않은 것을 부른다. 작업 하나가 실패해도 나머지는 계속한다.</summary>
    private static async Task RunStartupTasksAsync(ServiceContainer services, HashSet<IStartupTask> ran, StartupPhase? only)
    {
        foreach (var task in services.GetService<IEnumerable<IStartupTask>>() ?? [])
        {
            if ((only is { } phase && task.Phase != phase) || !ran.Add(task))
            {
                continue;
            }

            try
            {
                await task.RunAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                Trace.TraceError("시작 작업 '{0}' 실행 중 예외: {1}", task.GetType().FullName, ex);
            }
        }
    }
}
