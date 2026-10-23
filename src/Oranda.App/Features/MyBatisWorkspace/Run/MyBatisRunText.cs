using System.Globalization;
using System.Text;
using Oranda.Core.Execution;
using Oranda.Language.Lexing;

namespace Oranda.App.Features.MyBatisWorkspace.Run;

/// <summary>
/// 펼친 SQL 복사 글 만들기(「펼친 SQL 복사」·「값을 넣은 글 복사」, design.md 4.1). 바인드 자리(<c>:이름</c>) 토큰만 바꾸므로
/// 글자 리터럴·주석 안의 콜론은 건드리지 않는다. 리터럴 모양은 「바인드 변수」 창의 「편집기에 값으로 붙여 넣기」(<c>BindVariableStore.ToLiteral</c>)와 같다.
/// </summary>
public static class MyBatisRunText
{
    /// <summary><paramref name="sql"/> 의 <c>:이름</c> 바인드를 <paramref name="binds"/> 의 값 리터럴로 바꾼 글. 값이 없는 이름은 그대로 둔다.</summary>
    public static string WithValues(string sql, IReadOnlyDictionary<string, BindValue> binds)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(binds);
        var sb = new StringBuilder(sql.Length + 32);
        var last = 0;
        foreach (var token in SqlLexer.Tokenize(sql))
        {
            if (token.Kind != TokenKind.Bind || token.Length < 2)
            {
                continue;
            }

            var name = sql.Substring(token.Start + 1, token.Length - 1);
            if (!binds.TryGetValue(name, out var value) || Literal(value) is not { } literal)
            {
                continue;
            }

            sb.Append(sql, last, token.Start - last).Append(literal);
            last = token.Start + token.Length;
        }

        return sb.Append(sql, last, sql.Length - last).ToString();
    }

    /// <summary>
    /// 바인드 값 하나의 SQL 리터럴: 글자는 '…'(' 는 ''), 숫자는 불변 문화권, 참거짓은 1/0, 날짜는 TO_DATE·TO_TIMESTAMP, null 은 NULL.
    /// 만들 수 없는 값(OUT 바인드 등)이면 null.
    /// </summary>
    public static string? Literal(BindValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Direction != BindDirection.In)
        {
            return null;
        }

        return value.Value switch
        {
            null => "NULL",
            string s => (value.Type == BindDataType.NVarchar2 ? "N" : string.Empty) + Quote(s),
            bool b => b ? "1" : "0",
            decimal d => d.ToString(CultureInfo.InvariantCulture),
            double d => d.ToString("R", CultureInfo.InvariantCulture),
            DateTime dt when value.Type == BindDataType.Timestamp =>
                "TO_TIMESTAMP('" + dt.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture) + "', 'YYYY-MM-DD HH24:MI:SS.FF')",
            DateTime dt => "TO_DATE('" + dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "', 'YYYY-MM-DD HH24:MI:SS')",
            DateTimeOffset o => "TO_TIMESTAMP_TZ('" + o.ToString("yyyy-MM-dd HH:mm:ss.fffffff zzz", CultureInfo.InvariantCulture) + "', 'YYYY-MM-DD HH24:MI:SS.FF TZH:TZM')",
            byte[] bytes => "HEXTORAW('" + System.Convert.ToHexString(bytes) + "')",
            _ => null,
        };
    }

    private static string Quote(string text) => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";
}
