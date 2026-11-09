using System.Data;
using System.Data.Common;
using System.Globalization;
using Oranda.Core.Grid;

namespace Oranda.Tibero.Results;

/// <summary>
/// 리더 스키마 표 → <see cref="ResultColumn"/> 목록(R1). 표준 <see cref="DbDataReader"/> 만 쓴다(드라이버 형식 없음).
/// 규칙 근거 = docs/design/tibero-driver-values.md 1·5장.
/// <list type="bullet">
/// <item>형식은 스키마 표의 <c>ProviderType</c>(<c>TiberoDbType</c> 정수)으로 판정한다. ROWID 열은 <c>IsRowID</c> 로 알아본다(드라이버는 VARCHAR2 로 알린다).</item>
/// <item>스키마 표를 못 읽거나 그 열의 <c>ProviderType</c> 이 비면 <c>GetDataTypeName</c> 이름(없으면 <c>GetFieldType</c>)으로 같은 <c>ProviderType</c> 을 짐작한다
/// (<see cref="Resolve"/>). 그래야 BINARY_FLOAT/DOUBLE·LONG·LONG RAW 의 특별 읽기가 우회되지 않는다. 이름도 형식도 모르는 열은 기타 분류로 두어
/// <c>GetValue</c> 로 읽고 안전하게 글자로 바꾼다.</item>
/// <item>NUMBER 의 정밀도·스케일 -128(미지정)은 null 로 둔다.</item>
/// <item>출처(<see cref="ResultColumn.BaseOwner"/>·<see cref="ResultColumn.BaseTable"/>·<see cref="ResultColumn.BaseColumn"/>)는 채우지 않는다:
/// 드라이버가 모든 열에 FROM 첫 표 이름을 넣고 열 이름 자리에 별칭을 넣어 믿을 수 없다. 결과 편집(R4)이 문장을 해석해 확인한 뒤 채운다.</item>
/// </list>
/// </summary>
internal static class TiberoColumns
{
    /// <summary>NUMBER 정밀도·스케일이 지정되지 않았을 때 스키마 표가 주는 값.</summary>
    private const int Unspecified = -128;

    public static IReadOnlyList<ResultColumn> Read(DbDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return Read(SchemaOrNull(reader.GetSchemaTable), reader.FieldCount, reader.GetName, TypeNameOf(reader), FieldTypeOf(reader));
    }

    public static IReadOnlyList<ResultColumn> Read(DataTable? schema, int fieldCount, Func<int, string> name,
                                                   Func<int, string?>? dataTypeName = null, Func<int, Type?>? fieldType = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        var columns = new List<ResultColumn>(fieldCount);
        for (var i = 0; i < fieldCount; i++)
        {
            DataRow? row = schema is not null && i < schema.Rows.Count ? schema.Rows[i] : null;
            var (providerType, isRowId) = Resolve(schema, i, dataTypeName, fieldType);
            var (dbType, kind) = TiberoColumnTypes.Describe(providerType);
            if (isRowId)
            {
                (dbType, kind) = (TiberoColumnTypes.RowIdTypeName, ColumnDataKind.RowId);
            }

            columns.Add(new ResultColumn(i, name(i), dbType, kind)
            {
                Size = row is null ? null : Get<int>(row, "ColumnSize"),
                Precision = row is null ? null : Specified(Get<int>(row, "NumericPrecision")),
                Scale = row is null ? null : Specified(Get<int>(row, "NumericScale")),
                IsNullable = row is null || Get<bool>(row, "AllowDBNull") != false,
            });
        }

        return columns;
    }

    /// <summary>
    /// 열의 <c>ProviderType</c> 과 ROWID 여부. 스키마 표의 값이 있으면 그것(표가 정본). 없으면(표를 못 읽음·그 열 값이 빔)
    /// <paramref name="dataTypeName"/>(<c>GetDataTypeName</c>) 이름, 그것도 모르면 <paramref name="fieldType"/>(<c>GetFieldType</c>)이 글자일 때 VARCHAR2.
    /// 두 힌트가 없거나 둘 다 답하지 못하면 예전처럼 VARCHAR2. 힌트가 답했는데 이름·형식을 모르는 열은 <see cref="UnknownProviderType"/>
    /// (<c>GetValue</c> 로 읽고 안전하게 글자로 바꾼다).
    /// </summary>
    internal static (int ProviderType, bool IsRowId) Resolve(DataTable? schema, int ordinal,
                                                             Func<int, string?>? dataTypeName, Func<int, Type?>? fieldType)
    {
        if (schema is not null && ordinal < schema.Rows.Count)
        {
            var row = schema.Rows[ordinal];
            if (Get<int>(row, "ProviderType") is { } providerType)
            {
                return (providerType, Get<bool>(row, "IsRowID") == true);
            }
        }

        var name = dataTypeName?.Invoke(ordinal);
        if (ProviderTypeOfName(name) is { } known)
        {
            return known;
        }

        var type = fieldType?.Invoke(ordinal);
        return type == typeof(string) || (name is null && type is null)
            ? (TiberoColumnTypes.PtVarchar2, false)
            : (UnknownProviderType, false);
    }

    /// <summary>이름도 형식도 모르는 열의 <c>ProviderType</c>. <see cref="TiberoColumnTypes.Describe"/> 는 이를 <c>UNKNOWN(0)</c>·기타 분류로 본다.</summary>
    internal const int UnknownProviderType = 0;

    /// <summary>
    /// <c>GetDataTypeName</c> 이름(docs/design/tibero-driver-values.md 1장: <c>Decimal</c>·<c>BinaryFloat</c>·<c>TimeStampLTZ</c>·<c>RowID</c>…)이나
    /// 서버 형식 이름(<c>BINARY_FLOAT</c>·<c>LONG RAW</c>·<c>TIMESTAMP(6)</c>)을 <c>ProviderType</c> 으로. 대소문자·공백·밑줄·괄호 안은 가리지 않는다. 모르면 null.
    /// </summary>
    internal static (int ProviderType, bool IsRowId)? ProviderTypeOfName(string? dataTypeName)
    {
        if (string.IsNullOrWhiteSpace(dataTypeName) || dataTypeName.Contains('[', StringComparison.Ordinal)) // 배열 모양(Byte[]) 이름은 형식 이름이 아니다
        {
            return null;
        }

        var paren = dataTypeName.IndexOf('(', StringComparison.Ordinal);
        var head = paren < 0 ? dataTypeName : dataTypeName[..paren];
        var key = new string([.. head.Where(char.IsAsciiLetterOrDigit).Select(char.ToUpperInvariant)]);
        return key switch
        {
            "ROWID" => (TiberoColumnTypes.PtVarchar2, true),
            "VARCHAR2" or "VARCHAR" => (TiberoColumnTypes.PtVarchar2, false),
            "NVARCHAR2" or "NVARCHAR" => (TiberoColumnTypes.PtNVarchar2, false),
            "CHAR" => (TiberoColumnTypes.PtChar, false),
            "NCHAR" => (TiberoColumnTypes.PtNChar, false),
            "LONG" => (TiberoColumnTypes.PtLong, false),
            "LONGRAW" => (TiberoColumnTypes.PtLongRaw, false),
            "DECIMAL" or "NUMBER" or "FLOAT" or "DOUBLEPRECISION" or "NUMERIC" => (TiberoColumnTypes.PtDecimal, false),
            "BYTE" => (TiberoColumnTypes.PtByte, false),
            "INT16" => (TiberoColumnTypes.PtInt16, false),
            "INT32" => (TiberoColumnTypes.PtInt32, false),
            "INT64" => (TiberoColumnTypes.PtInt64, false),
            "SINGLE" => (TiberoColumnTypes.PtSingle, false),
            "DOUBLE" => (TiberoColumnTypes.PtDouble, false),
            "BINARYFLOAT" => (TiberoColumnTypes.PtBinaryFloat, false),
            "BINARYDOUBLE" => (TiberoColumnTypes.PtBinaryDouble, false),
            "DATE" => (TiberoColumnTypes.PtDate, false),
            "TIMESTAMP" => (TiberoColumnTypes.PtTimeStamp, false),
            "TIMESTAMPLTZ" or "TIMESTAMPWITHLOCALTIMEZONE" => (TiberoColumnTypes.PtTimeStampLTZ, false),
            "TIMESTAMPTZ" or "TIMESTAMPWITHTIMEZONE" => (TiberoColumnTypes.PtTimeStampTZ, false),
            "INTERVALYM" or "INTERVALYEARTOMONTH" => (TiberoColumnTypes.PtIntervalYM, false),
            "INTERVALDS" or "INTERVALDAYTOSECOND" => (TiberoColumnTypes.PtIntervalDS, false),
            "CLOB" => (TiberoColumnTypes.PtClob, false),
            "NCLOB" => (TiberoColumnTypes.PtNClob, false),
            "BLOB" => (TiberoColumnTypes.PtBlob, false),
            "BFILE" => (TiberoColumnTypes.PtBFile, false),
            "RAW" => (TiberoColumnTypes.PtRaw, false),
            "BOOLEAN" => (TiberoColumnTypes.PtBoolean, false),
            "XMLTYPE" => (TiberoColumnTypes.PtXmlType, false),
            "REFCURSOR" => (TiberoColumnTypes.PtRefCursor, false),
            _ => null,
        };
    }

    /// <summary>리더의 <c>GetDataTypeName</c>. 못 읽으면 null(이 열은 힌트 없음).</summary>
    internal static Func<int, string?> TypeNameOf(DbDataReader reader) => i => Hint(() => reader.GetDataTypeName(i));

    /// <summary>리더의 <c>GetFieldType</c>. 못 읽으면 null.</summary>
    internal static Func<int, Type?> FieldTypeOf(DbDataReader reader) => i => Hint(() => reader.GetFieldType(i));

    private static T? Hint<T>(Func<T> read)
        where T : class
    {
        try
        {
            return read();
        }
        catch (Exception e) when (e is DbException or InvalidOperationException or NotSupportedException or IndexOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>ProviderType 정수(스키마 표). 알 수 없으면 null.</summary>
    internal static int? ProviderTypeOf(DataTable? schema, int ordinal) =>
        schema is not null && ordinal < schema.Rows.Count ? Get<int>(schema.Rows[ordinal], "ProviderType") : null;

    /// <summary>
    /// 스키마 표 조회. 열 출처·ROWID 표시는 덤이므로 실패하면 건너뛰고 본 결과는 그대로 보여 준다
    /// (Oracle 쪽은 <c>VERSIONS BETWEEN</c> 같은 문장에서 보조 질의가 실패했다. Tibero 는 실측 못 했으나 같은 방어를 둔다).
    /// </summary>
    internal static DataTable? SchemaOrNull(Func<DataTable?> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        try
        {
            return read();
        }
        catch (Exception e) when (e is DbException or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    private static int? Specified(int? value) => value is null or Unspecified or < 0 ? null : value;

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
}
