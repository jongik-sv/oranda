using Oranda.App.Features.FormatterProfiles;

namespace Oranda.App.Tests.Features.FormatterProfiles;

/// <summary>TSK-05-03 시험 자리. 이 Task 가 채운다(지금은 틀 존재만 본다).</summary>
public class FormatterProfilesTests
{
    [Fact]
    public void Frame_exists()
    {
        Assert.NotNull(new FormatterProfilesViewModel());
    }
}
