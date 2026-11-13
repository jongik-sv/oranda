using System.Globalization;
using System.Text;
using Oranda.Core.Connection;
using Oranda.Core.Metadata;
using Oranda.Core.Rollback;

namespace Oranda.Testing.Rollback;

/// <summary>
/// Oracle 규칙(되돌리기 지점 설계 6.6 표)을 그대로 옮긴 시험용 방언. 공급자 방언(RB-10·RB-11)을 기다리지 않고 엔진·스크립트를 시험한다.
/// 읽은 값은 고정 형식 글자(string)라고 보고 형식 함수로 감싼다. BINARY_FLOAT·BINARY_DOUBLE 은 .NET float·double 을 받는다.
/// </summary>
public sealed class FakeRollbackDialect : IRollbackDialect
{
    public const string NumberMask = "'TM9', 'NLS_NUMERIC_CHARACTERS=''.,'''";
    public const string DateMask = "SYYYY-MM-DD HH24:MI:SS";
    public const string TimestampMask = "SYYYY-MM-DD HH24:MI:SS.FF9";
    public const string TimestampTzMask = "SYYYY-MM-DD HH24:MI:SS.FF9 TZR";

    public Dbms Dbms { get; init; } = Dbms.Oracle;

    public string PointName { get; init; } = "SCN";

    public IReadOnlyList<string> PointQueries { get; init; } =
    [
        "SELECT DBMS_FLASHBACK.GET_SYSTEM_CHANGE_NUMBER FROM DUAL",
        "SELECT TIMESTAMP_TO_SCN(SYSTIMESTAMP) FROM DUAL",
    ];

    public string SavepointName { get; init; } = "ORANDA$RB";

    public bool SupportsReturningRowId { get; init; } = true;

    public string? SideEffectsSql { get; init; } =
        "SELECT 'TRIGGER', TRIGGER_NAME FROM ALL_TRIGGERS WHERE TABLE_OWNER = :owner AND TABLE_NAME = :name";

    public int IdentifierBytes { get; init; } = 128;

    public string AsOf(long point) => "AS OF " + PointName + " " + point.ToString(CultureInfo.InvariantCulture);

    public int MaxIdentifierBytes(ServerInfo server) => IdentifierBytes;

    public RollbackValueKind KindOf(ColumnInfo column)
    {
        ArgumentNullException.ThrowIfNull(column);
        var t = column.DataType.ToUpperInvariant();
        return t switch
        {
            _ when t.StartsWith("NVARCHAR2", StringComparison.Ordinal) || t.StartsWith("NCHAR", StringComparison.Ordinal) => RollbackValueKind.NationalText,
            _ when t.StartsWith("VARCHAR", StringComparison.Ordinal) || t.StartsWith("CHAR", StringComparison.Ordinal) => RollbackValueKind.Text,
            _ when t.StartsWith("NUMBER", StringComparison.Ordinal) || t.StartsWith("FLOAT", StringComparison.Ordinal) || t == "INTEGER" => RollbackValueKind.Number,
            "BINARY_FLOAT" => RollbackValueKind.BinaryFloat,
            "BINARY_DOUBLE" => RollbackValueKind.BinaryDouble,
            "DATE" => RollbackValueKind.Date,
            _ when t.Contains("WITH LOCAL TIME ZONE", StringComparison.Ordinal) => RollbackValueKind.TimestampLtz,
            _ when t.Contains("WITH TIME ZONE", StringComparison.Ordinal) => RollbackValueKind.TimestampTz,
            _ when t.StartsWith("TIMESTAMP", StringComparison.Ordinal) => RollbackValueKind.Timestamp,
            _ when t.StartsWith("INTERVAL DAY", StringComparison.Ordinal) => RollbackValueKind.IntervalDaySecond,
            _ when t.StartsWith("INTERVAL YEAR", StringComparison.Ordinal) => RollbackValueKind.IntervalYearMonth,
            _ when t.StartsWith("RAW", StringComparison.Ordinal) => RollbackValueKind.Raw,
            "ROWID" => RollbackValueKind.RowId, // UROWID(n) 은 ROWIDTOCHAR 로 읽지 못하므로 큰 열로 본다
            _ => RollbackValueKind.Large,
        };
    }

    public string ReadExpression(RollbackColumn column, string qualifiedColumn)
    {
        ArgumentNullException.ThrowIfNull(column);
        var c = qualifiedColumn;
        return column.Kind switch
        {
            RollbackValueKind.Number => $"TO_CHAR({c}, {NumberMask})",
            RollbackValueKind.Date => $"TO_CHAR({c}, '{DateMask}')",
            RollbackValueKind.Timestamp => $"TO_CHAR({c}, '{TimestampMask}')",
            RollbackValueKind.TimestampTz => $"TO_CHAR({c}, '{TimestampTzMask}')",
            RollbackValueKind.TimestampLtz => $"TO_CHAR(SYS_EXTRACT_UTC(CAST({c} AS TIMESTAMP WITH TIME ZONE)), '{TimestampMask}')",
            RollbackValueKind.IntervalDaySecond or RollbackValueKind.IntervalYearMonth => $"TO_CHAR({c})",
            RollbackValueKind.Raw => $"RAWTOHEX({c})",
            RollbackValueKind.RowId => $"ROWIDTOCHAR({c})",
            RollbackValueKind.Large => $"CASE WHEN {c} IS NULL THEN 1 ELSE 0 END",
            _ => c,
        };
    }

    public string? Literal(RollbackColumn column, object? value, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(column);
        reason = null;
        if (value is null or DBNull)
        {
            return "NULL";
        }

        switch (column.Kind)
        {
            case RollbackValueKind.Large:
                reason = "큰 열";
                return null;
            case RollbackValueKind.BinaryFloat:
            case RollbackValueKind.BinaryDouble:
                return DoubleLiteral(Convert.ToDouble(value, CultureInfo.InvariantCulture));
            case RollbackValueKind.Number:
                return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        var s = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        switch (column.Kind)
        {
            case RollbackValueKind.Text:
                if (Encoding.UTF8.GetByteCount(s) > 4000)
                {
                    reason = "4000바이트를 넘는 글";
                    return null;
                }

                return Quote(s);
            case RollbackValueKind.NationalText:
                return "UNISTR(" + Quote(Unistr(s)) + ")";
            case RollbackValueKind.Date:
                return $"TO_DATE({Quote(s)}, '{DateMask}')";
            case RollbackValueKind.Timestamp:
                return $"TO_TIMESTAMP({Quote(s)}, '{TimestampMask}')";
            case RollbackValueKind.TimestampTz:
                return $"TO_TIMESTAMP_TZ({Quote(s)}, '{TimestampTzMask}')";
            case RollbackValueKind.TimestampLtz:
                return $"CAST(FROM_TZ(TO_TIMESTAMP({Quote(s)}, '{TimestampMask}'), 'UTC') AS TIMESTAMP WITH LOCAL TIME ZONE)";
            case RollbackValueKind.IntervalDaySecond:
                return $"TO_DSINTERVAL({Quote(s)})";
            case RollbackValueKind.IntervalYearMonth:
                return $"TO_YMINTERVAL({Quote(s)})";
            case RollbackValueKind.Raw:
                return $"HEXTORAW({Quote(s)})";
            case RollbackValueKind.RowId:
                return RowIdLiteral(s);
            default:
                return Quote(s);
        }
    }

    public string RowIdLiteral(string rowId) => $"CHARTOROWID({Quote(rowId)})";

    private static string Quote(string s) => "'" + s.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static string Unistr(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (ch == '\\')
            {
                sb.Append("\\005C");
            }
            else if (ch < 128)
            {
                sb.Append(ch);
            }
            else
            {
                sb.Append('\\').Append(((int)ch).ToString("X4", CultureInfo.InvariantCulture));
            }
        }

        return sb.ToString();
    }

    private static string DoubleLiteral(double d) => d switch
    {
        double.NaN => "BINARY_DOUBLE_NAN",
        double.PositiveInfinity => "BINARY_DOUBLE_INFINITY",
        double.NegativeInfinity => "-BINARY_DOUBLE_INFINITY",
        _ => "TO_BINARY_DOUBLE('" + d.ToString("R", CultureInfo.InvariantCulture) + "')",
    };
}
