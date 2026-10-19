using Oranda.App.Features.GridEdit;

namespace Oranda.App.Tests.Features.GridEdit;

/// <summary>TSK-06-04 시험 자리. 이 Task 가 채운다(지금은 틀 존재만 본다).</summary>
public class GridEditTests
{
    [Fact]
    public void Frame_exists()
    {
        Assert.Equal("Oranda.App.Features.GridEdit", typeof(GridEditCommands).Namespace);
    }
}
