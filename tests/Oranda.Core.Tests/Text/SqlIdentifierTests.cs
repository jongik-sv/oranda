using Oranda.Core.Text;

namespace Oranda.Core.Tests.Text;

// 소유: TSK-10-07. 식별자 따옴표 규칙 셋(필요할 때만 / 예약어도 감쌈 / 항상)을 고정한다.
public sealed class SqlIdentifierTests
{
    [Theory]
    [InlineData("EMP", true)]
    [InlineData("EMP_NO$#1", true)]
    [InlineData("emp", false)]
    [InlineData("1EMP", false)]
    [InlineData("_EMP", false)]
    [InlineData("E MP", false)]
    [InlineData("", false)]
    public void IsPlain(string name, bool expected) => Assert.Equal(expected, SqlIdentifier.IsPlain(name));

    [Theory]
    [InlineData("EMP", "EMP")]
    [InlineData("emp", "\"emp\"")]
    [InlineData("A\"B", "\"A\"\"B\"")]
    public void Quote_only_when_needed(string name, string expected) => Assert.Equal(expected, SqlIdentifier.Quote(name));

    [Fact]
    public void Quote_wraps_reserved_words_when_asked()
    {
        Func<string, bool> reserved = n => n == "ORDER";
        Assert.Equal("\"ORDER\"", SqlIdentifier.Quote("ORDER", reserved));
        Assert.Equal("EMP", SqlIdentifier.Quote("EMP", reserved));
        Assert.Equal("ORDER", SqlIdentifier.Quote("ORDER"));
    }

    [Theory]
    [InlineData("EMP", "\"EMP\"")]
    [InlineData("a\"b", "\"a\"\"b\"")]
    public void QuoteAlways(string name, string expected) => Assert.Equal(expected, SqlIdentifier.QuoteAlways(name));
}
