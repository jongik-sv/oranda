namespace Oranda.Language.Lexing;

/// <summary>렉서 토큰 종류. 강조·문장 분리·자동완성이 함께 쓴다.</summary>
public enum TokenKind : byte
{
    Identifier,
    Keyword,
    QuotedIdent,
    Number,
    StringLiteral,
    Comment,
    Hint,
    Operator,
    Punct,
    /// <summary><c>:name</c>, <c>:1</c> 바인드 변수.</summary>
    Bind,
    /// <summary><c>&amp;name</c>, <c>&amp;&amp;name</c>, <c>&amp;1</c> 치환 변수.</summary>
    Substitution,
    Error,
}

/// <summary>토큰 1개. <see cref="Start"/> 는 렉싱한 입력(줄 또는 문서) 안의 0 기반 위치.</summary>
public readonly record struct Token(int Start, int Length, TokenKind Kind)
{
    public int End => Start + Length;
}
