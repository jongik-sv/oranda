namespace Oranda.Core.Text;

/// <summary>SQL 문자열 리터럴 규칙 한 곳. 작은따옴표는 두 번으로 늘린다.</summary>
public static class SqlLiteral
{
    /// <summary>작은따옴표만 두 번으로 늘린다(바깥 따옴표는 붙이지 않는다).</summary>
    public static string Escape(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Replace("'", "''", StringComparison.Ordinal);
    }

    /// <summary>작은따옴표로 감싼 문자열 리터럴(안의 작은따옴표는 두 번).</summary>
    public static string Quote(string text) => "'" + Escape(text) + "'";
}
