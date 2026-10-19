using Oranda.App.Features.HistoryScripts;

namespace Oranda.App.Tests.Features.HistoryScripts;

/// <summary>TSK-06-06 시험 자리. 이 Task 가 채운다(지금은 틀 존재만 본다).</summary>
public class HistoryScriptsTests
{
    [Fact]
    public void Frame_exists()
    {
        Assert.NotNull(new ScriptsViewModel());
    }
}
