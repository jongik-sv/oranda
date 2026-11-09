using Oranda.Core.Execution;
using Oranda.Core.Transfer.Copy;
using Oranda.Tibero.Driver;
using Oranda.Tibero.Execution;
using Oranda.Tibero.Results;

namespace Oranda.Tibero.Tests.Execution;

// 소유: TB1 커밋 2. 대량 경로 형식표(TB1 설계 2.1)와 리더 열 → 읽는 법(1.2). 드라이버 없음.
public class TiberoBulkTypesTests
{
    [Theory]
    [InlineData("TIMESTAMP(6)", "TIMESTAMP")]
    [InlineData("VARCHAR2(10 CHAR)", "VARCHAR2")]
    [InlineData("NUMBER(38,10)", "NUMBER")]
    [InlineData("varchar", "VARCHAR")]
    [InlineData("  TIMESTAMP(9)   WITH  TIME ZONE ", "TIMESTAMP WITH TIME ZONE")]
    [InlineData("INTERVAL DAY(2) TO SECOND(6)", "INTERVAL DAY TO SECOND")]
    [InlineData("INTERVAL YEAR(9) TO MONTH", "INTERVAL YEAR TO MONTH")]
    [InlineData("INTERVAL DAY(9) TO SECOND(9)", "INTERVAL DAY TO SECOND")]
    [InlineData("LONG RAW", "LONG RAW")]
    public void Server_type_names_are_normalized(string serverType, string expected)
    {
        Assert.Equal(expected, TiberoBulkTypes.Normalize(serverType));
    }

    // 형식, 지원, 갈래, 키 바인드, 매개변수 종류, 감싸기, 배열
    [Theory]
    [InlineData("NUMBER(38,10)", nameof(TiberoBulkType.Number), true, BulkKind.Other, BindDataType.Number, nameof(TiberoParamKind.Decimal), false, true)]
    [InlineData("FLOAT(126)", nameof(TiberoBulkType.Number), true, BulkKind.Other, BindDataType.Number, nameof(TiberoParamKind.Decimal), false, true)]
    [InlineData("VARCHAR2(10 CHAR)", nameof(TiberoBulkType.Varchar2), true, BulkKind.Text, BindDataType.Varchar2, nameof(TiberoParamKind.Varchar2), false, true)]
    [InlineData("VARCHAR", nameof(TiberoBulkType.Varchar2), true, BulkKind.Text, BindDataType.Varchar2, nameof(TiberoParamKind.Varchar2), false, true)]
    [InlineData("CHAR(5)", nameof(TiberoBulkType.Char), true, BulkKind.Text, BindDataType.FixedChar, nameof(TiberoParamKind.Varchar2), false, true)]
    [InlineData("NVARCHAR2(20)", nameof(TiberoBulkType.NVarchar2), true, BulkKind.Text, BindDataType.NVarchar2, nameof(TiberoParamKind.NVarchar2), false, true)]
    [InlineData("NVARCHAR", nameof(TiberoBulkType.NVarchar2), true, BulkKind.Text, BindDataType.NVarchar2, nameof(TiberoParamKind.NVarchar2), false, true)]
    [InlineData("NCHAR(2)", nameof(TiberoBulkType.NChar), true, BulkKind.Text, BindDataType.NVarchar2, nameof(TiberoParamKind.NVarchar2), false, true)]
    [InlineData("CLOB", nameof(TiberoBulkType.Clob), true, BulkKind.Clob, null, nameof(TiberoParamKind.Clob), false, true)]
    [InlineData("NCLOB", nameof(TiberoBulkType.NClob), true, BulkKind.Clob, null, nameof(TiberoParamKind.NClob), false, true)]
    [InlineData("BLOB", nameof(TiberoBulkType.Blob), true, BulkKind.Blob, null, nameof(TiberoParamKind.Blob), false, false)]
    [InlineData("DATE", nameof(TiberoBulkType.Date), true, BulkKind.Other, BindDataType.Date, nameof(TiberoParamKind.Varchar2), true, true)]
    [InlineData("TIMESTAMP(9)", nameof(TiberoBulkType.Timestamp), true, BulkKind.Other, BindDataType.Timestamp, nameof(TiberoParamKind.Varchar2), true, true)]
    [InlineData("RAW(2000)", nameof(TiberoBulkType.Raw), true, BulkKind.Other, BindDataType.Raw, nameof(TiberoParamKind.Varchar2), true, true)]
    [InlineData("INTERVAL YEAR TO MONTH", nameof(TiberoBulkType.IntervalYm), true, BulkKind.Other, null, nameof(TiberoParamKind.Varchar2), true, true)]
    [InlineData("INTERVAL YEAR(9) TO MONTH", nameof(TiberoBulkType.IntervalYm), true, BulkKind.Other, null, nameof(TiberoParamKind.Varchar2), true, true)]
    [InlineData("INTERVAL DAY TO SECOND", nameof(TiberoBulkType.IntervalDs), true, BulkKind.Other, null, nameof(TiberoParamKind.Varchar2), true, true)]
    [InlineData("INTERVAL DAY(9) TO SECOND(9)", nameof(TiberoBulkType.IntervalDs), true, BulkKind.Other, null, nameof(TiberoParamKind.Varchar2), true, true)]
    public void Measured_types_are_supported(string serverType, string type, bool supported, BulkKind kind, BindDataType? keyBind,
                                             string paramKind, bool wrapped, bool arrayBind)
    {
        var shape = TiberoBulkTypes.ShapeOf(serverType);
        Assert.Equal(Enum.Parse<TiberoBulkType>(type), shape.Type);
        Assert.Equal(supported, shape.IsSupported);
        Assert.Equal(kind, shape.Kind);
        Assert.Equal(keyBind, shape.KeyBind);
        Assert.Equal(Enum.Parse<TiberoParamKind>(paramKind), shape.ParamKind);
        Assert.Equal(wrapped, shape.IsWrapped);
        Assert.Equal(arrayBind, shape.ArrayBind);
    }

    [Theory]
    [InlineData("BINARY_FLOAT", nameof(TiberoBulkType.BinaryFloat))]
    [InlineData("BINARY_DOUBLE", nameof(TiberoBulkType.BinaryDouble))]
    [InlineData("TIMESTAMP(6) WITH TIME ZONE", nameof(TiberoBulkType.TimestampTz))]
    [InlineData("TIMESTAMP(6) WITH LOCAL TIME ZONE", nameof(TiberoBulkType.TimestampLtz))]
    [InlineData("LONG", nameof(TiberoBulkType.Long))]
    [InlineData("LONG RAW", nameof(TiberoBulkType.LongRaw))]
    public void Unmeasured_types_are_known_but_off(string serverType, string type)
    {
        var shape = TiberoBulkTypes.ShapeOf(serverType);
        Assert.Equal(Enum.Parse<TiberoBulkType>(type), shape.Type);
        Assert.False(shape.IsSupported); // Q2: IT 측정이 녹색일 때만 켠다
    }

    [Theory]
    [InlineData("XMLTYPE")]
    [InlineData("BFILE")]
    [InlineData("ROWID")]
    [InlineData("SDO_GEOMETRY")]
    [InlineData("")]
    public void Other_types_are_unknown(string serverType)
    {
        var shape = TiberoBulkTypes.ShapeOf(serverType);
        Assert.Equal(TiberoBulkType.Unknown, shape.Type);
        Assert.False(shape.IsSupported);
        Assert.Null(shape.Kind);
        Assert.Null(shape.KeyBind);
    }

    [Fact]
    public void Wrap_puts_the_bind_inside_the_conversion_function()
    {
        Assert.Equal("TO_DATE(:C2, 'YYYY-MM-DD HH24:MI:SS')", TiberoBulkTypes.ShapeOf("DATE").Wrap("C2"));
        Assert.Equal("TO_TIMESTAMP(:C1, 'YYYY-MM-DD HH24:MI:SS.FF9')", TiberoBulkTypes.ShapeOf("TIMESTAMP(6)").Wrap("C1"));
        Assert.Equal("HEXTORAW(:C3)", TiberoBulkTypes.ShapeOf("RAW(16)").Wrap("C3"));
        Assert.Equal("TO_YMINTERVAL(:C7)", TiberoBulkTypes.ShapeOf("INTERVAL YEAR(9) TO MONTH").Wrap("C7"));
        Assert.Equal("TO_DSINTERVAL(:C8)", TiberoBulkTypes.ShapeOf("INTERVAL DAY(9) TO SECOND(9)").Wrap("C8"));
        Assert.Equal(":C4", TiberoBulkTypes.ShapeOf("NUMBER").Wrap("C4"));
        Assert.Equal(":C5", TiberoBulkTypes.ShapeOf("CLOB").Wrap("C5"));
        Assert.Equal(":C6", TiberoBulkTypes.ShapeOf("BLOB").Wrap("C6"));
        Assert.Throws<ArgumentException>(() => TiberoBulkTypes.ShapeOf("DATE").Wrap(""));
    }

    [Theory]
    [InlineData(TiberoColumnTypes.PtDecimal, nameof(TiberoBulkReadKind.Number))]
    [InlineData(TiberoColumnTypes.PtInt64, nameof(TiberoBulkReadKind.Number))]
    [InlineData(TiberoColumnTypes.PtDouble, nameof(TiberoBulkReadKind.Number))]
    [InlineData(TiberoColumnTypes.PtSingle, nameof(TiberoBulkReadKind.Number))]
    [InlineData(TiberoColumnTypes.PtByte, nameof(TiberoBulkReadKind.Number))]
    [InlineData(TiberoColumnTypes.PtBinaryFloat, nameof(TiberoBulkReadKind.BinaryFloat))]
    [InlineData(TiberoColumnTypes.PtBinaryDouble, nameof(TiberoBulkReadKind.BinaryDouble))]
    [InlineData(TiberoColumnTypes.PtVarchar2, nameof(TiberoBulkReadKind.Text))]
    [InlineData(TiberoColumnTypes.PtChar, nameof(TiberoBulkReadKind.Text))]
    [InlineData(TiberoColumnTypes.PtNVarchar2, nameof(TiberoBulkReadKind.Text))]
    [InlineData(TiberoColumnTypes.PtNChar, nameof(TiberoBulkReadKind.Text))]
    [InlineData(TiberoColumnTypes.PtLong, nameof(TiberoBulkReadKind.Long))]
    [InlineData(TiberoColumnTypes.PtClob, nameof(TiberoBulkReadKind.Clob))]
    [InlineData(TiberoColumnTypes.PtNClob, nameof(TiberoBulkReadKind.Clob))]
    [InlineData(TiberoColumnTypes.PtBlob, nameof(TiberoBulkReadKind.Blob))]
    [InlineData(TiberoColumnTypes.PtRaw, nameof(TiberoBulkReadKind.Raw))]
    [InlineData(TiberoColumnTypes.PtLongRaw, nameof(TiberoBulkReadKind.LongRaw))]
    [InlineData(TiberoColumnTypes.PtDate, nameof(TiberoBulkReadKind.Date))]
    [InlineData(TiberoColumnTypes.PtTimeStamp, nameof(TiberoBulkReadKind.Timestamp))]
    [InlineData(TiberoColumnTypes.PtTimeStampTZ, nameof(TiberoBulkReadKind.TimestampTz))]
    [InlineData(TiberoColumnTypes.PtTimeStampLTZ, nameof(TiberoBulkReadKind.TimestampLtz))]
    [InlineData(TiberoColumnTypes.PtIntervalYM, nameof(TiberoBulkReadKind.IntervalYm))]
    [InlineData(TiberoColumnTypes.PtIntervalDS, nameof(TiberoBulkReadKind.IntervalDs))]
    public void Provider_types_map_to_read_kinds(int providerType, string kind)
    {
        Assert.Equal(Enum.Parse<TiberoBulkReadKind>(kind), TiberoBulkTypes.ReadKindOf(providerType, isRowId: false, "COL"));
    }

    [Theory]
    [InlineData(TiberoColumnTypes.PtBFile, "BFILE")]
    [InlineData(TiberoColumnTypes.PtXmlType, "XMLTYPE")]
    [InlineData(TiberoColumnTypes.PtBoolean, "BOOLEAN")]
    [InlineData(TiberoColumnTypes.PtObject, "OBJECT")]
    [InlineData(TiberoColumnTypes.PtRefCursor, "REF CURSOR")]
    [InlineData(999, "UNKNOWN(999)")]
    public void Unreadable_provider_types_name_the_column(int providerType, string typeName)
    {
        var ex = Assert.Throws<NotSupportedException>(() => TiberoBulkTypes.ReadKindOf(providerType, isRowId: false, "DOC"));
        Assert.Equal($"데이터 복사가 지원하지 않는 열 형식입니다: DOC ({typeName})", ex.Message);
    }

    [Fact]
    public void Rowid_column_is_not_read_as_text()
    {
        // 드라이버는 ROWID 를 VARCHAR2(126) 로 알린다 — 스키마 IsRowID 로 막는다.
        var ex = Assert.Throws<NotSupportedException>(() => TiberoBulkTypes.ReadKindOf(TiberoColumnTypes.PtVarchar2, isRowId: true, "RID"));
        Assert.Contains("RID (ROWID)", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Decimal", nameof(TiberoBulkReadKind.Number))]
    [InlineData("Int64", nameof(TiberoBulkReadKind.Number))]
    [InlineData("BinaryFloat", nameof(TiberoBulkReadKind.BinaryFloat))]
    [InlineData("BINARY_DOUBLE", nameof(TiberoBulkReadKind.BinaryDouble))]
    [InlineData("Varchar2", nameof(TiberoBulkReadKind.Text))]
    [InlineData("NChar", nameof(TiberoBulkReadKind.Text))]
    [InlineData("Clob", nameof(TiberoBulkReadKind.Clob))]
    [InlineData("NClob", nameof(TiberoBulkReadKind.Clob))]
    [InlineData("Blob", nameof(TiberoBulkReadKind.Blob))]
    [InlineData("Raw", nameof(TiberoBulkReadKind.Raw))]
    [InlineData("Date", nameof(TiberoBulkReadKind.Date))]
    [InlineData("TimeStamp", nameof(TiberoBulkReadKind.Timestamp))]
    [InlineData("TimeStampTZ", nameof(TiberoBulkReadKind.TimestampTz))]
    [InlineData("TimeStampLTZ", nameof(TiberoBulkReadKind.TimestampLtz))]
    [InlineData("IntervalYM", nameof(TiberoBulkReadKind.IntervalYm))]
    [InlineData("IntervalDS", nameof(TiberoBulkReadKind.IntervalDs))]
    public void Data_type_names_are_the_fallback(string dataTypeName, string kind)
    {
        Assert.Equal(Enum.Parse<TiberoBulkReadKind>(kind), TiberoBulkTypes.ReadKindOf(dataTypeName, "COL"));
    }

    [Theory]
    [InlineData("RowID")]
    [InlineData("Boolean")]
    [InlineData("XmlType")]
    public void Unknown_data_type_names_name_the_column(string dataTypeName)
    {
        var ex = Assert.Throws<NotSupportedException>(() => TiberoBulkTypes.ReadKindOf(dataTypeName, "X"));
        Assert.Equal($"데이터 복사가 지원하지 않는 열 형식입니다: X ({dataTypeName})", ex.Message);
    }
}
