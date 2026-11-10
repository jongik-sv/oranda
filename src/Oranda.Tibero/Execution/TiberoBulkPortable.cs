using System.Globalization;
using Oranda.Core.Transfer.Copy.CrossDbms;

namespace Oranda.Tibero.Execution;

// 교차 복사(Oracle↔Tibero) 이식 값 멤버(IBulkValueCodec.IsReadable·ToPortable·FromPortable, 설계 docs/design/cross-dbms-copy.md 2·3장).
// 같은 DBMS 복사는 이 멤버를 부르지 않고 IsSupported·ToBindText 가 그대로다. 꺼진 형식(BINARY_*·TZ·LONG·LONG RAW)은 TiberoBulkShape.IsSupported 가
// 계속 거짓이고, 교차 쓰기만 CrossBindText·CrossBindExpression 우회로 TZ 와 BINARY_* 를 보낸다.
// TZ 쓰기 서식(TiberoBulkTypes.TimestampTzShape 의 TO_TIMESTAMP_TZ(:C1, 'YYYY-MM-DD HH24:MI:SS.FF9 TZH:TZM'))은 후보다 — XC5(실측 IT)가 검증하고,
// 실측이 녹색이면 그때 TiberoBulkShape.IsSupported 를 켠다(같은 DBMS 복사·사전 점검 동작이 바뀌는 것은 그때다).
internal sealed partial class TiberoBulkValueCodec
{
    /// <summary>
    /// 이 DBMS 에서 읽어 이식 값으로 낼 수 있는 형식인가. <see cref="IsSupported"/>(쓰기)와 별개다 — BINARY_*·TIMESTAMP WITH TIME ZONE·LONG·LONG RAW 는
    /// 읽기만 된다. LOCAL TIME ZONE(결정 D8)과 모르는 형식은 거짓.
    /// </summary>
    public bool IsReadable(string serverType)
        => TiberoBulkTypes.ShapeOf(serverType).Type is not (TiberoBulkType.Unknown or TiberoBulkType.TimestampLtz);

    /// <summary>
    /// 대량 읽기 값 → 이식 값. NULL 이면 null. 읽을 수 없는 형식(<see cref="IsReadable"/> 거짓)이면 <see cref="NotSupportedException"/>,
    /// 값 형식이 열과 맞지 않으면 <see cref="InvalidCastException"/>, 값을 만들 수 없으면 <see cref="PortableConversionException"/>(그 행 실패).
    /// </summary>
    public object? ToPortable(object? value, string serverType)
    {
        var type = ReadableShape(serverType).Type;
        if (value is null or DBNull)
        {
            return null;
        }

        switch (type)
        {
            case TiberoBulkType.Number:
                return value is TiberoBulkNumber n ? PortableNumber.FromText(n.Text) : throw Mismatch(value, serverType);
            case TiberoBulkType.BinaryFloat:
                return value is float f ? new PortableBinaryFloat(f) : throw Mismatch(value, serverType);
            case TiberoBulkType.BinaryDouble:
                return value is double d ? new PortableBinaryDouble(d) : throw Mismatch(value, serverType);
            case TiberoBulkType.Raw or TiberoBulkType.Blob or TiberoBulkType.LongRaw:
                return value is byte[] ? value : throw Mismatch(value, serverType);
            case TiberoBulkType.Date:
                return value is DateTime ? value : throw Mismatch(value, serverType);
            case TiberoBulkType.Timestamp:
                return value switch
                {
                    TiberoBulkTimestamp t => new PortableTimestamp(t.Seconds, t.Nanosecond),
                    DateTime dt => PortableTimestamp.FromDateTime(dt),
                    _ => throw Mismatch(value, serverType),
                };
            case TiberoBulkType.TimestampTz:
                return value is TiberoBulkTimestampTz tz ? ToPortableTz(tz) : throw Mismatch(value, serverType);
            case TiberoBulkType.IntervalYm:
                return value is TiberoBulkIntervalYm ym ? new PortableIntervalYm(ym.Months) : throw Mismatch(value, serverType);
            case TiberoBulkType.IntervalDs:
                return value is TiberoBulkIntervalDs ds ? new PortableIntervalDs(TotalNanoseconds(ds)) : throw Mismatch(value, serverType);
            default: // 문자·CLOB·NCLOB·LONG
                return value is string ? value : throw Mismatch(value, serverType);
        }
    }

    /// <summary>
    /// 이식 값 → 이 DBMS 대량 쓰기 값. <paramref name="bindType"/> 은 <c>CrossTypeMap</c> 이 정한 Tibero 쪽 바인드 형식이다(BINARY_* 는 NUMBER 로 온다, 결정 D6).
    /// 반환 값은 <see cref="ToBindText"/>·<see cref="ToBindBytes"/>(TZ 는 <see cref="CrossBindText"/>)가 받는 기존 값이다. NULL 이면 null.
    /// 이식 값 종류가 바인드 형식과 안 맞거나 값을 못 바꾸면(NaN·Inf, 모르는 시간대 지역) <see cref="PortableConversionException"/>(그 행 실패).
    /// LOCAL TIME ZONE·LONG·LONG RAW·모르는 형식은 <see cref="NotSupportedException"/>.
    /// </summary>
    public object? FromPortable(object? portable, string bindType)
    {
        var type = TiberoBulkTypes.ShapeOf(bindType).Type;
        if (type is TiberoBulkType.Unknown or TiberoBulkType.TimestampLtz or TiberoBulkType.Long or TiberoBulkType.LongRaw)
        {
            throw Unsupported(bindType);
        }

        if (portable is null or DBNull)
        {
            return null;
        }

        switch (type)
        {
            case TiberoBulkType.Number or TiberoBulkType.BinaryFloat or TiberoBulkType.BinaryDouble: // BINARY_* 바인드도 NUMBER 글자
                return portable switch
                {
                    PortableNumber n => NumberOf(n),
                    PortableBinaryFloat f => NumberOf(PortableNumber.FromSingle(f.Value)), // NaN·Inf = 행 실패
                    PortableBinaryDouble d => NumberOf(PortableNumber.FromDouble(d.Value)),
                    _ => throw PortableMismatch(portable, bindType),
                };
            case TiberoBulkType.Raw or TiberoBulkType.Blob:
                return portable is byte[] ? portable : throw PortableMismatch(portable, bindType);
            case TiberoBulkType.Date:
                // DateTime 은 연도 1 이상이라 기원전 DATE 는 여기까지 오지 못한다(원본 ToPortable 이 행 실패로 막는다).
                return portable is DateTime ? portable : throw PortableMismatch(portable, bindType);
            case TiberoBulkType.Timestamp:
                return portable is PortableTimestamp ts ? new TiberoBulkTimestamp(ts.Seconds, ts.Nanos) : throw PortableMismatch(portable, bindType);
            case TiberoBulkType.TimestampTz:
                return portable is PortableTimestampTz tz ? FromPortableTz(tz) : throw PortableMismatch(portable, bindType);
            case TiberoBulkType.IntervalYm:
                return portable is PortableIntervalYm ym ? new TiberoBulkIntervalYm(ym.Months) : throw PortableMismatch(portable, bindType);
            case TiberoBulkType.IntervalDs:
                return portable is PortableIntervalDs ds ? FromPortableDs(ds) : throw PortableMismatch(portable, bindType);
            default: // 문자·CLOB·NCLOB
                return portable is string ? portable : throw PortableMismatch(portable, bindType);
        }
    }

    /// <summary>Tibero NUMBER 가 담는 크기(0 이 아니면 1E-130 이상, 1E126 미만) 안인지 보고 값으로. 밖이면 그 행 실패(서버·드라이버가 묶음 전체를 실패시키거나 조용히 0 으로 저장하는 것을 막는다).</summary>
    private static TiberoBulkNumber NumberOf(PortableNumber number)
    {
        var text = number.Text.TrimStart('-');
        var dot = text.IndexOf('.', StringComparison.Ordinal);
        var intPart = dot < 0 ? text : text[..dot];
        var inRange = intPart != "0"
            ? intPart.Length <= MaxIntegerDigits
            : dot < 0 || text[(dot + 1)..].TakeWhile(c => c == '0').Count() < MaxLeadingFractionZeros;
        return inRange
            ? TiberoBulkNumber.Parse(number.Text)
            : throw new PortableConversionException($"Tibero NUMBER 범위(1E-130 ~ 1E126 미만) 밖의 값입니다: {(number.Text.Length <= 40 ? number.Text : number.Text[..40] + "…")}");
    }

    private const int MaxIntegerDigits = 126;
    private const int MaxLeadingFractionZeros = 130;

    /// <summary>
    /// 교차 쓰기용 <see cref="ToBindText"/>. 꺼진 형식이라 <see cref="ToBindText"/> 가 던지는 TIMESTAMP WITH TIME ZONE 을
    /// <c>yyyy-MM-dd HH:mm:ss.fffffffff ±HH:MM</c>(<see cref="TiberoBulkTimestampTz"/>, 벽시계 = UTC + 오프셋)로, BINARY_FLOAT·DOUBLE 바인드 형식을 NUMBER 글자
    /// (float·double 왕복 글자, NaN·Inf 는 <see cref="PortableConversionException"/>)로 낸다. 나머지는 <see cref="ToBindText"/> 에 맡긴다.
    /// </summary>
    internal static string? CrossBindText(object? value, string bindType)
    {
        switch (TiberoBulkTypes.ShapeOf(bindType).Type)
        {
            case TiberoBulkType.TimestampTz:
                return value switch
                {
                    null or DBNull => null,
                    TiberoBulkTimestampTz tz => BindStampTz(tz),
                    _ => throw Mismatch(value, bindType),
                };
            case TiberoBulkType.BinaryFloat or TiberoBulkType.BinaryDouble:
                return value switch
                {
                    null or DBNull => null,
                    float f => PortableNumber.FromSingle(f).Text,
                    double d => PortableNumber.FromDouble(d).Text,
                    TiberoBulkNumber n => n.Text,
                    _ => throw Mismatch(value, bindType),
                };
            default:
                return ToBindText(value, bindType);
        }
    }

    /// <summary>
    /// 교차 쓰기용 <see cref="BindExpression"/>. TIMESTAMP WITH TIME ZONE 은 형식표 후보 감싸기(<c>TO_TIMESTAMP_TZ(:C1, '… TZH:TZM')</c>), BINARY_* 는 NUMBER 처럼 감싸지 않는다.
    /// 나머지는 <see cref="BindExpression"/> 에 맡긴다.
    /// </summary>
    internal static string CrossBindExpression(string bindName, string bindType)
    {
        var shape = TiberoBulkTypes.ShapeOf(bindType);
        return shape.Type switch
        {
            TiberoBulkType.TimestampTz => shape.Wrap(bindName),
            TiberoBulkType.BinaryFloat or TiberoBulkType.BinaryDouble => ":" + bindName,
            _ => BindExpression(bindName, bindType),
        };
    }

    private static TiberoBulkShape ReadableShape(string serverType)
    {
        var shape = TiberoBulkTypes.ShapeOf(serverType);
        return shape.Type is TiberoBulkType.Unknown or TiberoBulkType.TimestampLtz
            ? throw new NotSupportedException($"교차 복사가 읽을 수 없는 형식입니다: {serverType}")
            : shape;
    }

    private static PortableTimestampTz ToPortableTz(TiberoBulkTimestampTz tz)
    {
        // 지역 이름은 서버가 오프셋만 남긴다(지역 보존 여부는 XC5 실측 전) → 오프셋만 든다.
        var local = LocalOf(tz);
        return new PortableTimestampTz(
            new PortableTimestamp(local, tz.Nanosecond),
            PortableTimestampTz.FormatOffset((int)tz.Offset.TotalMinutes),
            null);
    }

    private static DateTime LocalOf(TiberoBulkTimestampTz tz)
    {
        try
        {
            return tz.Utc + tz.Offset;
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new PortableConversionException("TIMESTAMP WITH TIME ZONE 의 벽시계 시각이 범위를 넘습니다.", ex);
        }
    }

    private static string BindStampTz(TiberoBulkTimestampTz tz)
        => LocalOf(tz).ToString(BindDateFormat, CultureInfo.InvariantCulture) + "." + Nanos(tz.Nanosecond)
            + " " + PortableTimestampTz.FormatOffset((int)tz.Offset.TotalMinutes);

    /// <summary>
    /// 이식 TZ 값 → <see cref="TiberoBulkTimestampTz"/>. 오프셋을 알면 그대로, 지역 이름만 있으면 그 벽시계 시각의 오프셋으로 바꾼다(결정 D7, 지역은 잃는다).
    /// </summary>
    private static TiberoBulkTimestampTz FromPortableTz(PortableTimestampTz tz)
    {
        var local = tz.Local.Seconds;
        TimeSpan offset;
        if (tz.OffsetMinutes is { } minutes)
        {
            offset = TimeSpan.FromMinutes(minutes);
        }
        else
        {
            offset = tz.Region is { } region
                ? RegionOffset(region, local)
                : throw new PortableConversionException("시간대 정보(오프셋 또는 지역)가 없습니다.");
        }

        try
        {
            return new TiberoBulkTimestampTz(local - offset, tz.Local.Nanos, offset);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new PortableConversionException("TIMESTAMP WITH TIME ZONE 의 UTC 시각이 범위를 넘습니다.", ex);
        }
    }

    private static TimeSpan RegionOffset(string region, DateTime local)
    {
        TimeZoneInfo zone;
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(region);
        }
        catch (TimeZoneNotFoundException ex)
        {
            throw new PortableConversionException($"알 수 없는 시간대 지역입니다: {region}", ex);
        }
        catch (InvalidTimeZoneException ex)
        {
            throw new PortableConversionException($"시간대 지역 정보가 올바르지 않습니다: {region}", ex);
        }

        if (zone.IsInvalidTime(local))
        {
            throw new PortableConversionException($"시간대 {region} 에 없는 벽시계 시각입니다(서머타임 건너뜀): {local.ToString(BindDateFormat, CultureInfo.InvariantCulture)}");
        }

        if (zone.IsAmbiguousTime(local))
        {
            throw new PortableConversionException($"시간대 {region} 에서 오프셋이 둘인 벽시계 시각입니다(서머타임 끝): {local.ToString(BindDateFormat, CultureInfo.InvariantCulture)}");
        }

        var offset = zone.GetUtcOffset(local);
        if (offset.Ticks % TimeSpan.TicksPerMinute != 0)
        {
            throw new PortableConversionException($"시간대 {region} 의 오프셋이 분 단위가 아닙니다: {offset.ToString("c", CultureInfo.InvariantCulture)}");
        }

        return offset;
    }

    /// <summary>총 나노초(부호 있음) → 일·초·나노초(셋 다 같은 부호). 일은 DAY(9) 한도(999999999)를 넘으면 행 실패.</summary>
    private static TiberoBulkIntervalDs FromPortableDs(PortableIntervalDs value)
    {
        var total = value.Nanoseconds;
        var sign = total < 0 ? -1 : 1;
        var abs = total < 0 ? -total : total;
        var days = abs / NanosPerDay;
        if (days > 999_999_999)
        {
            throw new PortableConversionException("INTERVAL DAY TO SECOND 의 일 수가 DAY(9) 한도를 넘습니다.");
        }

        var rest = (long)(abs % NanosPerDay);
        return new TiberoBulkIntervalDs(sign * (long)days, sign * (rest / NanosPerSecond), sign * (rest % NanosPerSecond));
    }

    private static PortableConversionException PortableMismatch(object portable, string bindType)
        => new($"이식 값({portable.GetType().Name})을 {bindType} 로 바꿀 수 없습니다.");
}
