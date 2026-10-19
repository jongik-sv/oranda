// grammars-v4 sql/plsql CSharp/PlSqlParserBase.cs 를 옮긴 것(Apache-2.0). 생성 파서의 기반 클래스.
#pragma warning disable IDE1006 // 생성 코드가 이 이름(소문자 시작)으로 부른다.
using Antlr4.Runtime;

namespace Oranda.Language.Parsing.Grammar;

public abstract class PlSqlParserBase : Parser
{
    private bool _isVersion10;
    private bool _isVersion11 = true;
    private bool _isVersion12 = true;
    private bool _lastUnitWasPlsql;

    protected PlSqlParserBase(ITokenStream input)
        : base(input)
    {
    }

    protected PlSqlParserBase(ITokenStream input, TextWriter output, TextWriter errorOutput)
        : base(input, output, errorOutput)
    {
    }

    public override void Reset()
    {
        _lastUnitWasPlsql = false;
        base.Reset();
    }

    public void setLastUnitPlsql() => _lastUnitWasPlsql = true;

    public void setLastUnitSql() => _lastUnitWasPlsql = false;

    public bool isLastUnitSql() => !_lastUnitWasPlsql;

    public bool isLastUnitPlsql() => _lastUnitWasPlsql;

    public bool isSolidusSeparator()
    {
        var stream = (ITokenStream)InputStream;
        var solidus = stream.LT(1);
        if (solidus == null || solidus.Type != PlSqlParser.SOLIDUS)
        {
            return false;
        }

        int solidusLine = solidus.Line;
        var prev = stream.LT(-1);
        if (prev != null && prev.Type != TokenConstants.EOF && prev.Line == solidusLine)
        {
            return false;
        }

        var next = stream.LT(2);
        return next == null || next.Type == TokenConstants.EOF || next.Line != solidusLine;
    }

    public bool isVersion10() => _isVersion10;

    public bool isVersion11() => _isVersion11;

    public bool isVersion12() => _isVersion12;

    public bool setVersion10(bool value) => _isVersion10 = value;

    public bool setVersion11(bool value) => _isVersion11 = value;

    public bool setVersion12(bool value) => _isVersion12 = value;

    public bool IsNotNumericFunction()
    {
        var stream = (ITokenStream)InputStream;
        var lt1 = stream.LT(1);
        var lt2 = stream.LT(2);
        return !((lt1.Type == PlSqlParser.SUM
                  || lt1.Type == PlSqlParser.COUNT
                  || lt1.Type == PlSqlParser.AVG
                  || lt1.Type == PlSqlParser.MIN
                  || lt1.Type == PlSqlParser.MAX
                  || lt1.Type == PlSqlParser.ROUND
                  || lt1.Type == PlSqlParser.LEAST
                  || lt1.Type == PlSqlParser.GREATEST)
                 && lt2.Type == PlSqlParser.LEFT_PAREN);
    }

    public bool isNotStartOfJoin()
    {
        var lt1 = ((ITokenStream)InputStream).LT(1);
        return lt1.Type != PlSqlParser.INNER
               && lt1.Type != PlSqlParser.CROSS
               && lt1.Type != PlSqlParser.NATURAL
               && lt1.Type != PlSqlParser.PARTITION
               && lt1.Type != PlSqlParser.FULL
               && lt1.Type != PlSqlParser.LEFT
               && lt1.Type != PlSqlParser.RIGHT
               && lt1.Type != PlSqlParser.OUTER;
    }
}
