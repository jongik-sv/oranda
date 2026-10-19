using Oranda.App.Features.DbTools;

namespace Oranda.App.Tests.Features.DbTools;

/// <summary>TSK-07-04 시험 자리. 이 Task 가 채운다(지금은 틀 존재만 본다).</summary>
public class DbToolsTests
{
    [Fact]
    public void Frame_exists()
    {
        Assert.NotNull(new DbInfoViewModel());
    }
}
