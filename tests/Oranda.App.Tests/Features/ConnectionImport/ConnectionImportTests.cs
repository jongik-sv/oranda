using Oranda.App.Features.ConnectionImport;

namespace Oranda.App.Tests.Features.ConnectionImport;

/// <summary>TSK-03-05 시험 자리. 이 Task 가 채운다(지금은 틀 존재만 본다).</summary>
public class ConnectionImportTests
{
    [Fact]
    public void Frame_exists()
    {
        Assert.Equal("Oranda.App.Features.ConnectionImport", typeof(ConnectionImportCommands).Namespace);
    }
}
