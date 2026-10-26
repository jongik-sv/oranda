using System.Data;
using System.Globalization;
using Oracle.ManagedDataAccess.Client;
using Oracle.ManagedDataAccess.Types;
using Oranda.Core.Grid;
using Oranda.Oracle.Sessions;

namespace Oranda.Oracle.Execution;

/// <summary>ODP.NET 값 → 그리드 원시 값(TRD 4.4 「타입 표시」). NUMBER 는 정밀도를 지키고(decimal 또는 문자열), LOB 은 미리보기.</summary>
internal static class OracleValues
{
    /// <summary>LOB 미리보기 길이(문자·바이트)의 기본값. 실제 값은 설정 <c>grid.initialLobFetchSize</c>(<see cref="IGridFetchSettings"/>).</summary>
    public const int LobPreviewSize = 4000;

    /// <summary>OracleDbType → (서버 타입 이름, 표시 분류).</summary>
    public static (string DbTypeName, ColumnDataKind Kind) Describe(OracleDbType type) => type switch
    {
        OracleDbType.Varchar2 => ("VARCHAR2", ColumnDataKind.Text),
        OracleDbType.NVarchar2 => ("NVARCHAR2", ColumnDataKind.Text),
        OracleDbType.Char => ("CHAR", ColumnDataKind.Text),
        OracleDbType.NChar => ("NCHAR", ColumnDataKind.Text),
        OracleDbType.Long => ("LONG", ColumnDataKind.Text),
        OracleDbType.Decimal or OracleDbType.Int16 or OracleDbType.Int32 or OracleDbType.Int64 or OracleDbType.Byte
            or OracleDbType.Double or OracleDbType.Single => ("NUMBER", ColumnDataKind.Number),
        OracleDbType.BinaryFloat => ("BINARY_FLOAT", ColumnDataKind.BinaryFloat),
        OracleDbType.BinaryDouble => ("BINARY_DOUBLE", ColumnDataKind.BinaryFloat),
        OracleDbType.Date => ("DATE", ColumnDataKind.Date),
        OracleDbType.TimeStamp => ("TIMESTAMP", ColumnDataKind.Timestamp),
        OracleDbType.TimeStampLTZ => ("TIMESTAMP WITH LOCAL TIME ZONE", ColumnDataKind.Timestamp),
        OracleDbType.TimeStampTZ => ("TIMESTAMP WITH TIME ZONE", ColumnDataKind.TimestampTz),
        OracleDbType.IntervalYM => ("INTERVAL YEAR TO MONTH", ColumnDataKind.IntervalYearMonth),
        OracleDbType.IntervalDS => ("INTERVAL DAY TO SECOND", ColumnDataKind.IntervalDaySecond),
        OracleDbType.Clob => ("CLOB", ColumnDataKind.Clob),
        OracleDbType.NClob => ("NCLOB", ColumnDataKind.Clob),
        OracleDbType.Blob => ("BLOB", ColumnDataKind.Blob),
        OracleDbType.BFile => ("BFILE", ColumnDataKind.Blob),
        OracleDbType.Raw => ("RAW", ColumnDataKind.Raw),
        OracleDbType.LongRaw => ("LONG RAW", ColumnDataKind.Raw),
        OracleDbType.Boolean => ("BOOLEAN", ColumnDataKind.Boolean),
        OracleDbType.Json => ("JSON", ColumnDataKind.Json),
        OracleDbType.Vector or OracleDbType.Vector_Int8 or OracleDbType.Vector_Float32 or OracleDbType.Vector_Float64
            or OracleDbType.Vector_Binary => ("VECTOR", ColumnDataKind.Vector),
        OracleDbType.XmlType => ("XMLTYPE", ColumnDataKind.Xml),
        OracleDbType.Object or OracleDbType.Array or OracleDbType.Ref => ("OBJECT", ColumnDataKind.UserDefinedType),
        OracleDbType.RefCursor => ("REF CURSOR", ColumnDataKind.RefCursor),
        _ => (type.ToString().ToUpperInvariant(), ColumnDataKind.Other),
    };

    /// <summary>리더의 열 정의. ROWID 는 스키마 표의 IsRowID 로 알아본다(ODP 는 VARCHAR2 로 알린다).</summary>
    public static IReadOnlyList<ResultColumn> ReadColumns(OracleDataReader reader)
    {
        var schema = SchemaOrNull(reader.GetSchemaTable);
        var columns = new List<ResultColumn>(reader.FieldCount);
        for (var i = 0; i < reader.FieldCount; i++)
        {
            DataRow? row = schema is not null && i < schema.Rows.Count ? schema.Rows[i] : null;
            var providerType = row is not null && Get<int>(row, "ProviderType") is { } pt ? (OracleDbType)pt : OracleDbType.Varchar2;
            var (dbType, kind) = Describe(providerType);
            if (row is not null && Get<bool>(row, "IsRowID") == true)
            {
                (dbType, kind) = ("ROWID", ColumnDataKind.RowId);
            }

            columns.Add(new ResultColumn(i, reader.GetName(i), dbType, kind)
            {
                Size = row is null ? null : Get<int>(row, "ColumnSize"),
                Precision = row is null ? null : Get<short>(row, "NumericPrecision") is { } p ? p : null,
                Scale = row is null ? null : Get<short>(row, "NumericScale") is { } sc ? sc : null,
                IsNullable = row is null || Get<bool>(row, "AllowDBNull") != false,
                BaseOwner = row is null ? null : GetString(row, "BaseSchemaName"),
                BaseTable = row is null ? null : GetString(row, "BaseTableName"),
                BaseColumn = row is null ? null : GetString(row, "BaseColumnName"),
            });
        }

        return columns;
    }

    /// <summary>
    /// 스키마 표 조회. ODP 는 이 호출에서 FROM 뒤 낱말을 표 이름으로 읽어 보조 질의를 보내므로, <c>표 VERSIONS BETWEEN …</c>·<c>표 AS OF …</c> 같은
    /// 문장에서는 ORA-00942 로 실패한다. 열 출처·ROWID 표시는 덤이므로 실패하면 건너뛰고 본 결과는 그대로 보여 준다.
    /// </summary>
    internal static DataTable? SchemaOrNull(Func<DataTable?> read)
    {
        try
        {
            return read();
        }
        catch (Exception e) when (e is OracleException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>셀 값 1개. null 은 SQL NULL.</summary>
    public static object? ReadCell(OracleDataReader reader, int i, ColumnDataKind kind, int lobPreviewSize = LobPreviewSize)
    {
        if (reader.IsDBNull(i))
        {
            return null;
        }

        switch (kind)
        {
            case ColumnDataKind.Number:
                return FromDecimal(reader.GetOracleDecimal(i));
            case ColumnDataKind.Text or ColumnDataKind.RowId:
                return reader.GetString(i);
            case ColumnDataKind.Date:
                return reader.GetOracleDate(i).Value;
            case ColumnDataKind.Timestamp:
                return reader.GetValue(i);
            case ColumnDataKind.TimestampTz:
                {
                    var tz = reader.GetOracleTimeStampTZ(i);
                    return new DateTimeOffset(tz.Value, tz.GetTimeZoneOffset());
                }

            case ColumnDataKind.IntervalYearMonth:
                return reader.GetOracleIntervalYM(i).ToString();
            case ColumnDataKind.IntervalDaySecond:
                return reader.GetTimeSpan(i);
            case ColumnDataKind.Clob:
                return ClobValue(reader.GetOracleClob(i), lobPreviewSize);
            case ColumnDataKind.Blob when string.Equals(reader.GetDataTypeName(i), "BFile", StringComparison.OrdinalIgnoreCase):
                return new LobValue(LobKind.BFile, -1) { Locator = reader.GetOracleBFile(i) };
            case ColumnDataKind.Blob:
                return BlobValue(reader.GetOracleBlob(i), lobPreviewSize);
            case ColumnDataKind.Raw:
                return (byte[])reader.GetValue(i);
            case ColumnDataKind.Boolean:
                return reader.GetBoolean(i);
            case ColumnDataKind.Json:
                return ToText(reader.GetValue(i));
            case ColumnDataKind.Xml:
                return ToText(reader.GetValue(i));
            default:
                return Normalize(reader.GetValue(i));
        }
    }

    /// <summary>OUT 매개변수 값. REF CURSOR 는 결과 집합(호출자가 Dispose). 실행이 세션 명령 잠금을 쥔 채 부른다(여기서 다시 잡지 않는다).</summary>
    public static Dictionary<string, object?> ReadOutValues(OracleSession session,
                                                            IReadOnlyList<(string Name, OracleParameter Parameter)> outs, int pageSize,
                                                            int lobPreviewSize = LobPreviewSize)
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, p) in outs)
        {
            values[name] = p.Value is OracleRefCursor rc
                ? (rc.IsNull ? null : new OracleResultSet(session, null, rc.GetDataReader(), pageSize, lobPreviewSize))
                : Normalize(p.Value);
        }

        return values;
    }

    /// <summary>ODP.NET 형식(Oracle*) → .NET 기본 형식.</summary>
    public static object? Normalize(object? value) => value switch
    {
        null or DBNull => null,
        INullable { IsNull: true } => null,
        OracleDecimal d => FromDecimal(d),
        OracleString s => s.Value,
        OracleDate d => d.Value,
        OracleTimeStamp t => t.Value,
        OracleTimeStampLTZ t => t.Value,
        OracleTimeStampTZ t => new DateTimeOffset(t.Value, t.GetTimeZoneOffset()),
        OracleIntervalDS t => t.Value,
        OracleIntervalYM t => t.ToString(),
        OracleBinary b => b.Value,
        OracleBoolean b => b.Value,
        OracleClob c => ReadAllText(c),
        OracleBlob b => b.Value,
        OracleXmlType x => x.Value,
        _ => value,
    };

    public static string? ToText(object? value) => Normalize(value) switch
    {
        null => null,
        string s => s,
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        var v => v.ToString(),
    };

    public static int ToInt(object? value) => Normalize(value) switch
    {
        null => 0,
        decimal d => (int)d,
        string s => int.Parse(s, CultureInfo.InvariantCulture),
        var v => Convert.ToInt32(v, CultureInfo.InvariantCulture),
    };

    /// <summary>decimal(28자리) 안에 들면 decimal, 아니면 드라이버 원시 문자열(정밀도 보존).</summary>
    public static object FromDecimal(OracleDecimal d)
    {
        var text = d.ToString();
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

    private static LobValue ClobValue(OracleClob clob, int lobPreviewSize)
    {
        var length = clob.Length / 2; // 바이트 → 문자(UCS-2)
        var preview = new char[(int)Math.Min(length, lobPreviewSize)];
        var read = preview.Length == 0 ? 0 : clob.Read(preview, 0, preview.Length);
        return new LobValue(clob.IsNClob ? LobKind.NClob : LobKind.Clob, length)
        {
            TextPreview = new string(preview, 0, read),
            IsTruncated = length > read,
            Locator = clob,
        };
    }

    private static LobValue BlobValue(OracleBlob blob, int lobPreviewSize)
    {
        var length = blob.Length;
        var preview = new byte[(int)Math.Min(length, lobPreviewSize)];
        var read = preview.Length == 0 ? 0 : blob.Read(preview, 0, preview.Length);
        return new LobValue(LobKind.Blob, length)
        {
            BinaryPreview = preview.AsMemory(0, read),
            IsTruncated = length > read,
            Locator = blob,
        };
    }

    private static string ReadAllText(OracleClob clob)
    {
        try
        {
            return clob.Value;
        }
        finally
        {
            clob.Dispose();
        }
    }

    private static T? Get<T>(DataRow row, string column)
        where T : struct
    {
        if (!row.Table.Columns.Contains(column) || row[column] is DBNull or null)
        {
            return null;
        }

        try
        {
            return (T)Convert.ChangeType(row[column], typeof(T), CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException)
        {
            return null;
        }
    }

    private static string? GetString(DataRow row, string column) =>
        row.Table.Columns.Contains(column) && row[column] is string s && s.Length > 0 ? s : null;
}
