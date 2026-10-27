using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dock.Avalonia.Controls;
using Oranda.App.Features.Binds;
using Oranda.App.Features.Execution;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell;
using Oranda.App.Shell.Docking;
using Oranda.App.Tests.Features.SqlEditor;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Execution;
using Oranda.Core.Sessions;

namespace Oranda.App.Tests.Features.Execution;

/// <summary>
/// 소유: TSK-14-13. 실행 중 표지: 실행한 SQL 탭 머리·그 탭의 작업 공간(문서 탭)·결과 영역 띠에 도는 스피너(<see cref="RunSpinner"/>)가 보이고
/// (다른 탭을 보고 있어도), 끝·오류·취소에 사라지며 회전도 멈춘다. 실행은 가짜 세션·멈춰 세운 가짜 엔진으로 오래 걸리게 한다.
/// </summary>
[Collection("GridEditStatics")]
[Oranda.App.Tests.TestSupport.ResetUiLanguage]
public sealed class RunMarkerTests
{
    // ---------- 표지 하나 ----------

    [Fact]
    public void Marker_counts_overlapping_runs_and_shows_elapsed_seconds_in_its_tip()
    {
        var marker = new RunMarker();
        Assert.False(marker.IsRunning);
        Assert.Equal(string.Empty, marker.Tip);

        marker.Begin();
        marker.Begin(); // 겹친 실행
        Assert.True(marker.IsRunning);
        Assert.Equal("실행 중 · 경과 0초", marker.Tip);
        marker.Refresh(DateTime.UtcNow.AddSeconds(12.4));
        Assert.Equal(12, marker.ElapsedSeconds);
        Assert.Equal("실행 중 · 경과 12초", marker.Tip);

        marker.End();
        Assert.True(marker.IsRunning); // 하나가 남았다
        marker.End();
        Assert.False(marker.IsRunning);
        Assert.Equal(string.Empty, marker.Tip);
        marker.End(); // 넘쳐도 음수가 되지 않는다
        marker.Begin();
        Assert.True(marker.IsRunning);
        marker.End();
        Assert.False(marker.IsRunning);
    }

    // ---------- 컨트롤러 → 탭·작업 공간 ----------

    public enum Ending
    {
        Success,
        Failure,
        Exception,
        Cancel,
    }

    [Theory]
    [InlineData(Ending.Success)]
    [InlineData(Ending.Failure)]
    [InlineData(Ending.Exception)]
    [InlineData(Ending.Cancel)]
    public Task Tab_and_workspace_show_running_while_another_tab_is_active_and_clear_on_every_ending(Ending ending) => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var ws = rig.Host.EnsureWorkspace();
        var running = rig.Host.NewDocument("select 1 from dual");
        var other = rig.Host.NewDocument("select 2 from dual");
        rig.Host.Active = running;
        var gate = new TaskCompletionSource<ExecutionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Exec.Handler = async (s, ct) =>
        {
            await using var reg = ct.Register(() => gate.TrySetCanceled(ct));
            return await gate.Task;
        };

        var run = rig.Controller.RunAsync(RunMode.Statement);
        await HeadlessHost.WaitUntil(() => rig.State.Busy && rig.Exec.Statements.Count == 1);
        rig.Host.Active = other; // 다른 탭을 본다

        Assert.True(running.Run.IsRunning);
        Assert.False(other.Run.IsRunning);
        Assert.True(ws.HasRunningTab);
        Assert.StartsWith("실행 중 · 경과 ", running.Run.Tip, StringComparison.Ordinal);
        Assert.Contains(running.Title, ws.RunTip, StringComparison.Ordinal);
        Assert.True(((ResultTabsViewModel)running.ResultPane!).Run.IsRunning); // 결과 영역 띠

        switch (ending)
        {
            case Ending.Success:
                gate.SetResult(Rig.Query(rig.Exec.Statements[0]));
                break;
            case Ending.Failure:
                gate.SetResult(new ExecutionResult(rig.Exec.Statements[0], ExecutionStatus.Failed) { Error = new DbError("ORA-00942", "table or view does not exist") { Number = 942 } });
                break;
            case Ending.Exception:
                gate.SetException(new InvalidOperationException("연결 끊김"));
                break;
            case Ending.Cancel:
                rig.Host.Active = running;
                rig.Controller.Cancel();
                break;
        }

        await run;

        Assert.False(running.Run.IsRunning);
        Assert.False(ws.HasRunningTab);
        Assert.Equal(string.Empty, ws.RunTip);
        Assert.False(((ResultTabsViewModel)running.ResultPane!).Run.IsRunning);
    });

    [Fact]
    public Task Panel_document_follows_the_workspace_marker() => Ui.Run(() =>
    {
        var host = new SqlEditorHost();
        var ws = host.EnsureWorkspace();
        var tab = host.NewDocument();
        var def = new Oranda.Core.Shell.ToolPanelDefinition
        {
            Id = "oranda.panel.sqlWorkspace",
            Title = "SQL",
            Kind = Oranda.Core.Shell.ToolPanelKind.Document,
            DefaultLocation = Oranda.Core.Shell.DockLocation.Document,
            ViewModelType = typeof(SqlWorkspaceViewModel),
            AllowMultiple = true,
        };
        var doc = new PanelDocument(def, "d1", "기본", ws);
        var changed = new List<string?>();
        doc.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        tab.Run.Begin();
        Assert.True(doc.IsRunning);
        Assert.Contains(nameof(PanelDocument.IsRunning), changed);
        Assert.Contains(tab.Title, doc.RunTip, StringComparison.Ordinal);

        tab.Run.End();
        Assert.False(doc.IsRunning);
    });

    [Fact]
    public Task Plsql_workspace_follows_its_tabs_markers() => Ui.Run(() =>
    {
        var ws = new Oranda.App.Features.PlsqlWorkspace.PlsqlWorkspaceViewModel();
        var tab = new Oranda.App.Features.PlsqlEditor.PlsqlEditorViewModel { Title = "RAISE_SAL" };
        ws.Add(tab);
        ws.Add(new Oranda.App.Features.PlsqlEditor.PlsqlEditorViewModel { Title = "OTHER" });
        var changed = new List<string?>();
        ws.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        tab.Editor.Run.Begin();
        Assert.True(ws.HasRunningTab);
        Assert.Contains(nameof(ws.HasRunningTab), changed);
        Assert.Equal("RAISE_SAL 실행 중 · 경과 0초", ws.RunTip);

        tab.Editor.Run.End();
        Assert.False(ws.HasRunningTab);
        Assert.Equal(string.Empty, ws.RunTip);
    });

    [Fact]
    public Task Sql_tab_list_menu_marks_running_tabs() => Ui.Run(() =>
    {
        var host = new SqlEditorHost();
        var ws = host.EnsureWorkspace();
        var running = host.NewDocument();
        var idle = host.NewDocument();
        running.Run.Begin();

        var marked = SqlWorkspaceView.TabListItem(ws, running);
        var plain = SqlWorkspaceView.TabListItem(ws, idle);

        Assert.True(marked.Icon is RunSpinner { IsActive: true, ShowAtOnce: true });
        Assert.StartsWith("실행 중 · 경과 ", ToolTip.GetTip(marked) as string, StringComparison.Ordinal);
        Assert.Null(plain.Icon);
        running.Run.End();
    });

    // ---------- 셸 화면 ----------

    [Fact]
    public Task Shell_shows_the_marker_on_the_sql_tab_and_the_workspace_tab_until_the_run_ends() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        var wiring = h.Services.GetService<IEnumerable<Oranda.Core.Composition.IStartupTask>>()!.OfType<SqlEditorDockWiring>().Single();
        await wiring.RunAsync(default);
        Dispatcher.UIThread.RunJobs();
        var host = SqlEditorHost.Current;
        var dock = h.Get<DockWorkspace>();
        var wsA = host.EnsureWorkspace();
        var running = host.NewDocument("select 1 from dual");
        var other = host.NewDocument("select 2 from dual");
        var wsB = host.NewWorkspace();
        host.Active = running;
        Dispatcher.UIThread.RunJobs();

        using var shellRig = new ShellRunRig(host);
        var gate = new TaskCompletionSource<ExecutionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        shellRig.Exec.Handler = async (s, ct) =>
        {
            await using var reg = ct.Register(() => gate.TrySetCanceled(ct));
            return await gate.Task;
        };

        var run = shellRig.Controller.RunAsync(RunMode.Statement);
        await HeadlessHost.WaitUntil(() => shellRig.State.Busy && shellRig.Exec.Statements.Count == 1);
        await HeadlessHost.WaitUntil(() => running.ResultPane is ResultTabsViewModel { IsRunBarVisible: true });
        await HeadlessHost.WaitUntil(() => SqlTabMarker(h, running) is { IsEffectivelyVisible: true, IsSpinning: true });
        RenderClose(h.Window, "running"); // ORANDA_RENDER_DIR 가 있을 때만

        // 같은 작업 공간의 다른 SQL 탭을 본다: 실행한 탭 머리에 표지, 보는 탭에는 없다.
        host.Active = other;
        await HeadlessHost.WaitUntil(() => SqlTabMarker(h, running) is { IsEffectivelyVisible: true, IsSpinning: true });
        Assert.False(SqlTabMarker(h, other)?.IsEffectivelyVisible ?? false);
        Assert.False(SqlTabMarker(h, other)?.IsSpinning ?? false); // 실행하지 않는 탭의 스피너는 돌지 않는다
        Assert.StartsWith("실행 중 · 경과 ", ToolTip.GetTip(SqlTabMarker(h, running)!) as string, StringComparison.Ordinal);

        // 다른 작업 공간을 본다: 실행 중인 작업 공간의 문서 탭 머리에 표지.
        host.Active = wsB.Tabs[0];
        var docA = dock.Documents.Single(d => ReferenceEquals(d.Panel, wsA));
        var docB = dock.Documents.Single(d => ReferenceEquals(d.Panel, wsB));
        await HeadlessHost.WaitUntil(() => DocMarker(h, docA) is { IsEffectivelyVisible: true, IsSpinning: true } m && m.Bounds.Width > 0);
        Assert.False(DocMarker(h, docB)?.IsEffectivelyVisible ?? false);
        Assert.False(DocMarker(h, docB)?.IsSpinning ?? false);
        Assert.Equal(12, DocMarker(h, docA)!.Bounds.Width);
        Assert.Contains(running.Title, ToolTip.GetTip(DocMarker(h, docA)!) as string, StringComparison.Ordinal);
        Assert.Equal("실행 중", Avalonia.Automation.AutomationProperties.GetName(DocMarker(h, docA)!));
        RenderClose(h.Window, "other-workspace");
        var docSpinner = DocMarker(h, docA)!;

        gate.SetResult(Rig.Query(shellRig.Exec.Statements[0]));
        await run;
        Dispatcher.UIThread.RunJobs();

        Assert.False(DocMarker(h, docA)?.IsEffectivelyVisible ?? false);
        Assert.False(docSpinner.IsSpinning); // 숨으면 회전도 멈춘다
        host.Active = other;
        Dispatcher.UIThread.RunJobs();
        await HeadlessHost.WaitUntil(() => SqlTabItem(h, running) is not null);
        Assert.False(SqlTabMarker(h, running)?.IsEffectivelyVisible ?? false);
        Assert.False(SqlTabMarker(h, running)?.IsSpinning ?? false);
    });

    [Fact]
    public Task Result_view_shows_a_running_band_with_elapsed_time() => Ui.Run(() =>
    {
        var tabs = new ResultTabsViewModel { RunBarDelay = TimeSpan.Zero };
        var view = new ResultTabsView { DataContext = tabs };
        var window = new Window { Width = 700, Height = 260, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var band = view.FindControl<Border>("RunBand")!;
            Assert.False(band.IsVisible);
            tabs.IsRunning = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(band.IsEffectivelyVisible);
            Assert.Contains(band.GetVisualDescendants().OfType<TextBlock>(), t => t.Text is { } s && s.StartsWith("실행 중 · 경과 ", StringComparison.Ordinal));
            var spinner = band.GetVisualDescendants().OfType<RunSpinner>().Single(); // 띠의 점 자리도 같은 스피너
            Assert.True(spinner.IsEffectivelyVisible);
            Assert.True(spinner.IsSpinning);
            tabs.IsRunning = false;
            Dispatcher.UIThread.RunJobs();
            Assert.False(band.IsVisible);
            Assert.False(spinner.IsSpinning);
        }
        finally
        {
            window.Close();
        }
    });

    // ---------- 스피너 ----------

    [Fact]
    public Task Spinner_spins_only_while_shown_and_attached() => Ui.Run(async () =>
    {
        var spinner = new RunSpinner();
        var host = new Border { Child = spinner };
        var window = new Window { Width = 200, Height = 80, Content = host };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            Assert.False(spinner.IsVisible); // 꺼져 있으면 보이지도 돌지도 않는다
            Assert.False(spinner.IsSpinning);

            spinner.IsActive = true;
            Dispatcher.UIThread.RunJobs();
            Assert.False(spinner.IsVisible); // 150 ms 지연: 짧은 실행에서는 나타나지 않는다
            await HeadlessHost.WaitUntil(() => spinner.IsVisible);
            Dispatcher.UIThread.RunJobs();
            Assert.True(spinner.IsSpinning);
            Assert.Equal(new Avalonia.Size(12, 12), spinner.Bounds.Size); // App.axaml 스타일: 지름 12
            Assert.NotNull(spinner.Foreground); // 테마 주황(OrandaIconOrangeBrush)
            Assert.Equal("실행 중", Avalonia.Automation.AutomationProperties.GetName(spinner));
            await CaptureTurningFramesAsync(window); // ORANDA_RENDER_DIR 가 있을 때만: 컴포지터가 그린 두 프레임의 호 위치가 다르다

            host.Child = null; // 시각 트리 밖: 멈춘다
            Assert.False(spinner.IsSpinning);
            host.Child = spinner; // 다시 붙으면 다시 돈다
            Dispatcher.UIThread.RunJobs();
            Assert.True(spinner.IsSpinning);

            spinner.IsActive = false; // 끝: 바로 숨고 멈춘다
            Assert.False(spinner.IsVisible);
            Assert.False(spinner.IsSpinning);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Short_run_never_shows_the_spinner_so_the_tab_keeps_its_width() => Ui.Run(async () =>
    {
        var spinner = new RunSpinner();
        var header = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Children = { spinner, new TextBlock { Text = "SQL 1" } } };
        var window = new Window { Width = 300, Height = 80, Content = new Border { HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left, Child = header } };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var idleWidth = header.Bounds.Width;
            spinner.IsActive = true;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(idleWidth, header.Bounds.Width);
            spinner.IsActive = false; // 150 ms 안에 끝난 실행
            await Task.Delay(250);
            Dispatcher.UIThread.RunJobs();
            Assert.False(spinner.IsVisible);
            Assert.False(spinner.IsSpinning);
            Assert.Equal(idleWidth, header.Bounds.Width);
        }
        finally
        {
            window.Close();
        }
    });

    /// <summary>
    /// ORANDA_RENDER_DIR 가 있을 때만(Skia 렌더): 컴포지터가 실제로 그린 프레임을 시간 차를 두고 두 번 떠서 저장하고, 서로 다른지(호가 돌았는지) 본다.
    /// RenderTargetBitmap 은 컴포지션 회전을 거치지 않으니 여기서는 Headless 의 렌더된 프레임을 쓴다.
    /// </summary>
    private static async Task CaptureTurningFramesAsync(Window window)
    {
        var dir = Environment.GetEnvironmentVariable("ORANDA_RENDER_DIR");
        if (string.IsNullOrEmpty(dir))
        {
            return;
        }

        Directory.CreateDirectory(dir);
        var frames = new List<byte[]>();
        for (var i = 0; i < 2; i++)
        {
            if (i > 0)
            {
                await Task.Delay(260); // 1초 한 바퀴의 약 1/4
                Dispatcher.UIThread.RunJobs();
            }

            using var frame = Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window)!;
            var path = Path.Combine(dir, $"runspinner-frame{i}.png");
#pragma warning disable CS0618 // 렌더 확인용
            frame.Save(path);
#pragma warning restore CS0618
            frames.Add(File.ReadAllBytes(path));
        }

        Assert.False(frames[0].AsSpan().SequenceEqual(frames[1]), "스피너가 도는 두 프레임이 같다(회전하지 않는다)");
    }

    /// <summary>ORANDA_RENDER_DIR 가 있을 때만 창을 라이트·다크로 2배(192 dpi) 그린다(작은 스피너를 눈으로 보려고).</summary>
    private static void RenderClose(Window window, string name)
    {
        var dir = Environment.GetEnvironmentVariable("ORANDA_RENDER_DIR");
        if (string.IsNullOrEmpty(dir))
        {
            return;
        }

        Directory.CreateDirectory(dir);
        try
        {
            foreach (var (variant, tone) in new[] { (Avalonia.Styling.ThemeVariant.Light, "light"), (Avalonia.Styling.ThemeVariant.Dark, "dark") })
            {
                Avalonia.Application.Current!.RequestedThemeVariant = variant;
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                var size = new Avalonia.PixelSize(Math.Max(1, (int)window.Bounds.Width) * 2, Math.Max(1, (int)window.Bounds.Height) * 2);
                using var bmp = new Avalonia.Media.Imaging.RenderTargetBitmap(size, new Avalonia.Vector(192, 192));
                bmp.Render(window);
#pragma warning disable CS0618 // 렌더 확인용: 새 오버로드가 없는 헤드리스 비트맵
                bmp.Save(Path.Combine(dir, $"runspinner-{name}-{tone}.png"));
#pragma warning restore CS0618
            }
        }
        finally
        {
            Avalonia.Application.Current!.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Default;
            Dispatcher.UIThread.RunJobs();
        }
    }

    // ---------- 도우미 ----------

    private static ListBoxItem? SqlTabItem(ShellHarness h, SqlEditorViewModel tab) =>
        h.Window.GetVisualDescendants().OfType<ListBoxItem>().FirstOrDefault(i => ReferenceEquals(i.DataContext, tab));

    private static RunSpinner? SqlTabMarker(ShellHarness h, SqlEditorViewModel tab) =>
        SqlTabItem(h, tab)?.GetVisualDescendants().OfType<RunSpinner>().FirstOrDefault();

    private static RunSpinner? DocMarker(ShellHarness h, PanelDocument doc) =>
        h.Window.GetVisualDescendants().OfType<DocumentTabStripItem>().FirstOrDefault(t => ReferenceEquals(t.DataContext, doc))
            ?.GetVisualDescendants().OfType<RunSpinner>().FirstOrDefault();

    /// <summary>셸의 편집기 호스트로 도는 실행 컨트롤러(가짜 세션·가짜 엔진).</summary>
    private sealed class ShellRunRig : IDisposable
    {
        public ShellRunRig(SqlEditorHost host)
        {
            Session = new FakeSession(new ConnectionProfile("dev", "scott", "localhost:1521/FREEPDB1"));
            State = new ExecutionState(Bus);
            var lang = Rig.LanguageForTests();
            Controller = new ExecutionController(new ExecutionDeps
            {
                State = State,
                Log = new ExecutionLog(Bus, State),
                Dialogs = new FakeDialogs(),
                Session = () => Session,
                Execution = () => Exec,
                Language = () => lang,
                Host = () => host,
                Binds = BindVariableStore.For(Bus),
                ActivatePanel = _ => { },
                TogglePanel = _ => { },
            });
            Bus.Publish(new SessionOpened(Session.Id, Session.Profile, Session.Server, SessionPurpose.Work));
        }

        public EventBus Bus { get; } = new();

        public FakeSession Session { get; }

        public FakeExecution Exec { get; } = new();

        public ExecutionState State { get; }

        public ExecutionController Controller { get; }

        public void Dispose() => Controller.Dispose();
    }
}
