using System.Globalization;
using Oracle.ManagedDataAccess.Client;
using Oracle.ManagedDataAccess.Types;
using Oranda.Core.Execution;
using Oranda.Oracle.Execution;

namespace Oranda.Oracle.Transfer.Copy;

/// <summary>
/// 실패 행 키 값 ↔ 기록 글자(<c>FailedRow.Key</c>). 값은 대량 읽기가 내는 ODP.NET 형식이다.
/// 글자 형식: 숫자 = 고정 소수점 invariant(지수 표기·자리 구분 쉼표 없음), DATE = <c>yyyy-MM-ddTHH:mm:ss</c>,
/// TIMESTAMP = <c>yyyy-MM-ddTHH:mm:ss.fffffff</c>(WITH TIME ZONE 은 끝에 <c>zzz</c>), RAW = 대문자 16진수, 문자 = 그대로.
/// 「실패 행만 다시 넣기」가 이 글자를 <see cref="FromText"/> 로 되돌려 키 바인드(<see cref="BindTypeOf"/>)로 원본을 다시 읽는다.
/// 키 열에는 마스킹을 걸 수 없으므로(설계 8.5) 이 글자에 마스킹 대상 값이 들어가지 않는다.
/// </summary>
internal static class FailedRowKeys
{
    private const string DateFormat = "yyyy-MM-dd'T'HH:mm:ss";
    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff";
    private const string SecondsFormat = "yyyy-MM-dd'T'HH:mm:ss";

    /// <summary>키 값 → 기록 글자. NULL 이면 null. 키로 쓸 수 없는 형식(INTERVAL·LOB 등)이면 <see cref="NotSupportedException"/>.</summary>
    public static string? ToText(object? value, string serverType)
    {
        if (value is null or DBNull)
        {
            return null;
        }

        switch (BulkBinds.ToDbType(serverType))
        {
            case OracleDbType.Decimal:
                return NumberText(value);
            case OracleDbType.BinaryFloat:
            case OracleDbType.BinaryDouble:
                return Convert.ToDouble(value, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture);
            case OracleDbType.Date:
                return value switch
                {
                    OracleDate { IsNull: true } => null,
                    OracleDate d => d.Value.ToString(DateFormat, CultureInfo.InvariantCulture),
                    DateTime dt => dt.ToString(DateFormat, CultureInfo.InvariantCulture),
                    _ => throw Unexpected(value, serverType),
                };
            case OracleDbType.TimeStamp:
                return value switch
                {
                    OracleTimeStamp { IsNull: true } => null,
                    OracleTimeStamp t => Stamp(t.Year, t.Month, t.Day, t.Hour, t.Minute, t.Second, t.Nanosecond),
                    DateTime dt => dt.ToString(TimestampFormat, CultureInfo.InvariantCulture),
                    _ => throw Unexpected(value, serverType),
                };
            case OracleDbType.TimeStampLTZ:
                return value switch
                {
                    OracleTimeStampLTZ { IsNull: true } => null,
                    OracleTimeStampLTZ t => Stamp(t.Year, t.Month, t.Day, t.Hour, t.Minute, t.Second, t.Nanosecond),
                    DateTime dt => dt.ToString(TimestampFormat, CultureInfo.InvariantCulture),
                    _ => throw Unexpected(value, serverType),
                };
            case OracleDbType.TimeStampTZ:
                return value switch
                {
                    OracleTimeStampTZ { IsNull: true } => null,
                    OracleTimeStampTZ t => Stamp(t.Year, t.Month, t.Day, t.Hour, t.Minute, t.Second, t.Nanosecond)
                                           + new DateTimeOffset(t.Value, t.GetTimeZoneOffset()).ToString("zzz", CultureInfo.InvariantCulture),
                    DateTimeOffset o => o.ToString(TimestampFormat + "zzz", CultureInfo.InvariantCulture),
                    _ => throw Unexpected(value, serverType),
                };
            case OracleDbType.Raw:
                return value switch
                {
                    byte[] b => Convert.ToHexString(b),
                    OracleBinary { IsNull: true } => null,
                    OracleBinary b => Convert.ToHexString(b.Value),
                    _ => throw Unexpected(value, serverType),
                };
            case OracleDbType.Varchar2:
            case OracleDbType.NVarchar2:
            case OracleDbType.Char:
            case OracleDbType.NChar:
                return value switch
                {
                    string s => s,
                    OracleString { IsNull: true } => null,
                    OracleString s => s.Value,
                    _ => throw Unexpected(value, serverType),
                };
            default:
                throw new NotSupportedException($"키 열로 다시 읽을 수 없는 형식입니다: {serverType}");
        }
    }

    /// <summary>기록 글자 → 키 값(대량 읽기와 같은 ODP.NET 형식). null 이면 null.</summary>
    public static object? FromText(string? text, string serverType)
    {
        if (text is null)
        {
            return null;
        }

        switch (BulkBinds.ToDbType(serverType))
        {
            case OracleDbType.Decimal:
                return ParseNumber(text);
            case OracleDbType.BinaryFloat:
                return float.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
            case OracleDbType.BinaryDouble:
                return double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
            case OracleDbType.Date:
                return new OracleDate(DateTime.ParseExact(text, DateFormat, CultureInfo.InvariantCulture));
            case OracleDbType.TimeStamp:
            {
                var (dt, nanos) = ParseStamp(text);
                return new OracleTimeStamp(dt.Year, dt.Month, dt.Day, dt.Hour, dt.Minute, dt.Second, nanos);
            }

            case OracleDbType.TimeStampLTZ:
            {
                var (dt, nanos) = ParseStamp(text);
                return new OracleTimeStampLTZ(dt.Year, dt.Month, dt.Day, dt.Hour, dt.Minute, dt.Second, nanos);
            }

            case OracleDbType.TimeStampTZ:
            {
                // 끝 6글자가 ±hh:mm 이다.
                var (dt, nanos) = ParseStamp(text[..^6]);
                return new OracleTimeStampTZ(dt.Year, dt.Month, dt.Day, dt.Hour, dt.Minute, dt.Second, nanos, text[^6..]);
            }
            case OracleDbType.Raw:
                return Convert.FromHexString(text);
            case OracleDbType.Varchar2:
            case OracleDbType.NVarchar2:
            case OracleDbType.Char:
            case OracleDbType.NChar:
                return text;
            default:
                throw new NotSupportedException($"키 열로 다시 읽을 수 없는 형식입니다: {serverType}");
        }
    }

    /// <summary>
    /// TIMESTAMP 글자. 소수 7자리(계약 형식, 100ns)로 쓰고, 그보다 작은 자리가 있으면(TIMESTAMP(8·9)) 9자리로 쓴다:
    /// 7자리로 자르면 다시 읽을 때 키가 맞지 않아 그 행을 찾지 못한다.
    /// </summary>
    private static string Stamp(int year, int month, int day, int hour, int minute, int second, int nanosecond)
    {
        var head = new DateTime(year, month, day, hour, minute, second).ToString(SecondsFormat, CultureInfo.InvariantCulture);
        return nanosecond % 100 == 0
            ? head + "." + (nanosecond / 100).ToString("D7", CultureInfo.InvariantCulture)
            : head + "." + nanosecond.ToString("D9", CultureInfo.InvariantCulture);
    }

    /// <summary><see cref="Stamp"/> 의 반대. 소수 7·9자리를 받는다.</summary>
    private static (DateTime Seconds, int Nanosecond) ParseStamp(string text)
    {
        var dot = text.IndexOf('.', StringComparison.Ordinal);
        if (dot < 0)
        {
            return (DateTime.ParseExact(text, SecondsFormat, CultureInfo.InvariantCulture), 0);
        }

        var seconds = DateTime.ParseExact(text[..dot], SecondsFormat, CultureInfo.InvariantCulture);
        var fraction = text[(dot + 1)..];
        if (fraction.Length is not (7 or 9) || !fraction.All(char.IsAsciiDigit))
        {
            throw new FormatException($"TIMESTAMP 키 글자의 소수 자리가 올바르지 않습니다: {text}");
        }

        var value = int.Parse(fraction, NumberStyles.None, CultureInfo.InvariantCulture);
        return (seconds, fraction.Length == 7 ? value * 100 : value);
    }

    /// <summary>키 열 형식 → 다시 읽기 바인드 형식(<see cref="FromText"/> 값을 그대로 싣는다).</summary>
    public static BindDataType BindTypeOf(string serverType) => BulkBinds.ToDbType(serverType) switch
    {
        OracleDbType.Decimal => BindDataType.Number,
        OracleDbType.BinaryFloat or OracleDbType.BinaryDouble => BindDataType.BinaryDouble,
        OracleDbType.Date => BindDataType.Date,
        OracleDbType.TimeStamp => BindDataType.Timestamp,
        OracleDbType.TimeStampLTZ => BindDataType.TimestampLtz,
        OracleDbType.TimeStampTZ => BindDataType.TimestampTz,
        OracleDbType.Raw => BindDataType.Raw,
        OracleDbType.NVarchar2 => BindDataType.NVarchar2,
        OracleDbType.Char or OracleDbType.NChar => BindDataType.FixedChar,
        OracleDbType.Varchar2 => BindDataType.Varchar2,
        _ => throw new NotSupportedException($"키 열로 다시 읽을 수 없는 형식입니다: {serverType}"),
    };

    /// <summary>
    /// NUMBER → 고정 소수점 글자. decimal 범위(유효 숫자 28~29자리)면 decimal 글자를 쓰고,
    /// 넘으면 ODP.NET 글자를 받아 지수 표기를 풀어 쓴다(NUMBER 는 유효 숫자 38자리).
    /// </summary>
    private static string? NumberText(object value)
    {
        switch (value)
        {
            case OracleDecimal { IsNull: true }:
                return null;
            case OracleDecimal od:
                // decimal 로 바꾸면 범위를 넘을 때 예외가 나고, 아주 작은 값은 자릿수가 잘린다. ODP.NET 글자를 받아 풀어 쓴다.
                return ExpandExponent(od.ToString());

            case decimal m:
                return m.ToString(CultureInfo.InvariantCulture);
            case IConvertible c:
                return c.ToDecimal(CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
            default:
                throw Unexpected(value, "NUMBER");
        }
    }

    /// <summary>
    /// 고정 소수점 글자 → OracleDecimal. 숫자만 남긴 정수 글자를 읽은 뒤 소수점을 옮긴다:
    /// decimal 을 거치면 아주 작은 값의 자릿수가 잘리고, 소수점 글자를 그대로 넘기면 문화권 구분자에 기댄다.
    /// </summary>
    private static OracleDecimal ParseNumber(string text)
    {
        var negative = text.StartsWith('-');
        var body = negative || text.StartsWith('+') ? text[1..] : text;
        var dot = body.IndexOf('.', StringComparison.Ordinal);
        var scale = dot < 0 ? 0 : body.Length - dot - 1;
        var digits = (dot < 0 ? body : body.Remove(dot, 1)).TrimStart('0');
        if (digits.Length == 0)
        {
            return OracleDecimal.Zero;
        }

        if (!digits.All(char.IsAsciiDigit))
        {
            throw new FormatException($"숫자 키 글자가 올바르지 않습니다: {text}");
        }

        var value = OracleDecimal.Parse(digits);
        if (scale > 0)
        {
            value = OracleDecimal.Shift(value, -scale);
        }

        return negative ? OracleDecimal.Negate(value) : value;
    }

    /// <summary><c>1.2345E+40</c> → <c>12345000…</c>. 지수가 없으면 그대로. 소수점이 쉼표로 오면(문화권) 점으로 바꾼다.</summary>
    internal static string ExpandExponent(string text)
    {
        text = text.Replace(',', '.');
        var e = text.IndexOfAny(['E', 'e']);
        if (e < 0)
        {
            return text;
        }

        var exponent = int.Parse(text.AsSpan(e + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        var mantissa = text[..e];
        var negative = mantissa.StartsWith('-');
        if (negative || mantissa.StartsWith('+'))
        {
            mantissa = mantissa[1..];
        }

        var dot = mantissa.IndexOf('.', StringComparison.Ordinal);
        var digits = dot < 0 ? mantissa : mantissa.Remove(dot, 1);
        var pointAt = (dot < 0 ? mantissa.Length : dot) + exponent; // 정수부 자리 수
        string body;
        if (pointAt <= 0)
        {
            body = "0." + new string('0', -pointAt) + digits;
        }
        else if (pointAt >= digits.Length)
        {
            body = digits + new string('0', pointAt - digits.Length);
        }
        else
        {
            body = digits[..pointAt] + "." + digits[pointAt..];
        }

        body = body.Contains('.', StringComparison.Ordinal) ? body.TrimEnd('0').TrimEnd('.') : body;
        body = body.TrimStart('0');
        if (body.Length == 0 || body[0] == '.')
        {
            body = "0" + body;
        }

        return negative && body != "0" ? "-" + body : body;
    }

    private static InvalidOperationException Unexpected(object value, string serverType) =>
        new($"키 열({serverType}) 값의 형식이 예상과 다릅니다: {value.GetType().Name}");
}
