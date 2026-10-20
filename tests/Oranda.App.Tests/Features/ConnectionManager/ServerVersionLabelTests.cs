using Oranda.App.Features.ConnectionManager;

namespace Oranda.App.Tests.Features.ConnectionManager;

// 소유: TSK-03-07. 상태 표시줄·접속 메시지의 서버 버전 짧은 이름(11.2 추가, 19c·26ai 는 그대로).
public class ServerVersionLabelTests
{
    [Theory]
    [InlineData("11.2.0.2.0", "11.2")]
    [InlineData("11.2.0.4.0", "11.2")]
    [InlineData("11", "11g")]
    [InlineData("19.0.0.0.0", "19c")]
    [InlineData("19.25.0.0.0", "19c")]
    [InlineData("23.4.0.24.5", "23ai")]
    [InlineData("23.26.0.0.0", "26ai")]
    [InlineData("", "")]
    [InlineData(null, "")]
    [InlineData("abc", "")]
    public void Status_bar_short_version(string? version, string expected)
        => Assert.Equal(expected, ConnectionStatusViewModel.ShortVersion(version));

    [Theory]
    [InlineData("11.2.0.2.0", "11g")]
    [InlineData("19.0.0.0.0", "19c")]
    [InlineData("23.26.0.0.0", "26ai")]
    [InlineData("x", null)]
    [InlineData(null, null)]
    public void Connection_test_label(string? version, string? expected)
        => Assert.Equal(expected, ConnectionService.ServerLabel(version));
}
