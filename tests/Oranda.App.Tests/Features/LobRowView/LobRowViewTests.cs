using Oranda.App.Features.LobRowView;

namespace Oranda.App.Tests.Features.LobRowView;

/// <summary>TSK-06-05 시험 자리. 이 Task 가 채운다(지금은 틀 존재만 본다).</summary>
public class LobRowViewTests
{
    [Fact]
    public void Frame_exists()
    {
        Assert.NotNull(new LobEditorViewModel());
    }
}
