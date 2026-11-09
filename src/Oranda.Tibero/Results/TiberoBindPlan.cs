using System.Data;
using Oranda.Core.Execution;

namespace Oranda.Tibero.Results;

/// <summary>드라이버 값 객체로 감싸야 하는지(정밀도·시간대를 잃지 않으려고).</summary>
internal enum TiberoBindWrapper
{
    /// <summary>값을 그대로 <c>TiberoParameter.Value</c> 에 넣는다.</summary>
    None,

    /// <summary><c>new TiberoTimeStamp(…, 나노초)</c> — <see cref="DateTime"/> 을 그대로 주면 밀리초까지만 전달된다.</summary>
    TimeStamp,

    /// <summary><c>new TiberoTimeStampTZ(DateTime, "±hh:mm")</c> — <see cref="DateTime"/> 을 그대로 주면 ODP-5012.</summary>
    TimeStampTz,
}

/// <summary>
/// <see cref="BindValue"/> 1개를 드라이버 매개변수로 옮기는 방법(드라이버 형식 없이 정수·열거로 표현). 규칙의 근거 = docs/design/tibero-driver-values.md 6장.
/// 드라이버 쪽 <c>TiberoBindBuilder</c> 가 이 계획대로 <c>TiberoParameter</c> 를 만든다.
/// </summary>
/// <param name="ProviderType"><c>TiberoDbType</c> 정수(<see cref="TiberoColumnTypes"/> 의 Pt 상수)</param>
/// <param name="Direction">매개변수 방향</param>
/// <param name="Value">매개변수 값(<see cref="DBNull"/> 가능). 감싸기 전 값</param>
/// <param name="Size">문자열·RAW 크기. null = 지정하지 않음</param>
/// <param name="Wrapper">값을 감쌀 드라이버 객체</param>
/// <param name="TzOffset"><see cref="TiberoBindWrapper.TimeStampTz"/> 의 오프셋 글자(<c>+09:00</c>)</param>
/// <param name="OutAsText">이 형식은 OUT 으로 받을 수 없어 <c>VARCHAR2</c> 로 받는다(서버가 글자로 바꿔 준다)</param>
internal sealed record TiberoBindPlan(int ProviderType, ParameterDirection Direction, object Value, int? Size,
                                      TiberoBindWrapper Wrapper = TiberoBindWrapper.None, string? TzOffset = null, bool OutAsText = false)
{
    /// <summary>OUT 문자열·RAW 의 기본 크기(PSM 최대).</summary>
    public const int DefaultOutSize = 32767;

    /// <summary>OUT 을 글자로 받을 때의 크기(날짜·간격·숫자의 글자 표현은 모두 이 안에 든다).</summary>
    public const int TextOutSize = 200;

    public static TiberoBindPlan For(BindValue bind)
    {
        ArgumentNullException.ThrowIfNull(bind);
        var direction = bind.Direction switch
        {
            BindDirection.Out => ParameterDirection.Output,
            BindDirection.InOut => ParameterDirection.InputOutput,
            BindDirection.ReturnValue => ParameterDirection.ReturnValue,
            _ => ParameterDirection.Input,
        };

        var isIn = bind.Direction == BindDirection.In;
        var type = ToProviderType(bind.Type);
        object value = bind.Direction == BindDirection.Out || bind.Value is null ? DBNull.Value : bind.Value;

        // OUT 으로 쓸 수 없는 형식은 글자로 받는다. 값은 서버가 형식을 바꿔 돌려준다.
        if (!isIn && !IsOutCapable(type))
        {
            if (bind.Direction != BindDirection.Out && value is not DBNull)
            {
                value = ToText(value);
            }

            return new TiberoBindPlan(TiberoColumnTypes.PtVarchar2, direction, value, Math.Max(bind.Size ?? 0, TextOutSize), OutAsText: true);
        }

        var wrapper = TiberoBindWrapper.None;
        string? tz = null;
        if (value is not DBNull)
        {
            switch (bind.Type)
            {
                case BindDataType.Boolean:
                    type = TiberoColumnTypes.PtDecimal;
                    value = value is bool b ? (b ? 1m : 0m) : value;
                    break;
                case BindDataType.Timestamp when value is DateTime:
                    wrapper = TiberoBindWrapper.TimeStamp;
                    break;
                case BindDataType.TimestampTz:
                    var dto = value switch
                    {
                        DateTimeOffset o => o,
                        DateTime dt => new DateTimeOffset(dt),
                        _ => default(DateTimeOffset?),
                    };
                    if (dto is { } d)
                    {
                        value = d;
                        wrapper = TiberoBindWrapper.TimeStampTz;
                        tz = TiberoWire.FormatOffset(d.Offset);
                    }

                    break;
            }
        }
        else if (bind.Type == BindDataType.Boolean)
        {
            type = TiberoColumnTypes.PtDecimal;
        }

        int? size = bind.Size;
        if (size is null && !isIn && type is TiberoColumnTypes.PtVarchar2 or TiberoColumnTypes.PtNVarchar2 or TiberoColumnTypes.PtChar or TiberoColumnTypes.PtRaw)
        {
            size = DefaultOutSize;
        }

        return new TiberoBindPlan(type, direction, value, size, wrapper, tz);
    }

    /// <summary><see cref="BindDataType"/> → <c>TiberoDbType</c> 정수.</summary>
    internal static int ToProviderType(BindDataType type) => type switch
    {
        BindDataType.Varchar2 => TiberoColumnTypes.PtVarchar2,
        BindDataType.NVarchar2 => TiberoColumnTypes.PtNVarchar2,
        BindDataType.FixedChar => TiberoColumnTypes.PtChar,
        BindDataType.Number => TiberoColumnTypes.PtDecimal,
        BindDataType.BinaryDouble => TiberoColumnTypes.PtBinaryDouble,
        BindDataType.Date => TiberoColumnTypes.PtDate,
        BindDataType.Timestamp => TiberoColumnTypes.PtTimeStamp,
        BindDataType.TimestampTz => TiberoColumnTypes.PtTimeStampTZ,
        BindDataType.TimestampLtz => TiberoColumnTypes.PtTimeStampLTZ,
        BindDataType.Clob => TiberoColumnTypes.PtClob,
        BindDataType.Blob => TiberoColumnTypes.PtBlob,
        BindDataType.Raw => TiberoColumnTypes.PtRaw,
        BindDataType.Boolean => TiberoColumnTypes.PtBoolean,
        // JSON 문자열은 JSON 형식이 없는 서버에서도 통하도록 CLOB 으로 보낸다.
        BindDataType.Json => TiberoColumnTypes.PtClob,
        BindDataType.RefCursor => TiberoColumnTypes.PtRefCursor,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };

    /// <summary>
    /// OUT·INOUT 매개변수로 쓸 수 있는 형식(ODP-590703 이 나지 않고 값도 안전하게 오는 것).
    /// <c>Decimal</c> 은 뺀다: 드라이버 1.1.35 가 OUT NUMBER 를 <c>ExecuteNonQuery</c> 안에서 <see cref="decimal"/> 로 바꾸다가
    /// 28자리를 넘으면 <see cref="OverflowException"/>(TbNumber.toDecimal)을 던진다(실서버 실측). 그래서 NUMBER OUT 도 글자로 받는다.
    /// </summary>
    internal static bool IsOutCapable(int providerType) => providerType is
        TiberoColumnTypes.PtVarchar2 or TiberoColumnTypes.PtNVarchar2 or TiberoColumnTypes.PtChar or TiberoColumnTypes.PtNChar
        or TiberoColumnTypes.PtDate or TiberoColumnTypes.PtTimeStamp or TiberoColumnTypes.PtRaw
        or TiberoColumnTypes.PtClob or TiberoColumnTypes.PtBlob or TiberoColumnTypes.PtRefCursor;

    private static string ToText(object value) => value switch
    {
        bool b => b ? "1" : "0",
        DateTimeOffset o => o.ToString("yyyy-MM-dd HH:mm:ss.fffffff zzz", System.Globalization.CultureInfo.InvariantCulture),
        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss.fffffff", System.Globalization.CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };
}
