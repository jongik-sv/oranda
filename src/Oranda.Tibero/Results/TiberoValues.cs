using System.Data.Common;
using System.Globalization;
using Oranda.Core.Execution;
using Oranda.Core.Grid;
using Oranda.Tibero.Driver;

namespace Oranda.Tibero.Results;

/// <summary>
/// 결과 셀·OUT 값 → 그리드 값(R1b). 드라이버 형식은 <see cref="TiberoDriver"/> 어댑터로만 만지고(첫 인자), 변환 규칙은 드라이버 무관 부분
/// (<see cref="TiberoCellConverter"/>·<see cref="TiberoWire"/>·<see cref="TiberoLobPreview"/>)이 가진다. 규칙 근거 = docs/design/tibero-driver-values.md 3장.
/// </summary>
internal static class TiberoValues
{
    /// <summary>LOB 셀의 기본 미리보기 길이(설정 <c>grid.initialLobFetchSize</c> 기본값과 같다).</summary>
    public const int LobPreviewSize = 4000;

    /// <summary>스키마 표를 한 번 읽어 열 목록과 열별 <c>ProviderType</c> 을 돌려준다(셀 읽기가 열 형식을 다시 묻지 않게).</summary>
    public static (IReadOnlyList<ResultColumn> Columns, int[] ProviderTypes) Describe(DbDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var schema = TiberoColumns.SchemaOrNull(reader.GetSchemaTable);
        var columns = TiberoColumns.Read(schema, reader.FieldCount, reader.GetName);
        var types = new int[columns.Count];
        for (var i = 0; i < types.Length; i++)
        {
            types[i] = TiberoColumns.ProviderTypeOf(schema, i) ?? TiberoColumnTypes.PtVarchar2;
        }

        return (columns, types);
    }

    /// <summary><see cref="TiberoResultSet"/> 에 주입하는 셀 읽기. <paramref name="providerTypes"/> 는 <see cref="Describe"/> 의 결과.</summary>
    public static TiberoCellReader CreateCellReader(TiberoDriver driver, IReadOnlyList<int> providerTypes, int lobPreviewSize = LobPreviewSize)
    {
        ArgumentNullException.ThrowIfNull(driver);
        ArgumentNullException.ThrowIfNull(providerTypes);
        return (reader, ordinal, column) => ReadCell(driver, reader, ordinal, column.Kind, providerTypes[ordinal], lobPreviewSize);
    }

    /// <summary>셀 값 1개. null 은 SQL NULL.</summary>
    public static object? ReadCell(TiberoDriver driver, DbDataReader reader, int i, ColumnDataKind kind, int providerType,
                                   int lobPreviewSize = LobPreviewSize)
    {
        ArgumentNullException.ThrowIfNull(driver);
        ArgumentNullException.ThrowIfNull(reader);
        if (reader.IsDBNull(i))
        {
            return null;
        }

        switch (TiberoCellConverter.SourceFor(kind, providerType))
        {
            case TiberoCellSource.Raw:
                return driver.ReadRawBytes(reader, i) is { } raw ? TiberoCellConverter.FromRaw(kind, raw) : null;
            case TiberoCellSource.String:
                return reader.GetString(i);
            case TiberoCellSource.LtzDateTime:
                return driver.ReadTimeStampLtz(reader, i);
            case TiberoCellSource.Clob:
                {
                    var lob = driver.ReadClob(reader, i);
                    return lob is null ? null : TiberoLobPreview.Clob(lob.IsNClob, lob.Length, lob.Read, lob, lobPreviewSize);
                }

            case TiberoCellSource.Blob:
                {
                    var lob = driver.ReadBlob(reader, i);
                    return lob is null ? null : TiberoLobPreview.Blob(lob.Length, lob.Read, lob, lobPreviewSize);
                }

            default:
                return Plain(TiberoCellConverter.FromClr(reader.GetValue(i)));
        }
    }

    /// <summary>
    /// OUT·INOUT·반환값 매개변수의 실행 뒤 값. REF CURSOR 는 <see cref="DbDataReader"/> 그대로(받은 쪽이 <see cref="TiberoResultSet"/> 으로 감싸고 Dispose),
    /// CLOB·BLOB 은 전체를 읽어 <see cref="string"/>·<see cref="byte"/>[] 로 준다(Oracle 과 같다).
    /// </summary>
    public static object? ReadOut(TiberoDriver driver, DbParameter parameter, BindValue bind)
    {
        ArgumentNullException.ThrowIfNull(driver);
        ArgumentNullException.ThrowIfNull(parameter);
        ArgumentNullException.ThrowIfNull(bind);
        return FromOut(driver.ReadOutValue(parameter), bind);
    }

    /// <summary><see cref="ReadOut"/> 의 변환 부분(어댑터 값 → 그리드 값).</summary>
    public static object? FromOut(TiberoOutValue value, BindValue bind)
    {
        if (value.IsNull)
        {
            return null;
        }

        switch (value.Kind)
        {
            case TiberoValueKind.Null:
                return null;
            case TiberoValueKind.String:
                return AsBind(bind, (string?)value.Value);
            case TiberoValueKind.Decimal:
                return FromDecimalText((string)value.Value!);
            case TiberoValueKind.Date:
                return value.Value;
            case TiberoValueKind.TimeStamp or TiberoValueKind.TimeStampLtz:
                return WithNanoseconds((DateTime)value.Value!, value.Nanosecond);
            case TiberoValueKind.Binary:
                return value.Value;
            case TiberoValueKind.Lob:
                return ReadAll((TiberoLob)value.Value!);
            case TiberoValueKind.RefCursor:
                return value.Value;
            case TiberoValueKind.Clr:
                return Plain(TiberoCellConverter.FromClr(value.Value));
            default:
                return value.Value;
        }
    }

    /// <summary>
    /// 드라이버가 글자로 돌려준 값(NUMBER·Boolean 등은 OUT 이 안 되거나 위험해 글자로 받는다)을 요청한 형식으로 되돌린다.
    /// NUMBER → <see cref="decimal"/>(28자리 이내) 또는 글자, Boolean → <c>1</c>/<c>0</c> 을 <see cref="bool"/>. 그 밖은 글자 그대로.
    /// </summary>
    private static object? AsBind(BindValue bind, string? text)
    {
        if (bind.Type == BindDataType.Number && text is not null)
        {
            var trimmed = text.Trim();
            return trimmed.Length == 0 ? null : FromDecimalText(trimmed);
        }

        if (bind.Type == BindDataType.Boolean)
        {
            if (text == "1")
            {
                return true;
            }

            if (text == "0")
            {
                return false;
            }
        }

        return text;
    }

    /// <summary>NUMBER 글자 → 28자리 이내면 <see cref="decimal"/>, 아니면 글자(정밀도 보존, Oracle <c>FromDecimal</c> 과 같은 규칙).</summary>
    internal static object FromDecimalText(string text)
    {
        var digits = 0;
        foreach (var ch in text)
        {
            if (char.IsAsciiDigit(ch))
            {
                digits++;
            }
            else if (ch is 'E' or 'e')
            {
                return text;
            }
        }

        return digits <= 28 && decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : text;
    }

    /// <summary>초까지만 오는 시각에 나노초를 더한다(틱 = 100ns 로 내림).</summary>
    internal static DateTime WithNanoseconds(DateTime seconds, int nanosecond) => seconds.AddTicks(nanosecond / 100);

    private static object ReadAll(TiberoLob lob)
    {
        using (lob)
        {
            if (lob.Kind == TiberoLobKind.Blob)
            {
                using var ms = new MemoryStream();
                var buffer = new byte[8192];
                int n;
                while ((n = lob.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ms.Write(buffer, 0, n);
                }

                return ms.ToArray();
            }

            var sb = new System.Text.StringBuilder();
            var chars = new char[8192];
            int read;
            while ((read = lob.Read(chars, 0, chars.Length)) > 0)
            {
                sb.Append(chars, 0, read);
            }

            return sb.ToString();
        }
    }

    /// <summary>그리드가 아는 .NET 기본 형식이 아니면 글자로 바꾼다(드라이버 객체가 셀에 남지 않게).</summary>
    private static object? Plain(object? value) => value switch
    {
        null or string or decimal or DateTime or DateTimeOffset or TimeSpan or byte[] or bool or byte or short or int or long or float or double or Guid => value,
        _ => TiberoCellConverter.ToText(value),
    };
}
