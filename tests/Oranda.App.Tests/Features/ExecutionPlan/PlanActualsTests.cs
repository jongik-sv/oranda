using System.Reflection;
using Oranda.App.Features.ExecutionPlan;
using Oranda.App.Features.Tuning.Trace;
using Oranda.App.Shell;
using Oranda.App.Shell.Features;
using Oranda.Core.Connection;
using Oranda.Core.Plans;
using Oranda.Core.Shell;
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
