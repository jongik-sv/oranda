using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace Oranda.Core.Tuning;

/// <summary>
/// 열 통계의 <c>low_value</c>·<c>high_value</c>(RAW, Oracle 내부 형식)를 자료형(<c>all_tab_columns.data_type</c>)에 맞춰 사람이 읽는 글로 푼다.
/// NUMBER·FLOAT = Oracle 숫자 형식(지수 바이트 + 100진 자리), DATE = 7바이트, TIMESTAMP = 11바이트(13바이트 = WITH TIME ZONE, UTC 로 저장),
/// CHAR·VARCHAR2 = DB 문자셋(UTF-8 먼저, 아니면 CP949), NCHAR·NVARCHAR2 = AL16UTF16, BINARY_FLOAT·DOUBLE = 정렬용 IEEE 형식,
/// INTERVAL DAY TO SECOND = 11바이트, INTERVAL YEAR TO MONTH = 5바이트(각 칸에 오프셋), 그 밖 = 16진.
/// 히스토그램 끝점(<see cref="HistogramEndpoint"/>)은 endpoint_actual_value(_raw) 가 없으면 endpoint_value(수)를 자료형에 맞춰 푼다.
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

        return DecodeOrNull(dataType, raw) ?? Convert.ToHexString(raw);
    }

    /// <summary>바이트를 자료형에 맞춰 푼다. 풀지 못하면 null.</summary>
    private static string? DecodeOrNull(string? dataType, byte[] raw)
    {
        var type = Normalize(dataType);
        return type switch
        {
            "NUMBER" or "FLOAT" or "INTEGER" or "DECIMAL" or "NUMERIC" or "SMALLINT" or "INT" => Number(raw),
            "DATE" => Date(raw),
            "BINARY_FLOAT" => BinaryFloat(raw),
            "BINARY_DOUBLE" => BinaryDouble(raw),
            "CHAR" or "VARCHAR2" or "VARCHAR" or "LONG" => Text(raw, national: false),
            "NCHAR" or "NVARCHAR2" or "NVARCHAR" => Text(raw, national: true),
            _ when type.StartsWith("TIMESTAMP", StringComparison.Ordinal) => Timestamp(raw, IsUtc(type)),
            _ when type.StartsWith("INTERVAL DAY", StringComparison.Ordinal) => IntervalDayToSecond(raw),
            _ when type.StartsWith("INTERVAL YEAR", StringComparison.Ordinal) => IntervalYearToMonth(raw),
            _ => null,
        };
    }

    private static string Normalize(string? dataType) => (dataType ?? string.Empty).Trim().ToUpperInvariant();

    /// <summary>TIMESTAMP WITH TIME ZONE(값은 UTC). WITH LOCAL TIME ZONE 은 DB 시간대라 UTC 가 아니다.</summary>
    private static bool IsUtc(string type) => type.Contains("TIME ZONE", StringComparison.Ordinal) && !type.Contains("LOCAL", StringComparison.Ordinal);

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
        return DateTimeText(year, month, day, hour, minute, second, nanos, utc);
    }

    private static string DateTimeText(int year, int month, int day, int hour, int minute, int second, uint nanos, bool utc)
    {
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
    /// INTERVAL DAY TO SECOND 11바이트: 일(4바이트 빅엔디언 + 0x80000000), 시·분·초(각 + 60), 나노초(4바이트 + 0x80000000).
    /// 음수 간격은 칸마다 음수로 담긴다. 글 = <c>+3 17:00:00</c>, <c>-0 01:00:00.5</c>.
    /// </summary>
    public static string? IntervalDayToSecond(byte[] raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        if (raw.Length != 11)
        {
            return null;
        }

        long days = (long)BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(0, 4)) - 0x8000_0000L;
        int hour = raw[4] - 60, minute = raw[5] - 60, second = raw[6] - 60;
        long nanos = (long)BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(7, 4)) - 0x8000_0000L;
        if (Math.Abs(hour) > 23 || Math.Abs(minute) > 59 || Math.Abs(second) > 59 || Math.Abs(nanos) > 999_999_999)
        {
            return null;
        }

        var negative = days < 0 || hour < 0 || minute < 0 || second < 0 || nanos < 0;
        return DaySecondText(negative, Math.Abs(days), Math.Abs(hour), Math.Abs(minute), Math.Abs(second), (uint)Math.Abs(nanos));
    }

    /// <summary>INTERVAL YEAR TO MONTH 5바이트: 연(4바이트 빅엔디언 + 0x80000000), 월(+ 60). 글 = <c>+1-09</c>, <c>-0-05</c>.</summary>
    public static string? IntervalYearToMonth(byte[] raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        if (raw.Length != 5)
        {
            return null;
        }

        long years = (long)BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(0, 4)) - 0x8000_0000L;
        var months = raw[4] - 60;
        if (Math.Abs(months) > 11)
        {
            return null;
        }

        return YearMonthText(years < 0 || months < 0, Math.Abs(years), Math.Abs(months));
    }

    private static string DaySecondText(bool negative, long days, int hour, int minute, int second, uint nanos)
    {
        var text = new StringBuilder();
        text.Append(negative ? '-' : '+').Append(CultureInfo.InvariantCulture, $"{days} {hour:00}:{minute:00}:{second:00}");
        if (nanos != 0)
        {
            text.Append('.').Append(nanos.ToString("000000000", CultureInfo.InvariantCulture).TrimEnd('0'));
        }

        return text.ToString();
    }

    private static string YearMonthText(bool negative, long years, long months) =>
        string.Create(CultureInfo.InvariantCulture, $"{(negative ? '-' : '+')}{years}-{months:00}");

    /// <summary>
    /// 히스토그램 끝점 글. 순서: endpoint_actual_value_raw(12c+, 16진)를 자료형대로 → endpoint_actual_value(글) → endpoint_value(수)를 자료형대로
    /// (<see cref="Endpoint"/>) → 그래도 안 되면 endpoint_value 그대로. 자료형을 모르면(옛 질의) actual → endpoint_value 그대로.
    /// </summary>
    public static string? HistogramEndpoint(string? dataType, object? actualRaw, string? actualText, string? endpointValue)
    {
        var bytes = actualRaw switch
        {
            byte[] b => b,
            string hex when TryHex(hex, out var b) => b,
            _ => null,
        };
        if (bytes is { Length: > 0 } && DecodeOrNull(dataType, bytes) is { } decoded)
        {
            return decoded;
        }

        if (!string.IsNullOrEmpty(actualText))
        {
            return actualText;
        }

        if (string.IsNullOrEmpty(endpointValue))
        {
            return null;
        }

        return Endpoint(dataType, endpointValue) ?? endpointValue;
    }

    /// <summary>
    /// endpoint_value(수, <c>TO_CHAR</c> 글)를 자료형대로 푼다. 풀지 못하면 null.
    /// 문자 = 앞 15바이트를 256진 정수로 담은 수(유효 15자리로 반올림돼 앞 5~6바이트만 확실하다),
    /// DATE·TIMESTAMP = 율리우스 일수(<c>TO_CHAR(d, 'J')</c>) + 하루 비율(WITH TIME ZONE 은 UTC), INTERVAL DAY TO SECOND = 초, INTERVAL YEAR TO MONTH = 달 수,
    /// 숫자 = 그대로.
    /// </summary>
    public static string? Endpoint(string? dataType, string endpointValue)
    {
        ArgumentNullException.ThrowIfNull(endpointValue);
        var type = Normalize(dataType);
        return type switch
        {
            "CHAR" or "VARCHAR2" or "VARCHAR" => EndpointText(endpointValue, national: false),
            "NCHAR" or "NVARCHAR2" or "NVARCHAR" => EndpointText(endpointValue, national: true),
            "DATE" => EndpointDate(endpointValue, utc: false),
            _ when type.StartsWith("TIMESTAMP", StringComparison.Ordinal) => EndpointDate(endpointValue, IsUtc(type)),
            _ when type.StartsWith("INTERVAL DAY", StringComparison.Ordinal) => EndpointDaySecond(endpointValue),
            _ when type.StartsWith("INTERVAL YEAR", StringComparison.Ordinal) => EndpointYearMonth(endpointValue),
            _ => null,
        };
    }

    /// <summary>
    /// 문자 열의 endpoint_value: 앞 15바이트(모자라면 0 바이트로 채움)를 빅엔디언 256진 정수로 본 수를 유효 15자리로 줄인 값.
    /// 줄일 때 생긴 오차보다 큰 바이트 자리까지만 되살린다(대개 앞 6바이트). 끝의 0 바이트를 버렸으면 글 전체, 아니면 뒤가 잘렸을 수 있어 「…」를 붙인다.
    /// </summary>
    public static string? EndpointText(string endpointValue, bool national)
    {
        ArgumentNullException.ThrowIfNull(endpointValue);
        if (!TryInteger(endpointValue, out var value, out var digits) || value.Sign <= 0)
        {
            return null;
        }

        const int Width = 15;
        var limit = BigInteger.Pow(256, Width);
        if (value >= limit)
        {
            return null;
        }

        // 유효 15자리 아래는 반올림돼 있다(실측): 오차 ≤ 0.5 × 10^(자릿수 - 15). 단위(256^k)가 오차의 두 배보다 커야 그 위 바이트가 확실하다.
        var error = digits > 15 ? 5 * BigInteger.Pow(10, digits - 16) : BigInteger.Zero;
        var unit = BigInteger.One;
        var dropped = 0;
        while (unit <= error * 2)
        {
            unit *= 256;
            dropped++;
        }

        var kept = Width - dropped;
        var prefix = BigInteger.Min((value + error) / unit, BigInteger.Pow(256, kept) - 1);
        if (prefix.IsZero)
        {
            return null;
        }

        var bytes = new byte[kept];
        var big = prefix.ToByteArray(isUnsigned: true, isBigEndian: true);
        big.CopyTo(bytes, kept - big.Length);

        var length = national ? kept - (kept % 2) : kept;
        var step = national ? 2 : 1;
        var end = length;
        while (end >= step && bytes.AsSpan(end - step, step).IndexOfAnyExcept((byte)0) < 0)
        {
            end -= step;
        }

        if (end == 0)
        {
            return null;
        }

        var complete = end < length;
        var text = Text(bytes[..end], national);
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        return complete ? text : text + "…";
    }

    /// <summary>DATE·TIMESTAMP 의 endpoint_value: 율리우스 일수(1582-10-15 앞은 율리우스력, Oracle 과 같다) + 하루 비율. 초 단위로 반올림한다.</summary>
    public static string? EndpointDate(string endpointValue, bool utc)
    {
        ArgumentNullException.ThrowIfNull(endpointValue);
        if (!decimal.TryParse(endpointValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || value < 1 || value > 5_373_485m)
        {
            return null;
        }

        var dayNumber = (long)decimal.Floor(value);
        var seconds = (long)decimal.Round((value - dayNumber) * 86_400m, MidpointRounding.AwayFromZero);
        if (seconds >= 86_400)
        {
            dayNumber++;
            seconds -= 86_400;
        }

        var (year, month, day) = FromJulianDay(dayNumber);
        if (year < 1 || year > 9999)
        {
            return null;
        }

        return DateTimeText(year, month, day, (int)(seconds / 3600), (int)(seconds / 60 % 60), (int)(seconds % 60), 0, utc);
    }

    /// <summary>INTERVAL DAY TO SECOND 의 endpoint_value = 초.</summary>
    public static string? EndpointDaySecond(string endpointValue)
    {
        ArgumentNullException.ThrowIfNull(endpointValue);
        if (!decimal.TryParse(endpointValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return null;
        }

        var negative = value < 0;
        var abs = Math.Abs(value);
        var whole = decimal.Floor(abs);
        var nanos = (uint)decimal.Round((abs - whole) * 1_000_000_000m);
        if (nanos >= 1_000_000_000)
        {
            whole++;
            nanos = 0;
        }

        var total = (long)whole;
        return DaySecondText(negative, total / 86_400, (int)(total / 3600 % 24), (int)(total / 60 % 60), (int)(total % 60), nanos);
    }

    /// <summary>INTERVAL YEAR TO MONTH 의 endpoint_value = 달 수.</summary>
    public static string? EndpointYearMonth(string endpointValue)
    {
        ArgumentNullException.ThrowIfNull(endpointValue);
        if (!long.TryParse(endpointValue, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var months))
        {
            return null;
        }

        var abs = Math.Abs(months);
        return YearMonthText(months < 0, abs / 12, abs % 12);
    }

    /// <summary>율리우스 일수(정오 기준 정수) → 달력 날짜(Meeus). 2299161(1582-10-15) 앞은 율리우스력.</summary>
    private static (int Year, int Month, int Day) FromJulianDay(long z)
    {
        var a = z;
        if (z >= 2_299_161)
        {
            var alpha = (long)Math.Floor((z - 1_867_216.25) / 36_524.25);
            a = z + 1 + alpha - (alpha / 4);
        }

        var b = a + 1524;
        var c = (long)Math.Floor((b - 122.1) / 365.25);
        var d = (long)Math.Floor(365.25 * c);
        var e = (long)Math.Floor((b - d) / 30.6001);
        var day = (int)(b - d - (long)Math.Floor(30.6001 * e));
        var month = (int)(e < 14 ? e - 1 : e - 13);
        var year = (int)(month > 2 ? c - 4716 : c - 4715);
        return (year, month, day);
    }

    /// <summary>
    /// 정수 글(부호 없음, 소수점·지수 표기 허용)을 큰 정수로. <paramref name="digits"/> = 정수 부분 자릿수.
    /// </summary>
    private static bool TryInteger(string text, out BigInteger value, out int digits)
    {
        value = BigInteger.Zero;
        digits = 0;
        var s = text.Trim();
        var exponent = 0;
        var e = s.IndexOfAny(['E', 'e']);
        if (e >= 0)
        {
            if (!int.TryParse(s[(e + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exponent))
            {
                return false;
            }

            s = s[..e];
        }

        var point = s.IndexOf('.', StringComparison.Ordinal);
        var intPart = point >= 0 ? s[..point] : s;
        var fracPart = point >= 0 ? s[(point + 1)..] : string.Empty;
        var all = intPart + fracPart;
        if (all.Length == 0 || !all.All(char.IsAsciiDigit))
        {
            return false;
        }

        var scale = exponent - fracPart.Length; // value = all × 10^scale
        var mantissa = BigInteger.Parse(all, NumberStyles.None, CultureInfo.InvariantCulture);
        value = scale >= 0 ? mantissa * BigInteger.Pow(10, scale) : mantissa / BigInteger.Pow(10, -scale);
        digits = value.IsZero ? 0 : value.ToString(CultureInfo.InvariantCulture).Length;
        return true;
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
