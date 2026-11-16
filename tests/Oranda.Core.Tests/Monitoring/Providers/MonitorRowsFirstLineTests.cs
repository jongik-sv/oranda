using Oranda.Core.Monitoring.Providers;

namespace Oranda.Core.Tests.Monitoring.Providers;

// 감시·튜닝 화면 여섯 곳이 따로 갖던 FirstLine 사본을 하나로 합친 규칙을 고정한다.
public sealed class MonitorRowsFirstLineTests
{
    [Theory]
    [InlineData("", "")]
    [InlineData("one", "one")]
    [InlineData("  one  ", "one")]
    [InlineData("a\nb", "a")]
    [InlineData("a\r\nb", "a")]
    [InlineData("\nb", "")]
    public void FirstLine_returns_the_trimmed_first_line(string message, string expected)
        => Assert.Equal(expected, MonitorRows.FirstLine(message));
}
