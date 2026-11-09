using System.Reflection;
using Oranda.App.Features.ExecutionPlan;
using Oranda.App.Features.Tuning.Trace;
using Oranda.App.Shell;
using Oranda.App.Shell.Features;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Plans;
using Oranda.Core.Sessions;
using Oranda.Core.Shell;
using Oranda.Core.Tuning;
using CorePlan = Oranda.Core.Plans.ExecutionPlan;

namespace Oranda.App.Tests.Features.ExecutionPlan;

// P6b: Tibero 는 통계를 안 모으면 실제 열이 null 이 아니라 0 — 0 을 실제 값처럼 보이지 않게 하고 이유를 보인다. Oracle 은 그대로.
[Collection("DbmsFeatureKeysSerial")]
public class PlanActualsTests
{
    private static PlanStep Step(int id, long? rows, long? starts, TimeSpan? time) =>
        new(id, id == 0 ? null : id - 1, id, "TABLE ACCESS") { ActualRows = rows, Starts = starts, ActualTime = time };

    private static CorePlan Actual(params PlanStep[] steps) => new(steps, "text") { Cursor = new CursorRef("abc123") };

    [Fact]
    public void Tibero_all_zero_actual_columns_are_hidden_with_the_reason()
    {
        var plan = Actual(Step(0, 0, 0, TimeSpan.Zero), Step(1, 0, 0, TimeSpan.Zero));

        var normalized = PlanActuals.Normalize(plan, Dbms.Tibero, out var reason);

        Assert.NotNull(reason);
        Assert.Contains("실제 통계 없음", reason, StringComparison.Ordinal);
        Assert.All(normalized.Steps, s =>
        {
            Assert.Null(s.ActualRows);
            Assert.Null(s.Starts);
            Assert.Null(s.ActualTime);
        });
        Assert.True(PlanViewModel.NeedsStatisticsBand(normalized)); // 0 이 아니라 「A-Rows 없음」 으로 다뤄진다
        Assert.False(Oranda.App.Features.ExecutionPlan.PlanMetrics.Compute(normalized.Steps).Actual != PlanActualMetric.None);
    }

    [Fact]
    public void Tibero_with_any_real_actual_value_is_left_alone()
    {
        var plan = Actual(Step(0, 0, 0, TimeSpan.Zero), Step(1, 5, 1, null));

        var normalized = PlanActuals.Normalize(plan, Dbms.Tibero, out var reason);

        Assert.Null(reason);
        Assert.Same(plan, normalized);
    }

    [Fact]
    public void Oracle_estimated_plans_and_already_empty_actuals_are_never_touched()
    {
        var zeros = Actual(Step(0, 0, 0, TimeSpan.Zero));
        Assert.Same(zeros, PlanActuals.Normalize(zeros, Dbms.Oracle, out var oracleReason)); // Oracle 불변
        Assert.Null(oracleReason);

        var estimated = new CorePlan([Step(0, 0, 0, TimeSpan.Zero)], "text"); // 커서 없음 = 예상 계획
        Assert.Same(estimated, PlanActuals.Normalize(estimated, Dbms.Tibero, out var estimatedReason));
        Assert.Null(estimatedReason);

        var empty = Actual(Step(0, null, null, null));
        Assert.Same(empty, PlanActuals.Normalize(empty, Dbms.Tibero, out var emptyReason));
        Assert.Null(emptyReason);
    }

    [Theory]
    [InlineData("NO", false)]
    [InlineData("n", false)]
    [InlineData("YES", true)]
    [InlineData("Y", true)]
    [InlineData("???", null)]
    [InlineData(null, null)]
    public void Gather_value_is_read_from_the_report_env_parameter_row(string? value, bool? expected)
    {
        IReadOnlyList<TuningEnvRow> env =
        [
            new(TuningEnvKind.Version, "banner", "Tibero 6"),
            new(TuningEnvKind.Parameter, PlanActuals.GatherParameter, value),
        ];

        Assert.Equal(expected, PlanActuals.GatherStatsFrom(env));
        Assert.Null(PlanActuals.GatherStatsFrom([]));
        Assert.Null(PlanActuals.GatherStatsFrom(null));
    }

    [Fact]
    public void Gather_value_decides_over_the_zero_guess()
    {
        var zeros = Actual(Step(0, 0, 0, TimeSpan.Zero));

        // 켜져 있다고 읽었으면 0 도 실제 값이다.
        Assert.Same(zeros, PlanActuals.Normalize(zeros, Dbms.Tibero, out var onReason, gatherStats: true));
        Assert.Null(onReason);

        // 꺼져 있다고 읽었으면 0 을 숨기고 환경 값을 이유로 댄다.
        var off = PlanActuals.Normalize(zeros, Dbms.Tibero, out var offReason, gatherStats: false);
        Assert.Contains("GATHER_SQL_PLAN_STAT", offReason, StringComparison.Ordinal);
        Assert.Null(off.Steps[0].ActualRows);

        // 꺼져 있고 이미 모두 비었으면 계획은 그대로, 이유만 붙는다.
        var empty = Actual(Step(0, null, null, null));
        Assert.Same(empty, PlanActuals.Normalize(empty, Dbms.Tibero, out var emptyReason, gatherStats: false));
        Assert.NotNull(emptyReason);

        // 꺼져 있다고 읽어도 값이 하나라도 있으면(모순) 건드리지 않는다.
        var real = Actual(Step(0, 5, 1, null));
        Assert.Same(real, PlanActuals.Normalize(real, Dbms.Tibero, out var realReason, gatherStats: false));
        Assert.Null(realReason);
    }

    private sealed class Services(params object[] instances) : IServiceProvider
    {
        public object? GetService(Type serviceType) => instances.FirstOrDefault(serviceType.IsInstanceOfType);
    }

    private sealed class ZeroPlans : IExecutionPlanService
    {
        public Task<CorePlan> ExplainAsync(ISession s, SqlStatement stmt, CancellationToken ct) => Task.FromResult(new CorePlan([], "x"));

        public Task<CorePlan> GetCursorPlanAsync(ISession s, CursorRef cursor, string format, CancellationToken ct) =>
            Task.FromResult(Actual(Step(0, 0, 0, TimeSpan.Zero)));
    }

    private sealed class EnvTuning(string? gather, bool fail = false) : ITuningService
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<TuningEnvRow>> GetTuningEnvironmentAsync(ISession s, CancellationToken ct)
        {
            Calls++;
            return fail
                ? throw new InvalidOperationException("boom")
                : Task.FromResult<IReadOnlyList<TuningEnvRow>>([new TuningEnvRow(TuningEnvKind.Parameter, PlanActuals.GatherParameter, gather)]);
        }
    }

    private sealed class TiberoSession : ISession
    {
        public Guid Id => Guid.NewGuid();

        public ConnectionProfile Profile { get; } = new("tb", "sys", "h:8629/s", Dbms: Dbms.Tibero);

        public ServerInfo Server => throw new NotSupportedException();

        public SessionPurpose Purpose => SessionPurpose.Work;

        public string DbKey => string.Empty;

        public SessionState State => SessionState.Idle;

        public bool HasPendingTransaction => false;

        public Task CommitAsync(CancellationToken ct) => Task.CompletedTask;

        public Task RollbackAsync(CancellationToken ct) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Theory]
    [InlineData("NO", true)]
    [InlineData("YES", false)]
    [InlineData(null, true)] // 값을 모르면 0 값 추정 그대로
    public async Task Show_cursor_on_tibero_asks_the_environment_once_and_follows_it(string? gather, bool hidden)
    {
        var tuning = new EnvTuning(gather);
        var vm = new PlanViewModel(new Services(new ZeroPlans(), tuning), new PlanShared()) { PostToUi = a => a() };

        await vm.ShowCursorAsync(new TiberoSession(), new CursorRef("abc"), default);

        Assert.Equal(1, tuning.Calls);
        Assert.Equal(hidden, vm.Rows[0].ActualText.Length == 0);
        Assert.Equal(hidden, vm.StatusText.Contains("실제 통계 없음", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Environment_read_failure_falls_back_to_the_zero_guess()
    {
        var tuning = new EnvTuning(null, fail: true);
        var vm = new PlanViewModel(new Services(new ZeroPlans(), tuning), new PlanShared()) { PostToUi = a => a() };

        await vm.ShowCursorAsync(new TiberoSession(), new CursorRef("abc"), default);

        Assert.Equal(1, tuning.Calls);
        Assert.Contains("실제 통계 없음", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void Trace_viewer_command_is_off_on_tibero_and_unchanged_on_oracle()
    {
        var enablement = typeof(TraceCommandHandler).GetCustomAttributes<OrandaCommandAttribute>()
            .Single(a => a.Id == "oranda.tuning.traceViewer").Enablement!;
        Assert.Contains("!unsupportedSessionTrace", enablement, StringComparison.Ordinal);

        var keys = new ContextKeyService();
        keys.Set("hasSession", true);
        try
        {
            DbmsFeatureKeys.Apply(keys, Dbms.Oracle);
            Assert.True(keys.Evaluate(enablement)); // noImplTuning 키는 없으면 거짓 — Oracle 은 이전과 같다
            DbmsFeatureKeys.Apply(keys, Dbms.Tibero);
            Assert.False(keys.Evaluate(enablement));
        }
        finally
        {
            DbmsFeatureKeys.Apply(keys, null);
        }
    }
}
