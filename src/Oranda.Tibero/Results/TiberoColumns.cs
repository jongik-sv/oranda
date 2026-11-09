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
        return Read(SchemaOrNull(reader.GetSchemaTable), reader.FieldCount, reader.GetName);
    }

    public static IReadOnlyList<ResultColumn> Read(DataTable? schema, int fieldCount, Func<int, string> name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var columns = new List<ResultColumn>(fieldCount);
        for (var i = 0; i < fieldCount; i++)
        {
            DataRow? row = schema is not null && i < schema.Rows.Count ? schema.Rows[i] : null;
            var providerType = row is not null && Get<int>(row, "ProviderType") is { } pt ? pt : TiberoColumnTypes.PtVarchar2;
            var (dbType, kind) = TiberoColumnTypes.Describe(providerType);
            if (row is not null && Get<bool>(row, "IsRowID") == true)
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
