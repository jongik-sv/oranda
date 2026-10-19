using Oranda.App.Features.DataTransfer;

namespace Oranda.App.Tests.Features.DataTransfer;

/// <summary>TSK-06-07 시험 자리. 이 Task 가 채운다(지금은 틀 존재만 본다).</summary>
public class DataTransferTests
{
    [Fact]
    public void Frame_exists()
    {
        Assert.Equal("Oranda.App.Features.DataTransfer", typeof(DataTransferCommands).Namespace);
    }
}
