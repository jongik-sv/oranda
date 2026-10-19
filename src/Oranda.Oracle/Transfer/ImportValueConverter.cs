using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Oranda.Core.Grid;
using Oranda.Core.Metadata;

namespace Oranda.Oracle.Transfer;

/// <summary>
/// 가져오기 셀 값 → 대상 열 타입의 SQL 리터럴(타입 변환 미리보기도 같은 규칙). 값이 열 타입에 안 맞으면 <see cref="FormatException"/>.
/// 빈 글자는 NULL(Oracle 은 빈 문자열을 NULL 로 본다). 소유: TSK-06-07.
/// </summary>
public static partial class ImportValueConverter
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static readonly string[] DateFormats =
    [
        "yyyy-MM-dd", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF",
        "yyyy-MM-dd HH:mm:ss.FFFFFFF", "yyyy/MM/dd", "yyyy/MM/dd HH:mm:ss", "yyyyMMdd", "yyyyMMddHHmmss",
    ];

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

    /// <summary>셀 값 하나를 대상 열의 SQL 리터럴로.</summary>
    public static string ToLiteral(ColumnInfo column, object? cell)
    {
        ArgumentNullException.ThrowIfNull(column);
        var kind = KindOf(column.DataType);
        var rc = new ResultColumn(0, column.Name, column.DataType, kind);
        if (cell is null or DBNull || (cell is string { Length: 0 }))
        {
            return "NULL";
        }

        var text = cell is string str ? str : null;
        switch (kind)
        {
            case ColumnDataKind.Number or ColumnDataKind.BinaryFloat:
                return NumberLiteral(cell);
            case ColumnDataKind.Date:
                return CellValueFormatter.ToSqlLiteral(rc, ToDateTime(cell));
            case ColumnDataKind.Timestamp:
                return CellValueFormatter.ToSqlLiteral(rc, ToDateTime(cell));
            case ColumnDataKind.TimestampTz:
                return CellValueFormatter.ToSqlLiteral(rc, ToDateTimeOffset(cell));
            case ColumnDataKind.Boolean:
                return ToBool(cell) ? "TRUE" : "FALSE";
            case ColumnDataKind.Raw:
                return CellValueFormatter.ToSqlLiteral(rc, HexBytes(text ?? throw new FormatException("16진수 글자가 아님.")));
            case ColumnDataKind.Blob:
                return BlobLiteral(HexBytes(text ?? throw new FormatException("16진수 글자가 아님.")));
            case ColumnDataKind.Clob:
                return ClobLiteral(AsText(cell));
            case ColumnDataKind.IntervalYearMonth:
                return $"INTERVAL {CellValueFormatter.Quote(AsText(cell).Trim())} YEAR TO MONTH";
            case ColumnDataKind.IntervalDaySecond:
                return $"INTERVAL {CellValueFormatter.Quote(AsText(cell).Trim())} DAY TO SECOND";
            case ColumnDataKind.Xml:
                return CellValueFormatter.ToSqlLiteral(rc, AsText(cell));
            default:
                return CellValueFormatter.Quote(AsText(cell));
        }
    }

    /// <summary>미리보기용: 변환이 되면 리터럴, 안 되면 null 과 이유.</summary>
    public static bool TryConvert(ColumnInfo column, object? cell, out string literal, out string? error)
    {
        try
        {
            literal = ToLiteral(column, cell);
            error = null;
            return true;
        }
        catch (FormatException ex)
        {
            literal = string.Empty;
            error = ex.Message;
            return false;
        }
    }

    private static string AsText(object cell) => cell switch
    {
        string s => s,
        bool b => b ? "TRUE" : "FALSE",
        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss", Inv),
        _ => Convert.ToString(cell, Inv) ?? string.Empty,
    };

    private static string NumberLiteral(object cell)
    {
        if (cell is bool or DateTime)
        {
            throw new FormatException($"숫자가 아님: '{AsText(cell)}'");
        }

        var s = (cell is string str ? str : Convert.ToString(cell, Inv))!.Trim();
        if (!NumberPattern().IsMatch(s))
        {
            throw new FormatException($"숫자가 아님: '{s}'");
        }

        return s.StartsWith('+') ? s[1..] : s;
    }

    private static DateTime ToDateTime(object cell)
    {
        switch (cell)
        {
            case DateTime dt:
                return dt;
            case DateTimeOffset dto:
                return dto.DateTime;
            case string s:
                var t = s.Trim();
                if (DateTime.TryParseExact(t, DateFormats, Inv, DateTimeStyles.None, out var exact))
                {
                    return exact;
                }

                if (DateTime.TryParse(t, Inv, DateTimeStyles.None, out var any))
                {
                    return any;
                }

                throw new FormatException($"날짜가 아님: '{s}'");
            default:
                throw new FormatException($"날짜가 아님: '{AsText(cell)}'");
        }
    }

    private static DateTimeOffset ToDateTimeOffset(object cell)
    {
        if (cell is DateTimeOffset dto)
        {
            return dto;
        }

        if (cell is string s && DateTimeOffset.TryParse(s.Trim(), Inv, DateTimeStyles.AssumeLocal, out var parsed))
        {
            return parsed;
        }

        if (cell is DateTime or string)
        {
            return new DateTimeOffset(ToDateTime(cell), TimeZoneInfo.Local.GetUtcOffset(ToDateTime(cell)));
        }

        throw new FormatException($"시각이 아님: '{AsText(cell)}'");
    }

    private static bool ToBool(object cell)
    {
        if (cell is bool b)
        {
            return b;
        }

        return AsText(cell).Trim().ToUpperInvariant() switch
        {
            "TRUE" or "T" or "1" or "Y" or "YES" or "예" => true,
            "FALSE" or "F" or "0" or "N" or "NO" or "아니오" => false,
            var other => throw new FormatException($"참/거짓이 아님: '{other}'"),
        };
    }

    private static byte[] HexBytes(string s)
    {
        var t = s.Trim();
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            t = t[2..];
        }

        t = t.Replace(" ", string.Empty, StringComparison.Ordinal);
        try
        {
            return Convert.FromHexString(t);
        }
        catch (FormatException)
        {
            throw new FormatException($"16진수가 아님: '{(s.Length > 20 ? s[..20] + "…" : s)}'");
        }
    }

    private static string BlobLiteral(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return "NULL";
        }

        var parts = new List<string>();
        for (var i = 0; i < bytes.Length; i += 2000)
        {
            parts.Add($"TO_BLOB(HEXTORAW('{Convert.ToHexString(bytes, i, Math.Min(2000, bytes.Length - i))}'))");
        }

        return string.Join("||", parts);
    }

    /// <summary>문자열 리터럴 한도(4000바이트) 때문에 CLOB 은 나눠서 TO_CLOB 이어 붙인다.</summary>
    private static string ClobLiteral(string text)
    {
        if (Encoding.UTF8.GetByteCount(text) <= 3000)
        {
            return CellValueFormatter.Quote(text);
        }

        var parts = new List<string>();
        var sb = new StringBuilder();
        var bytes = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var n = rune.Utf8SequenceLength;
            if (bytes + n > 3000)
            {
                parts.Add($"TO_CLOB({CellValueFormatter.Quote(sb.ToString())})");
                sb.Clear();
                bytes = 0;
            }

            sb.Append(rune.ToString());
            bytes += n;
        }

        if (sb.Length > 0)
        {
            parts.Add($"TO_CLOB({CellValueFormatter.Quote(sb.ToString())})");
        }

        return string.Join("||", parts);
    }

    [GeneratedRegex(@"^[+-]?(\d+(\.\d*)?|\.\d+)([eE][+-]?\d+)?$")]
    private static partial Regex NumberPattern();
}
