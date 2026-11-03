using Avalonia.Threading;
using Oranda.App.Features.Binds;
using Oranda.App.Features.ConnectionManager;
using Oranda.App.Features.DedicatedSession;
using Oranda.App.Features.Execution;
using Oranda.App.Features.ExecutionPlan;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Tests.Features.ConnectionManager;
using Oranda.App.Tests.Features.Execution;
using Oranda.App.Tests.Features.SqlEditor;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Execution;
using Oranda.Core.Plans;
using Oranda.Core.Sessions;
using Oranda.Core.Shell;

namespace Oranda.App.Tests.Features.ExecutionPlan;

/// <summary>
/// 연결 시험: 명령 처리기(<c>ExecuteAsync</c>)에서 실행 길까지, 시작한 작업 공간(owner)의 전달, 메뉴 「다시 실행」의 세션 확인.
/// 대기에는 시간 제한을 둔다.
/// </summary>
public class PlanRunActualWiringTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    // ---- M4b: 명령 ExecuteAsync 의 RunActual 분기 → RunActualAsync ----

    [Fact]
    public Task The_command_handler_routes_run_actual_through_the_prepare_path_for_the_tab_that_started_it() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        var wiring = h.Services.GetService<IEnumerable<Oranda.Core.Composition.IStartupTask>>()!.OfType<SqlEditorDockWiring>().Single();
        await wiring.RunAsync(default);
        var tab = SqlEditorHost.Current.NewDocument("select 1 from dual");
        Dispatcher.UIThread.RunJobs();
        SqlEditorHost.Current.Active = tab;

        // 접속이 없다: 처리기 → RunActualAsync(활성 탭) → 문장 결정 → 다시 실행 길(세션 없음)까지 간다. 어느 단계에서 끊겨도 상태 줄이 달라진다.
        // 계획 창 모델은 정적 연결(PlanPanelViewModel.Instance)이라 같은 UI 스레드의 다른 시험이 중간에 바꾸면 못 볼 수 있어, 그때만 세 번까지 다시 해 본다.
        PlanViewModel? vm = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await new ExecutionPlanCommandHandler(h.Services).ExecuteAsync(new CommandContext(ExecutionPlanCommands.RunActualId, h.Get<IContextKeys>()), default);
            Dispatcher.UIThread.RunJobs();
            vm = PlanPanelViewModel.Instance!.For(tab)!;
            if (vm.StatusText.Contains("접속", StringComparison.Ordinal))
            {
                break;
            }
        }

        Assert.Contains("접속", vm!.StatusText, StringComparison.Ordinal);
        Assert.DoesNotContain("활성 SQL 탭이 없습니다", vm.StatusText, StringComparison.Ordinal);
        Assert.DoesNotContain("실행할 문장이 없습니다", vm.StatusText, StringComparison.Ordinal);
        Assert.Null(vm.CurrentPlan);
    });

    // ---- N5b·N6: 시작한 작업 공간(owner)이 바쁨 표시에 전달된다 ----

    [Fact]
    public Task The_run_remembers_the_workspace_that_started_it_not_the_one_active_when_the_session_was_taken() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var tabA = rig.Host.NewDocument("select 'a' from dual");
        var wsB = rig.Host.NewWorkspace();
        var tabB = wsB.Tabs[0];
        tabB.Document.Text = "select 'b' from dual";
        var wsA = rig.Host.WorkspaceOf(tabA);
        Assert.NotSame(wsA, wsB);
        rig.State.ActiveWorkspace = () => rig.Host.Active is { } t ? rig.Host.WorkspaceOf(t) : null;

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Exec.Handler = async (s, ct) =>
        {
            started.TrySetResult();
            await gate.Task.WaitAsync(ct);
            return Rig.Query(s);
        };
        var plans = new PlanBoostTests.FakePlans { LastCursor = new CursorRef("x", 0) };
        plans.Cursors["x"] = PlanBoostTests.WithActuals("x");
        var runner = new PlanStatsRerunner(rig.State, () => BindVariableStore.ForTab(tabB, rig.Bus), () => rig.Session, () => rig.Exec, () => plans,
            () => rig.Host.WorkspaceOf(tabB));
        var vm = new PlanViewModel(plans) { PostToUi = x => x(), HasCommand = _ => false, Rerunner = (mode, stmt, ct) => runner.RunAsync(stmt, mode, ct) };

        rig.Host.Active = tabA; // 세션을 잡는 순간의 활성 작업 공간은 A, 실행은 B 의 탭이 시작했다(접속 확인 대화상자 등으로 사이에 옮겨 간 경우)
        var running = vm.RunWithActualRowsAsync(tabB.GetStatementAtCaret());
        await started.Task.WaitAsync(Limit);

        Assert.Same(wsB, rig.State.RunOwnerOf(rig.Session.Id)); // 시작한 쪽 = B
        Assert.True(rig.State.IsBusy(rig.Session.Id));
        Assert.True(rig.State.ShownBusyElsewhere); // 지금 A 를 보고 있으면 「다른 작업 공간에서 실행 중」
        rig.Host.Active = tabB;
        Assert.False(rig.State.ShownBusyElsewhere); // B 로 돌아오면 아니다

        gate.SetResult();
        await running.WaitAsync(Limit);
        Assert.False(rig.State.IsBusy(rig.Session.Id));
    });

    [Fact]
    public Task The_command_handlers_rerunner_passes_the_workspace_of_its_own_tab_even_when_another_tab_is_active() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        var wiring = h.Services.GetService<IEnumerable<Oranda.Core.Composition.IStartupTask>>()!.OfType<SqlEditorDockWiring>().Single();
        await wiring.RunAsync(default);
        var host = SqlEditorHost.Current;
        var tabA = host.NewDocument("select 'a' from dual");
        var wsB = host.NewWorkspace();
        var tabB = wsB.Tabs[0];
        host.Active = tabA;

        var rerunner = ExecutionPlanCommandHandler.CreateRerunner(h.Services, tabB);

        Assert.NotNull(rerunner.OwnerSource);
        Assert.Same(wsB, rerunner.OwnerSource!());
        Assert.NotSame(wsB, host.WorkspaceOf(host.Active!));
    });

    // ---- 메뉴 「실제 행 수 받으며 다시 실행」도 일반 실행과 같은 길로 세션을 정한다 ----

    private sealed class RecordingEngine : IExecutionService
    {
        public List<(ISession Session, string Text)> Sent { get; } = [];

        public TaskCompletionSource QueryStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ExecutionResult> ExecuteAsync(ISession s, SqlStatement stmt, IReadOnlyDictionary<string, BindValue> binds, ExecutionOptions? options, CancellationToken ct)
        {
            Sent.Add((s, stmt.Text));
            if (stmt.Kind == StatementKind.Ddl)
            {
                return Task.FromResult(new ExecutionResult(stmt, ExecutionStatus.Succeeded));
            }

            QueryStarted.TrySetResult();
            return Task.Run(async () =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return new ExecutionResult(stmt, ExecutionStatus.Cancelled) { Elapsed = TimeSpan.FromMilliseconds(1) };
            }, CancellationToken.None);
        }

        public IAsyncEnumerable<ExecutionResult> ExecuteScriptAsync(ISession s, IReadOnlyList<SqlStatement> stmts, ScriptErrorMode mode, ExecutionOptions? options, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    [Fact]
    public Task The_menu_rerun_prepares_the_tab_first_so_alter_session_goes_to_its_own_new_session() => Ui.Run(async () =>
    {
        using var fx = new ServiceFixture();
        fx.Sessions.PublishOpened = true;
        var host = new SqlEditorHost();
        var state = new ExecutionState(fx.Events);
        var registry = new DedicatedSessionRegistry(host, state, fx.Events);
        var router = new SessionRouter(fx.Service, host, state, registry);
        var profile = await fx.Service.SaveProfileAsync(new ConnectionProfile("a", "scott", "localhost:1521/FREEPDB1"), null, false, default);
        var sessionA = (await fx.Service.ConnectAsync(profile, "pw", default)).Session!;
        Dispatcher.UIThread.RunJobs();
        host.EnsureWorkspace();
        var tabA = host.NewDocument("select 'a' from dual");
        var wsB = host.NewWorkspace();
        var tabB = wsB.Tabs[0];
        tabB.Document.Text = "select 'b' from dual";
        Assert.True(await router.PrepareAsync(tabA));
        host.Active = tabB; // B 는 첫 실행 전이다
        Dispatcher.UIThread.RunJobs();
        Assert.True(router.IsPendingOwn(tabB));

        var engine = new RecordingEngine();
        var controller = new ExecutionController(new ExecutionDeps
        {
            State = state,
            Log = new ExecutionLog(fx.Events, state),
            Dialogs = new FakeDialogs(),
            Session = () => router.Current,
            TabSession = t => router.SessionFor(t),
            MissingConnection = t => router.MissingProfileOf(t),
            PrepareSession = (t, ct) => router.PrepareAsync(t, ct),
            UseConnection = (t, s) => router.UseConnection(t, s),
            Execution = () => engine,
            Language = () => Rig.LanguageForTests(),
            Host = () => host,
            Binds = BindVariableStore.For(fx.Events),
        });
        var plans = new PlanBoostTests.FakePlans { LastCursor = new CursorRef("x", 0) };
        plans.Cursors["x"] = PlanBoostTests.WithActuals("x");
        var runner = new PlanStatsRerunner(state, () => BindVariableStore.ForTab(tabB, fx.Events), () => router.SessionFor(tabB), () => engine, () => plans, () => host.WorkspaceOf(tabB));
        var vm = new PlanViewModel(plans) { PostToUi = x => x(), HasCommand = _ => false, Rerunner = (mode, stmt, ct) => runner.RunAsync(stmt, mode, ct) };
        vm.LastStatementSource = () => tabB.GetStatementAtCaret(); // 다시 실행할 문장(탭의 기억 대신 지금 문장)

        var running = ExecutionPlanCommandHandler.RerunPreparedAsync(vm, tabB, controller, PlanRerunMode.StatisticsLevel, default);
        await engine.QueryStarted.Task.WaitAsync(Limit);

        // ALTER SESSION 도 질의도 B 자신의 새 세션으로 갔다. A 의 세션에는 아무것도 남지 않았다.
        Assert.Equal(2, engine.Sent.Count);
        Assert.Contains("ALTER SESSION", engine.Sent[0].Text, StringComparison.Ordinal);
        Assert.All(engine.Sent, x => Assert.NotSame(sessionA, x.Session));
        Assert.Same(engine.Sent[0].Session, engine.Sent[1].Session);
        Assert.False(state.IsBusy(sessionA.Id));

        vm.CancelRerun();
        await running.WaitAsync(Limit);
        Assert.False(vm.IsRerunning);
    });
}
