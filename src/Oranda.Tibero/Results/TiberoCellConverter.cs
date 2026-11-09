using System.Globalization;
using Oranda.Core.Grid;

namespace Oranda.Tibero.Results;

/// <summary>셀 값을 어디서 읽어야 하는지(리더 호출 종류). 어댑터를 부르는 쪽(<c>TiberoValues</c>)이 이 값으로 고른다.</summary>
internal enum TiberoCellSource
{
    /// <summary><c>ReadRawBytes</c> 의 서버 원시 바이트를 <see cref="TiberoCellConverter.FromRaw"/> 로 해석한다.</summary>
    Raw,

    /// <summary><c>DbDataReader.GetValue</c> 결과를 <see cref="TiberoCellConverter.FromClr"/> 로 정리한다.</summary>
    Clr,

    /// <summary><c>DbDataReader.GetString</c>.</summary>
    String,

    /// <summary>어댑터 <c>ReadTimeStampLtz</c> (<see cref="DateTime"/>).</summary>
    LtzDateTime,

    /// <summary>어댑터 <c>ReadClob</c> 로케이터 + 미리보기.</summary>
    Clob,

    /// <summary>어댑터 <c>ReadBlob</c> 로케이터 + 미리보기.</summary>
    Blob,
}

/// <summary>
/// 드라이버가 준 원시 바이트·.NET 값 → 그리드 원시 값(TRD 4.4 「타입 표시」). 드라이버 형식을 모르는 순수 변환이라 어디서나 시험한다.
/// 규칙 근거 = docs/design/tibero-driver-values.md 3장. NUMBER 는 정밀도를 지키고(decimal 또는 문자열), 시각·간격은 원시 바이트를 직접 해석한다.
/// </summary>
internal static class TiberoCellConverter
{
    /// <summary>열 분류·<c>ProviderType</c> 으로 값을 어디서 읽을지 정한다.</summary>
    public static TiberoCellSource SourceFor(ColumnDataKind kind, int providerType) => kind switch
    {
        ColumnDataKind.Number when TiberoColumnTypes.IsNativeNumeric(providerType) => TiberoCellSource.Clr,
        ColumnDataKind.Number => TiberoCellSource.Raw,
        ColumnDataKind.Timestamp when providerType == TiberoColumnTypes.PtTimeStampLTZ => TiberoCellSource.LtzDateTime,
        ColumnDataKind.Timestamp or ColumnDataKind.TimestampTz or ColumnDataKind.IntervalYearMonth or ColumnDataKind.IntervalDaySecond => TiberoCellSource.Raw,
        ColumnDataKind.Text or ColumnDataKind.RowId => TiberoCellSource.String,
        ColumnDataKind.Clob => TiberoCellSource.Clob,
        ColumnDataKind.Blob => TiberoCellSource.Blob,
        _ => TiberoCellSource.Clr,
    };

    /// <summary>
    /// 원시 바이트 → 값. 해석에 실패하면(<see cref="FormatException"/>: 범위 밖 날짜, 알 수 없는 모양) 셀이 아니라 읽기가 터지지 않도록
    /// 바이트를 16진 글자로 돌려준다(원본을 잃지 않고 눈에 띈다).
    /// </summary>
    public static object FromRaw(ColumnDataKind kind, ReadOnlySpan<byte> raw)
    {
        try
        {
            return kind switch
            {
                ColumnDataKind.Number => TiberoWire.DecodeNumber(raw),
                ColumnDataKind.Timestamp => TiberoWire.DecodeTimestamp(raw),
                ColumnDataKind.TimestampTz => TiberoWire.DecodeTimestampTz(raw),
                ColumnDataKind.IntervalYearMonth => TiberoWire.DecodeIntervalYearMonth(raw),
                ColumnDataKind.IntervalDaySecond => TiberoWire.DecodeIntervalDaySecond(raw),
                _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "원시 바이트로 읽는 분류가 아닙니다."),
            };
        }
        catch (FormatException)
        {
            return "0x" + Convert.ToHexString(raw);
        }
    }

    /// <summary>드라이버 <c>GetValue</c> 값 → 그리드 값. <c>sbyte[]</c>(RAW)는 <c>byte[]</c> 로, 그 밖은 그대로(알려진 .NET 형식).</summary>
    public static object? FromClr(object? value) => value switch
    {
        null or DBNull => null,
        sbyte[] raw => TiberoWire.ToBytes(raw),
        _ => value,
    };

    /// <summary>글자 변환(OUT 값 → 문자열 매개변수 등). 서식은 invariant.</summary>
    public static string? ToText(object? value) => FromClr(value) switch
    {
        null => null,
        string s => s,
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        var v => v.ToString(),
    };

    public static int ToInt(object? value) => FromClr(value) switch
    {
        null => 0,
        decimal d => (int)d,
        string s => int.Parse(s, CultureInfo.InvariantCulture),
        var v => Convert.ToInt32(v, CultureInfo.InvariantCulture),
    };
}
