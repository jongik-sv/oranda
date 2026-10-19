using Oranda.App.Features.Navigation;

namespace Oranda.App.Tests.Features.Navigation;

/// <summary>TSK-04-06 시험 자리. 이 Task 가 채운다(지금은 틀 존재만 본다).</summary>
public class NavigationTests
{
    [Fact]
    public void Frame_exists()
    {
        Assert.Equal("Oranda.App.Features.Navigation", typeof(NavigationCommands).Namespace);
    }
}
