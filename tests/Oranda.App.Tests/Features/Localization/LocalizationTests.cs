using Oranda.App.Features.Localization;

namespace Oranda.App.Tests.Features.Localization;

/// <summary>TSK-10-02 시험 자리. 이 Task 가 채운다(지금은 틀 존재만 본다).</summary>
public class LocalizationTests
{
    [Fact]
    public void Frame_exists()
    {
        Assert.Equal("Oranda.App.Features.Localization", typeof(LocalizationCommands).Namespace);
    }
}
