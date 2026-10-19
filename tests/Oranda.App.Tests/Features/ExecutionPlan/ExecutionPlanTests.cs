using Oranda.App.Features.ExecutionPlan;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Plans;
using Oranda.Core.Sessions;

namespace Oranda.App.Tests.Features.ExecutionPlan;

/// <summary>TSK-07-01 실행 계획 창: 형식 옵션, 위험 표시, 서비스 호출 결과 반영.</summary>
public class ExecutionPlanTests
{
    [Fact]
    public void Frame_exists()
    {
        Assert.NotNull(new PlanViewModel());
    }

    [Fact]
    public void Format_options_build_the_dbms_xplan_string()
    {
        var vm = new PlanViewModel();
        Assert.Equal("ALLSTATS LAST", vm.BuildFormat());

        vm.PeekedBinds = true;
        vm.Alias = true;
        Assert.Equal("ALLSTATS LAST +PEEKED_BINDS +ALIAS", vm.BuildFormat());

        vm.BaseFormat = "typical";
        vm.Outline = true;
        Assert.Equal("TYPICAL +PEEKED_BINDS +ALIAS +OUTLINE", vm.BuildFormat());
    }

    [Fact]
    public void Risk_text_names_each_flag()
    {
        Assert.Equal(string.Empty, PlanRowItem.RiskDescription(PlanRisk.None));
        Assert.Equal("전체 스캔, 행 수 차이", PlanRowItem.RiskDescription(PlanRisk.FullScan | PlanRisk.CardinalityMisestimate));
    }

    [Fact]
    public void Apply_fills_rows_text_and_status()
    {
        var vm = new PlanViewModel();
        var plan = new Oranda.Core.Plans.ExecutionPlan(
        [
            new PlanStep(0, null, 0, "SELECT STATEMENT"),
            new PlanStep(1, 0, 1, "TABLE ACCESS") { Options = "FULL", ObjectName = "T", ActualRows = 3000, Risk = PlanRisk.FullScan, FilterPredicates = "G=1" },
        ],
            "plan text")
        {
            Cursor = new CursorRef("abc", 0),
        };

        vm.Apply(plan, "실제");

        Assert.Equal(2, vm.Rows.Count);
        Assert.Equal("  TABLE ACCESS FULL", vm.Rows[1].Operation);
        Assert.Equal("3,000", vm.Rows[1].ActualText);
        Assert.Equal("전체 스캔", vm.Rows[1].RiskText);
        Assert.Equal("filter: G=1", vm.Rows[1].Predicates);
        Assert.Equal("plan text", vm.PlanText);
        Assert.Contains("위험 1단계", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Show_cursor_passes_chosen_format_and_service_failure_goes_to_status()
    {
        var service = new FakePlans();
        var vm = new PlanViewModel(service) { PostToUi = a => a() };
        vm.LastOnly = false;
        vm.PeekedBinds = true;

        await vm.ShowCursorAsync(new NoSession(), new CursorRef("abc"), default).ConfigureAwait(true);

        Assert.Equal("ALLSTATS +PEEKED_BINDS", service.Format);
        Assert.Equal("실제 계획 실패: 공유 풀에 없음", vm.StatusText);
        Assert.Equal("abc", vm.LastCursor?.SqlId);
    }

    private sealed class NoSession : ISession
    {
        public Guid Id => Guid.Empty;

        public ConnectionProfile Profile => throw new NotSupportedException();

        public ServerInfo Server => throw new NotSupportedException();

        public SessionPurpose Purpose => SessionPurpose.Work;

        public string DbKey => string.Empty;

        public SessionState State => SessionState.Idle;

        public bool HasPendingTransaction => false;

        public Task CommitAsync(CancellationToken ct) => Task.CompletedTask;

        public Task RollbackAsync(CancellationToken ct) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakePlans : IExecutionPlanService
    {
        public string? Format { get; private set; }

        public Task<Oranda.Core.Plans.ExecutionPlan> ExplainAsync(ISession s, SqlStatement stmt, CancellationToken ct) =>
            Task.FromResult(new Oranda.Core.Plans.ExecutionPlan([], "x"));

        public Task<Oranda.Core.Plans.ExecutionPlan> GetCursorPlanAsync(ISession s, CursorRef cursor, string format, CancellationToken ct)
        {
            Format = format;
            throw new InvalidOperationException("공유 풀에 없음\n둘째 줄");
        }
    }
}
