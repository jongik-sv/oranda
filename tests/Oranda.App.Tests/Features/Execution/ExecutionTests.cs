using Oranda.App.Features.Execution;

namespace Oranda.App.Tests.Features.Execution;

/// <summary>TSK-06-02 시험 자리. 이 Task 가 채운다(지금은 틀 존재만 본다).</summary>
public class ExecutionTests
{
    [Fact]
    public void Frame_exists()
    {
        Assert.NotNull(new OutputViewModel());
    }
}
