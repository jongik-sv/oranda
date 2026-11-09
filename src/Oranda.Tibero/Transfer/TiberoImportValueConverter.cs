using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Grid;
using Oranda.Core.Metadata;
using Oranda.Core.Transfer;

namespace Oranda.Tibero.Transfer;

/// <summary>
/// <see cref="IImportValueConverter"/> 의 Tibero 구현(TB2). 리터럴 문법은 Oracle 과 같은 공통 규칙(<see cref="ImportLiteralBuilder"/>: 서식 명시 <c>TO_DATE</c>·
/// <c>TO_TIMESTAMP</c>·<c>TO_TIMESTAMP_TZ</c>, <c>INTERVAL '…'</c>, <c>HEXTORAW</c>, <c>TO_BLOB(HEXTORAW(…))</c>·<c>TO_CLOB(…)</c>)이고 6.2.15 로 모두 확인했다
/// (docs/design/tibero-driver-transfer.md). 달라지는 것은 열 타입 이름표 뿐이다: Tibero 사전은 <c>VARCHAR</c>(Oracle 의 VARCHAR2)·<c>NVARCHAR</c>,
/// <c>TIMESTAMP(6)</c>·<c>TIMESTAMP(6) WITH TIME ZONE</c>, <c>INTERVAL DAY(3) TO SECOND(0)</c> 처럼 정밀도가 붙은 이름을 낸다.
/// BOOLEAN 열·리터럴은 이 서버에 없어(<c>SELECT TRUE FROM DUAL</c> = ODP-8026) 분류하지 않는다.
/// DBMS 키 서비스로 등록한다(가져오기 대화 상자가 세션 DBMS 로 고른다).
/// </summary>
[OrandaService(typeof(IImportValueConverter), Dbms = Dbms.Tibero)]
internal sealed class TiberoImportValueConverter : IImportValueConverter
{
    /// <summary>
    /// BLOB 리터럴 한도. Tibero 6.2.15 는 <c>TO_BLOB(HEXTORAW(…))</c> 가 RAW 한계 2000바이트까지이고(2001바이트 = ODP-11025) BLOB 끼리 <c>||</c> 도 못 이어
    /// (ODP-11003) 더 큰 값은 리터럴로 만들 수 없다. 그런 행은 가져오기 경고(이유 포함)로 남고 나머지는 들어간다.
    /// </summary>
    internal const int MaxBlobBytes = 2000;

    public ColumnDataKind KindOf(string dataType)
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
            "NUMBER" or "FLOAT" or "INTEGER" or "INT" or "SMALLINT" or "DECIMAL" or "NUMERIC" or "REAL" or "DOUBLE PRECISION"
                or "BINARY_INTEGER" or "PLS_INTEGER" => ColumnDataKind.Number,
            "BINARY_FLOAT" or "BINARY_DOUBLE" => ColumnDataKind.BinaryFloat,
            "CLOB" or "NCLOB" => ColumnDataKind.Clob,
            "BLOB" => ColumnDataKind.Blob,
            "RAW" or "LONG RAW" => ColumnDataKind.Raw,
            "ROWID" or "UROWID" => ColumnDataKind.RowId,
            _ when t.StartsWith("VARCHAR", StringComparison.Ordinal) || t.StartsWith("NVARCHAR", StringComparison.Ordinal)
                   || t.StartsWith("CHAR", StringComparison.Ordinal) || t.StartsWith("NCHAR", StringComparison.Ordinal)
                   || t == "LONG" => ColumnDataKind.Text,
            _ => ColumnDataKind.Other,
        };
    }

    public string ToLiteral(ColumnInfo column, object? cell)
    {
        ArgumentNullException.ThrowIfNull(column);
        return ImportLiteralBuilder.ToLiteral(column, KindOf(column.DataType), cell, MaxBlobBytes);
    }

    public bool TryConvert(ColumnInfo column, object? cell, out string literal, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(column);
        return ImportLiteralBuilder.TryConvert(column, KindOf(column.DataType), cell, out literal, out reason, MaxBlobBytes);
    }
}
