using Oranda.App.Features.Completion;

namespace Oranda.App.Tests.Features.Completion;

/// <summary>TSK-04-05 시험 자리. 이 Task 가 채운다(지금은 틀 존재만 본다).</summary>
public class CompletionTests
{
    [Fact]
    public void Frame_exists()
    {
        Assert.Equal("Oranda.App.Features.Completion", typeof(CompletionCommands).Namespace);
    }
}
