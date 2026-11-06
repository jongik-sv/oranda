using System.Text.RegularExpressions;
using Oracle.ManagedDataAccess.Client;

namespace Oranda.Oracle.Execution;

/// <summary>대량 실행 경로의 서버 형식(<c>ColumnInfo.DataType</c>) → 바인드 형식. 1차 지원 타입만 받는다(설계 8.3).</summary>
internal static partial class BulkBinds
{
    /// <summary>
    /// 정밀도·길이 괄호(<c>TIMESTAMP(6)</c>, <c>INTERVAL DAY(2) TO SECOND(6)</c>, <c>VARCHAR2(10 CHAR)</c>)와 공백 차이를 지우고 대응시킨다.
    /// 그 밖의 형식(LONG, BFILE, XMLTYPE, 객체, VECTOR, JSON 등)은 <see cref="NotSupportedException"/>.
    /// </summary>
    public static OracleDbType ToDbType(string serverType)
    {
        ArgumentNullException.ThrowIfNull(serverType);
        var name = Spaces().Replace(Precision().Replace(serverType, string.Empty), " ").Trim().ToUpperInvariant();
        return name switch
        {
            "VARCHAR2" or "VARCHAR" => OracleDbType.Varchar2,
            "CHAR" => OracleDbType.Char,
            "NVARCHAR2" => OracleDbType.NVarchar2,
            "NCHAR" => OracleDbType.NChar,
            "NUMBER" or "FLOAT" => OracleDbType.Decimal,
            "BINARY_FLOAT" => OracleDbType.BinaryFloat,
            "BINARY_DOUBLE" => OracleDbType.BinaryDouble,
            "DATE" => OracleDbType.Date,
            "TIMESTAMP" => OracleDbType.TimeStamp,
            "TIMESTAMP WITH TIME ZONE" => OracleDbType.TimeStampTZ,
            "TIMESTAMP WITH LOCAL TIME ZONE" => OracleDbType.TimeStampLTZ,
            "INTERVAL DAY TO SECOND" => OracleDbType.IntervalDS,
            "INTERVAL YEAR TO MONTH" => OracleDbType.IntervalYM,
            "RAW" => OracleDbType.Raw,
            "CLOB" => OracleDbType.Clob,
            "NCLOB" => OracleDbType.NClob,
            "BLOB" => OracleDbType.Blob,
            _ => throw new NotSupportedException($"데이터 복사가 지원하지 않는 형식입니다: {serverType}"),
        };
    }

    /// <summary>
    /// 리더 열 형식 이름(<c>OracleDataReader.GetDataTypeName</c>, OracleDbType 이름) → 읽는 방법. 서버 형식 이름(<c>BINARY_FLOAT</c>)도 받는다.
    /// .NET 필드 형식(<c>GetFieldType</c>)으로 고르지 않는다: 정밀도가 작은 NUMBER 를 Single·Double 로 알려 값이 바뀐다.
    /// NUMBER 는 정수·실수 형식으로 알려도 모두 OracleDecimal 로 읽는다. LONG·LONG RAW 와 그 밖의 형식은 <see cref="NotSupportedException"/>(설계 8.3).
    /// </summary>
    public static BulkReadKind ReadKind(string dataTypeName)
    {
        ArgumentNullException.ThrowIfNull(dataTypeName);
        var name = Spaces().Replace(dataTypeName, " ").Trim().ToUpperInvariant();
        return name switch
        {
            "CLOB" or "NCLOB" => BulkReadKind.Clob,
            "BLOB" => BulkReadKind.Blob,
            "RAW" => BulkReadKind.Raw,
            "BINARYFLOAT" or "BINARY_FLOAT" => BulkReadKind.BinaryFloat,
            "BINARYDOUBLE" or "BINARY_DOUBLE" => BulkReadKind.BinaryDouble,
            "DECIMAL" or "NUMBER" or "FLOAT" or "INT16" or "INT32" or "INT64" or "BYTE" or "SINGLE" or "DOUBLE" => BulkReadKind.Number,
            "VARCHAR2" or "VARCHAR" or "NVARCHAR2" or "CHAR" or "NCHAR" => BulkReadKind.Text,
            "DATE" => BulkReadKind.Date,
            "TIMESTAMP" => BulkReadKind.Timestamp,
            "TIMESTAMPTZ" or "TIMESTAMP WITH TIME ZONE" => BulkReadKind.TimestampTz,
            "TIMESTAMPLTZ" or "TIMESTAMP WITH LOCAL TIME ZONE" => BulkReadKind.TimestampLtz,
            "INTERVALDS" or "INTERVAL DAY TO SECOND" => BulkReadKind.IntervalDs,
            "INTERVALYM" or "INTERVAL YEAR TO MONTH" => BulkReadKind.IntervalYm,
            _ => throw new NotSupportedException($"데이터 복사가 지원하지 않는 열 형식입니다: {dataTypeName}"),
        };
    }

    [GeneratedRegex(@"\([^)]*\)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Precision();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Spaces();
}

/// <summary>대량 읽기에서 열 값 하나를 읽는 방법(<see cref="BulkBinds.ReadKind"/>).</summary>
internal enum BulkReadKind
{
    Clob,
    Blob,
    Raw,
    BinaryFloat,
    BinaryDouble,
    Number,
    Text,
    Date,
    Timestamp,
    TimestampTz,
    TimestampLtz,
    IntervalDs,
    IntervalYm,
}
