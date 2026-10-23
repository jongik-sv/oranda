namespace Oranda.Language.Lexing;

/// <summary>구문 강조에서 토큰을 칠할 종류. <see cref="None"/> = 칠하지 않음(연산자·구두점·보통 식별자 등).</summary>
public enum SqlColorKind : byte
{
    None,
    Keyword,
    StringLiteral,
    Comment,
    Number,
    /// <summary>바인드(<c>:name</c>)와 치환(<c>&amp;name</c>) 변수.</summary>
    Bind,
    Hint,
    /// <summary>바로 뒤(공백 허용)에 여는 괄호가 오는 식별자.</summary>
    Function,
    Quoted,
}

/// <summary>
/// L1 렉서 토큰 → 칠할 종류. SQL 편집기·MyBatis 매퍼 편집기·한 줄 SQL 칸이 같은 규칙으로 칠하도록 한 곳에 둔다.
/// 각 화면은 종류를 자기 브러시·span 종류로 옮기기만 한다.
/// </summary>
public static class SqlTokenColors
{
    /// <summary><paramref name="input"/> = 토큰을 렉싱한 글(줄 하나 또는 글 전체, <see cref="Token.Start"/> 의 기준).</summary>
    public static SqlColorKind Of(Token token, ReadOnlySpan<char> input) => token.Kind switch
    {
        TokenKind.Keyword => SqlColorKind.Keyword,
        TokenKind.StringLiteral => SqlColorKind.StringLiteral,
        TokenKind.Comment => SqlColorKind.Comment,
        TokenKind.Number => SqlColorKind.Number,
        TokenKind.Bind or TokenKind.Substitution => SqlColorKind.Bind,
        TokenKind.Hint => SqlColorKind.Hint,
        TokenKind.QuotedIdent => SqlColorKind.Quoted,
        TokenKind.Identifier when IsFunctionCall(input, token.End) => SqlColorKind.Function,
        _ => SqlColorKind.None,
    };

    /// <summary>식별자 끝(<paramref name="end"/>) 바로 뒤에 공백(' ' 만)을 건너 여는 괄호가 오면 함수 호출로 본다. 탭·줄바꿈은 건너지 않는다.</summary>
    public static bool IsFunctionCall(ReadOnlySpan<char> input, int end)
    {
        int pos = end;
        while (pos < input.Length && input[pos] == ' ')
        {
            pos++;
        }

        return pos < input.Length && input[pos] == '(';
    }
}
