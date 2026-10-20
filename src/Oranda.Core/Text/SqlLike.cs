namespace Oranda.Core.Text;

/// <summary>LIKE 패턴 조각 처리. SQLite 저장소 세 곳이 같은 규칙(<c>\</c> 를 이스케이프 문자로)을 쓴다.</summary>
public static class SqlLike
{
    /// <summary><c>\</c>·<c>%</c>·<c>_</c> 앞에 <c>\</c> 를 붙인다(<c>ESCAPE '\'</c> 와 함께 쓴다).</summary>
    public static string Escape(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
    }
}
