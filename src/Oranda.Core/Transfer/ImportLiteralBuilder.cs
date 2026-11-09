using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Oranda.Core.Grid;
using Oranda.Core.Metadata;

namespace Oranda.Core.Transfer;

/// <summary>
/// 가져오기 셀 값 → 대상 열의 SQL 리터럴(DBMS 공통 규칙). 열 타입 분류(<see cref="ColumnDataKind"/>)만 DBMS 마다 달라
/// 각 DBMS 의 <see cref="IImportValueConverter"/> 가 자기 <c>KindOf</c> 결과를 넘겨 부른다. Oracle·Tibero 가 같이 쓰는 리터럴 문법:
/// <c>TO_DATE/TO_TIMESTAMP/TO_TIMESTAMP_TZ(서식 명시)</c>, <c>INTERVAL '…' YEAR TO MONTH / DAY TO SECOND</c>, <c>HEXTORAW</c>,
/// <c>TO_BLOB(HEXTORAW(…))</c>·<c>TO_CLOB(…)</c> 이어 붙이기. 값이 열 타입에 안 맞으면 <see cref="FormatException"/>. 빈 글자는 NULL.
/// </summary>
public static partial class ImportLiteralBuilder
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static readonly string[] DateFormats =
    [
        "yyyy-MM-dd", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF",
        "yyyy-MM-dd HH:mm:ss.FFFFFFF", "yyyy/MM/dd", "yyyy/MM/dd HH:mm:ss", "yyyyMMdd", "yyyyMMddHHmmss",
    ];

    /// <summary>
    /// 셀 값 하나를 대상 열(분류 <paramref name="kind"/>)의 SQL 리터럴로. <paramref name="maxBlobBytes"/> 가 있으면 BLOB 값이 그보다 클 때 <see cref="FormatException"/>
    /// (BLOB 끼리 <c>||</c> 로 잇지 못하는 DBMS 용 — Tibero 는 한 리터럴이 2000바이트까지). 없으면 2000바이트씩 <c>TO_BLOB(HEXTORAW(…))</c> 를 <c>||</c> 로 잇는다(Oracle).
    /// </summary>
    public static string ToLiteral(ColumnInfo column, ColumnDataKind kind, object? cell, int? maxBlobBytes = null)
    {
        ArgumentNullException.ThrowIfNull(column);
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
                return BlobLiteral(HexBytes(text ?? throw new FormatException("16진수 글자가 아님.")), maxBlobBytes);
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
    public static bool TryConvert(ColumnInfo column, ColumnDataKind kind, object? cell, out string literal, out string? error, int? maxBlobBytes = null)
    {
        try
        {
            literal = ToLiteral(column, kind, cell, maxBlobBytes);
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

    private static string BlobLiteral(byte[] bytes, int? maxBytes)
    {
        if (bytes.Length == 0)
        {
            return "NULL";
        }

        if (maxBytes is { } max && bytes.Length > max)
        {
            throw new FormatException($"BLOB 값이 {bytes.Length.ToString(Inv)}바이트라 한 리터럴 한계 {max.ToString(Inv)}바이트를 넘는다.");
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
