using Avalonia.Controls;
using Avalonia.Interactivity;
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
using Oranda.Core.Grid;
using Oranda.Core.Plans;
using Oranda.Core.Sessions;
using CorePlan = Oranda.Core.Plans.ExecutionPlan;

namespace Oranda.App.Tests.Features.ExecutionPlan;

/// <summary>
/// 「실행하고 실제 계획」의 위험 지점: 첫 실행 전 작업 공간의 세션 묶기, 끼어든 조회의 계획, 일반 실행과 같은 문장 결정, 취소 표시·중단 단추 연결, WITH … DELETE.
/// 취소·대기 시험은 시간 제한을 둔다(회귀가 무한 대기가 아니라 실패로 나오게).
/// </summary>
public class PlanRunActualRiskTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    private static void Publishing(Rig rig) =>
        rig.Exec.Handler = (s, _) =>
        {
            var r = Rig.Query(s);
            rig.Bus.Publish(new StatementExecuted(rig.Session.Id, s, r, ExecutionOptions.Default));
            return Task.FromResult(r);
        };

    private static PlanViewModel NewPlan(Rig rig, SqlEditorViewModel tab, PlanBoostTests.FakePlans plans)
    {
        var runner = new PlanStatsRerunner(rig.State, () => BindVariableStore.ForTab(tab, rig.Bus), () => rig.Session, () => rig.Exec, () => plans);
        return new PlanViewModel(plans) { PostToUi = a => a(), HasCommand = _ => false, Rerunner = (mode, stmt, ct) => runner.RunAsync(stmt, mode, ct) };
    }

    private static PlanBoostTests.FakePlans PlansWithActuals(string sqlId = "x")
    {
        var plans = new PlanBoostTests.FakePlans { LastCursor = new CursorRef(sqlId, 0) };
        plans.Cursors[sqlId] = PlanBoostTests.WithActuals(sqlId);
        return plans;
    }

    // ---- B1: 첫 실행 전인 작업 공간은 일반 실행과 같은 길로 자기 세션을 얻는다 ----

    /// <summary>실행 엔진 가짜: 어느 세션으로 보냈는지 남기고 풀어 줄 때까지 기다린다.</summary>
    private sealed class SessionRecordingEngine : IExecutionService
    {
        public ISession? Used { get; private set; }

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ExecutionResult> ExecuteAsync(ISession s, SqlStatement stmt, IReadOnlyDictionary<string, BindValue> binds, ExecutionOptions? options, CancellationToken ct)
        {
            Used = s;
            Started.TrySetResult();
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
    public Task A_fresh_workspace_gets_its_own_session_through_the_normal_prepare_path_and_ctrl_break_stops_it() => Ui.Run(async () =>
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
        Assert.True(await router.PrepareAsync(tabA)); // 작업 공간 A 가 접속의 세션을 쓴다
        host.Active = tabB; // B 는 아직 한 번도 실행하지 않았다
        Dispatcher.UIThread.RunJobs();
        Assert.True(router.IsPendingOwn(tabB));

        // 운영 연결(ExecutionCommands.Resolve · 처리기 Wire)과 같은 모양.
        var engine = new SessionRecordingEngine();
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
        var plans = PlansWithActuals();
        var runner = new PlanStatsRerunner(state, () => BindVariableStore.ForTab(tabB, fx.Events), () => router.SessionFor(tabB), () => engine, () => plans,
            () => host.WorkspaceOf(tabB));
        var vm = new PlanViewModel(plans) { PostToUi = x => x(), HasCommand = _ => false, Rerunner = (mode, stmt, ct) => runner.RunAsync(stmt, mode, ct) };

        var running = ExecutionPlanCommandHandler.RunActualAsync(vm, tabB, controller, default);
        await engine.Started.Task.WaitAsync(Limit);

        // B 의 문장은 B 자신의 새 세션에서 돈다. A 의 세션은 그대로 한가하다.
        Assert.NotNull(engine.Used);
        Assert.NotSame(sessionA, engine.Used);
        Assert.False(state.IsBusy(sessionA.Id));
        Assert.True(state.IsBusy(engine.Used!.Id));
        Assert.True(state.ShownBusy); // B 화면의 바쁨 표시가 맞다
        Assert.True(vm.IsRerunning);

        controller.Cancel(); // 편집기의 Ctrl+Break
        await running.WaitAsync(Limit);
        Assert.False(vm.IsRerunning);
        Assert.False(state.IsBusy(engine.Used.Id));
        Assert.Contains("취소", vm.StatusText, StringComparison.Ordinal);
        Assert.Null(vm.CurrentPlan);
    });

    // ---- M2·M3: 일반 실행(Ctrl+Enter)과 같은 문장 결정, 커서 문장이 탭의 마지막 실행보다 먼저 ----

    [Fact]
    public Task The_statement_follows_ctrl_enter_rules_selection_blank_lines_and_the_cursor_not_the_last_run() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        Publishing(rig);
        var tab = rig.Host.NewDocument("select 1 from dual");
        rig.Host.Active = tab;
        await rig.Controller.RunAsync(RunMode.Statement);
        Assert.Contains("1 from dual", tab.LastRun.Statement!.Text, StringComparison.Ordinal);

        // 탭의 마지막 실행은 select 1 이지만 지금 커서는 다른 문장이다 → 커서 문장을 보낸다.
        tab.Document.Text = "select 2 from dual";
        tab.CaretOffset = 3;
        var plans = PlansWithActuals();
        var vm = NewPlan(rig, tab, plans);
        var before = rig.Exec.Statements.Count;
        await ExecutionPlanCommandHandler.RunActualAsync(vm, tab, rig.Controller, default);
        Assert.Contains("2 from dual", Assert.Single(rig.Exec.Statements.Skip(before)).Text, StringComparison.Ordinal);

        // 선택 영역이 있으면 선택한 문장(하위 질의)을 보낸다(바깥 문장 전체가 아니다).
        tab.Document.Text = "select a from (select b from huge) x";
        var sub = tab.Text.IndexOf("select b", StringComparison.Ordinal);
        tab.Select(sub, "select b from huge".Length);
        before = rig.Exec.Statements.Count;
        await ExecutionPlanCommandHandler.RunActualAsync(NewPlan(rig, tab, plans), tab, rig.Controller, default);
        var sent = Assert.Single(rig.Exec.Statements.Skip(before)).Text;
        Assert.Contains("b from huge", sent, StringComparison.Ordinal);
        Assert.DoesNotContain("select a", sent, StringComparison.Ordinal);

        // 「빈 줄 = 문장 구분」 설정이면 커서가 있는 첫 문장만 보내고, 뒤의 DELETE 는 보내지 않는다.
        rig.State.BlankLineDelimiter = true;
        tab.Select(0, 0); // 앞 시험의 선택 해제
        tab.Document.Text = "select * from a\n\ndelete from b";
        tab.Select(0, 0);
        tab.CaretOffset = 3;
        before = rig.Exec.Statements.Count;
        await ExecutionPlanCommandHandler.RunActualAsync(NewPlan(rig, tab, plans), tab, rig.Controller, default);
        var only = Assert.Single(rig.Exec.Statements.Skip(before)).Text;
        Assert.Contains("from a", only, StringComparison.Ordinal);
        Assert.DoesNotContain("delete", only, StringComparison.OrdinalIgnoreCase);

        // 선택 영역이 문장 여러 개면 실행하지 않고 안내한다.
        tab.Document.Text = "select 1 from dual;\nselect 2 from dual;";
        tab.Select(0, tab.Text.Length);
        var multi = NewPlan(rig, tab, plans);
        before = rig.Exec.Statements.Count;
        await ExecutionPlanCommandHandler.RunActualAsync(multi, tab, rig.Controller, default);
        Assert.Equal(before, rig.Exec.Statements.Count);
        Assert.Contains("여러 개", multi.StatusText, StringComparison.Ordinal);

        // 활성 탭이 없으면 안내만.
        var none = NewPlan(rig, tab, plans);
        await ExecutionPlanCommandHandler.RunActualAsync(none, null, rig.Controller, default);
        Assert.Contains("활성 SQL 탭이 없습니다", none.StatusText, StringComparison.Ordinal);
    });

    // ---- B2: 끼어든 조회의 계획은 열지 않는다 ----

    private static string Header(string sql) => "SQL_ID  7h35uxf5uhmm1, child number 0\n-------------------------------------\n" + sql + "\n\nPlan hash value: 1838229974\n";

    [Theory]
    [InlineData("select /*+ GATHER_PLAN_STATISTICS */ 1 from dual", "select /*+ GATHER_PLAN_STATISTICS */ 1 from dual", true)]
    [InlineData("SELECT /*+ gather_plan_statistics */   1\n  FROM   DUAL", "select /*+ GATHER_PLAN_STATISTICS */ 1 from dual", true)] // 공백·줄바꿈·대소문자
    [InlineData("select /*+ GATHER_PLAN_STATISTICS */ 1 from", "select /*+ GATHER_PLAN_STATISTICS */ 1 from dual", true)] // 서버가 앞부분만 보여 줌(잘림)
    [InlineData("select /*+ GATHER_PLAN_STATISTICS */ 1 from dual\r\n", "select /*+ GATHER_PLAN_STATISTICS */ 1 from dual", true)]
    [InlineData("select owner, table_name from all_tables where rownum < 5", "select /*+ GATHER_PLAN_STATISTICS */ 1 from dual", false)] // 메타데이터 조회
    [InlineData("select /*+ GATHER_PLAN_STATISTICS */ 2 from dual", "select /*+ GATHER_PLAN_STATISTICS */ 1 from dual", false)]
    public void The_plan_text_must_show_the_sql_that_was_sent(string shown, string sent, bool expected) =>
        Assert.Equal(expected, PlanSqlMatch.Matches(Header(shown), sent));

    [Fact]
    public void A_plan_text_without_the_sql_id_header_cannot_be_checked_and_is_not_rejected() =>
        Assert.True(PlanSqlMatch.Matches("Plan hash value: 1", "select 1 from dual"));

    [Fact]
    public Task A_plan_of_a_statement_squeezed_in_while_reading_is_not_opened_but_the_real_one_is() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var tab = rig.Host.NewDocument("select 1 from dual");
        Publishing(rig);
        var plans = new PlanBoostTests.FakePlans { LastCursor = new CursorRef("meta", 0) };
        plans.Cursors["meta"] = PlanBoostTests.WithActuals("meta") with { Text = Header("select owner from all_tables where rownum < 5") };
        var vm = NewPlan(rig, tab, plans);

        await vm.RunWithActualRowsAsync(tab.GetStatementAtCaret());

        Assert.Null(vm.CurrentPlan); // 남의 계획을 이 문장의 것으로 보이지 않는다
        Assert.Contains("계획을 열지 않았습니다", vm.StatusText, StringComparison.Ordinal);
        Assert.False(vm.IsRerunning);
        Assert.False(rig.State.IsBusy(rig.Session.Id));

        // 읽은 계획의 본문이 보낸 사본과 같으면(공백·대소문자 다름) 연다.
        plans.LastCursor = new CursorRef("mine", 0);
        plans.Cursors["mine"] = PlanBoostTests.WithActuals("mine") with { Text = Header("SELECT /*+ gather_plan_statistics */ 1\n FROM dual") };
        await vm.RunWithActualRowsAsync(tab.GetStatementAtCaret());
        Assert.Equal("mine", vm.CurrentPlan?.Cursor?.SqlId);
    });

    [Fact]
    public Task The_band_rerun_goes_through_the_same_check() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var tab = rig.Host.NewDocument("select 1 from dual");
        Publishing(rig);
        var plans = new PlanBoostTests.FakePlans { LastCursor = new CursorRef("meta", 0) };
        plans.Cursors["meta"] = PlanBoostTests.WithActuals("meta") with { Text = Header("select owner from all_tables") };
        var vm = NewPlan(rig, tab, plans);
        await vm.RerunAsync(PlanRerunMode.Hint, tab.GetStatementAtCaret());
        Assert.Null(vm.CurrentPlan);
        Assert.Contains("계획을 열지 않았습니다", vm.StatusText, StringComparison.Ordinal);
    });

    // ---- 중간 수정 1: 이 버튼으로 연 계획의 띠는 실제로 계획을 얻은 문장을 다시 실행한다 ----

    [Fact]
    public Task The_band_rerun_after_run_actual_sends_the_statement_the_plan_came_from_not_the_last_run_of_the_tab() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        Publishing(rig);
        var tab = rig.Host.NewDocument("select 1 from dual");
        rig.Host.Active = tab;
        await rig.Controller.RunAsync(RunMode.Statement); // 탭의 마지막 실행 = select 1
        tab.Document.Text = "select 2 from dual";
        tab.CaretOffset = 3;
        var plans = new PlanBoostTests.FakePlans { LastCursor = new CursorRef("noA", 0) };
        plans.Cursors["noA"] = PlanBoostTests.NoActuals("noA"); // A-Rows 가 없어 띠가 보인다
        var vm = NewPlan(rig, tab, plans);
        vm.LastStatementSource = () => tab.LastRun.Statement; // 처리기 Wire 와 같다

        await ExecutionPlanCommandHandler.RunActualAsync(vm, tab, rig.Controller, default);
        Assert.True(vm.ShowStatisticsBand);
        var before = rig.Exec.Statements.Count;

        await vm.RerunAsync(PlanRerunMode.Hint); // 띠의 「힌트 넣은 사본 실행」
        Assert.Contains("2 from dual", Assert.Single(rig.Exec.Statements.Skip(before)).Text, StringComparison.Ordinal);
    });

    // ---- M7·M8: 취소 중 표시, 중단 단추 연결 ----

    [Fact]
    public Task Ctrl_break_marks_the_session_as_cancelling_until_the_copy_has_stopped() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var tab = rig.Host.NewDocument("select 1 from dual");
        rig.Host.Active = tab;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Exec.Handler = async (s, ct) =>
        {
            started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                cancelSeen.TrySetResult();
                await release.Task;
                throw;
            }

            return Rig.Query(s);
        };
        var vm = NewPlan(rig, tab, PlansWithActuals());

        var running = vm.RunWithActualRowsAsync(tab.GetStatementAtCaret());
        await started.Task.WaitAsync(Limit);
        Assert.Equal(SessionRunState.Running, rig.State.ShownRunState);

        rig.Controller.Cancel();
        await cancelSeen.Task.WaitAsync(Limit);
        Assert.Equal(SessionRunState.Cancelling, rig.State.ShownRunState); // 「취소 중」 표시

        release.SetResult();
        await running.WaitAsync(Limit);
        Assert.Equal(SessionRunState.Idle, rig.State.ShownRunState);
    });

    [Fact]
    public Task The_stop_button_in_the_plan_window_cancels_the_running_copy() => HeadlessHost.Run(async () =>
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = new PlanViewModel { PostToUi = a => a(), CanRunCommand = _ => true };
        vm.Rerunner = async (_, _, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("취소되어야 한다");
        };
        var view = new PlanView { DataContext = vm };
        var window = new Window { Content = view, Width = 1200, Height = 500 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var stmt = new SqlStatement("select 1 from dual", default, StatementKind.Query);
            var running = vm.RunWithActualRowsAsync(stmt);
            await started.Task.WaitAsync(Limit);
            Dispatcher.UIThread.RunJobs();
            var stop = view.FindControl<Button>("CancelRunButton")!;
            Assert.True(stop.IsVisible);

            stop.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await running.WaitAsync(Limit);

            Assert.False(vm.IsRerunning);
            Assert.Contains("취소", vm.StatusText, StringComparison.Ordinal);
            Dispatcher.UIThread.RunJobs();
            Assert.False(stop.IsVisible);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task A_substitution_variable_statement_opens_the_plan_of_the_text_the_server_got() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var tab = rig.Host.NewDocument("select &v from dual");
        rig.Exec.Handler = (s, _) =>
        {
            // 실행 엔진은 치환한 글을 서버에 보내고 결과의 Statement 에 그 글을 담는다(실제 서버로 확인: result.Statement.Text = 치환한 글).
            var sent = s with { Text = s.Text.Replace("&v", "'hello'", StringComparison.Ordinal) };
            var r = Rig.Query(sent);
            rig.Bus.Publish(new StatementExecuted(rig.Session.Id, sent, r, ExecutionOptions.Default));
            return Task.FromResult(r);
        };
        var plans = new PlanBoostTests.FakePlans { LastCursor = new CursorRef("sub", 0) };
        plans.Cursors["sub"] = PlanBoostTests.WithActuals("sub") with { Text = Header("select /*+ GATHER_PLAN_STATISTICS */ 'hello' from dual") };
        var vm = NewPlan(rig, tab, plans);
        var stmt = new SqlStatement("select &v from dual", default, StatementKind.Query) { Verb = "SELECT", SubstitutionNames = ["v"] };

        await vm.RunWithActualRowsAsync(stmt);

        Assert.Equal("sub", vm.CurrentPlan?.Cursor?.SqlId); // 치환 전 글(&v)과 대조했다면 거부됐을 계획
        Assert.DoesNotContain("계획을 열지 않았습니다", vm.StatusText, StringComparison.Ordinal);
    });

    // ---- WITH … DELETE: 분류기는 질의로 보지만 사본이 데이터를 바꾸므로 실행하지 않는다 ----

    [Theory]
    [InlineData("WITH a AS (SELECT 1 FROM dual) DELETE FROM t", true)]
    [InlineData("WITH a AS (SELECT 1 FROM dual) INSERT INTO t SELECT * FROM a", true)]
    [InlineData("with a as (select 1 x from dual), b as (select 2 y from dual) update t set c = 1", true)]
    [InlineData("WITH a AS (SELECT 1 FROM dual) MERGE INTO t USING a ON (1=1) WHEN MATCHED THEN UPDATE SET c = 1", true)]
    [InlineData("WITH a AS (SELECT 1 x FROM dual) SELECT * FROM a", false)]
    [InlineData("WITH a AS (SELECT 1 FROM dual), b AS (SELECT (2) y FROM dual) DELETE FROM t WHERE x IN (SELECT 1 FROM dual)", true)]
    [InlineData("/* 머리 주석 */ WITH a AS (SELECT 1 FROM dual) UPDATE t SET c = 1", true)]
    [InlineData("SELECT * FROM t WHERE c IN (SELECT c FROM u)", false)]
    [InlineData("WITH a AS (SELECT 1 FROM dual) SELECT 'DELETE' d, \"UPDATE\" FROM a -- insert\n", false)]
    [InlineData("WITH a AS (SELECT 1 FROM dual) SELECT /* delete */ q'[update t]' FROM a", false)]
    // 정상 조회를 막지 않는다: WITH 로 시작하지 않는 문장은 검사하지 않고, WITH 안에서도 낱말이 괄호 안·점 뒤·바인드·MODEL 규칙이면 본문 동사가 아니다.
    [InlineData("SELECT merge FROM t", false)]
    [InlineData("SELECT * FROM merge m", false)]
    [InlineData("SELECT pkg.delete(1) FROM dual", false)]
    [InlineData("SELECT :update FROM dual", false)]
    [InlineData("SELECT * FROM t MODEL DIMENSION BY (a) MEASURES (b) RULES UPDATE (b[1] = 2)", false)]
    [InlineData("WITH a AS (SELECT 1 x FROM dual) SELECT merge FROM a", false)]
    [InlineData("WITH merge AS (SELECT 1 x FROM dual) SELECT * FROM merge m", false)]
    [InlineData("WITH a AS (SELECT 1 x FROM dual) SELECT pkg.delete(1), s.update(2) FROM a", false)]
    [InlineData("WITH a AS (SELECT 1 x FROM dual) SELECT :update, :1 FROM a", false)]
    [InlineData("WITH a AS (SELECT 1 x FROM dual) SELECT * FROM a MODEL DIMENSION BY (x) MEASURES (x y) RULES UPDATE (y[1] = 2)", false)]
    public Task A_with_statement_that_changes_data_is_not_run_as_a_copy(string sql, bool dml) => Ui.Run(async () =>
    {
        Assert.Equal(dml, PlanStatsRerunner.IsWithChangingData(sql));
        var stmt = new SqlStatement(sql, default, StatementKind.Query) { Verb = "WITH" };
        Assert.Equal(!dml, PlanStatsRerunner.CanAutoRerun(stmt));
        if (dml)
        {
            Assert.Throws<InvalidOperationException>(() => PlanStatsRerunner.BuildCopy(stmt, PlanRerunMode.Hint));
            using var rig = new Rig();
            var tab = rig.Host.NewDocument(sql);
            var vm = NewPlan(rig, tab, PlansWithActuals());
            await vm.RunWithActualRowsAsync(stmt);
            Assert.Empty(rig.Exec.Statements);
            Assert.Contains("질의(SELECT)만", vm.StatusText, StringComparison.Ordinal);
        }
    });
}
