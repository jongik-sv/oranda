using Oranda.Language.Lexing;

namespace Oranda.Language.Tests.Lexing;

/// <summary>
/// 구문 강조 공용 분류(R01): SQL 편집기·MyBatis 매퍼 편집기·한 줄 SQL 칸이 함께 쓰는 토큰 → 칠할 종류 규칙.
/// 세 곳에 복사돼 있던 규칙을 그대로 옮겼으므로 예전 동작(함수 호출은 ' ' 만 건너뜀 등)을 못 박는다.
/// </summary>
public class SqlTokenColorsTests
{
    [Theory]
    [InlineData(TokenKind.Keyword, SqlColorKind.Keyword)]
    [InlineData(TokenKind.StringLiteral, SqlColorKind.StringLiteral)]
    [InlineData(TokenKind.Comment, SqlColorKind.Comment)]
    [InlineData(TokenKind.Number, SqlColorKind.Number)]
    [InlineData(TokenKind.Bind, SqlColorKind.Bind)]
    [InlineData(TokenKind.Substitution, SqlColorKind.Bind)]
    [InlineData(TokenKind.Hint, SqlColorKind.Hint)]
    [InlineData(TokenKind.QuotedIdent, SqlColorKind.Quoted)]
    [InlineData(TokenKind.Identifier, SqlColorKind.None)]
    [InlineData(TokenKind.Operator, SqlColorKind.None)]
    [InlineData(TokenKind.Punct, SqlColorKind.None)]
    [InlineData(TokenKind.Error, SqlColorKind.None)]
    public void Every_token_kind_maps_to_one_color_kind(TokenKind token, SqlColorKind expected) =>
        Assert.Equal(expected, SqlTokenColors.Of(new Token(0, 1, token), "x"));

    [Theory]
    [InlineData("nvl(a)", true)]
    [InlineData("nvl   (a)", true)] // MyBatis 편집기는 태그를 공백으로 가린 줄을 넘긴다
    [InlineData("nvl\t(a)", false)]
    [InlineData("nvl\n(a)", false)]
    [InlineData("nvl\r(a)", false)]
    [InlineData("nvl", false)]
    [InlineData("nvl  ", false)]
    [InlineData("nvl, b", false)]
    public void Identifier_is_a_function_only_when_spaces_then_an_open_paren_follow(string input, bool function) =>
        Assert.Equal(function ? SqlColorKind.Function : SqlColorKind.None, SqlTokenColors.Of(new Token(0, 3, TokenKind.Identifier), input));

    [Fact]
    public void Lexed_line_colors_follow_the_shared_rule()
    {
        const string sql = "select nvl (sal, 0), 'x' /*+ hint */ from emp where id = :id -- c";
        var kinds = SqlLexer.Tokenize(sql).Select(t => (sql.Substring(t.Start, t.Length), SqlTokenColors.Of(t, sql)))
            .Where(p => p.Item2 != SqlColorKind.None).ToList();

        Assert.Contains(("select", SqlColorKind.Keyword), kinds);
        Assert.Contains(("nvl", SqlColorKind.Function), kinds);
        Assert.Contains(("'x'", SqlColorKind.StringLiteral), kinds);
        Assert.Contains(("0", SqlColorKind.Number), kinds);
        Assert.Contains((":id", SqlColorKind.Bind), kinds);
        Assert.Contains(("-- c", SqlColorKind.Comment), kinds);
        Assert.DoesNotContain(kinds, k => k.Item1 is "sal" or "emp" or "id");
    }
}
