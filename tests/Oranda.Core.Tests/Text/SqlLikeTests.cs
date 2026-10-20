using Oranda.Core.Text;

namespace Oranda.Core.Tests.Text;

// 소유: TSK-10-07. 세 SQLite 저장소가 나눠 쓰던 LIKE 이스케이프 규칙을 고정한다.
public sealed class SqlLikeTests
{
    [Theory]
    [InlineData("abc", "abc")]
    [InlineData("50%", "50\\%")]
    [InlineData("a_b", "a\\_b")]
    [InlineData("a\\b", "a\\\\b")]
    [InlineData("\\%_", "\\\\\\%\\_")]
    [InlineData("", "")]
    public void Escape(string input, string expected) => Assert.Equal(expected, SqlLike.Escape(input));
}
