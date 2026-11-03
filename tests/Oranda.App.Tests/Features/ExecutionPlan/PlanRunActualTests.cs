using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Oranda.App.Features.Binds;
using Oranda.App.Features.Execution;
using Oranda.App.Features.ExecutionPlan;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell;
using Oranda.App.Tests.Features.Execution;
using Oranda.App.Tests.Features.MyBatisWorkspace.Flow;
using Oranda.App.Tests.Features.SqlEditor;
using Oranda.App.Tests.Shell;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Events;
using Oranda.Core.Execution;
using Oranda.Core.Plans;
using Oranda.Core.Shell;

namespace Oranda.App.Tests.Features.ExecutionPlan;

/// <summary>
/// 「실행하고 실제 계획(A-Rows)」(oranda.plan.runActual): 활성 탭의 현재 문장을 한 번에 힌트 사본으로 끝까지 읽고 A-Rows 가 있는 실제 계획을 연다.
/// 새 실행 길 없이 PlanStatsRerunner → 실행 엔진을 쓰므로 정책 관문·세션 바쁨·취소가 그대로 적용된다.
/// </summary>
public class PlanRunActualTests
{
    private const string Id = ExecutionPlanCommands.RunActualId;

    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10); // 회귀가 무한 대기가 아니라 실패로 나오게

    // ---- 활성 조건 ----

    private sealed class KeySet(IEnumerable<string> on) : IContextKeys
    {
        private readonly HashSet<string> _on = new(on, StringComparer.Ordinal);

        public bool Evaluate(string? expression) =>
            string.IsNullOrEmpty(expression) || ContextKeyReasons.Evaluate(expression, _on.Contains);
    }

    [Fact]
    public void Command_is_enabled_only_with_a_session_an_idle_session_and_a_plain_sql_document()
    {
        var registry = ShellTestHelpers.CreateCommandRegistry();
        Assert.False(registry.CanExecute(Id, new KeySet(["sqlDocActive"]))); // 세션 없음
        Assert.False(registry.CanExecute(Id, new KeySet(["hasSession", "sqlDocActive", "sessionBusy"]))); // 바쁨
        Assert.False(registry.CanExecute(Id, new KeySet(["hasSession", "sqlDocActive", "mybatisDocActive"]))); // MyBatis 문서
        Assert.True(registry.CanExecute(Id, new KeySet(["hasSession", "sqlDocActive"])));
        Assert.True(registry.CanExecute(Id, new KeySet(["dedicatedSession", "sqlDocActive"])));
        Assert.Equal("sqlDocFocus", registry.Find(Id)!.When);
    }

    // ---- 실행 ----

    private static void Publishing(Rig rig, Func<SqlStatement, ExecutionResult>? make = null) =>
        rig.Exec.Handler = (s, _) =>
        {
            var r = make?.Invoke(s) ?? Rig.Query(s);
            rig.Bus.Publish(new StatementExecuted(rig.Session.Id, s, r, ExecutionOptions.Default));
            return Task.FromResult(r);
        };

    private static async Task RunIn(Rig rig, SqlEditorViewModel tab)
    {
        rig.Host.Active = tab;
        await rig.Controller.RunAsync(RunMode.Statement);
    }

    /// <summary>운영과 같은 연결: 탭의 바인드·세션 → PlanStatsRerunner → 실행 엔진(여기서는 가짜). 읽은 커서의 계획은 A-Rows 가 있다.</summary>
    private static PlanViewModel NewPlan(Rig rig, SqlEditorViewModel tab, out PlanBoostTests.FakePlans plans)
    {
        plans = new PlanBoostTests.FakePlans { LastCursor = new CursorRef("arows", 0) };
        plans.Cursors["arows"] = PlanBoostTests.WithActuals("arows");
        var service = plans;
        var runner = new PlanStatsRerunner(rig.State, () => BindVariableStore.ForTab(tab, rig.Bus), () => rig.Session, () => rig.Exec, () => service);
        return new PlanViewModel(plans) { PostToUi = a => a(), HasCommand = _ => false, Rerunner = (mode, stmt, ct) => runner.RunAsync(stmt, mode, ct) };
    }

    [Fact]
    public Task A_query_runs_once_as_a_hint_copy_and_opens_the_plan_with_a_rows_without_touching_the_tab_record() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var a = rig.Host.NewDocument("select 1 from dual");
        var b = rig.Host.NewDocument("select 2 from dual");
        Publishing(rig);
        await RunIn(rig, a);
        await RunIn(rig, b);
        var beforeA = a.LastRun;
        var beforeB = b.LastRun;
        var sent = rig.Exec.Statements.Count;
        Assert.Equal(2, rig.State.RunLog.CurrentSequence(rig.Session.Id));

        var vm = NewPlan(rig, a, out _);
        rig.Host.Active = a;
        await vm.RunWithActualRowsAsync(a.GetStatementAtCaret());

        // 한 번만, 힌트 사본으로, 이력에 남기지 않고(SkipHistory 는 PlanStatsRerunner 몫) 보냈다. 편집기 글은 그대로.
        var copy = Assert.Single(rig.Exec.Statements.Skip(sent));
        Assert.Contains("GATHER_PLAN_STATISTICS", copy.Text, StringComparison.Ordinal);
        Assert.Equal("select 1 from dual", a.Text);
        // A-Rows 가 있는 실제 계획이 열렸다(띠 없음).
        Assert.NotNull(vm.CurrentPlan?.Cursor);
        Assert.All(vm.CurrentPlan!.Steps, s => Assert.NotNull(s.ActualRows));
        Assert.False(vm.ShowStatisticsBand);
        Assert.False(vm.IsRerunning);
        Assert.False(rig.State.IsBusy(rig.Session.Id));

        // 탭 기록(LastRun)은 이 실행을 남기지 않는다(앱이 대신 보낸 사본): 두 탭의 기록 그대로, 세션 순번만 오른다.
        Assert.Same(beforeA, a.LastRun);
        Assert.Same(beforeB, b.LastRun);
        Assert.Equal(3, rig.State.RunLog.CurrentSequence(rig.Session.Id));

        // 그래서 뒤이은 「실제 계획」(Ctrl+Shift+E, 세션의 직전 문장)은 어느 탭에서도 남의 계획을 읽지 않고 안내만 한다 — 이미 열린 계획은 지우지 않는다.
        var probe = new PlanViewModel(new PlanBoostTests.FakePlans()) { PostToUi = x => x() };
        var read = 0;
        Task<CursorRef?> Last(Oranda.Core.Sessions.ISession s, CancellationToken ct)
        {
            read++;
            return Task.FromResult<CursorRef?>(new CursorRef("arows"));
        }

        await ExecutionPlanCommandHandler.ShowActualAsync(probe, a, rig.Session, rig.State, Last, default);
        await ExecutionPlanCommandHandler.ShowActualAsync(probe, b, rig.Session, rig.State, Last, default);
        Assert.Equal(0, read);
        Assert.Contains("이 탭의 실행이 세션의 마지막 실행이 아닙니다", probe.StatusText, StringComparison.Ordinal);
        Assert.NotNull(vm.CurrentPlan);
    });

    [Theory]
    [InlineData("DELETE FROM t WHERE a = 1", StatementKind.Dml)]
    [InlineData("INSERT INTO t VALUES (1)", StatementKind.Dml)]
    [InlineData("CREATE TABLE t (a NUMBER)", StatementKind.Ddl)]
    [InlineData("BEGIN NULL; END;", StatementKind.PlSql)]
    [InlineData("SELECT * FROM t FOR UPDATE", StatementKind.Query)]
    public Task Statements_other_than_plain_queries_are_not_executed_and_the_user_is_told(string sql, StatementKind kind) => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var tab = rig.Host.NewDocument(sql);
        Publishing(rig);
        var vm = NewPlan(rig, tab, out _);

        await vm.RunWithActualRowsAsync(new SqlStatement(sql, default, kind));

        Assert.Empty(rig.Exec.Statements); // 서버에 아무것도 보내지 않았다
        Assert.Null(vm.CurrentPlan);
        Assert.Contains("질의(SELECT)만", vm.StatusText, StringComparison.Ordinal);
        Assert.False(rig.State.IsBusy(rig.Session.Id));
        Assert.Equal(0, tab.LastRun.Sequence);

        await vm.RunWithActualRowsAsync(null);
        Assert.Contains("문장이 없습니다", vm.StatusText, StringComparison.Ordinal);
        Assert.Empty(rig.Exec.Statements);
    });

    [Fact]
    public Task The_execution_policy_gate_still_decides_and_a_denied_copy_leaves_no_plan() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var tab = rig.Host.NewDocument("select 1 from dual");
        rig.Exec.Handler = (s, _) => Task.FromResult(new ExecutionResult(s, ExecutionStatus.Denied)
        {
            Policy = new PolicyDecision(PolicyDecisionKind.Deny, PolicyRule.ReadOnlyProfile, "읽기 전용 접속입니다"),
        });
        var vm = NewPlan(rig, tab, out _);

        await vm.RunWithActualRowsAsync(tab.GetStatementAtCaret());

        Assert.Single(rig.Exec.Statements); // 관문까지는 갔고 거기서 막혔다(관문을 건너뛰는 길이 없다)
        Assert.Null(vm.CurrentPlan);
        Assert.Contains("읽기 전용 접속입니다", vm.StatusText, StringComparison.Ordinal);
        Assert.False(vm.IsRerunning);
        Assert.False(rig.State.IsBusy(rig.Session.Id));
    });

    [Fact]
    public Task Cancelling_stops_the_run_frees_the_session_and_keeps_the_tab_record() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var tab = rig.Host.NewDocument("select 1 from dual");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Exec.Handler = async (s, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("취소되어야 한다");
        };
        var vm = NewPlan(rig, tab, out _);
        vm.CanRunCommand = _ => true;

        var running = vm.RunWithActualRowsAsync(tab.GetStatementAtCaret());
        await started.Task.WaitAsync(Limit);
        Assert.True(vm.IsRerunning);
        Assert.True(rig.State.IsBusy(rig.Session.Id)); // 실행 중에는 다른 문장과 겹치지 않는다
        Assert.False(vm.CanRunActualNow); // 중복 실행 방지: 단추 꺼짐

        vm.CancelRerun();
        await running.WaitAsync(Limit);

        Assert.False(vm.IsRerunning);
        Assert.False(rig.State.IsBusy(rig.Session.Id));
        Assert.Contains("취소", vm.StatusText, StringComparison.Ordinal);
        Assert.Null(vm.CurrentPlan);
        Assert.Equal(0, tab.LastRun.Sequence);
        Assert.True(vm.CanRunActualNow);
    });

    [Fact]
    public Task Switching_tabs_while_it_runs_keeps_the_statement_and_the_record_of_the_tab_that_started_it() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var a = rig.Host.NewDocument("select 1 from dual");
        var b = rig.Host.NewDocument("select 2 from dual");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Exec.Handler = async (s, _) =>
        {
            started.TrySetResult();
            await gate.Task;
            var r = Rig.Query(s);
            rig.Bus.Publish(new StatementExecuted(rig.Session.Id, s, r, ExecutionOptions.Default));
            return r;
        };
        var vm = NewPlan(rig, a, out _);

        rig.Host.Active = a;
        var running = vm.RunWithActualRowsAsync(a.GetStatementAtCaret()); // 시작할 때 문장을 잡는다
        await started.Task.WaitAsync(Limit);
        rig.Host.Active = b; // 도는 사이 다른 탭으로 옮긴다
        gate.SetResult();
        await running.WaitAsync(Limit);

        Assert.Contains("1 from dual", Assert.Single(rig.Exec.Statements).Text, StringComparison.Ordinal); // 탭 a 의 문장(힌트 사본)
        Assert.NotNull(vm.CurrentPlan?.Cursor);
        Assert.Equal(0, a.LastRun.Sequence);
        Assert.Equal(0, b.LastRun.Sequence); // 지금 보는 탭에 남의 기록이 들어가지 않는다
        Assert.Null(b.LastRun.Statement);
    });

    // ---- 단추 ----

    [Fact]
    public Task Buttons_follow_the_enabled_state_call_the_command_and_show_a_cancel_button_while_running() => HeadlessHost.Run(() =>
    {
        var called = new List<string>();
        var active = false;
        var vm = new PlanViewModel
        {
            PostToUi = a => a(),
            CanRunCommand = _ => active,
            RunCommand = id =>
            {
                called.Add(id);
                return Task.CompletedTask;
            },
        };
        var view = new PlanView { DataContext = vm };
        var window = new Window { Content = view, Width = 1200, Height = 500 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var run = view.FindControl<Button>("RunActualButton")!;
            var cancel = view.FindControl<Button>("CancelRunButton")!;
            Assert.False(run.IsEnabled); // 활성 SQL 탭·세션 없음
            Assert.False(cancel.IsVisible);
            var tip = ToolTip.GetTip(run) as string;
            Assert.Contains("모든 행을 끝까지", tip, StringComparison.Ordinal);
            Assert.Contains("Ctrl+Alt+Shift+E", tip, StringComparison.Ordinal);
            MyBatisFlowRig.Render(window, "plan-run-actual-disabled", 1200, 500);

            active = true;
            vm.RefreshRunButtons();
            Dispatcher.UIThread.RunJobs();
            Assert.True(run.IsEnabled);
            run.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal([Id], called);
            MyBatisFlowRig.Render(window, "plan-run-actual", 1200, 500);

            // 실행 중: 실행 단추는 꺼지고 중단 단추가 나온다.
            vm.IsRerunning = true;
            Dispatcher.UIThread.RunJobs();
            Assert.False(run.IsEnabled);
            Assert.True(cancel.IsVisible);
            MyBatisFlowRig.Render(window, "plan-run-actual-running", 1200, 500);
            vm.IsRerunning = false;
        }
        finally
        {
            window.Close();
        }

        return Task.CompletedTask;
    });

    // ---- 실행 취소(Ctrl+Break = ExecutionController.Cancel) ----

    [Theory]
    [InlineData(true)] // 실행하고 실제 계획
    [InlineData(false)] // 기존 「다시 실행」(띠·메뉴)
    public Task The_execution_cancel_command_also_stops_a_running_plan_copy(bool runActual) => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var tab = rig.Host.NewDocument("select 1 from dual");
        rig.Host.Active = tab;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Exec.Handler = async (s, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("취소되어야 한다");
        };
        var vm = NewPlan(rig, tab, out _);
        var stmt = tab.GetStatementAtCaret();
        var running = runActual ? vm.RunWithActualRowsAsync(stmt) : vm.RerunAsync(PlanRerunMode.Hint, stmt);
        await started.Task.WaitAsync(Limit);
        Assert.True(rig.State.IsBusy(rig.Session.Id));

        rig.Controller.Cancel(); // 편집기의 Ctrl+Break 와 같은 길(계획 창의 중단 단추가 아니다)
        await running.WaitAsync(Limit);

        Assert.False(vm.IsRerunning);
        Assert.False(rig.State.IsBusy(rig.Session.Id));
        Assert.Contains("취소", vm.StatusText, StringComparison.Ordinal);
        Assert.Null(vm.CurrentPlan);
        rig.Controller.Cancel(); // 도는 일이 없으면 아무 일도 없다
    });

    // ---- FOR UPDATE 판정(토큰 기반) ----

    [Theory]
    [InlineData("SELECT * FROM t FOR UPDATE", true)]
    [InlineData("select * from t for update nowait", true)]
    [InlineData("SELECT * FROM t FOR\n  UPDATE", true)]
    [InlineData("SELECT * FROM t FOR /* 잠금 */ UPDATE", true)]
    [InlineData("SELECT * FROM t FOR/**/UPDATE", true)]
    [InlineData("SELECT * FROM t FOR -- 줄 주석\n UPDATE", true)]
    [InlineData("SELECT * FROM t For Update Of a.x Skip Locked", true)]
    [InlineData("SELECT 'FOR UPDATE' AS x FROM t", false)]
    [InlineData("SELECT * FROM t -- FOR UPDATE\n", false)]
    [InlineData("SELECT * /* FOR UPDATE */ FROM t", false)]
    [InlineData("SELECT * FROM t WHERE c = q'[FOR UPDATE]'", false)]
    [InlineData("SELECT * FROM t", false)]
    public void For_update_is_judged_by_tokens_so_comments_hide_nothing_and_strings_do_not_trip_it(string sql, bool locks) =>
        Assert.Equal(!locks, PlanStatsRerunner.CanAutoRerun(new SqlStatement(sql, default, StatementKind.Query)));
}
