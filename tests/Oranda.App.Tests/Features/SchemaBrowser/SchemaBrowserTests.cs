using Oranda.App.Features.SchemaBrowser;

namespace Oranda.App.Tests.Features.SchemaBrowser;

/// <summary>TSK-07-02 시험 자리. 이 Task 가 채운다(지금은 틀 존재만 본다).</summary>
public class SchemaBrowserTests
{
    [Fact]
    public void Frame_exists()
    {
        Assert.NotNull(new SchemaBrowserViewModel());
    }
}
