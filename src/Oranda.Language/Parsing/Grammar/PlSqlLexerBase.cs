// grammars-v4 sql/plsql CSharp/PlSqlLexerBase.cs 를 옮긴 것(Apache-2.0). 생성 렉서의 기반 클래스.
using Antlr4.Runtime;

namespace Oranda.Language.Parsing.Grammar;

public abstract class PlSqlLexerBase : Lexer
{
    private readonly ICharStream _input;

    protected PlSqlLexerBase(ICharStream input, TextWriter output, TextWriter errorOutput)
        : base(input, output, errorOutput)
    {
        _input = input;
    }

    protected PlSqlLexerBase(ICharStream input)
        : base(input)
    {
        _input = input;
    }

    public bool IsNewlineAtPos(int pos)
    {
        int la = _input.LA(pos);
        return la == -1 || la == '\n';
    }

    public bool IsQQuoteDelimiter()
    {
        int openingOffset = TokenStartCharIndex + 2 - _input.Index;
        int opening = _input.LA(openingOffset);
        int closing = _input.LA(-2);
        if (char.IsHighSurrogate((char)opening))
        {
            int openingLow = _input.LA(openingOffset + 1);
            if (char.IsLowSurrogate((char)openingLow))
            {
                opening = char.ConvertToUtf32((char)opening, (char)openingLow);
            }
        }

        if (char.IsLowSurrogate((char)closing))
        {
            int closingHigh = _input.LA(-3);
            if (char.IsHighSurrogate((char)closingHigh))
            {
                closing = char.ConvertToUtf32((char)closingHigh, (char)closing);
            }
        }

        return opening switch
        {
            '[' => closing == ']',
            '{' => closing == '}',
            '<' => closing == '>',
            '(' => closing == ')',
            _ => closing == opening,
        };
    }
}
