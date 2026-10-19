using Oranda.App.Features.SessionMonitor;

namespace Oranda.App.Tests.Features.SessionMonitor;

/// <summary>TSK-09-01 시험 자리. 이 Task 가 채운다(지금은 틀 존재만 본다).</summary>
public class SessionMonitorTests
{
    [Fact]
    public void Frame_exists()
    {
        Assert.NotNull(new SessionMonitorViewModel());
    }
}
