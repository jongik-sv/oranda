using Oranda.App.Features.ExecutionPlan;

namespace Oranda.App.Tests.Features.ExecutionPlan;

/// <summary>TSK-07-01 시험 자리. 이 Task 가 채운다(지금은 틀 존재만 본다).</summary>
public class ExecutionPlanTests
{
    [Fact]
    public void Frame_exists()
    {
        Assert.NotNull(new PlanViewModel());
    }
}
