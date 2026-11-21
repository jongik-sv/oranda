using System.Globalization;
using System.Text;

namespace Oranda.Tibero.Tuning;

/// <summary>
/// Tibero 열 통계 글 풀이. Tibero 6 의 <c>*_TAB_COL_STATISTICS.LOW_VALUE·HIGH_VALUE</c> 와 <c>*_TAB_HISTOGRAMS.ENDPOINT_ACTUAL_VALUE</c> 는
/// Oracle 처럼 RAW 내부 형식이 아니라 글이다(실측, Tibero 6 build 302172):
/// 숫자 = <c>' -3.4113000000000000000000000000000000000E+02'</c>(앞 공백·지수 표기), DATE = <c>20200103140000</c>(YYYYMMDDHH24MISS),
/// TIMESTAMP = <c>20230615093001623456000</c>(+ 나노초 9자리), 문자 = 그대로.
/// 숫자는 일반 숫자 글로, 날짜는 Oracle 쪽(<c>ColumnStatValue</c>)과 같은 모양(<c>yyyy-MM-dd[ HH:mm:ss[.fffffffff]]</c>, 자정이면 날짜만)으로 바꾸고, 나머지는 그대로 둔다.
/// </summary>
internal static class TiberoStatValue
{
    /// <summary>low_value·high_value 글을 자료형에 맞춰 푼다. 값이 없으면 null, 풀지 못하면 받은 글 그대로.</summary>
    public static string? Decode(string? dataType, object? value)
    {
        var text = value switch
        {
            null or DBNull => null,
            string s => s,
            byte[] b => Encoding.UTF8.GetString(b),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture),
        };
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var type = (dataType ?? string.Empty).Trim().ToUpperInvariant();
        var decoded = type switch
        {
            "NUMBER" or "FLOAT" or "INTEGER" or "DECIMAL" or "NUMERIC" or "SMALLINT" or "INT" or "BINARY_FLOAT" or "BINARY_DOUBLE" => Number(text),
            "DATE" => Date(text),
            _ when type.StartsWith("TIMESTAMP", StringComparison.Ordinal) => Date(text),
            _ => null,
        };
        return decoded ?? text;
    }

    /// <summary>
    /// 히스토그램 끝점 글: ENDPOINT_ACTUAL_VALUE(늘 채워진다)를 <see cref="Decode"/> 로. 비었으면 문자 열이 아닐 때만 ENDPOINT_VALUE
    /// (문자 열의 ENDPOINT_VALUE 는 65.25390625 같은 뜻 없는 수다).
    /// </summary>
    public static string? HistogramEndpoint(string? dataType, string? actualText, string? endpointValue)
    {
        if (!string.IsNullOrEmpty(actualText))
        {
            return Decode(dataType, actualText);
        }

        var type = (dataType ?? string.Empty).Trim().ToUpperInvariant();
        var character = type.Contains("CHAR", StringComparison.Ordinal) || type is "CLOB" or "NCLOB" or "LONG";
        return character || string.IsNullOrEmpty(endpointValue) ? null : Decode(dataType, endpointValue);
    }

    /// <summary>지수 표기 숫자 글(<c>' -3.4113000E+02'</c>)을 일반 숫자 글(<c>-341.13</c>)로. 아주 크거나 작으면 짧은 지수 표기.</summary>
    public static string? Number(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var s = text.Trim();
        if (s.Length == 0)
        {
            return null;
        }

        var negative = s[0] == '-';
        if (s[0] is '-' or '+')
        {
            s = s[1..];
        }

        var exponent = 0;
        var e = s.IndexOfAny(['E', 'e']);
        if (e >= 0)
        {
            if (!int.TryParse(s[(e + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exponent))
            {
                return null;
            }

            s = s[..e];
        }

        var point = s.IndexOf('.', StringComparison.Ordinal);
        var intPart = point >= 0 ? s[..point] : s;
        var fracPart = point >= 0 ? s[(point + 1)..] : string.Empty;
        var digits = intPart + fracPart;
        if (digits.Length == 0 || !digits.All(char.IsAsciiDigit))
        {
            return null;
        }

        // digits 의 소수점 위치(앞에서 몇 자리 뒤).
        var position = intPart.Length + exponent;
        var lead = digits.Length - digits.TrimStart('0').Length;
        digits = digits.TrimStart('0');
        position -= lead;
        digits = digits.TrimEnd('0');
        if (digits.Length == 0)
        {
            return "0";
        }

        var sign = negative ? "-" : string.Empty;
        if (position > 40 || position < -40)
        {
            var frac = digits.Length > 1 ? "." + digits[1..] : string.Empty;
            var power = position - 1;
            return string.Create(CultureInfo.InvariantCulture, $"{sign}{digits[0]}{frac}E{(power >= 0 ? "+" : string.Empty)}{power}");
        }

        string whole, fraction;
        if (position <= 0)
        {
            whole = "0";
            fraction = new string('0', -position) + digits;
        }
        else if (position >= digits.Length)
        {
            whole = digits + new string('0', position - digits.Length);
            fraction = string.Empty;
        }
        else
        {
            whole = digits[..position];
            fraction = digits[position..];
        }

        return sign + (fraction.Length == 0 ? whole : whole + "." + fraction);
    }

    /// <summary>14자리(YYYYMMDDHH24MISS) 또는 23자리(+ 나노초 9자리) 날짜 글. 다른 모양이면 null.</summary>
    public static string? Date(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var s = text.Trim();
        if (s.Length is not (14 or 23) || !s.All(char.IsAsciiDigit))
        {
            return null;
        }

        int year = Part(0, 4), month = Part(4, 2), day = Part(6, 2), hour = Part(8, 2), minute = Part(10, 2), second = Part(12, 2);
        if (year < 1 || month is < 1 or > 12 || day is < 1 or > 31 || hour > 23 || minute > 59 || second > 59)
        {
            return null;
        }

        var nanos = s.Length == 23 ? s[14..].TrimEnd('0') : string.Empty;
        var result = new StringBuilder();
        result.Append(CultureInfo.InvariantCulture, $"{year:0000}-{month:00}-{day:00}");
        if (hour != 0 || minute != 0 || second != 0 || nanos.Length != 0)
        {
            result.Append(CultureInfo.InvariantCulture, $" {hour:00}:{minute:00}:{second:00}");
            if (nanos.Length != 0)
            {
                result.Append('.').Append(nanos);
            }
        }

        return result.ToString();

        int Part(int start, int length) => int.Parse(s.AsSpan(start, length), NumberStyles.None, CultureInfo.InvariantCulture);
    }
}
