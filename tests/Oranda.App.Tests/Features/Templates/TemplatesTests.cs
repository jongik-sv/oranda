using Oranda.App.Features.Templates;

namespace Oranda.App.Tests.Features.Templates;

/// <summary>TSK-04-07 시험 자리. 이 Task 가 채운다(지금은 틀 존재만 본다).</summary>
public class TemplatesTests
{
    [Fact]
    public void Frame_exists()
    {
        Assert.NotNull(new TemplatesViewModel());
    }
}
