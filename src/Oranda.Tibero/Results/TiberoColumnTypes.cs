using Oranda.Core.Grid;

namespace Oranda.Tibero.Results;

/// <summary>
/// Tibero 열 형식(<c>TiberoDbType</c> 정수, 스키마 표의 <c>ProviderType</c>) → (서버 타입 이름, 표시 분류). 드라이버 형식을 참조하지 않으려
/// 정수로 받는다(값은 TDP.NET 1.1.35 의 <c>TiberoDbType</c> 열거값, docs/design/tibero-driver-values.md 1장).
/// </summary>
internal static class TiberoColumnTypes
{
    public const int PtBFile = 101;
    public const int PtBlob = 102;
    public const int PtByte = 103;
    public const int PtChar = 104;
    public const int PtClob = 105;
    public const int PtDate = 106;
    public const int PtDecimal = 107;
    public const int PtDouble = 108;
    public const int PtLong = 109;
    public const int PtLongRaw = 110;
    public const int PtInt16 = 111;
    public const int PtInt32 = 112;
    public const int PtInt64 = 113;
    public const int PtIntervalDS = 114;
    public const int PtIntervalYM = 115;
    public const int PtNClob = 116;
    public const int PtNChar = 117;
    public const int PtNVarchar2 = 119;
    public const int PtRaw = 120;
    public const int PtRefCursor = 121;
    public const int PtSingle = 122;
    public const int PtTimeStamp = 123;
    public const int PtTimeStampLTZ = 124;
    public const int PtTimeStampTZ = 125;
    public const int PtVarchar2 = 126;
    public const int PtXmlType = 127;
    public const int PtArray = 128;
    public const int PtObject = 129;
    public const int PtRef = 130;
    public const int PtBinaryDouble = 132;
    public const int PtBinaryFloat = 133;
    public const int PtBoolean = 134;

    /// <summary>ROWID 열의 서버 타입 이름(스키마 표 <c>IsRowID</c> 로 알아본다. 드라이버는 VARCHAR2 로 알린다).</summary>
    public const string RowIdTypeName = "ROWID";

    public static (string DbTypeName, ColumnDataKind Kind) Describe(int providerType) => providerType switch
    {
        PtVarchar2 => ("VARCHAR2", ColumnDataKind.Text),
        PtNVarchar2 => ("NVARCHAR2", ColumnDataKind.Text),
        PtChar => ("CHAR", ColumnDataKind.Text),
        PtNChar => ("NCHAR", ColumnDataKind.Text),
        PtLong => ("LONG", ColumnDataKind.Text),
        PtDecimal or PtByte or PtInt16 or PtInt32 or PtInt64 or PtDouble or PtSingle => ("NUMBER", ColumnDataKind.Number),
        PtBinaryFloat => ("BINARY_FLOAT", ColumnDataKind.BinaryFloat),
        PtBinaryDouble => ("BINARY_DOUBLE", ColumnDataKind.BinaryFloat),
        PtDate => ("DATE", ColumnDataKind.Date),
        PtTimeStamp => ("TIMESTAMP", ColumnDataKind.Timestamp),
        PtTimeStampLTZ => ("TIMESTAMP WITH LOCAL TIME ZONE", ColumnDataKind.Timestamp),
        PtTimeStampTZ => ("TIMESTAMP WITH TIME ZONE", ColumnDataKind.TimestampTz),
        PtIntervalYM => ("INTERVAL YEAR TO MONTH", ColumnDataKind.IntervalYearMonth),
        PtIntervalDS => ("INTERVAL DAY TO SECOND", ColumnDataKind.IntervalDaySecond),
        PtClob => ("CLOB", ColumnDataKind.Clob),
        PtNClob => ("NCLOB", ColumnDataKind.Clob),
        PtBlob => ("BLOB", ColumnDataKind.Blob),
        PtBFile => ("BFILE", ColumnDataKind.Blob),
        PtRaw => ("RAW", ColumnDataKind.Raw),
        PtLongRaw => ("LONG RAW", ColumnDataKind.Raw),
        PtBoolean => ("BOOLEAN", ColumnDataKind.Boolean),
        PtXmlType => ("XMLTYPE", ColumnDataKind.Xml),
        PtArray or PtObject or PtRef => ("OBJECT", ColumnDataKind.UserDefinedType),
        PtRefCursor => ("REF CURSOR", ColumnDataKind.RefCursor),
        _ => ("UNKNOWN(" + providerType + ")", ColumnDataKind.Other),
    };

    /// <summary>NUMBER 로 보는 형식 중 드라이버가 정수·실수 .NET 형식으로 주는 것(원시 바이트가 아니라 <c>GetValue</c> 로 읽는다).</summary>
    public static bool IsNativeNumeric(int providerType) => providerType is PtByte or PtInt16 or PtInt32 or PtInt64 or PtDouble or PtSingle;
}
