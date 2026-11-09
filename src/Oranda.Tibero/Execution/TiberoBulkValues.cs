using System.Buffers.Binary;
using System.Globalization;
using System.Text.RegularExpressions;
using Oranda.Tibero.Results;

namespace Oranda.Tibero.Execution;

// 대량 읽기(TiberoBulkExecutionService.StreamRowsAsync)가 내는 Tibero 전용 값(TB1 설계 1.2). 드라이버 형식(TiberoDecimal 등)·LOB 위치자가 아니라
// Oranda 값이라 원본 세션 값이 대상 세션 명령으로 넘어가도 연결이 섞이지 않는다(결정 25: 드라이버 형식은 Driver/ 밖으로 안 나감).
// 문자·LOB·RAW·DATE·BINARY_* 는 .NET 기본 형식(string·byte[]·DateTime·float·double)으로 내므로 여기에 없다.

/// <summary>
/// NUMBER 값 = 정확한 고정 소수점 글자(<c>-12.5</c>, <c>0.0000001</c>, 1E125 는 126자). 지수 표기·자리 구분·앞 0·뒤 0 이 없고 0 은 <c>"0"</c>(<c>-0</c> 없음).
/// decimal(28자리 한계)을 거치지 않아 38자리·1E±125 가 그대로다. 만드는 곳은 <see cref="FromBytes"/>·<see cref="Parse"/>·<see cref="FromDecimal"/>·<see cref="FromInt64"/> 뿐이다.
/// </summary>
internal sealed partial record TiberoBulkNumber
{
    private TiberoBulkNumber(string text) => Text = text;

    /// <summary>0.</summary>
    public static TiberoBulkNumber Zero { get; } = new("0");

    /// <summary>정규화된 고정 소수점 글자.</summary>
    public string Text { get; }

    /// <summary>원시 NUMBER 바이트(길이 바이트로 시작, <see cref="TiberoWire.ParseNumber"/>) → 값. 바이트가 틀리면 <see cref="FormatException"/>.</summary>
    public static TiberoBulkNumber FromBytes(ReadOnlySpan<byte> bytes)
    {
        var (negative, digits, pointExp) = TiberoWire.ParseNumber(bytes);
        if (digits.Length == 0)
        {
            return Zero;
        }

        var intDigits = digits.Length + pointExp; // 소수점 위치(첫 숫자 앞에서부터)
        string body;
        if (pointExp >= 0)
        {
            body = digits + new string('0', pointExp);
        }
        else if (intDigits > 0)
        {
            body = digits[..intDigits] + "." + digits[intDigits..];
        }
        else
        {
            body = "0." + new string('0', -intDigits) + digits;
        }

        return new(negative ? "-" + body : body);
    }

    /// <summary>
    /// 고정 소수점 글자(실패 행 키 글자·마스킹 고정값) → 값. 앞 부호 <c>+</c>/<c>-</c>, <c>.5</c>·<c>5.</c> 처럼 한쪽이 빈 소수점도 받는다
    /// (Oracle <c>FailedRowKeys</c> 가 받는 모양과 같게). 앞 0·뒤 0 을 지우고 <c>-0</c> 은 <c>0</c> 으로 맞춘다.
    /// 모양이 틀리면(지수·공백·쉼표·줄바꿈·숫자 없음) <see cref="FormatException"/>.
    /// </summary>
    public static TiberoBulkNumber Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!FixedPoint().IsMatch(text))
        {
            throw new FormatException($"NUMBER 글자가 고정 소수점 모양이 아닙니다: {text}");
        }

        var negative = text[0] == '-';
        var body = text[0] is '-' or '+' ? text[1..] : text;
        var dot = body.IndexOf('.', StringComparison.Ordinal);
        var integer = (dot < 0 ? body : body[..dot]).TrimStart('0');
        var fraction = dot < 0 ? string.Empty : body[(dot + 1)..].TrimEnd('0');
        if (integer.Length == 0 && fraction.Length == 0)
        {
            return Zero;
        }

        var normalized = (integer.Length == 0 ? "0" : integer) + (fraction.Length == 0 ? string.Empty : "." + fraction);
        return new(negative ? "-" + normalized : normalized);
    }

    /// <summary>decimal → 값(invariant, decimal 글자는 지수 표기가 없다).</summary>
    public static TiberoBulkNumber FromDecimal(decimal value) => Parse(value.ToString(CultureInfo.InvariantCulture));

    /// <summary>long → 값.</summary>
    public static TiberoBulkNumber FromInt64(long value) => Parse(value.ToString(CultureInfo.InvariantCulture));

    /// <summary>소수점 앞 글자(부호 포함, <c>-0.5</c> → <c>-0</c>).</summary>
    public string IntegerText
    {
        get
        {
            var dot = Text.IndexOf('.', StringComparison.Ordinal);
            return dot < 0 ? Text : Text[..dot];
        }
    }

    public override string ToString() => Text;

    // \A·\z: $ 는 끝 줄바꿈 앞에서도 맞으므로 쓰지 않는다. 숫자는 소수점 앞이나 뒤 한쪽에는 꼭 있다.
    [GeneratedRegex(@"\A[+-]?([0-9]+(\.[0-9]*)?|\.[0-9]+)\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex FixedPoint();
}

/// <summary>
/// TIMESTAMP(n) 값 = 초까지의 시각 + 나노초. <c>GetValue</c> 는 소수초를 버리고 DateTime 은 100ns 아래를 버리므로 따로 든다(TIMESTAMP(9) 손실 없음).
/// </summary>
/// <param name="Seconds">초까지의 시각(Kind=Unspecified, 소수초 없음).</param>
/// <param name="Nanosecond">0~999999999.</param>
internal sealed record TiberoBulkTimestamp(DateTime Seconds, int Nanosecond)
{
    public DateTime Seconds { get; } = TiberoBulkTime.WholeSeconds(Seconds, nameof(Seconds));

    public int Nanosecond { get; } = TiberoBulkTime.CheckNanosecond(Nanosecond);

    /// <summary>원시 TIMESTAMP 바이트(12바이트, DATE 8바이트면 나노초 0) → 값.</summary>
    public static TiberoBulkTimestamp FromBytes(ReadOnlySpan<byte> bytes)
        => new(TiberoBulkTime.SecondsOf(bytes), TiberoWire.DecodeNanoseconds(bytes));

    /// <summary>DateTime(100ns 틱까지) → 값.</summary>
    public static TiberoBulkTimestamp FromDateTime(DateTime value)
    {
        var fraction = value.Ticks % TimeSpan.TicksPerSecond;
        return new(DateTime.SpecifyKind(value.AddTicks(-fraction), DateTimeKind.Unspecified), (int)(fraction * 100));
    }
}

/// <summary>
/// TIMESTAMP WITH TIME ZONE 값 = UTC 시각 + 나노초 + 오프셋. 지역 이름은 오프셋만 남고, <c>-00:30</c> 은 부호를 알 수 없어 양수다(<see cref="TiberoWire.ReadOffset"/>).
/// 쓰기는 아직 꺼져 있다(TB1 설계 2.1).
/// </summary>
internal sealed record TiberoBulkTimestampTz(DateTime Utc, int Nanosecond, TimeSpan Offset)
{
    public DateTime Utc { get; } = TiberoBulkTime.WholeSeconds(Utc, nameof(Utc));

    public int Nanosecond { get; } = TiberoBulkTime.CheckNanosecond(Nanosecond);

    /// <summary>원시 바이트(앞 12바이트 UTC 시각, 이어서 [시 오프셋+100][분 오프셋]…) → 값.</summary>
    public static TiberoBulkTimestampTz FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 14)
        {
            throw new FormatException("TIMESTAMP WITH TIME ZONE 바이트가 모자랍니다.");
        }

        return new(TiberoBulkTime.SecondsOf(bytes), TiberoWire.DecodeNanoseconds(bytes), TiberoWire.ReadOffset(bytes[12], bytes[13]));
    }
}

/// <summary>TIMESTAMP WITH LOCAL TIME ZONE 값 = DB 시간대 시각(원시 바이트 그대로) + 나노초. 쓰기는 아직 꺼져 있다(TB1 설계 2.1).</summary>
internal sealed record TiberoBulkTimestampLtz(DateTime DbTime, int Nanosecond)
{
    public DateTime DbTime { get; } = TiberoBulkTime.WholeSeconds(DbTime, nameof(DbTime));

    public int Nanosecond { get; } = TiberoBulkTime.CheckNanosecond(Nanosecond);

    /// <summary>원시 바이트(TIMESTAMP 와 같은 모양) → 값.</summary>
    public static TiberoBulkTimestampLtz FromBytes(ReadOnlySpan<byte> bytes)
        => new(TiberoBulkTime.SecondsOf(bytes), TiberoWire.DecodeNanoseconds(bytes));
}

/// <summary>INTERVAL YEAR TO MONTH 값 = 총 개월(<c>1-2</c> = 14). long 을 그대로 내지 않는다(숫자 결과로 오인 방지). 쓰기 글자는 <c>+Y-M</c>(<c>TiberoBulkValueCodec.ToBindText</c>).</summary>
internal sealed record TiberoBulkIntervalYm(long Months)
{
    /// <summary>원시 바이트(5바이트) → 값.</summary>
    public static TiberoBulkIntervalYm FromBytes(ReadOnlySpan<byte> bytes) => new(TiberoWire.DecodeIntervalYearMonth(bytes));
}

/// <summary>
/// INTERVAL DAY TO SECOND 값 = 일·초(하루 안)·나노초, 셋 다 같은 부호(<c>-5 23:59:59.999999999</c> = −5, −86399, −999999999).
/// TimeSpan 은 100ns 아래를 버리고 DAY(9) 를 다 못 담아 쓰지 않는다. 쓰기 글자는 <c>+D HH:MI:SS.FFFFFFFFF</c>(<c>TiberoBulkValueCodec.ToBindText</c>).
/// </summary>
internal sealed record TiberoBulkIntervalDs(long Days, long Seconds, long Nanoseconds)
{
    private const int Bias = 0x40000000;

    /// <summary>원시 바이트(12바이트: 일·초·나노초 각 4바이트 빅엔디언 − 0x40000000) → 값.</summary>
    public static TiberoBulkIntervalDs FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 12)
        {
            throw new FormatException("INTERVAL DAY TO SECOND 바이트가 모자랍니다.");
        }

        return new(
            (long)BinaryPrimitives.ReadInt32BigEndian(bytes[..4]) - Bias,
            (long)BinaryPrimitives.ReadInt32BigEndian(bytes[4..8]) - Bias,
            (long)BinaryPrimitives.ReadInt32BigEndian(bytes[8..12]) - Bias);
    }
}

/// <summary>시각 값 기록형의 공용 검사·바이트 풀기.</summary>
internal static class TiberoBulkTime
{
    /// <summary>초까지 시각인지 본다(소수초가 있으면 나노초와 겹쳐 두 번 센다). Kind 는 Unspecified 로 맞춘다.</summary>
    public static DateTime WholeSeconds(DateTime value, string name)
    {
        if (value.Ticks % TimeSpan.TicksPerSecond != 0)
        {
            throw new ArgumentOutOfRangeException(name, value, "초 아래 자리는 나노초에 넣어야 합니다.");
        }

        return DateTime.SpecifyKind(value, DateTimeKind.Unspecified);
    }

    public static int CheckNanosecond(int nanosecond) => nanosecond is >= 0 and <= 999_999_999
        ? nanosecond
        : throw new ArgumentOutOfRangeException(nameof(nanosecond), nanosecond, "나노초는 0~999999999 입니다.");

    /// <summary>DATE·TIMESTAMP 모양 바이트의 앞 8바이트(초까지) → 시각.</summary>
    public static DateTime SecondsOf(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 8)
        {
            throw new FormatException("DATE/TIMESTAMP 바이트가 모자랍니다.");
        }

        return TiberoWire.DecodeTimestamp(bytes[..8]);
    }
}
