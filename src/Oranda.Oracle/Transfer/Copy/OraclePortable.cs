using Oracle.ManagedDataAccess.Client;
using Oracle.ManagedDataAccess.Types;
using Oranda.Core.Transfer.Copy.CrossDbms;
using Oranda.Oracle.Execution;

namespace Oranda.Oracle.Transfer.Copy;

/// <summary>
/// Oracle 대량 값(ODP.NET 형식) ↔ 중립 이식 값(<see cref="PortableNumber"/> 등)의 변환 본문(<see cref="OracleBulkValueCodec"/> 이 위임한다).
/// 읽기 쪽 값 종류는 <c>OracleBulkExecutionService.ReadRow</c> 가 내는 것(NUMBER = OracleDecimal, BINARY_FLOAT = float, BINARY_DOUBLE = double,
/// DATE = OracleDate, TIMESTAMP = OracleTimeStamp, WITH TIME ZONE = OracleTimeStampTZ, INTERVAL = OracleIntervalYM·DS, 문자·CLOB = string, RAW·BLOB = byte[])이고,
/// 쓰기 쪽 값 종류는 <c>ExecuteArrayAsync</c> 가 <see cref="BulkBinds.ToDbType"/> 형식으로 배열 바인드하는 값이다. 서버 형식 이름은 <see cref="BulkBinds.ToDbType"/> 과 같은 규칙으로 읽는다.
/// 값 하나를 못 바꾸면 <see cref="PortableConversionException"/>(그 행 실패, 결정 D5).
/// </summary>
internal static class OraclePortable
{
    /// <summary>Oracle NUMBER 유효숫자 한도.</summary>
    private const int MaxSignificantDigits = 38;

    /// <summary>Oracle NUMBER 가 받는 10 의 거듭제곱 범위(최고 자리 기준).</summary>
    private const int MinExponent = -130;

    private const int MaxExponent = 125;

    private const long NanosPerSecond = 1_000_000_000L;

    private const long SecondsPerDay = 86_400L;

    /// <summary>INTERVAL 의 일·연 정밀도 최대(9자리).</summary>
    private const long MaxIntervalMagnitude = 999_999_999L;

    /// <summary>ODP.NET 값(<paramref name="serverType"/> 열) → 이식 값. NULL 은 null.</summary>
    public static object? ToPortable(object? value, string serverType)
    {
        var dbType = BulkBinds.ToDbType(serverType);
        if (dbType == OracleDbType.TimeStampLTZ)
        {
            // 결정 D8: 교차 복사에서 제외한 형식.
            throw new NotSupportedException($"데이터 복사가 지원하지 않는 형식입니다: {serverType}");
        }

        if (value is null or DBNull)
        {
            return null;
        }

        switch (dbType)
        {
            case OracleDbType.Decimal:
                return value switch
                {
                    OracleDecimal { IsNull: true } => null,
                    OracleDecimal d => PortableNumber.FromText(d.ToString().Replace(',', '.')),
                    _ => throw Unexpected(value, serverType),
                };
            case OracleDbType.BinaryFloat:
                return value is float f ? new PortableBinaryFloat(f) : throw Unexpected(value, serverType);
            case OracleDbType.BinaryDouble:
                return value is double dbl ? new PortableBinaryDouble(dbl) : throw Unexpected(value, serverType);
            case OracleDbType.Varchar2:
            case OracleDbType.NVarchar2:
            case OracleDbType.Char:
            case OracleDbType.NChar:
            case OracleDbType.Clob:
            case OracleDbType.NClob:
                return value is string s ? s : throw Unexpected(value, serverType);
            case OracleDbType.Raw:
            case OracleDbType.Blob:
                return value is byte[] bytes ? bytes : throw Unexpected(value, serverType);
            case OracleDbType.Date:
                return value switch
                {
                    OracleDate { IsNull: true } => null,
                    OracleDate d => ToSeconds(d, serverType),
                    DateTime dt => TruncateToSeconds(dt),
                    _ => throw Unexpected(value, serverType),
                };
            case OracleDbType.TimeStamp:
                return value switch
                {
                    OracleTimeStamp { IsNull: true } => null,
                    OracleTimeStamp t => ToTimestamp(t.Year, t.Month, t.Day, t.Hour, t.Minute, t.Second, t.Nanosecond),
                    DateTime dt => PortableTimestamp.FromDateTime(dt),
                    _ => throw Unexpected(value, serverType),
                };
            case OracleDbType.TimeStampTZ:
                return value switch
                {
                    OracleTimeStampTZ { IsNull: true } => null,
                    OracleTimeStampTZ t => ToTimestampTz(t),
                    DateTimeOffset o => new PortableTimestampTz(
                        PortableTimestamp.FromDateTime(o.DateTime), PortableTimestampTz.FormatOffset((int)o.Offset.TotalMinutes), null),
                    _ => throw Unexpected(value, serverType),
                };
            case OracleDbType.IntervalYM:
                return value switch
                {
                    OracleIntervalYM { IsNull: true } => null,
                    OracleIntervalYM y => new PortableIntervalYm(((long)y.Years * 12) + y.Months),
                    _ => throw Unexpected(value, serverType),
                };
            case OracleDbType.IntervalDS:
                return value switch
                {
                    OracleIntervalDS { IsNull: true } => null,
                    OracleIntervalDS i => ToIntervalDs(i),
                    _ => throw Unexpected(value, serverType),
                };
            default:
                throw new NotSupportedException($"데이터 복사가 지원하지 않는 형식입니다: {serverType}");
        }
    }

    /// <summary>
    /// 이식 값 → <paramref name="bindType"/> 으로 배열 바인드할 ODP.NET/.NET 값. NUMBER = OracleDecimal, BINARY_FLOAT = float, BINARY_DOUBLE = double,
    /// DATE = DateTime(초), TIMESTAMP = OracleTimeStamp, WITH TIME ZONE = OracleTimeStampTZ, INTERVAL = OracleIntervalYM·DS, 문자·LOB = string, RAW·BLOB = byte[].
    /// null 은 null.
    /// </summary>
    public static object? FromPortable(object? portable, string bindType)
    {
        var dbType = BulkBinds.ToDbType(bindType);
        if (dbType == OracleDbType.TimeStampLTZ)
        {
            throw new NotSupportedException($"데이터 복사가 지원하지 않는 형식입니다: {bindType}");
        }

        if (portable is null or DBNull)
        {
            return null;
        }

        switch (dbType)
        {
            case OracleDbType.Decimal:
                return portable switch
                {
                    PortableNumber n => ToOracleDecimal(n),
                    PortableBinaryFloat bf => ToOracleDecimal(PortableNumber.FromSingle(bf.Value)),
                    PortableBinaryDouble bd => ToOracleDecimal(PortableNumber.FromDouble(bd.Value)),
                    _ => throw Mismatch(portable, bindType),
                };
            case OracleDbType.BinaryFloat:
                return portable switch
                {
                    PortableBinaryFloat bf => bf.Value,
                    PortableBinaryDouble bd => (float)bd.Value,
                    _ => throw Mismatch(portable, bindType),
                };
            case OracleDbType.BinaryDouble:
                return portable switch
                {
                    PortableBinaryDouble bd => bd.Value,
                    PortableBinaryFloat bf => (double)bf.Value,
                    _ => throw Mismatch(portable, bindType),
                };
            case OracleDbType.Varchar2:
            case OracleDbType.NVarchar2:
            case OracleDbType.Char:
            case OracleDbType.NChar:
            case OracleDbType.Clob:
            case OracleDbType.NClob:
                return portable is string s ? s : throw Mismatch(portable, bindType);
            case OracleDbType.Raw:
            case OracleDbType.Blob:
                return portable is byte[] bytes ? bytes : throw Mismatch(portable, bindType);
            case OracleDbType.Date:
                return portable is DateTime dt ? TruncateToSeconds(dt) : throw Mismatch(portable, bindType);
            case OracleDbType.TimeStamp:
                return portable is PortableTimestamp ts
                    ? new OracleTimeStamp(ts.Seconds.Year, ts.Seconds.Month, ts.Seconds.Day, ts.Seconds.Hour, ts.Seconds.Minute, ts.Seconds.Second, ts.Nanos)
                    : throw Mismatch(portable, bindType);
            case OracleDbType.TimeStampTZ:
                return portable is PortableTimestampTz tz ? ToOracleTimeStampTz(tz) : throw Mismatch(portable, bindType);
            case OracleDbType.IntervalYM:
                return portable is PortableIntervalYm ym ? ToOracleIntervalYm(ym) : throw Mismatch(portable, bindType);
            case OracleDbType.IntervalDS:
                return portable is PortableIntervalDs ds ? ToOracleIntervalDs(ds) : throw Mismatch(portable, bindType);
            default:
                throw new NotSupportedException($"데이터 복사가 지원하지 않는 형식입니다: {bindType}");
        }
    }

    private static DateTime ToSeconds(OracleDate value, string serverType)
    {
        try
        {
            return TruncateToSeconds(value.Value);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            // 기원전 등 DateTime 범위 밖.
            throw new PortableConversionException($"DATE 값이 .NET 날짜 범위를 벗어납니다({serverType}, 기원전 포함).", ex);
        }
    }

    private static DateTime TruncateToSeconds(DateTime value) =>
        DateTime.SpecifyKind(new DateTime(value.Ticks - (value.Ticks % TimeSpan.TicksPerSecond)), DateTimeKind.Unspecified);

    private static PortableTimestamp ToTimestamp(int year, int month, int day, int hour, int minute, int second, int nanosecond)
    {
        try
        {
            return new PortableTimestamp(new DateTime(year, month, day, hour, minute, second), nanosecond);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new PortableConversionException($"TIMESTAMP 값이 .NET 날짜 범위를 벗어납니다(기원전 포함): {year}", ex);
        }
    }

    /// <summary>
    /// OracleTimeStampTZ 의 TimeZone 은 오프셋(<c>+09:00</c>)이면 오프셋만, 지역 이름(<c>Asia/Seoul</c>)이면 지역을 담고(그 시각의 오프셋을 알면 함께),
    /// 시각 필드는 그 시간대의 벽시계 시각이다.
    /// </summary>
    private static PortableTimestampTz ToTimestampTz(OracleTimeStampTZ t)
    {
        var local = ToTimestamp(t.Year, t.Month, t.Day, t.Hour, t.Minute, t.Second, t.Nanosecond);
        var zone = t.TimeZone;
        if (PortableTimestampTz.TryParseOffsetMinutes(zone, out var minutes))
        {
            return new PortableTimestampTz(local, PortableTimestampTz.FormatOffset(minutes), null);
        }

        string? offset = null;
        try
        {
            var span = t.GetTimeZoneOffset();
            if (span.Ticks % TimeSpan.TicksPerMinute == 0)
            {
                offset = PortableTimestampTz.FormatOffset((int)span.TotalMinutes);
            }
        }
        catch (Exception ex) when (ex is InvalidTimeZoneException or ArgumentException or InvalidOperationException or OverflowException)
        {
            // 오프셋을 못 구하면 지역만 싣는다(대상이 지역을 못 받으면 그 값 하나가 실패).
        }

        return new PortableTimestampTz(local, offset, zone);
    }

    private static PortableIntervalDs ToIntervalDs(OracleIntervalDS i)
    {
        // 구성 요소는 모두 같은 부호(음수 간격이면 전부 음수 또는 0)다.
        Int128 seconds = (((Int128)i.Days * 24) + i.Hours) * 60;
        seconds = ((seconds + i.Minutes) * 60) + i.Seconds;
        return new PortableIntervalDs((seconds * NanosPerSecond) + i.Nanoseconds);
    }

    /// <summary>
    /// 고정소수점 글자 → OracleDecimal. 유효숫자(끝 0 을 뗀 자릿수)가 38 을 넘거나 지수가 Oracle 범위(-130..125) 밖이면 <see cref="PortableConversionException"/>:
    /// ODP.NET 은 범위 밖 값을 조용히 0 으로 만들거나 예외 없이 깨진 값을 낸다. 숫자만 남긴 정수를 읽고 소수점을 옮겨 문화권 구분자에 기대지 않는다.
    /// </summary>
    private static OracleDecimal ToOracleDecimal(PortableNumber number)
    {
        var text = number.Text;
        var negative = text.StartsWith('-');
        var body = negative ? text[1..] : text;
        var dot = body.IndexOf('.', StringComparison.Ordinal);
        var fractionLength = dot < 0 ? 0 : body.Length - dot - 1;
        var digits = (dot < 0 ? body : body.Remove(dot, 1)).TrimStart('0');
        if (digits.Length == 0)
        {
            return OracleDecimal.Zero;
        }

        var mantissa = digits.TrimEnd('0');
        var shift = digits.Length - mantissa.Length - fractionLength;
        var leadingExponent = mantissa.Length - 1 + shift;
        if (mantissa.Length > MaxSignificantDigits)
        {
            throw new PortableConversionException($"NUMBER 로 옮길 수 없는 값입니다(유효숫자 {MaxSignificantDigits}자리 초과): {Shorten(text)}");
        }

        if (leadingExponent is < MinExponent or > MaxExponent)
        {
            throw new PortableConversionException($"NUMBER 로 옮길 수 없는 값입니다(지수 범위 밖): {Shorten(text)}");
        }

        try
        {
            var value = OracleDecimal.Parse(mantissa);
            if (shift != 0)
            {
                value = OracleDecimal.Shift(value, shift);
            }

            return negative ? OracleDecimal.Negate(value) : value;
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException or IndexOutOfRangeException)
        {
            throw new PortableConversionException($"NUMBER 로 옮길 수 없는 값입니다: {Shorten(text)}", ex);
        }
    }

    /// <summary>지역이 있으면 지역으로, 없으면 오프셋으로 만든다. Oracle 이 모르는 지역은 생성 시점에 던지므로 그 값 하나의 실패로 바꾼다.</summary>
    private static OracleTimeStampTZ ToOracleTimeStampTz(PortableTimestampTz value)
    {
        var zone = value.Region ?? value.Offset;
        var seconds = value.Local.Seconds;
        try
        {
            var made = new OracleTimeStampTZ(seconds.Year, seconds.Month, seconds.Day, seconds.Hour, seconds.Minute, seconds.Second, value.Local.Nanos, zone);
            if (value.Region is not null && value.OffsetMinutes is { } offsetMinutes && made.GetTimeZoneOffset() != TimeSpan.FromMinutes(offsetMinutes))
            {
                // 서머타임이 끝나는 날의 겹치는 시각: 지역만으로는 표준시 쪽으로 굳어 1시간 어긋난다 — 원본이 알려 준 오프셋을 따른다(지역은 잃음).
                zone = value.Offset;
                made = new OracleTimeStampTZ(seconds.Year, seconds.Month, seconds.Day, seconds.Hour, seconds.Minute, seconds.Second, value.Local.Nanos, zone);
            }

            return made;
        }
        catch (Exception ex) when (ex is InvalidTimeZoneException or ArgumentException or FormatException or OverflowException or InvalidOperationException)
        {
            throw new PortableConversionException($"시간대를 Oracle 값으로 만들 수 없습니다: {zone}", ex);
        }
    }

    private static OracleIntervalYM ToOracleIntervalYm(PortableIntervalYm value)
    {
        var years = value.Months / 12;
        if (Math.Abs(years) > MaxIntervalMagnitude)
        {
            throw new PortableConversionException($"INTERVAL YEAR TO MONTH 값이 범위를 넘습니다: {value.Months}개월");
        }

        return new OracleIntervalYM((int)years, (int)(value.Months % 12));
    }

    private static OracleIntervalDS ToOracleIntervalDs(PortableIntervalDs value)
    {
        var negative = value.Nanoseconds < 0;
        var abs = negative ? -value.Nanoseconds : value.Nanoseconds;
        var nanos = (int)(abs % NanosPerSecond);
        var totalSeconds = abs / NanosPerSecond;
        var days = totalSeconds / SecondsPerDay;
        if (days > MaxIntervalMagnitude)
        {
            throw new PortableConversionException($"INTERVAL DAY TO SECOND 값이 범위를 넘습니다: {days}일");
        }

        var rest = (int)(totalSeconds % SecondsPerDay);
        var sign = negative ? -1 : 1;
        return new OracleIntervalDS(
            sign * (int)days, sign * (rest / 3600), sign * (rest % 3600 / 60), sign * (rest % 60), sign * nanos);
    }

    private static string Shorten(string text) => text.Length <= 40 ? text : text[..40] + "…";

    private static InvalidCastException Unexpected(object value, string serverType) =>
        new($"{serverType} 열 값의 형식이 예상과 다릅니다: {value.GetType().Name}");

    private static PortableConversionException Mismatch(object portable, string bindType) =>
        new($"{bindType} 로 옮길 수 없는 이식 값 종류입니다: {portable.GetType().Name}");
}
