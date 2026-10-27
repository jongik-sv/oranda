using Avalonia.Controls;
using Avalonia.Controls.Shapes;
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
/// 소유: TSK-14-13. 실행 중 표지: 실행한 SQL 탭 머리·그 탭의 작업 공간(문서 탭)·결과 영역 띠에 「실행 중」 이 보이고(다른 탭을 보고 있어도),
/// 끝·오류·취소에 사라진다. 실행은 가짜 세션·멈춰 세운 가짜 엔진으로 오래 걸리게 한다.
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

        Assert.True(marked.Icon is Ellipse e && e.Classes.Contains("run-marker"));
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
        Oranda.App.Tests.Features.ConnectionManager.MultiSessionUiTests.Render(h.Window, "runmarker-running"); // ORANDA_RENDER_DIR 가 있을 때만

        // 같은 작업 공간의 다른 SQL 탭을 본다: 실행한 탭 머리에 표지, 보는 탭에는 없다.
        host.Active = other;
        await HeadlessHost.WaitUntil(() => SqlTabMarker(h, running) is { IsEffectivelyVisible: true });
        Assert.False(SqlTabMarker(h, other)?.IsEffectivelyVisible ?? false);
        Assert.StartsWith("실행 중 · 경과 ", ToolTip.GetTip(SqlTabMarker(h, running)!) as string, StringComparison.Ordinal);

        // 다른 작업 공간을 본다: 실행 중인 작업 공간의 문서 탭 머리에 표지.
        host.Active = wsB.Tabs[0];
        var docA = dock.Documents.Single(d => ReferenceEquals(d.Panel, wsA));
        var docB = dock.Documents.Single(d => ReferenceEquals(d.Panel, wsB));
        await HeadlessHost.WaitUntil(() => DocMarker(h, docA) is { IsEffectivelyVisible: true } m && m.Bounds.Width > 0);
        Assert.False(DocMarker(h, docB)?.IsEffectivelyVisible ?? false);
        Assert.Contains(running.Title, ToolTip.GetTip(DocMarker(h, docA)!) as string, StringComparison.Ordinal);
        Oranda.App.Tests.Features.ConnectionManager.MultiSessionUiTests.Render(h.Window, "runmarker-other-workspace");

        gate.SetResult(Rig.Query(shellRig.Exec.Statements[0]));
        await run;
        Dispatcher.UIThread.RunJobs();

        Assert.False(DocMarker(h, docA)?.IsEffectivelyVisible ?? false);
        host.Active = other;
        Dispatcher.UIThread.RunJobs();
        await HeadlessHost.WaitUntil(() => SqlTabItem(h, running) is not null);
        Assert.False(SqlTabMarker(h, running)?.IsEffectivelyVisible ?? false);
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
            tabs.IsRunning = false;
            Dispatcher.UIThread.RunJobs();
            Assert.False(band.IsVisible);
        }
        finally
        {
            window.Close();
        }
    });

    // ---------- 도우미 ----------

    private static ListBoxItem? SqlTabItem(ShellHarness h, SqlEditorViewModel tab) =>
        h.Window.GetVisualDescendants().OfType<ListBoxItem>().FirstOrDefault(i => ReferenceEquals(i.DataContext, tab));

    private static Ellipse? SqlTabMarker(ShellHarness h, SqlEditorViewModel tab) =>
        SqlTabItem(h, tab)?.GetVisualDescendants().OfType<Ellipse>().FirstOrDefault(e => e.Classes.Contains("run-marker"));

    private static Ellipse? DocMarker(ShellHarness h, PanelDocument doc) =>
        h.Window.GetVisualDescendants().OfType<DocumentTabStripItem>().FirstOrDefault(t => ReferenceEquals(t.DataContext, doc))
            ?.GetVisualDescendants().OfType<Ellipse>().FirstOrDefault(e => e.Classes.Contains("run-marker"));

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
