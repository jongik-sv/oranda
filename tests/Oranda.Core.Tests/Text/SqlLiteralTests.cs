using Oranda.Core.Text;
using Xunit;

namespace Oranda.Core.Tests.Text;

public class SqlLiteralTests
{
    [Theory]
    [InlineData("", "''")]
    [InlineData("abc", "'abc'")]
    [InlineData("it's", "'it''s'")]
    [InlineData("''", "''''''")]
    [InlineData("a\r\nb\"c", "'a\r\nb\"c'")]
    public void Quote_wraps_and_doubles_single_quotes(string input, string expected) =>
        Assert.Equal(expected, SqlLiteral.Quote(input));

    [Fact]
    public void Escape_doubles_only_single_quotes() =>
        Assert.Equal("a''b\"c", SqlLiteral.Escape("a'b\"c"));

    [Fact]
    public void Null_throws()
    {
        Assert.Throws<ArgumentNullException>(() => SqlLiteral.Escape(null!));
        Assert.Throws<ArgumentNullException>(() => SqlLiteral.Quote(null!));
    }
}
