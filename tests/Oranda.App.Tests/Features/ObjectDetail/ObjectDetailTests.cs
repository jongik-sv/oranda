using Oranda.App.Features.ObjectDetail;

namespace Oranda.App.Tests.Features.ObjectDetail;

/// <summary>TSK-07-03 시험 자리. 이 Task 가 채운다(지금은 틀 존재만 본다).</summary>
public class ObjectDetailTests
{
    [Fact]
    public void Frame_exists()
    {
        Assert.NotNull(new ObjectDetailViewModel());
    }
}
