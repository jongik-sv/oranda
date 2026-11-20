using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Oranda.Core.Tuning;

/// <summary>
/// 열 통계의 <c>low_value</c>·<c>high_value</c>(RAW, Oracle 내부 형식)를 자료형(<c>all_tab_columns.data_type</c>)에 맞춰 사람이 읽는 글로 푼다.
/// NUMBER·FLOAT = Oracle 숫자 형식(지수 바이트 + 100진 자리), DATE = 7바이트, TIMESTAMP = 11바이트(13바이트 = WITH TIME ZONE, UTC 로 저장),
/// CHAR·VARCHAR2 = DB 문자셋(UTF-8 먼저, 아니면 CP949), NCHAR·NVARCHAR2 = AL16UTF16, BINARY_FLOAT·DOUBLE = 정렬용 IEEE 형식, 그 밖 = 16진.
/// 풀지 못한 값은 16진으로 보인다(틀린 글을 보이지 않는다). 통계는 앞부분만 담으므로(긴 글은 잘림) 잘린 글자는 버린다.
/// </summary>
public static class ColumnStatValue
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    private static readonly Encoding StrictUtf16Be = new UnicodeEncoding(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: true);

    private static readonly Lazy<Encoding?> StrictCp949 = new(() =>
    {
        try
        {
            return CodePagesEncodingProvider.Instance.GetEncoding(949, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        }
        catch (ArgumentException)
        {
            return null;
        }
    });

    /// <summary>16진 글(<c>RAWTOHEX</c>) 또는 바이트 배열을 푼다. 값이 없으면 null.</summary>
    public static string? Decode(string? dataType, object? raw) => raw switch
    {
        null or DBNull => null,
        byte[] bytes => Decode(dataType, bytes),
        string hex when TryHex(hex, out var bytes) => Decode(dataType, bytes),
        string other => other.Length == 0 ? null : other,
        _ => Convert.ToString(raw, CultureInfo.InvariantCulture),
    };

    /// <summary>바이트를 자료형에 맞춰 푼다. 빈 배열이면 null.</summary>
    public static string? Decode(string? dataType, byte[]? raw)
    {
        if (raw is null || raw.Length == 0)
        {
            return null;
        }

        var type = (dataType ?? string.Empty).Trim().ToUpperInvariant();
        var text = type switch
        {
            "NUMBER" or "FLOAT" or "INTEGER" or "DECIMAL" or "NUMERIC" or "SMALLINT" or "INT" => Number(raw),
            "DATE" => Date(raw),
            "BINARY_FLOAT" => BinaryFloat(raw),
            "BINARY_DOUBLE" => BinaryDouble(raw),
            "CHAR" or "VARCHAR2" or "VARCHAR" or "LONG" => Text(raw, national: false),
            "NCHAR" or "NVARCHAR2" or "NVARCHAR" => Text(raw, national: true),
            _ when type.StartsWith("TIMESTAMP", StringComparison.Ordinal) => Timestamp(raw, type.Contains("TIME ZONE", StringComparison.Ordinal) && !type.Contains("LOCAL", StringComparison.Ordinal)),
            _ => null,
        };
        return text ?? Convert.ToHexString(raw);
    }

    /// <summary>Oracle NUMBER 내부 형식. 첫 바이트 = 부호·100진 지수, 나머지 = 100진 자리(양수 d+1, 음수 101-d, 음수 끝 102).</summary>
    public static string? Number(byte[] raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        if (raw.Length == 0 || raw.Length > 22)
        {
            return null;
        }

        var head = raw[0];
        if (head == 0x80 && raw.Length == 1)
        {
            return "0";
        }

        if (head == 0xFF && raw.Length == 2 && raw[1] == 0x65)
        {
            return "Infinity";
        }

        if (head == 0x00 && raw.Length == 1)
        {
            return "-Infinity";
        }

        var positive = (head & 0x80) != 0;
        int exponent;
        var digits = new List<int>(raw.Length);
        if (positive)
        {
            exponent = head - 193;
            for (var i = 1; i < raw.Length; i++)
            {
                var d = raw[i] - 1;
                if (d is < 0 or > 99)
                {
                    return null;
                }

                digits.Add(d);
            }
        }
        else
        {
            exponent = 62 - head;
            var end = raw.Length;
            if (end > 1 && raw[end - 1] == 102)
            {
                end--;
            }

            for (var i = 1; i < end; i++)
            {
                var d = 101 - raw[i];
                if (d is < 0 or > 99)
                {
                    return null;
                }

                digits.Add(d);
            }
        }

        if (digits.Count == 0)
        {
            return null;
        }

        return (positive ? string.Empty : "-") + Base100(digits, exponent);
    }

    /// <summary>100진 자리(첫 자리가 100^exponent)를 10진 글로. 아주 크거나 작으면 지수 표기.</summary>
    private static string Base100(List<int> digits, int exponent)
    {
        var all = new StringBuilder(digits.Count * 2);
        foreach (var d in digits)
        {
            all.Append(d.ToString("00", CultureInfo.InvariantCulture));
        }

        // all 의 소수점 위치: 첫 쌍이 100^exponent 이므로 (exponent+1) 쌍 뒤.
        var point = (exponent + 1) * 2;
        var mantissa = all.ToString();
        if (point > 60 || point < -40)
        {
            var trimmed = mantissa.TrimStart('0');
            var lead = mantissa.Length - trimmed.Length;
            trimmed = trimmed.TrimEnd('0');
            if (trimmed.Length == 0)
            {
                return "0";
            }

            var power = point - lead - 1;
            var frac = trimmed.Length > 1 ? "." + trimmed[1..] : string.Empty;
            return string.Create(CultureInfo.InvariantCulture, $"{trimmed[0]}{frac}E{(power >= 0 ? "+" : string.Empty)}{power}");
        }

        string integer, fraction;
        if (point <= 0)
        {
            integer = "0";
            fraction = new string('0', -point) + mantissa;
        }
        else if (point >= mantissa.Length)
        {
            integer = mantissa + new string('0', point - mantissa.Length);
            fraction = string.Empty;
        }
        else
        {
            integer = mantissa[..point];
            fraction = mantissa[point..];
        }

        integer = integer.TrimStart('0');
        if (integer.Length == 0)
        {
            integer = "0";
        }

        fraction = fraction.TrimEnd('0');
        return fraction.Length == 0 ? integer : integer + "." + fraction;
    }

    /// <summary>DATE 7바이트: 세기+100, 연+100, 월, 일, 시+1, 분+1, 초+1. 자정이면 날짜만.</summary>
    public static string? Date(byte[] raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        return raw.Length == 7 ? DateTimeText(raw, 0, utc: false) : null;
    }

    /// <summary>TIMESTAMP: 7바이트 = 초까지, 11바이트 = + 나노초(빅엔디언), 13바이트 = + 시간대(값은 UTC).</summary>
    public static string? Timestamp(byte[] raw, bool withTimeZone)
    {
        ArgumentNullException.ThrowIfNull(raw);
        if (raw.Length is not (7 or 11 or 13))
        {
            return null;
        }

        var nanos = raw.Length >= 11 ? BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(7, 4)) : 0u;
        if (nanos > 999_999_999)
        {
            return null;
        }

        var utc = withTimeZone && raw.Length == 13;
        return DateTimeText(raw, nanos, utc);
    }

    private static string? DateTimeText(byte[] raw, uint nanos, bool utc)
    {
        int century = raw[0], yy = raw[1], month = raw[2], day = raw[3], hour = raw[4] - 1, minute = raw[5] - 1, second = raw[6] - 1;
        if (century < 100 || yy < 100 || month is < 1 or > 12 || day is < 1 or > 31 || hour is < 0 or > 23 || minute is < 0 or > 59 || second is < 0 or > 59)
        {
            return null; // 기원전·잘못된 바이트는 16진으로.
        }

        var year = ((century - 100) * 100) + (yy - 100);
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"{year:0000}-{month:00}-{day:00}");
        if (hour != 0 || minute != 0 || second != 0 || nanos != 0 || utc)
        {
            text.Append(CultureInfo.InvariantCulture, $" {hour:00}:{minute:00}:{second:00}");
            if (nanos != 0)
            {
                text.Append('.').Append(nanos.ToString("000000000", CultureInfo.InvariantCulture).TrimEnd('0'));
            }
        }

        if (utc)
        {
            text.Append(" UTC");
        }

        return text.ToString();
    }

    /// <summary>BINARY_FLOAT 4바이트: 양수는 부호 비트를 켜고, 음수는 모든 비트를 뒤집어 저장한다(바이트 순 비교 = 값 순).</summary>
    public static string? BinaryFloat(byte[] raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        if (raw.Length != 4)
        {
            return null;
        }

        var bits = BinaryPrimitives.ReadUInt32BigEndian(raw);
        bits = (bits & 0x8000_0000u) != 0 ? bits & 0x7FFF_FFFFu : ~bits;
        return BitConverter.UInt32BitsToSingle(bits).ToString("R", CultureInfo.InvariantCulture);
    }

    /// <summary>BINARY_DOUBLE 8바이트(형식은 <see cref="BinaryFloat"/> 와 같다).</summary>
    public static string? BinaryDouble(byte[] raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        if (raw.Length != 8)
        {
            return null;
        }

        var bits = BinaryPrimitives.ReadUInt64BigEndian(raw);
        bits = (bits & 0x8000_0000_0000_0000ul) != 0 ? bits & 0x7FFF_FFFF_FFFF_FFFFul : ~bits;
        return BitConverter.UInt64BitsToDouble(bits).ToString("R", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 문자 열. 일반 문자는 DB 문자셋(AL32UTF8 을 먼저, 아니면 KO16MSWIN949 = CP949), 국가 문자는 AL16UTF16(빅엔디언).
    /// 통계는 앞 32바이트 남짓만 담아 끝 글자가 잘릴 수 있다 — 끝의 모자란 바이트는 버린다. 어느 쪽으로도 안 풀리면 null(16진).
    /// </summary>
    public static string? Text(byte[] raw, bool national)
    {
        ArgumentNullException.ThrowIfNull(raw);
        if (national)
        {
            var even = raw.Length - (raw.Length % 2);
            if (TryDecode(StrictUtf16Be, raw, even, 2, out var n))
            {
                return Clean(n);
            }

            return TryDecode(StrictUtf8, raw, raw.Length, 3, out var u) ? Clean(u) : null;
        }

        if (TryDecode(StrictUtf8, raw, raw.Length, 3, out var s))
        {
            return Clean(s);
        }

        return StrictCp949.Value is { } cp949 && TryDecode(cp949, raw, raw.Length, 1, out var k) ? Clean(k) : null;
    }

    /// <summary>앞 <paramref name="length"/> 바이트를 엄격하게 푼다. 안 되면 끝에서 <paramref name="maxTrim"/> 바이트까지 덜어 다시 해 본다(잘린 글자).</summary>
    private static bool TryDecode(Encoding encoding, byte[] raw, int length, int maxTrim, out string text)
    {
        var step = encoding is UnicodeEncoding ? 2 : 1;
        for (var trim = 0; trim <= maxTrim && length - trim > 0; trim += step)
        {
            try
            {
                text = encoding.GetString(raw, 0, length - trim);
                return true;
            }
            catch (DecoderFallbackException)
            {
            }
        }

        text = string.Empty;
        return false;
    }

    /// <summary>CHAR 의 뒤 공백은 지우고, 제어 문자가 있으면 풀지 않은 것으로 본다.</summary>
    private static string? Clean(string text)
    {
        var trimmed = text.TrimEnd(' ');
        foreach (var ch in trimmed)
        {
            if (char.IsControl(ch) && ch is not ('\t' or '\n' or '\r'))
            {
                return null;
            }
        }

        return trimmed;
    }

    private static bool TryHex(string hex, out byte[] bytes)
    {
        bytes = [];
        if (hex.Length == 0 || hex.Length % 2 != 0)
        {
            return false;
        }

        foreach (var ch in hex)
        {
            if (!char.IsAsciiHexDigit(ch))
            {
                return false;
            }
        }

        bytes = Convert.FromHexString(hex);
        return true;
    }
}
