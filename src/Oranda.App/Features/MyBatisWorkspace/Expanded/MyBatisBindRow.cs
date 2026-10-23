using System.Globalization;
using Oranda.App.Features.Localization;

namespace Oranda.App.Features.MyBatisWorkspace.Expanded;

/// <summary>「바인드 · 치환」 표의 한 줄. 바인드는 <c>:이름</c>, 치환은 <c>${이름}</c> 에 타입 「치환」 이다.</summary>
public sealed record MyBatisBindRow(string Name, string TypeText, string ValueText, bool IsSubstitution, bool IsNullValue)
{
    /// <summary>바인드 값을 표에 보일 글자: 글자는 SQL 처럼 작은따옴표로 싸고, null 은 <c>null</c>.</summary>
    public static string FormatValue(object? value) => value switch
    {
        null => "null",
        string s => "'" + s.Replace("'", "''", StringComparison.Ordinal) + "'",
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    /// <summary>값 형의 이름표(글자·숫자·참거짓·null). jdbcType 이 없는 바인드의 타입 칸에 쓴다.</summary>
    public static string KindLabel(object? value) => value switch
    {
        null => "null",
        bool => UiText.Get("MyBatis.kind.boolean", "참거짓"),
        sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal => UiText.Get("MyBatis.kind.number", "숫자"),
        _ => UiText.Get("MyBatis.kind.text", "글자"),
    };
}
