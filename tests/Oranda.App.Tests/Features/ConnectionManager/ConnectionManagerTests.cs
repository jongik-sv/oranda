using Oranda.App.Features.ConnectionManager;

namespace Oranda.App.Tests.Features.ConnectionManager;

/// <summary>TSK-03-04 시험 자리. 이 Task 가 채운다(지금은 틀 존재만 본다).</summary>
public class ConnectionManagerTests
{
    [Fact]
    public void Frame_exists()
    {
        Assert.Equal("Oranda.App.Features.ConnectionManager", typeof(ConnectionManagerCommands).Namespace);
    }
}
