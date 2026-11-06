using Oracle.ManagedDataAccess.Client;
using Oranda.Oracle.Execution;

namespace Oranda.Oracle.Tests.Execution;

// 소유: 데이터 복사 Task 6. 서버 형식(ColumnInfo.DataType) → 바인드 배열 형식. 1차 지원 밖은 NotSupportedException(설계 8.3).
public class BulkBindsTests
{
    [Theory]
    [InlineData("TIMESTAMP(6) WITH LOCAL TIME ZONE", OracleDbType.TimeStampLTZ)]
    [InlineData("TIMESTAMP(9) WITH TIME ZONE", OracleDbType.TimeStampTZ)]
    [InlineData("TIMESTAMP(3)", OracleDbType.TimeStamp)]
    [InlineData("INTERVAL DAY(2) TO SECOND(6)", OracleDbType.IntervalDS)]
    [InlineData("INTERVAL YEAR(2) TO MONTH", OracleDbType.IntervalYM)]
    [InlineData("BINARY_FLOAT", OracleDbType.BinaryFloat)]
    [InlineData("BINARY_DOUBLE", OracleDbType.BinaryDouble)]
    [InlineData("NUMBER", OracleDbType.Decimal)]
    [InlineData("varchar2(10 char)", OracleDbType.Varchar2)]
    [InlineData("NCHAR", OracleDbType.NChar)]
    [InlineData("NCLOB", OracleDbType.NClob)]
    [InlineData("BLOB", OracleDbType.Blob)]
    [InlineData("RAW", OracleDbType.Raw)]
    [InlineData("DATE", OracleDbType.Date)]
    public void Maps_server_types_to_db_types(string serverType, OracleDbType expected)
    {
        Assert.Equal(expected, BulkBinds.ToDbType(serverType));
    }

    [Theory]
    [InlineData("XMLTYPE")]
    [InlineData("LONG")]
    [InlineData("BFILE")]
    [InlineData("VECTOR")]
    public void Unsupported_server_types_throw(string serverType)
    {
        Assert.Throws<NotSupportedException>(() => BulkBinds.ToDbType(serverType));
    }

    [Theory]
    [InlineData("Decimal", "Number")]
    [InlineData("Single", "Number")] // 정밀도가 작은 NUMBER 도 OracleDecimal 로 읽는다
    [InlineData("Double", "Number")]
    [InlineData("Int32", "Number")]
    [InlineData("BinaryFloat", "BinaryFloat")]
    [InlineData("BinaryDouble", "BinaryDouble")]
    [InlineData("NClob", "Clob")]
    [InlineData("NChar", "Text")]
    [InlineData("Raw", "Raw")]
    [InlineData("TimeStampLTZ", "TimestampLtz")]
    [InlineData("IntervalDS", "IntervalDs")]
    [InlineData("IntervalYM", "IntervalYm")]
    public void Maps_reader_types_to_read_kinds(string dataTypeName, string expected)
    {
        Assert.Equal(expected, BulkBinds.ReadKind(dataTypeName).ToString());
    }

    [Theory]
    [InlineData("Long")]
    [InlineData("LongRaw")]
    [InlineData("LONG RAW")]
    [InlineData("BFile")]
    [InlineData("XmlType")]
    public void Long_and_other_unsupported_reader_types_throw(string dataTypeName)
    {
        Assert.Throws<NotSupportedException>(() => BulkBinds.ReadKind(dataTypeName));
    }
}
