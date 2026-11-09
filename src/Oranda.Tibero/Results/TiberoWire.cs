using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Oranda.Tibero.Results;

/// <summary>
/// Tibero 가 보내는 원시 바이트(<c>TiberoDataReader.GetTiberoBinary(i).Value</c>) → .NET 값.
/// 드라이버(TDP.NET 1.1.35)의 NUMBER(28자리 초과)·TIMESTAMP(소수초)·TIMESTAMP WITH TIME ZONE·INTERVAL 변환이 틀리거나 던지므로
/// 바이트를 직접 해석한다. 바이트 형식과 실측 값은 docs/design/tibero-driver-values.md 2장. 드라이버를 참조하지 않아 어디서나 시험한다.
/// </summary>
internal static class TiberoWire
{
    private const int Bias = 0x40000000;

    /// <summary>decimal 이 안전하게 담는 최대 유효 자릿수.</summary>
    private const int MaxDecimalDigits = 28;

    /// <summary>
    /// NUMBER → 28자리·스케일 28 안이면 <see cref="decimal"/>, 아니면 문자열(<c>1E+125</c> 형, 정밀도 보존). 입력은 길이 바이트로 시작한다(<c>02 C1 81</c> = 1).
    /// </summary>
    public static object DecodeNumber(ReadOnlySpan<byte> bytes)
    {
        var (negative, digits, pointExp) = ParseNumber(bytes);
        if (digits.Length == 0)
        {
            return 0m;
        }

        var intDigits = digits.Length + pointExp; // 소수점 위치(첫 숫자 앞에서부터)
        if (digits.Length <= MaxDecimalDigits && pointExp >= -MaxDecimalDigits && intDigits <= MaxDecimalDigits)
        {
            var plain = Plain(digits, pointExp, intDigits);
            if (decimal.TryParse(plain, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var v))
            {
                return negative ? -v : v;
            }
        }

        var text = intDigits is > -10 and <= 40 && pointExp >= -40 ? Plain(digits, pointExp, intDigits) : Scientific(digits, intDigits);
        return negative ? "-" + text : text;
    }

    /// <summary>NUMBER 바이트를 (부호, 유효 숫자 문자열, 10 의 지수) 로 푼다. 값 = 숫자 × 10^지수. 0 이면 숫자가 빈 문자열.</summary>
    internal static (bool Negative, string Digits, int PointExp) ParseNumber(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 2 || bytes[0] != bytes.Length - 1)
        {
            throw new FormatException("NUMBER 바이트 길이가 맞지 않습니다.");
        }

        var first = bytes[1];
        if (bytes.Length == 2 && first == 0x80)
        {
            return (false, string.Empty, 0);
        }

        var negative = first < 0x80;
        var exp = negative ? 0xFF - first - 0xC0 : first - 0xC0;
        var end = bytes.Length;
        if (negative && bytes[end - 1] == 0xF0)
        {
            end--;
        }

        var sb = new StringBuilder((end - 2) * 2);
        for (var i = 2; i < end; i++)
        {
            var d = negative ? 0x80 - bytes[i] : bytes[i] - 0x80;
            if (d is < 0 or > 99)
            {
                throw new FormatException("NUMBER 숫자 바이트가 범위를 벗어났습니다.");
            }

            sb.Append((char)('0' + (d / 10))).Append((char)('0' + (d % 10)));
        }

        var groups = sb.Length / 2;
        var digits = sb.ToString();
        var pointExp = (2 * exp) - (2 * groups);

        var trimmedStart = digits.AsSpan().TrimStart('0');
        var trimmed = trimmedStart.TrimEnd('0');
        if (trimmed.Length == 0)
        {
            return (false, string.Empty, 0);
        }

        pointExp += trimmedStart.Length - trimmed.Length;
        return (negative, trimmed.ToString(), pointExp);
    }

    private static string Plain(string digits, int pointExp, int intDigits)
    {
        if (pointExp >= 0)
        {
            return digits + new string('0', pointExp);
        }

        if (intDigits > 0)
        {
            return digits[..intDigits] + "." + digits[intDigits..];
        }

        return "0." + new string('0', -intDigits) + digits;
    }

    private static string Scientific(string digits, int intDigits)
    {
        var mantissa = digits.Length == 1 ? digits : digits[..1] + "." + digits[1..];
        var exp = intDigits - 1;
        return mantissa + "E" + (exp < 0 ? "-" : "+") + Math.Abs(exp).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>DATE(8바이트) 또는 TIMESTAMP(12바이트, 나노초 4바이트 포함) → <see cref="DateTime"/>(Kind=Unspecified). 나노초는 틱(100ns)으로 내림.</summary>
    public static DateTime DecodeTimestamp(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 8)
        {
            throw new FormatException("DATE/TIMESTAMP 바이트가 모자랍니다.");
        }

        var dt = ReadDateTime(bytes);
        if (bytes.Length >= 12)
        {
            dt = dt.AddTicks(BinaryPrimitives.ReadUInt32BigEndian(bytes[8..12]) / 100);
        }

        return dt;
    }

    /// <summary>TIMESTAMP 의 나노초(0~999999999). DATE 는 0.</summary>
    public static int DecodeNanoseconds(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 12 ? (int)BinaryPrimitives.ReadUInt32BigEndian(bytes[8..12]) : 0;

    /// <summary>TIMESTAMP WITH TIME ZONE(17바이트): 앞 12바이트는 UTC 시각, 이어서 [시 오프셋+100][분 오프셋] → <see cref="DateTimeOffset"/>(오프셋 적용한 현지 시각). 범위를 넘으면 <see cref="FormatException"/>.</summary>
    public static DateTimeOffset DecodeTimestampTz(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 14)
        {
            throw new FormatException("TIMESTAMP WITH TIME ZONE 바이트가 모자랍니다.");
        }

        var utc = DecodeTimestamp(bytes[..12]);
        var offset = ReadOffset(bytes[12], bytes[13]);
        try
        {
            return new DateTimeOffset(utc + offset, offset);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            // 0001-01-01·9999-12-31 경계에서 오프셋을 적용하면 범위를 벗어난다. 호출자가 글자로 내린다.
            throw new FormatException("TIMESTAMP WITH TIME ZONE 가 DateTimeOffset 범위를 넘습니다.", ex);
        }
    }

    /// <summary>시간대 오프셋 바이트. 분의 부호는 시 오프셋을 따른다(시 오프셋이 0 이면 양수로 본다 — 서버가 -00:30 과 +00:30 을 같게 저장한다).</summary>
    internal static TimeSpan ReadOffset(byte hourByte, byte minuteByte)
    {
        var hours = hourByte - 100;
        var minutes = hours < 0 ? -minuteByte : minuteByte;
        return new TimeSpan(hours, minutes, 0);
    }

    /// <summary>INTERVAL YEAR TO MONTH(5바이트) → 총 개월 수(<c>1-2</c> = 14).</summary>
    public static long DecodeIntervalYearMonth(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 5)
        {
            throw new FormatException("INTERVAL YEAR TO MONTH 바이트가 모자랍니다.");
        }

        long years = BinaryPrimitives.ReadInt32BigEndian(bytes[..4]) - Bias;
        return (years * 12) + (bytes[4] - 11);
    }

    /// <summary>INTERVAL DAY TO SECOND(12바이트: 일·초·나노초 각 4바이트) → <see cref="TimeSpan"/>. 나노초는 틱으로 내림. 범위를 넘으면 <see cref="FormatException"/>.</summary>
    public static TimeSpan DecodeIntervalDaySecond(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 12)
        {
            throw new FormatException("INTERVAL DAY TO SECOND 바이트가 모자랍니다.");
        }

        long days = BinaryPrimitives.ReadInt32BigEndian(bytes[..4]) - Bias;
        long seconds = BinaryPrimitives.ReadInt32BigEndian(bytes[4..8]) - Bias;
        long nanos = BinaryPrimitives.ReadInt32BigEndian(bytes[8..12]) - Bias;
        try
        {
            return new TimeSpan(checked((days * TimeSpan.TicksPerDay) + (seconds * TimeSpan.TicksPerSecond) + (nanos / 100)));
        }
        catch (OverflowException ex)
        {
            // INTERVAL DAY(9) 는 TimeSpan 범위(약 1067만 일)를 넘을 수 있다. 호출자가 글자로 내린다.
            throw new FormatException("INTERVAL DAY TO SECOND 가 TimeSpan 범위를 넘습니다.", ex);
        }
    }

    /// <summary>
    /// BINARY_FLOAT(4바이트) → <see cref="float"/>, BINARY_DOUBLE(8바이트) → <see cref="double"/>. 서버가 저장한 바이트는 빅엔디언이고 크기 순서를 지키는 부호화다:
    /// 맨 위 비트가 1(양수·+0·NaN)이면 부호 비트만 뒤집고, 0(음수)이면 모든 비트를 뒤집은 뒤 IEEE 754 로 읽는다.
    /// 드라이버(TDP.NET 1.1.35)의 <c>GetValue</c>/<c>GetFloat</c>/<c>GetDouble</c> 은 음수에서 틀린 값을 주므로(-2.25 → -1.8749999) 이 바이트를 직접 푼다.
    /// 길이가 4·8 이 아니면 <see cref="FormatException"/>.
    /// </summary>
    internal static object DecodeBinaryFloat(ReadOnlySpan<byte> bytes)
    {
        switch (bytes.Length)
        {
            case 4:
                {
                    var stored = BinaryPrimitives.ReadUInt32BigEndian(bytes);
                    var bits = (stored & 0x80000000u) != 0 ? stored ^ 0x80000000u : ~stored;
                    return BitConverter.UInt32BitsToSingle(bits);
                }

            case 8:
                {
                    var stored = BinaryPrimitives.ReadUInt64BigEndian(bytes);
                    var bits = (stored & 0x8000000000000000ul) != 0 ? stored ^ 0x8000000000000000ul : ~stored;
                    return BitConverter.UInt64BitsToDouble(bits);
                }

            default:
                throw new FormatException("BINARY_FLOAT/BINARY_DOUBLE 원시 바이트는 4바이트 또는 8바이트여야 합니다: " + bytes.Length);
        }
    }

    /// <summary>드라이버가 RAW 를 <c>sbyte[]</c> 로 주므로 같은 메모리를 <c>byte[]</c> 로 옮긴다(복사).</summary>
    public static byte[] ToBytes(sbyte[] raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        var bytes = new byte[raw.Length];
        Buffer.BlockCopy(raw, 0, bytes, 0, raw.Length);
        return bytes;
    }

    /// <summary>바인드용 오프셋 글자(<c>+09:00</c>, <c>-05:30</c>). 드라이버 <c>TiberoTimeStampTZ(DateTime, string)</c> 가 받는 형식.</summary>
    public static string FormatOffset(TimeSpan offset)
    {
        var sign = offset < TimeSpan.Zero ? '-' : '+';
        var abs = offset.Duration();
        return string.Create(CultureInfo.InvariantCulture, $"{sign}{abs.Hours:00}:{abs.Minutes:00}");
    }

    private static DateTime ReadDateTime(ReadOnlySpan<byte> b)
    {
        if (b[0] < 100 || b[1] < 100)
        {
            throw new FormatException("기원전 날짜는 지원하지 않습니다.");
        }

        var year = ((b[0] - 100) * 100) + (b[1] - 100);
        return new DateTime(year, b[2], b[3], b[4], b[5], b[6], DateTimeKind.Unspecified);
    }
}
