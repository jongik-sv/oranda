using Oranda.App.Features.SqlMonitor;

namespace Oranda.App.Tests.Features.SqlMonitor;

/// <summary>TSK-09-02 시험 자리. 이 Task 가 채운다(지금은 틀 존재만 본다).</summary>
public class SqlMonitorTests
{
    [Fact]
    public void Frame_exists()
    {
        Assert.NotNull(new SqlMonitorViewModel());
    }
}
