using Oranda.App.Features.PlsqlRun;

namespace Oranda.App.Tests.Features.PlsqlRun;

/// <summary>TSK-08-02 시험 자리. 이 Task 가 채운다(지금은 틀 존재만 본다).</summary>
public class PlsqlRunTests
{
    [Fact]
    public void Frame_exists()
    {
        Assert.Equal("Oranda.App.Features.PlsqlRun", typeof(PlsqlRunCommands).Namespace);
    }
}
