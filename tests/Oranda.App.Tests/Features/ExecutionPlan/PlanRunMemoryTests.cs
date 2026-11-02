using Oranda.App.Features.ExecutionPlan;
using Oranda.App.Features.Execution;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Tests.Features.SqlEditor;
using Oranda.App.Tests.Features.Execution;
using Oranda.Core.Events;
using Oranda.Core.Execution;
using Oranda.Core.Plans;
using Oranda.Core.Sessions;
using CorePlan = Oranda.Core.Plans.ExecutionPlan;
using TestData = Oranda.Testing.TestData;
using TestSession = Oranda.Testing.FakeSession;

namespace Oranda.App.Tests.Features.ExecutionPlan;

/// <summary>탭마다 마지막 실행 문장·커서를 기억한다(실행을 시작한 탭에 귀속)와, 닫힌 탭의 모델이 일을 이어 가지 않는 것.</summary>
public class PlanRunMemoryTests
{
    private const string Guidance = "이 탭의 실행이 세션의 마지막 실행이 아닙니다";

    // 실행 엔진은 커서를 알려 주지 않는다(운영 코드에서 ExecutionResult.Cursor 를 채우는 곳이 없다): 시험도 Cursor 없이 앱과 같게 돌린다.
    // 운영의 OracleExecutionService 처럼 문장을 끝낼 때 StatementExecuted 를 낸다(가짜 엔진은 내지 않으므로 여기서 낸다).
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

    private sealed class Probe(PlanBoostTests.FakePlans plans)
    {
        public int Calls { get; private set; }

        public Task<CursorRef?> LastCursor(ISession s, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult<CursorRef?>(new CursorRef("sqlLast"));
        }

        public PlanViewModel Vm { get; } = new(plans) { PostToUi = x => x() };

        public Task Actual(Rig rig, SqlEditorViewModel? tab, ISession? session = null) =>
            ExecutionPlanCommandHandler.ShowActualAsync(Vm, tab, session ?? rig.Session, rig.State, LastCursor, default);
    }

    private static (Probe Probe, PlanBoostTests.FakePlans Plans) NewProbe()
    {
        var plans = new PlanBoostTests.FakePlans();
        plans.Cursors["sqlLast"] = new CorePlan([new PlanStep(0, null, 0, "SELECT STATEMENT") { Cost = 3 }], "last");
        return (new Probe(plans), plans);
    }

    [Fact]
    public Task Each_tab_remembers_its_own_last_statement_and_only_the_session_last_runner_reads_the_plan() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var a = rig.Host.NewDocument("select 1 from dual");
        var b = rig.Host.NewDocument("select 2 from dual");
        Publishing(rig);
        await RunIn(rig, a);
        await RunIn(rig, b);
        Assert.Equal("select 1 from dual", a.LastRun.Statement!.Text);
        Assert.Equal("select 2 from dual", b.LastRun.Statement!.Text);

        var (pa, _) = NewProbe();
        await pa.Actual(rig, a); // A 로 돌아와 실제 계획: 세션의 직전 문장은 B 의 것
        Assert.Equal(0, pa.Calls);
        Assert.Null(pa.Vm.CurrentPlan);
        Assert.Contains(Guidance, pa.Vm.StatusText, StringComparison.Ordinal);

        var (pb, _) = NewProbe();
        await pb.Actual(rig, b); // 마지막으로 실행한 탭
        Assert.Equal(1, pb.Calls);
        Assert.NotNull(pb.Vm.CurrentPlan);
    });

    [Fact]
    public Task A_run_that_finishes_after_switching_tabs_is_recorded_on_the_tab_that_started_it() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var a = rig.Host.NewDocument("select 1 from dual");
        var b = rig.Host.NewDocument("select 2 from dual");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Exec.Handler = async (s, _) =>
        {
            await gate.Task;
            var r = Rig.Query(s);
            rig.Bus.Publish(new StatementExecuted(rig.Session.Id, s, r, ExecutionOptions.Default));
            return r;
        };

        rig.Host.Active = a;
        var running = rig.Controller.RunAsync(RunMode.Statement);
        rig.Host.Active = b; // 긴 문장이 도는 사이 다른 탭으로 옮긴다
        gate.SetResult();
        await running;

        Assert.NotNull(a.LastRun.Statement);
        Assert.Null(b.LastRun.Statement); // 지금 보는 탭에는 남의 문장이 들어가지 않는다
        Assert.Equal(0, b.LastRun.Sequence);
    });

    [Fact]
    public Task A_new_tab_a_closed_last_tab_and_runs_by_other_features_never_read_the_session_last_statement() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var a = rig.Host.NewDocument("select 1 from dual");
        var b = rig.Host.NewDocument("select 2 from dual");
        Publishing(rig);
        await RunIn(rig, b);
        await RunIn(rig, a);

        // ① 원본 탭을 실행한 뒤 새 (튜닝) 탭: 기록이 없다 → 원본의 계획을 읽지 않는다.
        var tuned = rig.Host.NewDocument("select 1 from dual");
        var (p1, _) = NewProbe();
        await p1.Actual(rig, tuned);
        Assert.Equal(0, p1.Calls);
        Assert.Contains(Guidance, p1.Vm.StatusText, StringComparison.Ordinal);

        // ③ 마지막으로 실행한 탭(A)이 닫혔다: B 의 기록은 세션의 현재 순번이 아니다.
        rig.Host.Close(a);
        var (p3, _) = NewProbe();
        await p3.Actual(rig, b);
        Assert.Equal(0, p3.Calls);

        // ④ 다른 기능(그리드 필터·행 수 세기·계획 다시 실행 …)이 같은 세션에서 문장을 보냈다: 탭 기록을 남기지 않아도 순번은 오른다.
        await RunIn(rig, b);
        var (ok, _) = NewProbe();
        await ok.Actual(rig, b);
        Assert.Equal(1, ok.Calls); // 방금 B 가 실행했으니 읽는다
        var other = Rig.Query(new SqlStatement("select count(*) from (select 2 from dual)", new Oranda.Core.Text.TextRange(0, 10), StatementKind.Query));
        rig.Bus.Publish(new StatementExecuted(rig.Session.Id, other.Statement, other, ExecutionOptions.Default));
        var (p4, _) = NewProbe();
        await p4.Actual(rig, b);
        Assert.Equal(0, p4.Calls);
        Assert.Contains(Guidance, p4.Vm.StatusText, StringComparison.Ordinal);
    });

    [Fact]
    public Task Statements_that_were_not_sent_to_the_server_do_not_change_who_ran_last() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var a = rig.Host.NewDocument("select 1 from dual");
        var b = rig.Host.NewDocument("select 2 from dual");
        Publishing(rig);
        await RunIn(rig, a);

        // ⑤ B 의 문장이 정책 거부·확인 취소(보내기 전)·세션 오류로 끝났다.
        foreach (var make in new Func<SqlStatement, ExecutionResult>[]
        {
            s => new ExecutionResult(s, ExecutionStatus.Denied),
            s => new ExecutionResult(s, ExecutionStatus.Cancelled),
            s => new ExecutionResult(s, ExecutionStatus.Failed) { Error = new DbError("ORANDA-SESSION", "세션이 끊겼습니다") },
        })
        {
            Publishing(rig, make);
            await RunIn(rig, b);
            Assert.Equal(0, b.LastRun.Sequence); // 보내지 않은 문장은 탭에도 남지 않는다
        }

        var (p, _) = NewProbe();
        await p.Actual(rig, a);
        Assert.Equal(1, p.Calls); // 판정 불변: A 가 여전히 마지막 실행 주체

        // 서버가 오류로 답한 문장은 보낸 것이라 세션 순번은 오르지만, 탭 기록은 성공했을 때만 남는다
        // (구문 분석 실패는 세션의 직전 문장을 바꾸지 않을 수 있어, 실패한 탭이 다른 탭의 계획을 읽지 않게 한다).
        Publishing(rig, s => new ExecutionResult(s, ExecutionStatus.Failed) { Error = new DbError("ORA-00942", "table or view does not exist") { Number = 942 } });
        await RunIn(rig, b);
        Assert.Equal(0, b.LastRun.Sequence);
        var (p2, _) = NewProbe();
        await p2.Actual(rig, a); // 순번이 올랐으니 A 도 더는 마지막이 아니다
        Assert.Equal(0, p2.Calls);
        var (p3, _) = NewProbe();
        await p3.Actual(rig, b); // 실패한 탭 = 안내
        Assert.Equal(0, p3.Calls);
        Assert.Contains(Guidance, p3.Vm.StatusText, StringComparison.Ordinal);
    });

    [Fact]
    public Task Actual_plan_waits_while_the_session_is_running_and_a_tab_of_another_session_is_not_read() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var a = rig.Host.NewDocument("select 1 from dual");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Exec.Handler = async (s, _) =>
        {
            await gate.Task;
            var r = Rig.Query(s);
            rig.Bus.Publish(new StatementExecuted(rig.Session.Id, s, r, ExecutionOptions.Default));
            return r;
        };

        rig.Host.Active = a;
        var running = rig.Controller.RunAsync(RunMode.Statement);
        var (busy, _) = NewProbe();
        await busy.Actual(rig, a); // ⑥ 실행 중
        Assert.Equal(0, busy.Calls);
        Assert.Contains("다른 문장이 실행 중입니다", busy.Vm.StatusText, StringComparison.Ordinal);
        gate.SetResult();
        await running;

        var (ok, _) = NewProbe();
        await ok.Actual(rig, a);
        Assert.Equal(1, ok.Calls); // 끝난 뒤 정상

        // ② 탭이 다른 세션을 쓰게 됐다(접속 변경·재접속): 그 세션에 다른 문장이 있으면 이 탭의 기록(옛 세션)은 맞지 않는다.
        var second = new TestSession(TestData.Profile());
        var foreign = Rig.Query(new SqlStatement("select 9 from dual", new Oranda.Core.Text.TextRange(0, 10), StatementKind.Query));
        rig.Bus.Publish(new StatementExecuted(second.Id, foreign.Statement, foreign, ExecutionOptions.Default));
        var (other, _) = NewProbe();
        await other.Actual(rig, a, second);
        Assert.Equal(0, other.Calls);
        Assert.Contains(Guidance, other.Vm.StatusText, StringComparison.Ordinal);

        // 이 앱에서 보낸 문장이 없는 세션은 옛 경로로 찾고, 세션이 닫히면 기록을 버린다.
        var fresh = new TestSession(TestData.Profile());
        var (old, _) = NewProbe();
        await old.Actual(rig, a, fresh);
        Assert.Equal(1, old.Calls);
        rig.Bus.Publish(new SessionClosed(second.Id));
        Assert.Null(rig.State.RunLog.CurrentSequence(second.Id));
    });

    [Fact]
    public async Task Always_hint_reruns_again_for_each_newly_run_statement_through_the_tab_source()
    {
        var plans = new PlanBoostTests.FakePlans();
        var session = new TestSession(TestData.Profile());
        var vm = new PlanViewModel(plans) { PostToUi = a => a(), HasCommand = _ => false };
        vm.AlwaysHint = true;
        var current = new SqlStatement("select 1 from dual", new Oranda.Core.Text.TextRange(0, 18), StatementKind.Query);
        vm.LastStatementSource = () => current; // 운영 경로: 탭의 기억(LastStatement 설정자는 쓰지 않는다)
        var runs = new List<SqlStatement>();
        vm.Rerunner = (_, s, _) =>
        {
            runs.Add(s);
            plans.Cursors["still"] = PlanBoostTests.NoActuals("still");
            return Task.FromResult(new PlanRerunOutcome(session, new CursorRef("still"), 0));
        };

        vm.Apply(PlanBoostTests.NoActuals(), "실제");
        for (var i = 0; i < 20 && vm.IsRerunning; i++)
        {
            await Task.Delay(10);
        }

        Assert.Single(runs);
        vm.Apply(PlanBoostTests.NoActuals(), "실제"); // 같은 문장이면 또 하지 않는다
        Assert.Single(runs);

        current = new SqlStatement("select 2 from dual", new Oranda.Core.Text.TextRange(0, 18), StatementKind.Query); // 새로 실행한 문장
        vm.Apply(PlanBoostTests.NoActuals(), "실제");
        for (var i = 0; i < 20 && vm.IsRerunning; i++)
        {
            await Task.Delay(10);
        }

        Assert.Equal(2, runs.Count);
        Assert.Equal("select 2 from dual", runs[1].Text);
    }

    [Fact]
    public async Task Rerun_uses_the_statement_of_the_tab_source_and_a_disposed_model_cancels_and_stops()
    {
        var calls = new List<SqlStatement>();
        var stmtA = new SqlStatement("select 1 from dual", new Oranda.Core.Text.TextRange(0, 18), StatementKind.Query);
        var vm = new PlanViewModel { PostToUi = a => a(), LastStatementSource = () => stmtA };
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.Rerunner = async (_, s, ct) =>
        {
            calls.Add(s);
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("취소되어야 한다");
        };

        var running = vm.RerunAsync(PlanRerunMode.Hint);
        await started.Task;
        Assert.Same(stmtA, Assert.Single(calls)); // 이 탭이 실행한 문장

        vm.Dispose(); // 탭을 닫으면 진행 중인 다시 실행이 취소된다
        await running;
        Assert.False(vm.IsRerunning);
        Assert.Contains("취소", vm.StatusText, StringComparison.Ordinal);

        await vm.RerunAsync(PlanRerunMode.Hint); // 해제된 뒤에는 시작하지 않는다
        Assert.Single(calls);
    }
}
