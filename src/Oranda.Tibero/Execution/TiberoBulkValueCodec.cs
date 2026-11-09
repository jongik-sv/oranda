using System.Globalization;
using Oranda.Core.Execution;
using Oranda.Core.Transfer.Copy;

namespace Oranda.Tibero.Execution;

/// <summary>
/// Tibero 대량 값(<see cref="TiberoBulkNumber"/>·<see cref="TiberoBulkTimestamp"/>·.NET 기본 형식)의 해석(<see cref="IBulkValueCodec"/>, TB1 설계 2장).
/// 형식표는 <see cref="TiberoBulkTypes.ShapeOf"/> 한 곳이고, 꺼진 형식(<see cref="TiberoBulkShape.IsSupported"/> 거짓)은 지원하지 않는 형식과 같게 다룬다.
/// 키 글자는 Oracle <c>FailedRowKeys</c> 와 같은 형식이다(숫자 = 고정 소수점, DATE = <c>yyyy-MM-ddTHH:mm:ss</c>, TIMESTAMP = 소수 7자리 또는 9자리, RAW = 대문자 16진).
/// Tibero 전용 멤버 <see cref="BindExpression"/>·<see cref="ToBindText"/>·<see cref="ToBindBytes"/> 는 배열 DML 의 값 자리 글과 바인드 값(글자)을 정한다.
/// 등록하지 않는다(키 서비스 아님, Tibero 복사 하위 형식 <c>TiberoCopyRunner</c>·<c>TiberoCopyPrecheckService</c>(TI1b)가 넘긴다).
/// </summary>
internal sealed class TiberoBulkValueCodec : IBulkValueCodec
{
    public static readonly TiberoBulkValueCodec Instance = new();

    private const string KeyDateFormat = "yyyy-MM-dd'T'HH:mm:ss";
    private const string BindDateFormat = "yyyy-MM-dd HH:mm:ss";
    private const long NanosPerTick = 100;
    private const long NanosPerSecond = 1_000_000_000;
    private const long NanosPerDay = 86_400 * NanosPerSecond;

    public bool IsSupported(string serverType) => TiberoBulkTypes.ShapeOf(serverType).IsSupported;

    public BulkKind KindOf(string serverType)
    {
        var shape = Supported(serverType);
        return shape.Kind ?? throw Unsupported(serverType);
    }

    public BindDataType BindTypeOf(string serverType) => Keyed(serverType).KeyBind!.Value;

    public string? ToKeyText(object? value, string serverType)
    {
        if (value is null or DBNull)
        {
            return null; // Oracle FailedRowKeys 처럼 형식보다 NULL 을 먼저 본다
        }

        return Keyed(serverType).Type switch
        {
            TiberoBulkType.Number => NumberOf(value, serverType).Text,
            TiberoBulkType.Date => value is DateTime d
                ? d.ToString(KeyDateFormat, CultureInfo.InvariantCulture)
                : throw Unexpected(value, serverType),
            TiberoBulkType.Timestamp => KeyStamp(TimestampOf(value, serverType)),
            TiberoBulkType.Raw => value is byte[] b ? Convert.ToHexString(b) : throw Unexpected(value, serverType),
            _ => value as string ?? throw Unexpected(value, serverType), // 문자(VARCHAR2·CHAR·NVARCHAR2·NCHAR)
        };
    }

    public object? FromKeyText(string? text, string serverType)
    {
        if (text is null)
        {
            return null;
        }

        return Keyed(serverType).Type switch
        {
            TiberoBulkType.Number => TiberoBulkNumber.Parse(text),
            TiberoBulkType.Date => DateTime.ParseExact(text, KeyDateFormat, CultureInfo.InvariantCulture),
            TiberoBulkType.Timestamp => ParseKeyStamp(text),
            TiberoBulkType.Raw => Convert.FromHexString(text),
            _ => text,
        };
    }

    /// <summary>숫자 결과 → long. NULL 이면 null, long 범위를 넘으면 끝값, 소수는 버린다. 글자는 invariant 로 읽는다(V$ 값 등).</summary>
    public long? ToInt64(object? value)
    {
        switch (value)
        {
            case null or DBNull:
                return null;
            case TiberoBulkNumber n:
                return Clamp(n.IntegerText);
            case string s when decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed):
                return Clamp(Math.Truncate(parsed).ToString(CultureInfo.InvariantCulture));
            case string s when TryFixedPoint(s) is { } big:
                return Clamp(big.IntegerText); // decimal 범위 밖 고정 소수점 글자도 끝값으로(계약)
            case double or float:
                var d = Math.Truncate(Convert.ToDouble(value, CultureInfo.InvariantCulture)); // 반올림 아님, 버림(계약)
                return double.IsNaN(d) ? null : d >= long.MaxValue ? long.MaxValue : d <= long.MinValue ? long.MinValue : (long)d;
            case IConvertible c:
                return Convert.ToInt64(c, CultureInfo.InvariantCulture);
            default:
                return null;
        }
    }

    private static TiberoBulkNumber? TryFixedPoint(string text)
    {
        try
        {
            return TiberoBulkNumber.Parse(text.Trim());
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public string? ToText(object? value) => value switch
    {
        null or DBNull => null,
        string s => s,
        TiberoBulkNumber n => n.Text,
        TiberoBulkTimestamp t => t.Seconds.ToString(BindDateFormat, CultureInfo.InvariantCulture) + "." + Nanos(t.Nanosecond),
        TiberoBulkIntervalYm ym => IntervalYmText(ym.Months),
        TiberoBulkIntervalDs ds => IntervalDsText(TotalNanoseconds(ds)),
        _ =>Convert.ToString(value, CultureInfo.InvariantCulture),
    };

    /// <summary>NUMBER 값(<see cref="TiberoBulkNumber"/>)만 글자. 문자 결과(MAX 가 글자인 열 등)는 숫자로 보지 않는다.</summary>
    public string? NumberText(object? value) => value is TiberoBulkNumber n ? n.Text : null;

    public bool IsZero(object? value) => value is TiberoBulkNumber { Text: "0" };

    public long ToInt64Exact(object? value) => value is TiberoBulkNumber n
        ? long.Parse(n.IntegerText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture) // 범위 밖 = OverflowException
        : throw new InvalidCastException("NUMBER 값이 아닙니다.");

    /// <summary>
    /// 배열 DML 값 자리 글(TB1 설계 2.1): DATE = <c>TO_DATE(:C1, 'YYYY-MM-DD HH24:MI:SS')</c>, TIMESTAMP = <c>TO_TIMESTAMP(:C1, '… .FF9')</c>,
    /// RAW = <c>HEXTORAW(:C1)</c>, INTERVAL YEAR TO MONTH = <c>TO_YMINTERVAL(:C1)</c>, INTERVAL DAY TO SECOND = <c>TO_DSINTERVAL(:C1)</c>, 그 밖 = <c>:C1</c>. <paramref name="bindName"/> 은 쌍점 없는 이름. Tibero 복사 방언의 <c>BindPlaceholder</c> 와
    /// 서비스의 감싸기 확인이 이것 하나를 쓴다. 지원하지 않는(꺼진) 형식이면 <see cref="NotSupportedException"/>.
    /// </summary>
    public static string BindExpression(string bindName, string serverType) => Supported(serverType).Wrap(bindName);

    /// <summary>
    /// 대량 값 → 바인드 글자(<c>string?[]</c> 배열 원소·스칼라 값, TB1 설계 2.1). NULL 이면 null.
    /// NUMBER = 고정 소수점(<see cref="TiberoBulkNumber"/>·decimal·정수), 문자·CLOB = string 그대로, DATE = <c>yyyy-MM-dd HH:mm:ss</c>(DateTime),
    /// TIMESTAMP = <c>yyyy-MM-dd HH:mm:ss.</c> + 나노초 9자리(<see cref="TiberoBulkTimestamp"/>·DateTime), RAW = 대문자 16진(byte[]),
    /// INTERVAL YEAR TO MONTH = <c>+Y-M</c>(<see cref="TiberoBulkIntervalYm"/>), INTERVAL DAY TO SECOND = <c>+D HH:MI:SS.FFFFFFFFF</c>
    /// (<see cref="TiberoBulkIntervalDs"/>·TimeSpan; 부호는 전체에 하나). INTERVAL 은 복사 키로 쓰지 않아 <c>BindTypeOf</c>·<c>ToKeyText</c>·<c>FromKeyText</c> 는 NotSupported.
    /// 값 형식이 열과 맞지 않으면 <see cref="InvalidCastException"/>, BLOB 이면 <see cref="ToBindBytes"/> 를 쓰라는 <see cref="NotSupportedException"/>,
    /// 지원하지 않는 형식이면 <see cref="NotSupportedException"/>.
    /// </summary>
    public static string? ToBindText(object? value, string serverType)
    {
        var shape = Supported(serverType);
        if (shape.Type == TiberoBulkType.Blob)
        {
            throw new NotSupportedException($"이진 LOB 은 글자로 바인드하지 않습니다(ToBindBytes): {serverType}");
        }

        if (value is null or DBNull)
        {
            return null;
        }

        return shape.Type switch
        {
            TiberoBulkType.Number => BindNumberOf(value, serverType),
            TiberoBulkType.Date => value is DateTime d
                ? d.ToString(BindDateFormat, CultureInfo.InvariantCulture)
                : throw Mismatch(value, serverType),
            TiberoBulkType.Timestamp => value switch
            {
                TiberoBulkTimestamp t => BindStamp(t),
                DateTime dt => BindStamp(TiberoBulkTimestamp.FromDateTime(dt)),
                _ => throw Mismatch(value, serverType),
            },
            TiberoBulkType.Raw => value is byte[] b ? Convert.ToHexString(b) : throw Mismatch(value, serverType),
            TiberoBulkType.IntervalYm => value is TiberoBulkIntervalYm ym ? IntervalYmText(ym.Months) : throw Mismatch(value, serverType),
            TiberoBulkType.IntervalDs => value switch
            {
                TiberoBulkIntervalDs ds => IntervalDsText(TotalNanoseconds(ds)),
                TimeSpan ts => IntervalDsText((Int128)ts.Ticks * NanosPerTick), // 100ns 단위(틱)
                _ => throw Mismatch(value, serverType),
            },
            _ => value as string ?? throw Mismatch(value, serverType), // 문자·CLOB·NCLOB
        };
    }

    /// <summary>BLOB 값 → 스칼라 바인드 byte[](그대로). NULL 이면 null. 빈 byte[] 는 서버가 NULL 로 넣는다(실측 3.1). BLOB 이 아니거나 값이 byte[] 가 아니면 예외.</summary>
    public static byte[]? ToBindBytes(object? value, string serverType)
    {
        var shape = Supported(serverType);
        if (shape.Type != TiberoBulkType.Blob)
        {
            throw new NotSupportedException($"이진 바인드는 BLOB 만 씁니다(ToBindText): {serverType}");
        }

        return value switch
        {
            null or DBNull => null,
            byte[] b => b,
            _ => throw Mismatch(value, serverType),
        };
    }

    private static TiberoBulkShape Supported(string serverType)
    {
        var shape = TiberoBulkTypes.ShapeOf(serverType);
        return shape.IsSupported ? shape : throw Unsupported(serverType);
    }

    private static TiberoBulkShape Keyed(string serverType)
    {
        var shape = TiberoBulkTypes.ShapeOf(serverType);
        return shape is { IsSupported: true, KeyBind: not null }
            ? shape
            : throw new NotSupportedException($"키 열로 다시 읽을 수 없는 형식입니다: {serverType}");
    }

    private static TiberoBulkNumber NumberOf(object value, string serverType) => value switch
    {
        TiberoBulkNumber n => n,
        decimal m => TiberoBulkNumber.FromDecimal(m),
        long or int or short or sbyte or byte or ushort or uint => TiberoBulkNumber.FromInt64(Convert.ToInt64(value, CultureInfo.InvariantCulture)),
        ulong u => TiberoBulkNumber.Parse(u.ToString(CultureInfo.InvariantCulture)),
        _ => throw Unexpected(value, serverType),
    };

    private static string BindNumberOf(object value, string serverType)
    {
        try
        {
            return NumberOf(value, serverType).Text;
        }
        catch (InvalidOperationException)
        {
            throw Mismatch(value, serverType);
        }
    }

    private static TiberoBulkTimestamp TimestampOf(object value, string serverType) => value switch
    {
        TiberoBulkTimestamp t => t,
        DateTime dt => TiberoBulkTimestamp.FromDateTime(dt),
        _ => throw Unexpected(value, serverType),
    };

    /// <summary>
    /// TIMESTAMP 키 글자. 소수 7자리(100ns)로 쓰고 그보다 작은 자리가 있으면(TIMESTAMP(8·9)) 9자리 — 7자리로 자르면 다시 읽을 때 키가 맞지 않는다
    /// (Oracle <c>FailedRowKeys</c> 와 같은 규칙).
    /// </summary>
    private static string KeyStamp(TiberoBulkTimestamp t)
    {
        var head = t.Seconds.ToString(KeyDateFormat, CultureInfo.InvariantCulture);
        return t.Nanosecond % 100 == 0
            ? head + "." + (t.Nanosecond / 100).ToString("D7", CultureInfo.InvariantCulture)
            : head + "." + Nanos(t.Nanosecond);
    }

    /// <summary><see cref="KeyStamp"/> 의 반대. 소수 없음·7자리·9자리를 받는다.</summary>
    private static TiberoBulkTimestamp ParseKeyStamp(string text)
    {
        var dot = text.IndexOf('.', StringComparison.Ordinal);
        if (dot < 0)
        {
            return new(DateTime.ParseExact(text, KeyDateFormat, CultureInfo.InvariantCulture), 0);
        }

        var seconds = DateTime.ParseExact(text[..dot], KeyDateFormat, CultureInfo.InvariantCulture);
        var fraction = text[(dot + 1)..];
        if (fraction.Length is not (7 or 9) || !fraction.All(char.IsAsciiDigit))
        {
            throw new FormatException($"TIMESTAMP 키 글자의 소수 자리가 올바르지 않습니다: {text}");
        }

        var value = int.Parse(fraction, NumberStyles.None, CultureInfo.InvariantCulture);
        return new(seconds, fraction.Length == 7 ? value * 100 : value);
    }

    /// <summary>TO_TIMESTAMP 서식 <see cref="TiberoBulkTypes.TimestampFormat"/>(FF9) 과 짝인 글자.</summary>
    private static string BindStamp(TiberoBulkTimestamp t)
        => t.Seconds.ToString(BindDateFormat, CultureInfo.InvariantCulture) + "." + Nanos(t.Nanosecond);

    private static string Nanos(int nanosecond) => nanosecond.ToString("D9", CultureInfo.InvariantCulture);

    /// <summary>
    /// INTERVAL YEAR TO MONTH 글자 <c>+Y-M</c>(TO_YMINTERVAL 이 받는 모양, 달은 0~11). 총 개월의 부호가 앞에 붙는다: 14 → <c>+1-2</c>, −131 → <c>-10-11</c>, 0 → <c>+0-0</c>.
    /// </summary>
    private static string IntervalYmText(long months)
    {
        var years = Math.Abs(months / 12); // 나눈 뒤라 long.MinValue 도 넘치지 않는다
        var rest = Math.Abs(months % 12);
        return (months < 0 ? "-" : "+")
            + years.ToString(CultureInfo.InvariantCulture) + "-" + rest.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>레코드(일·초·나노초) → 총 나노초(부호 있음). 세 값의 부호가 섞여 있어도 합으로 정규화하고, Int128 이라 넘치지 않는다.</summary>
    private static Int128 TotalNanoseconds(TiberoBulkIntervalDs value)
        => ((Int128)value.Days * NanosPerDay) + ((Int128)value.Seconds * NanosPerSecond) + value.Nanoseconds;

    /// <summary>
    /// INTERVAL DAY TO SECOND 글자 <c>+D HH:MI:SS.FFFFFFFFF</c>(TO_DSINTERVAL 이 받는 모양, 나노초 9자리). 부호는 전체에 하나:
    /// −1일 −1초 −1ns → <c>-1 00:00:01.000000001</c>.
    /// </summary>
    private static string IntervalDsText(Int128 totalNanoseconds)
    {
        var negative = totalNanoseconds < 0;
        var abs = negative ? -totalNanoseconds : totalNanoseconds;
        var days = abs / NanosPerDay;
        var rest = (long)(abs % NanosPerDay);
        var seconds = rest / NanosPerSecond;
        var nanos = (int)(rest % NanosPerSecond);
        return (negative ? "-" : "+")
            + days.ToString(CultureInfo.InvariantCulture)
            + " " + (seconds / 3600).ToString("D2", CultureInfo.InvariantCulture)
            + ":" + (seconds / 60 % 60).ToString("D2", CultureInfo.InvariantCulture)
            + ":" + (seconds % 60).ToString("D2", CultureInfo.InvariantCulture)
            + "." + Nanos(nanos);
    }

    /// <summary>정수 글자(부호 포함) → long, 범위 밖이면 부호대로 끝값.</summary>
    private static long Clamp(string integer)
    {
        if (long.TryParse(integer, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v))
        {
            return v;
        }

        return integer.StartsWith('-') ? long.MinValue : long.MaxValue;
    }

    private static NotSupportedException Unsupported(string serverType) => new($"데이터 복사가 지원하지 않는 형식입니다: {serverType}");

    // 키 글자: Oracle FailedRowKeys.Unexpected 와 같은 종류(InvalidOperationException).
    private static InvalidOperationException Unexpected(object value, string serverType) =>
        new($"키 열({serverType}) 값의 형식이 예상과 다릅니다: {value.GetType().Name}");

    private static InvalidCastException Mismatch(object value, string serverType) =>
        new($"열({serverType})에 바인드할 수 없는 값 형식입니다: {value.GetType().Name}");
}
