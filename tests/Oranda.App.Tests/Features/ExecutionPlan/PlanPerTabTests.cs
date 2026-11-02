using Avalonia.Controls;
using Avalonia.Threading;
using Oranda.App.Features.ExecutionPlan;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Execution;
using Oranda.Core.Plans;
using Oranda.Core.Sessions;
using CorePlan = Oranda.Core.Plans.ExecutionPlan;
using TestData = Oranda.Testing.TestData;
using TestSession = Oranda.Testing.FakeSession;

namespace Oranda.App.Tests.Features.ExecutionPlan;

/// <summary>실행 계획을 SQL 탭마다 따로 보관한다(P3): 창은 하나이고 활성 탭이 바뀌면 그 탭의 계획으로 바꿔 보인다.</summary>
public class PlanPerTabTests
{
    private static CorePlan Join() => new(PlanMetricsTests.HashJoin(), "join");

    private static CorePlan Scan() =>
        new([new PlanStep(0, null, 0, "SELECT STATEMENT") { Cost = 3 }, new PlanStep(1, 0, 1, "TABLE ACCESS") { Options = "FULL", Cost = 3 }], "scan");

    /// <summary>예상 계획이 끝나는 때를 시험이 정하는 계획 서비스.</summary>
    private sealed class GatedPlans : IExecutionPlanService
    {
        public TaskCompletionSource<CorePlan> Explain { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<CorePlan> ExplainAsync(ISession s, SqlStatement stmt, CancellationToken ct) => Explain.Task;

        public Task<CorePlan> GetCursorPlanAsync(ISession s, CursorRef cursor, string format, CancellationToken ct) => throw new NotSupportedException();

        public Task<CursorRef?> GetLastCursorAsync(ISession s, CancellationToken ct) => Task.FromResult<CursorRef?>(null);

        public Task<string> GetSqlMonitorReportAsync(ISession s, CursorRef cursor, bool html, CancellationToken ct) => throw new NotSupportedException();
    }

    [Fact]
    public Task Switching_tabs_keeps_each_tabs_plan_tree_selection_collapse_sql_id_and_status() => HeadlessHost.Run(() =>
    {
        var host = new SqlEditorHost();
        var a = host.NewDocument("select 1 from dual");
        var b = host.NewDocument("select 2 from dual");
        host.Active = a;
        using var panel = PlanPanelViewModel.ForHost(host, new PlanBoostTests.FakePlans());
        var view = new PlanPanelView { DataContext = panel };
        var window = new Window { Content = view, Width = 1400, Height = 600 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var planA = panel.Active!;
            planA.Apply(Join(), "예상");
            planA.SqlIdText = "7h35uxf5uhmm1";
            planA.ToggleRow(planA.Rows[1]); // HASH JOIN 접기
            planA.SelectedRow = planA.Rows[0];
            var status = planA.StatusText;
            var rowCount = planA.Rows.Count;
            var selected = planA.SelectedRow;
            Dispatcher.UIThread.RunJobs();

            host.Active = b;
            Dispatcher.UIThread.RunJobs();
            var planB = panel.Active!;
            Assert.NotSame(planA, planB);
            Assert.Empty(planB.Rows); // 다른 탭의 계획이 비치지 않는다
            Assert.Null(planB.SqlIdText);
            planB.Apply(Scan(), "실제");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(2, planB.Rows.Count);

            host.Active = a;
            Dispatcher.UIThread.RunJobs();
            Assert.Same(planA, panel.Active);
            Assert.Equal(status, planA.StatusText);
            Assert.Equal("7h35uxf5uhmm1", planA.SqlIdText);
            Assert.Equal(rowCount, planA.Rows.Count);
            Assert.True(planA.HasCollapsed);
            Assert.Same(selected, planA.SelectedRow); // 화면이 바뀌면서 양방향 묶음이 null 을 되쓰지 않는다

            // 화면도 A 의 View 로 돌아와 있고, 그 View 의 목록이 같은 줄을 고르고 있다.
            var shown = Assert.IsType<PlanView>(view.FindControl<ContentControl>("PlanHost")!.Content);
            Assert.Same(planA, shown.DataContext);
            Assert.Same(selected, shown.FindControl<ListBox>("PlanList")!.SelectedItem);
        }
        finally
        {
            window.Close();
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task A_plan_started_in_one_tab_lands_in_that_tab_even_after_switching_away() => HeadlessHost.Run(async () =>
    {
        var host = new SqlEditorHost();
        var a = host.NewDocument("select 1 from dual");
        var b = host.NewDocument("select 2 from dual");
        host.Active = a;
        var plans = new GatedPlans();
        using var panel = PlanPanelViewModel.ForHost(host, plans);
        var planA = panel.Active!;
        var session = new TestSession(TestData.Profile());

        var running = planA.ExplainAsync(session, a.GetStatementAtCaret()!);
        host.Active = b; // 계획을 만드는 동안 다른 탭으로 옮긴다
        var planB = panel.Active!;
        plans.Explain.SetResult(Scan());
        await running;
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(planA.CurrentPlan); // 시작한 탭에 들어간다
        Assert.Equal(2, planA.Rows.Count);
        Assert.Null(planB.CurrentPlan); // 지금 보는 탭은 그대로다
        Assert.Empty(planB.Rows);
        Assert.Same(planB, panel.Active);
    });

    [Fact]
    public Task A_plan_pinned_in_one_tab_compares_with_the_plan_of_another_tab_and_survives_closing_the_first() => HeadlessHost.Run(() =>
    {
        var host = new SqlEditorHost();
        var a = host.NewDocument("select 1 from dual");
        var b = host.NewDocument("select 2 from dual");
        host.Active = a;
        using var panel = PlanPanelViewModel.ForHost(host, new PlanBoostTests.FakePlans());
        var planA = panel.Active!;
        planA.Apply(Join(), "예상");
        Assert.True(planA.PinCurrent());
        Assert.Contains($"[{a.Title}]", planA.PinnedText, StringComparison.Ordinal); // 어느 탭의 계획인지 보인다

        host.Active = b; // 튜닝 후 탭
        var planB = panel.Active!;
        Assert.True(planB.HasPinned); // 고정은 탭 사이에서 공유한다
        planB.Apply(Scan(), "예상");
        Assert.True(planB.CompareWithPinned());
        Assert.NotNull(planB.Comparison);
        Assert.Equal(PlanViewModel.CompareTabIndex, planB.SelectedTabIndex);
        Assert.Equal(0, planA.SelectedTabIndex); // 비교 탭으로 가는 것은 비교한 탭뿐이다

        // 형식 옵션도 앱 전체가 함께 쓴다.
        planB.PeekedBinds = true;
        Assert.True(planA.PeekedBinds);

        // 탭을 닫으면 그 탭의 계획은 해제되고, 비교 기준으로 고정한 사본은 남는다.
        host.Close(a);
        Assert.False(panel.Has(a.TabId));
        Assert.True(planB.HasPinned);
        Assert.NotNull(planB.PinnedPlan);
        return Task.CompletedTask;
    });
}
