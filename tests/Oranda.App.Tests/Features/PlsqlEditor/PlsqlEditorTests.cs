using Oranda.App.Features.PlsqlEditor;

namespace Oranda.App.Tests.Features.PlsqlEditor;

/// <summary>TSK-08-01 시험 자리. 이 Task 가 채운다(지금은 틀 존재만 본다).</summary>
public class PlsqlEditorTests
{
    [Fact]
    public void Frame_exists()
    {
        Assert.NotNull(new PlsqlEditorViewModel());
    }
}
