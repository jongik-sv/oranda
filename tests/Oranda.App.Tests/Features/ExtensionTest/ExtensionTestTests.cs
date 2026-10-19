using Oranda.App.Features.ExtensionTest;

namespace Oranda.App.Tests.Features.ExtensionTest;

/// <summary>TSK-10-03 시험 자리. 이 Task 가 채운다(지금은 틀 존재만 본다).</summary>
public class ExtensionTestTests
{
    [Fact]
    public void Frame_exists()
    {
        Assert.NotNull(new ExtensionTestViewModel());
    }
}
