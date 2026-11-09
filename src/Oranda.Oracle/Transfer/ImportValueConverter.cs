using Oranda.Core.Grid;
using Oranda.Core.Metadata;
using Oranda.Core.Transfer;

namespace Oranda.Oracle.Transfer;

/// <summary>
/// 가져오기 셀 값 → 대상 열 타입의 SQL 리터럴(타입 변환 미리보기도 같은 규칙). 값이 열 타입에 안 맞으면 <see cref="FormatException"/>.
/// 빈 글자는 NULL(Oracle 은 빈 문자열을 NULL 로 본다). 소유: TSK-06-07.
/// </summary>
public static class ImportValueConverter
{
    /// <summary>대상 열 타입 이름 → 표시 분류.</summary>
    public static ColumnDataKind KindOf(string dataType)
    {
        ArgumentNullException.ThrowIfNull(dataType);
        var t = dataType.Trim().ToUpperInvariant();
        if (t.StartsWith("TIMESTAMP", StringComparison.Ordinal))
        {
            return t.Contains("TIME ZONE", StringComparison.Ordinal) ? ColumnDataKind.TimestampTz : ColumnDataKind.Timestamp;
        }

        if (t.StartsWith("INTERVAL YEAR", StringComparison.Ordinal))
        {
            return ColumnDataKind.IntervalYearMonth;
        }

        if (t.StartsWith("INTERVAL DAY", StringComparison.Ordinal))
        {
            return ColumnDataKind.IntervalDaySecond;
        }

        return t switch
        {
            "DATE" => ColumnDataKind.Date,
            "NUMBER" or "FLOAT" or "INTEGER" or "INT" or "SMALLINT" or "DECIMAL" or "NUMERIC" or "REAL" or "DOUBLE PRECISION" => ColumnDataKind.Number,
            "BINARY_FLOAT" or "BINARY_DOUBLE" => ColumnDataKind.BinaryFloat,
            "CLOB" or "NCLOB" or "LONG" => ColumnDataKind.Clob,
            "BLOB" => ColumnDataKind.Blob,
            "RAW" or "LONG RAW" => ColumnDataKind.Raw,
            "BOOLEAN" => ColumnDataKind.Boolean,
            "XMLTYPE" => ColumnDataKind.Xml,
            "JSON" => ColumnDataKind.Json,
            "VECTOR" => ColumnDataKind.Vector,
            "ROWID" or "UROWID" => ColumnDataKind.RowId,
            _ when t.StartsWith("VARCHAR", StringComparison.Ordinal) || t.StartsWith("NVARCHAR", StringComparison.Ordinal)
                   || t.StartsWith("CHAR", StringComparison.Ordinal) || t.StartsWith("NCHAR", StringComparison.Ordinal) => ColumnDataKind.Text,
            _ => ColumnDataKind.Other,
        };
    }

    /// <summary>셀 값 하나를 대상 열의 SQL 리터럴로(공통 규칙 = <see cref="ImportLiteralBuilder"/>).</summary>
    public static string ToLiteral(ColumnInfo column, object? cell)
    {
        ArgumentNullException.ThrowIfNull(column);
        return ImportLiteralBuilder.ToLiteral(column, KindOf(column.DataType), cell);
    }

    /// <summary>미리보기용: 변환이 되면 리터럴, 안 되면 null 과 이유.</summary>
    public static bool TryConvert(ColumnInfo column, object? cell, out string literal, out string? error)
    {
        ArgumentNullException.ThrowIfNull(column);
        return ImportLiteralBuilder.TryConvert(column, KindOf(column.DataType), cell, out literal, out error);
    }
}
