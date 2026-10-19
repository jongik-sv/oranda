using Oranda.App.Features.SqlEditor;

namespace Oranda.App.Tests.Features.SqlEditor;

/// <summary>TSK-04-02 시험 자리. 이 Task 가 채운다(지금은 틀 존재만 본다).</summary>
public class SqlEditorTests
{
    [Fact]
    public void Frame_exists()
    {
        Assert.NotNull(new SqlEditorViewModel());
    }
}
