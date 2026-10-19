using Oranda.App.Features.Help;

namespace Oranda.App.Tests.Features.Help;

/// <summary>TSK-10-01 시험 자리. 이 Task 가 채운다(지금은 틀 존재만 본다).</summary>
public class HelpTests
{
    [Fact]
    public void Frame_exists()
    {
        Assert.Equal("Oranda.App.Features.Help", typeof(HelpCommands).Namespace);
    }
}
