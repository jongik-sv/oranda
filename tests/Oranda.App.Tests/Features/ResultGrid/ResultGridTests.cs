using Oranda.App.Features.ResultGrid;

namespace Oranda.App.Tests.Features.ResultGrid;

/// <summary>TSK-06-03 시험 자리. 이 Task 가 채운다(지금은 틀 존재만 본다).</summary>
public class ResultGridTests
{
    [Fact]
    public void Frame_exists()
    {
        Assert.NotNull(new SqlResultPaneViewModel());
    }
}
