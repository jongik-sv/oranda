using System.Globalization;
using System.Text;

namespace Oranda.Core.Transfer.Copy.CrossDbms;

/// <summary>
/// 이식 값 하나를 만들거나 대상 값으로 바꾸다 실패했다(NaN·Inf·기원전 날짜·모르는 시간대 지역 등). 결정 D5: 그 열 전체가 아니라 <b>그 행</b>이 실패한다.
/// 메시지는 사용자에게 보이는 실패 행 사유로 쓰인다.
/// </summary>
public sealed class PortableConversionException : Exception
{
    public PortableConversionException(string message)
        : base(message)
    {
    }

    public PortableConversionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// 숫자 이식 값(NUMBER·FLOAT). 고정소수점 글자(<c>-123.45</c>)로 들고 다녀 어느 쪽 드라이버의 숫자 형식(<c>OracleDecimal</c>·Tibero 원시 숫자)도 손실 없이 지난다.
/// 지수 표기·NaN·Inf 는 담지 않는다(<see cref="FromText"/> 가 지수 표기를 고정소수점으로 풀고, 나머지는 <see cref="PortableConversionException"/>).
/// </summary>
public readonly record struct PortableNumber
{
    /// <summary>숫자 자릿수 한도(양쪽 DBMS 모두 38자리 유효숫자, 지수는 -130..125 안). 풀어 쓴 글자가 이보다 길어지는 지수는 거절한다.</summary>
    private const int MaxExpandedLength = 400;

    private readonly string? _text;

    private PortableNumber(string text) => _text = text;

    /// <summary>고정소수점 글자. 기본값(default)이면 <c>"0"</c>.</summary>
    public string Text => _text ?? "0";

    public override string ToString() => Text;

    /// <summary>기본값(default)과 <c>"0"</c> 을 같게 보도록 <see cref="Text"/> 로 비교한다.</summary>
    public bool Equals(PortableNumber other) => string.Equals(Text, other.Text, StringComparison.Ordinal);

    public override int GetHashCode() => Text.GetHashCode(StringComparison.Ordinal);

    /// <summary>
    /// 숫자 글자(<c>12.5</c>, <c>-1.5E+3</c>, <c>1e-5</c>, <c>.5</c>)를 이식 값으로. 앞뒤 공백 무시. 지수는 풀고, 정수부 앞 0·소수부 끝 0 과 <c>-0</c> 은 정규화한다.
    /// 숫자가 아니거나 NaN·Inf 이면 <see cref="PortableConversionException"/>.
    /// </summary>
    public static PortableNumber FromText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var s = text.Trim();
        if (s.Length == 0)
        {
            throw new PortableConversionException("숫자 값이 비었습니다.");
        }

        var i = 0;
        var negative = false;
        if (s[i] is '+' or '-')
        {
            negative = s[i] == '-';
            i++;
        }

        var digits = new StringBuilder();
        var intDigits = 0;
        var seenPoint = false;
        for (; i < s.Length; i++)
        {
            var c = s[i];
            if (c is >= '0' and <= '9')
            {
                digits.Append(c);
                if (!seenPoint)
                {
                    intDigits++;
                }
            }
            else if (c == '.' && !seenPoint)
            {
                seenPoint = true;
            }
            else
            {
                break;
            }
        }

        if (digits.Length == 0)
        {
            throw new PortableConversionException($"숫자로 읽을 수 없는 값입니다: {Shorten(text)}");
        }

        long exponent = 0;
        if (i < s.Length)
        {
            if (s[i] is not ('E' or 'e'))
            {
                throw new PortableConversionException($"숫자로 읽을 수 없는 값입니다: {Shorten(text)}");
            }

            if (!long.TryParse(s.AsSpan(i + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exponent)
                || exponent is < -MaxExpandedLength or > MaxExpandedLength)
            {
                throw new PortableConversionException($"숫자의 지수가 범위를 넘습니다: {Shorten(text)}");
            }
        }

        // digits = 정수부+소수부 연속, 소수점 위치 = intDigits + exponent.
        var all = digits.ToString();
        var point = (long)intDigits + exponent;
        string intPart;
        string fracPart;
        if (point <= 0)
        {
            intPart = "0";
            fracPart = new string('0', (int)Math.Min(-point, MaxExpandedLength)) + all;
        }
        else if (point >= all.Length)
        {
            if (point - all.Length > MaxExpandedLength)
            {
                throw new PortableConversionException($"숫자의 지수가 범위를 넘습니다: {Shorten(text)}");
            }

            intPart = all + new string('0', (int)(point - all.Length));
            fracPart = string.Empty;
        }
        else
        {
            intPart = all[..(int)point];
            fracPart = all[(int)point..];
        }

        intPart = intPart.TrimStart('0');
        if (intPart.Length == 0)
        {
            intPart = "0";
        }

        fracPart = fracPart.TrimEnd('0');
        var isZero = intPart == "0" && fracPart.Length == 0;
        var result = (negative && !isZero ? "-" : string.Empty) + intPart + (fracPart.Length > 0 ? "." + fracPart : string.Empty);
        if (result.Length > MaxExpandedLength)
        {
            throw new PortableConversionException($"숫자의 지수가 범위를 넘습니다: {Shorten(text)}");
        }

        return new PortableNumber(result);
    }

    /// <summary><see cref="decimal"/> → 이식 값.</summary>
    public static PortableNumber FromDecimal(decimal value) => FromText(value.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// <see cref="double"/> 의 왕복 글자(<c>"R"</c>)로 만든 이식 값. NaN·±Inf 는 <see cref="PortableConversionException"/>(Oracle BINARY_* → Tibero NUMBER, 결정 D6).
    /// </summary>
    public static PortableNumber FromDouble(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            throw new PortableConversionException($"NUMBER 로 옮길 수 없는 값입니다: {value.ToString(CultureInfo.InvariantCulture)}");
        }

        return FromText(value.ToString("R", CultureInfo.InvariantCulture));
    }

    /// <summary><see cref="float"/> 의 왕복 글자(<c>"R"</c>)로 만든 이식 값(<see cref="FromDouble"/> 과 같은 규칙).</summary>
    public static PortableNumber FromSingle(float value)
    {
        if (float.IsNaN(value) || float.IsInfinity(value))
        {
            throw new PortableConversionException($"NUMBER 로 옮길 수 없는 값입니다: {value.ToString(CultureInfo.InvariantCulture)}");
        }

        return FromText(value.ToString("R", CultureInfo.InvariantCulture));
    }

    private static string Shorten(string text) => text.Length <= 40 ? text : text[..40] + "…";
}

/// <summary>BINARY_FLOAT 이식 값(NaN·±Inf 포함). 대상이 BINARY_FLOAT·BINARY_DOUBLE 일 때 쓴다(대상이 NUMBER 면 <see cref="PortableNumber.FromSingle"/>).</summary>
public readonly record struct PortableBinaryFloat(float Value);

/// <summary>BINARY_DOUBLE 이식 값(NaN·±Inf 포함).</summary>
public readonly record struct PortableBinaryDouble(double Value);

/// <summary>
/// 시각 이식 값(TIMESTAMP). <see cref="Seconds"/> 는 초 아래를 버린 달력 시각(<see cref="DateTimeKind.Unspecified"/>), <see cref="Nanos"/> 는 0..999,999,999.
/// 시간대가 없는 벽시계 시각이다. DATE 는 <see cref="DateTime"/> 을 그대로 쓴다.
/// </summary>
public readonly record struct PortableTimestamp
{
    public PortableTimestamp(DateTime seconds, int nanos)
    {
        if (nanos is < 0 or > 999_999_999)
        {
            throw new PortableConversionException($"초 아래 값이 범위를 넘습니다: {nanos}");
        }

        Seconds = DateTime.SpecifyKind(new DateTime(seconds.Ticks - (seconds.Ticks % TimeSpan.TicksPerSecond)), DateTimeKind.Unspecified);
        Nanos = nanos;
    }

    /// <summary>초 단위로 자른 달력 시각.</summary>
    public DateTime Seconds { get; }

    /// <summary>초 아래 나노초.</summary>
    public int Nanos { get; }

    /// <summary>.NET <see cref="DateTime"/>(100ns 틱)에서 만든다. 틱 아래(나노 단위 끝 두 자리)는 0.</summary>
    public static PortableTimestamp FromDateTime(DateTime value) =>
        new(value, (int)(value.Ticks % TimeSpan.TicksPerSecond) * 100);
}

/// <summary>
/// 시간대 있는 시각 이식 값(TIMESTAMP WITH TIME ZONE). <see cref="Local"/> 은 그 시간대의 벽시계 시각이다.
/// 시간대는 <see cref="Offset"/>(<c>+09:00</c>, <c>-03:30</c>) 또는 <see cref="Region"/>(<c>Asia/Seoul</c>) 중 하나 이상으로 든다.
/// 지역 이름은 대상이 지역을 못 받으면 그 시각의 오프셋으로 바뀌고 지역은 사라진다(결정 D7).
/// </summary>
public readonly record struct PortableTimestampTz
{
    public PortableTimestampTz(PortableTimestamp local, string? offset, string? region)
    {
        if (string.IsNullOrWhiteSpace(offset) && string.IsNullOrWhiteSpace(region))
        {
            throw new PortableConversionException("시간대 정보(오프셋 또는 지역)가 없습니다.");
        }

        var offsetMinutes = 0;
        if (!string.IsNullOrWhiteSpace(offset) && !TryParseOffsetMinutes(offset, out offsetMinutes))
        {
            throw new PortableConversionException($"시간대 오프셋을 읽을 수 없습니다: {offset}");
        }

        Local = local;
        Offset = string.IsNullOrWhiteSpace(offset) ? null : FormatOffset(offsetMinutes);
        Region = string.IsNullOrWhiteSpace(region) ? null : region.Trim();
    }

    /// <summary>그 시간대의 벽시계 시각.</summary>
    public PortableTimestamp Local { get; }

    /// <summary><c>±HH:MM</c> 로 정규화한 오프셋. 모르면 null(지역만 아는 값).</summary>
    public string? Offset { get; }

    /// <summary>시간대 지역 이름. 오프셋만 아는 값이면 null.</summary>
    public string? Region { get; }

    /// <summary>오프셋 분(동쪽 +). 오프셋을 모르면 null.</summary>
    public int? OffsetMinutes => Offset is not null && TryParseOffsetMinutes(Offset, out var m) ? m : null;

    /// <summary>분 → <c>±HH:MM</c>.</summary>
    public static string FormatOffset(int minutes)
    {
        var abs = Math.Abs(minutes);
        return string.Create(CultureInfo.InvariantCulture, $"{(minutes < 0 ? '-' : '+')}{abs / 60:D2}:{abs % 60:D2}");
    }

    /// <summary><c>±HH:MM</c>(또는 <c>±H:MM</c>, 분은 정확히 두 자리) 오프셋 글자 → 분. 범위는 ±15:59 까지만 받는다.</summary>
    public static bool TryParseOffsetMinutes(string? text, out int minutes)
    {
        minutes = 0;
        if (text is null)
        {
            return false;
        }

        var s = text.Trim();
        if (s.Length < 5 || s[0] is not ('+' or '-'))
        {
            return false;
        }

        var colon = s.IndexOf(':', StringComparison.Ordinal);
        if (colon is < 2 or > 3 || s.Length != colon + 3
            || !int.TryParse(s.AsSpan(1, colon - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var h)
            || !int.TryParse(s.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var m)
            || m > 59 || h > 15)
        {
            return false;
        }

        minutes = (s[0] == '-' ? -1 : 1) * ((h * 60) + m);
        return true;
    }
}

/// <summary>INTERVAL YEAR TO MONTH 이식 값. 부호 있는 전체 개월 수.</summary>
public readonly record struct PortableIntervalYm(long Months);

/// <summary>INTERVAL DAY TO SECOND 이식 값. 부호 있는 전체 나노초(일 9자리·초 소수 9자리까지 <see cref="Int128"/> 안).</summary>
public readonly record struct PortableIntervalDs(Int128 Nanoseconds);
