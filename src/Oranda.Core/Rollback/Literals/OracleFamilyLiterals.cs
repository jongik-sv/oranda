using System.Globalization;
using System.Text;
using Oranda.Core.Metadata;

namespace Oranda.Core.Rollback.Literals;

/// <summary>
/// Oracle 계열(Oracle·Tibero) 공용 값 읽기 식과 리터럴(되돌리기 지점 설계 6.6, RD-4). 공급자 방언(<see cref="IRollbackDialect"/>)이
/// <see cref="IRollbackDialect.KindOf"/>·<see cref="IRollbackDialect.ReadExpression"/>·<see cref="IRollbackDialect.Literal"/> 를 이것으로 구현한다.
/// <para>
/// 값은 서버에서 고정 형식 글자로 읽고(<see cref="ReadExpression"/>), 리터럴은 그 글자를 형식 함수로 감싼다. 그래서 .NET 값으로 바꿀 때 줄어드는
/// 정밀도(TIMESTAMP 9자리, NUMBER 38자리, 시간대 지역 이름)를 잃지 않는다. 리터럴 모양(작은따옴표 두 번, <c>TO_DATE</c>·<c>HEXTORAW</c>·<c>CHARTOROWID</c>)은
/// 「INSERT 문으로 복사」(<c>CellValueFormatter.ToSqlLiteral</c>)와 같다. 형식 마스크가 다른 DBMS 는 방언이 식을 바꾼다(Tibero 는 RB-13 실측).
/// </para>
/// </summary>
public static class OracleFamilyLiterals
{
    /// <summary>NUMBER·FLOAT 읽기의 형식 인자(<c>TO_CHAR(c, …)</c>). 소수점은 늘 <c>.</c>.</summary>
    public const string NumberFormat = "'TM9', 'NLS_NUMERIC_CHARACTERS=''.,'''";

    public const string DateMask = "SYYYY-MM-DD HH24:MI:SS";

    public const string TimestampMask = "SYYYY-MM-DD HH24:MI:SS.FF9";

    /// <summary>
    /// 시간대 지역 이름(<c>TZR</c>)까지 읽는다. 한계: 서머타임이 끝나 같은 벽시계 시각이 두 번 나오는 한 시간 안의 값은 지역 이름만으로
    /// 한 순간으로 다시 읽히므로, 다른 쪽 순간에 저장된 값이면 조건이 0행이 될 수 있다(드묾, 실측은 RB-12·RB-13).
    /// </summary>
    public const string TimestampTzMask = "SYYYY-MM-DD HH24:MI:SS.FF9 TZR";

    /// <summary>작은따옴표 리터럴의 한도(바이트, 서버 문자셋과 무관하게 UTF-8 로 센 보수적 값).</summary>
    public const int MaxTextLiteralBytes = 4000;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>사전 형식 이름(<see cref="ColumnInfo.DataType"/>) → 값 종류. 모르는 형식은 큰 열로 본다(조건에 넣지 않는 보수 원칙).</summary>
    public static RollbackValueKind KindOf(ColumnInfo column)
    {
        ArgumentNullException.ThrowIfNull(column);
        return KindOf(column.DataType);
    }

    /// <summary>형식 이름(예: <c>TIMESTAMP(6) WITH LOCAL TIME ZONE</c>) → 값 종류.</summary>
    public static RollbackValueKind KindOf(string dataType)
    {
        ArgumentNullException.ThrowIfNull(dataType);
        var t = dataType.Trim().ToUpperInvariant();
        var head = t.Split('(', ' ')[0];
        return head switch
        {
            "VARCHAR2" or "VARCHAR" or "CHAR" or "CHARACTER" => RollbackValueKind.Text,
            "NVARCHAR2" or "NCHAR" => RollbackValueKind.NationalText,
            "NUMBER" or "FLOAT" or "INTEGER" or "INT" or "SMALLINT" or "DECIMAL" or "NUMERIC" or "REAL" or "DOUBLE" => RollbackValueKind.Number,
            "BINARY_FLOAT" => RollbackValueKind.BinaryFloat,
            "BINARY_DOUBLE" => RollbackValueKind.BinaryDouble,
            "DATE" => RollbackValueKind.Date,
            "TIMESTAMP" when t.Contains("WITH LOCAL TIME ZONE", StringComparison.Ordinal) => RollbackValueKind.TimestampLtz,
            "TIMESTAMP" when t.Contains("WITH TIME ZONE", StringComparison.Ordinal) => RollbackValueKind.TimestampTz,
            "TIMESTAMP" => RollbackValueKind.Timestamp,
            "INTERVAL" when t.Contains("DAY", StringComparison.Ordinal) => RollbackValueKind.IntervalDaySecond,
            "INTERVAL" when t.Contains("YEAR", StringComparison.Ordinal) => RollbackValueKind.IntervalYearMonth,
            "RAW" => RollbackValueKind.Raw,
            "ROWID" => RollbackValueKind.RowId,
            _ => RollbackValueKind.Large,
        };
    }

    /// <summary>잠금 읽기·직후 값 읽기 SELECT 목록의 읽는 식(설계 6.6 표).</summary>
    /// <param name="column">열</param>
    /// <param name="qualifiedColumn">별칭·표 이름을 붙인 열 참조</param>
    public static string ReadExpression(RollbackColumn column, string qualifiedColumn)
    {
        ArgumentNullException.ThrowIfNull(column);
        ArgumentException.ThrowIfNullOrEmpty(qualifiedColumn);
        var c = qualifiedColumn;
        return column.Kind switch
        {
            RollbackValueKind.Number => $"TO_CHAR({c}, {NumberFormat})",
            RollbackValueKind.Date => $"TO_CHAR({c}, '{DateMask}')",
            RollbackValueKind.Timestamp => $"TO_CHAR({c}, '{TimestampMask}')",
            RollbackValueKind.TimestampTz => $"TO_CHAR({c}, '{TimestampTzMask}')",
            RollbackValueKind.TimestampLtz => $"TO_CHAR(SYS_EXTRACT_UTC(CAST({c} AS TIMESTAMP WITH TIME ZONE)), '{TimestampMask}')",
            RollbackValueKind.IntervalDaySecond or RollbackValueKind.IntervalYearMonth => $"TO_CHAR({c})",
            RollbackValueKind.Raw => $"RAWTOHEX({c})",
            RollbackValueKind.RowId => $"ROWIDTOCHAR({c})",
            RollbackValueKind.Large => $"CASE WHEN {c} IS NULL THEN 1 ELSE 0 END",
            _ => c,
        };
    }

    /// <summary>ROWID 를 읽는 식(<c>ROWIDTOCHAR(ROWID)</c>, 별칭이 있으면 붙인다).</summary>
    public static string RowIdReadExpression(string? alias = null) =>
        string.IsNullOrEmpty(alias) ? "ROWIDTOCHAR(ROWID)" : $"ROWIDTOCHAR({alias}.ROWID)";

    /// <summary>
    /// 값 → 리터럴. NULL·<see cref="DBNull"/> 은 <c>"NULL"</c>. 리터럴로 쓸 수 없으면 null 이고 <paramref name="reason"/> 이 불완전 이유다
    /// (큰 열, 4000바이트를 넘는 글, 숫자가 아닌 숫자 글 등).
    /// </summary>
    /// <param name="column">열</param>
    /// <param name="value"><see cref="ReadExpression"/> 로 읽은 값. 대개 글자이며, 이진 부동소수는 .NET float·double</param>
    /// <param name="reason">null 을 돌려줄 때의 이유</param>
    public static string? Literal(RollbackColumn column, object? value, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(column);
        reason = null;
        if (value is null or DBNull)
        {
            return "NULL";
        }

        switch (column.Kind)
        {
            case RollbackValueKind.Large:
                reason = $"큰 열({column.Name})";
                return null;
            case RollbackValueKind.BinaryFloat:
            case RollbackValueKind.BinaryDouble:
                return BinaryLiteral(value, column, out reason);
            case RollbackValueKind.Number:
                return NumberLiteral(value, column, out reason);
            case RollbackValueKind.Raw:
                return RawLiteral(value, column, out reason);
        }

        if (value is not string s)
        {
            // 날짜·시각·간격·글을 읽는 식(고정 형식 글자)으로 읽지 않은 드라이버 값이다. .NET 값은 정밀도·시간대를 잃어 조건이 0행이 될 수 있다.
            reason = $"값을 글자로 읽지 않음({column.Name})";
            return null;
        }

        switch (column.Kind)
        {
            case RollbackValueKind.Text:
                return Fits(s, column, out reason) ? Quote(s) : null;
            case RollbackValueKind.NationalText:
                var escaped = Unistr(s);
                return Fits(escaped, column, out reason) ? $"UNISTR({Quote(escaped)})" : null;
            case RollbackValueKind.Date:
                return $"TO_DATE({Quote(s)}, '{DateMask}')";
            case RollbackValueKind.Timestamp:
                return $"TO_TIMESTAMP({Quote(s)}, '{TimestampMask}')";
            case RollbackValueKind.TimestampTz:
                return $"TO_TIMESTAMP_TZ({Quote(s)}, '{TimestampTzMask}')";
            case RollbackValueKind.TimestampLtz:
                return $"CAST(FROM_TZ(TO_TIMESTAMP({Quote(s)}, '{TimestampMask}'), 'UTC') AS TIMESTAMP WITH LOCAL TIME ZONE)";
            case RollbackValueKind.IntervalDaySecond:
                return $"TO_DSINTERVAL({Quote(s)})";
            case RollbackValueKind.IntervalYearMonth:
                return $"TO_YMINTERVAL({Quote(s)})";
            case RollbackValueKind.RowId:
                return RowIdLiteral(s);
            default:
                return Quote(s);
        }
    }

    /// <summary>ROWID 글 → <c>CHARTOROWID('…')</c>.</summary>
    public static string RowIdLiteral(string rowId)
    {
        ArgumentNullException.ThrowIfNull(rowId);
        return $"CHARTOROWID({Quote(rowId)})";
    }

    /// <summary>
    /// 조건 한 조각: 리터럴이 <c>NULL</c> 이면 <c>c IS NULL</c>, 아니면 <c>c = 리터럴</c>(설계 6.6 「NULL 은 IS NULL」).
    /// </summary>
    public static string Predicate(string qualifiedColumn, string literal)
    {
        ArgumentException.ThrowIfNullOrEmpty(qualifiedColumn);
        ArgumentNullException.ThrowIfNull(literal);
        return literal == "NULL" ? qualifiedColumn + " IS NULL" : qualifiedColumn + " = " + literal;
    }

    /// <summary>작은따옴표 리터럴(안의 작은따옴표는 두 번).</summary>
    public static string Quote(string s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return "'" + s.Replace("'", "''", StringComparison.Ordinal) + "'";
    }

    /// <summary>
    /// <c>UNISTR</c> 인자 글: ASCII 밖 글자와 역슬래시를 <c>\XXXX</c>(UTF-16 단위)로 바꾼다. 서로게이트 쌍은 두 단위로 나뉘어 그대로 맞는다.
    /// 제어 문자(ASCII 32 미만)도 바꿔 SQL 글이 줄바꿈 등으로 흔들리지 않게 한다.
    /// </summary>
    public static string Unistr(string s)
    {
        ArgumentNullException.ThrowIfNull(s);
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (ch is >= ' ' and < (char)127 && ch != '\\')
            {
                sb.Append(ch);
            }
            else
            {
                sb.Append('\\').Append(((int)ch).ToString("X4", Inv));
            }
        }

        return sb.ToString();
    }

    private static bool Fits(string literalBody, RollbackColumn column, out string? reason)
    {
        if (Encoding.UTF8.GetByteCount(literalBody) > MaxTextLiteralBytes)
        {
            reason = $"{MaxTextLiteralBytes}바이트를 넘는 글({column.Name})";
            return false;
        }

        reason = null;
        return true;
    }

    private static string? RawLiteral(object value, RollbackColumn column, out string? reason)
    {
        reason = null;
        var hex = value switch
        {
            byte[] bytes => Convert.ToHexString(bytes),
            string text => text.Trim(),
            _ => null,
        };
        if (hex is null || hex.Length % 2 != 0 || !hex.All(char.IsAsciiHexDigit))
        {
            reason = $"RAW 16진 글이 아님({column.Name})";
            return null;
        }

        return Fits(hex, column, out reason) ? $"HEXTORAW('{hex}')" : null;
    }

    private static string? NumberLiteral(object value, RollbackColumn column, out string? reason)
    {
        reason = null;
        switch (value)
        {
            case decimal or long or int or short or byte or sbyte or ushort or uint or ulong:
                return Convert.ToString(value, Inv);
            case double or float:
                // NUMBER 를 .NET 부동소수로 읽으면 정밀도를 잃는다. 읽는 식(TO_CHAR TM9)을 쓰지 않은 값이므로 조건이 0행이 될 수 있다.
                reason = $"숫자를 글자로 읽지 않음({column.Name})";
                return null;
        }

        var s = Text(value).Trim();
        if (!IsNumberText(s))
        {
            reason = $"숫자 글이 아님({column.Name})";
            return null;
        }

        return s;
    }

    /// <summary>
    /// BINARY_FLOAT 열은 <c>TO_BINARY_FLOAT('R')</c>·<c>BINARY_FLOAT_NAN</c>·<c>BINARY_FLOAT_INFINITY</c>, BINARY_DOUBLE 열은 같은 이름의 DOUBLE 판.
    /// BINARY_FLOAT 열을 <c>TO_BINARY_DOUBLE</c> 로 견주면 0행이다(Tibero 실측, 조정 2026-11-13).
    /// </summary>
    private static string? BinaryLiteral(object value, RollbackColumn column, out string? reason)
    {
        reason = null;
        double d;
        switch (value)
        {
            case double x:
                d = x;
                break;
            case float f:
                d = f;
                break;
            case string str when column.Kind == RollbackValueKind.BinaryFloat && float.TryParse(str, NumberStyles.Float, Inv, out var parsedSingle):
                d = parsedSingle;
                break;
            case string str when double.TryParse(str, NumberStyles.Float, Inv, out var parsed):
                d = parsed;
                break;
            case string:
                reason = $"이진 부동소수 글이 아님({column.Name})";
                return null;
            case decimal or long or int or short:
                d = Convert.ToDouble(value, Inv);
                break;
            default:
                reason = $"이진 부동소수 값이 아님({column.Name})";
                return null;
        }

        var single = column.Kind == RollbackValueKind.BinaryFloat;
        var type = single ? "BINARY_FLOAT" : "BINARY_DOUBLE";
        if (double.IsNaN(d))
        {
            return type + "_NAN";
        }

        if (double.IsInfinity(d))
        {
            return (d < 0 ? "-" : string.Empty) + type + "_INFINITY";
        }

        var text = single ? ((float)d).ToString("R", Inv) : d.ToString("R", Inv);
        return $"TO_{type}('{text}')";
    }

    /// <summary>TM9 가 내는 숫자 글: 부호, 숫자, 소수점 하나, 지수(<c>E+n</c>). 앞자리 0 이 빠진 <c>.5</c> 도 받는다.</summary>
    private static bool IsNumberText(string s)
    {
        if (s.Length == 0)
        {
            return false;
        }

        var i = s[0] is '-' or '+' ? 1 : 0;
        var digits = 0;
        var dot = false;
        for (; i < s.Length; i++)
        {
            var ch = s[i];
            if (char.IsAsciiDigit(ch))
            {
                digits++;
            }
            else if (ch == '.' && !dot)
            {
                dot = true;
            }
            else if (ch is 'E' or 'e' && digits > 0)
            {
                var j = i + 1;
                if (j < s.Length && s[j] is '+' or '-')
                {
                    j++;
                }

                return j < s.Length && s[j..].All(char.IsAsciiDigit);
            }
            else
            {
                return false;
            }
        }

        return digits > 0;
    }

    private static string Text(object value) => value as string ?? Convert.ToString(value, Inv) ?? string.Empty;
}
