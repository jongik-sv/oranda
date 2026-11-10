using System.Text.RegularExpressions;
using Oranda.Core.Execution;
using Oranda.Core.Transfer.Copy;
using Oranda.Tibero.Driver;
using Oranda.Tibero.Results;

namespace Oranda.Tibero.Execution;

/// <summary>대량 경로가 아는 Tibero 서버 형식 갈래(<see cref="TiberoBulkTypes.Normalize"/> 뒤의 이름 하나 또는 몇 개).</summary>
internal enum TiberoBulkType
{
    /// <summary>모르는 형식(XMLTYPE·BFILE·ROWID·객체 등).</summary>
    Unknown,

    /// <summary>NUMBER·FLOAT.</summary>
    Number,

    /// <summary>VARCHAR2·VARCHAR.</summary>
    Varchar2,

    /// <summary>CHAR.</summary>
    Char,

    /// <summary>NVARCHAR2·NVARCHAR.</summary>
    NVarchar2,

    /// <summary>NCHAR.</summary>
    NChar,

    Clob,
    NClob,
    Blob,
    Date,

    /// <summary>TIMESTAMP(n).</summary>
    Timestamp,

    Raw,
    BinaryFloat,
    BinaryDouble,
    TimestampTz,
    TimestampLtz,
    IntervalYm,
    IntervalDs,
    Long,
    LongRaw,
}

/// <summary>
/// 서버 형식 하나의 대량 경로 모양(TB1 설계 2.1 표 한 줄). 꺼진 형식(<see cref="IsSupported"/> 거짓)도 후보 값(감싸기·매개변수 종류)을 든다 —
/// IT 측정이 녹색이면 <see cref="IsSupported"/> 만 켠다. 코덱(<c>TiberoBulkValueCodec</c>)은 꺼진 형식의 후보를 쓰지 않는다.
/// </summary>
/// <param name="Type">형식 갈래.</param>
/// <param name="IsSupported">복사 쓰기 지원(실측된 형식만). 사전 점검 <c>UnsupportedType</c> 의 근거.</param>
/// <param name="Kind">마스킹 갈래. 모르는 형식이면 null.</param>
/// <param name="KeyBind">키 되읽기 바인드 형식. 키로 쓸 수 없으면(LOB·LONG 등) null.</param>
/// <param name="ParamKind">쓰기 매개변수 종류. 정하지 않았으면 null.</param>
/// <param name="BindPrefix">값 자리 앞 글(<c>TO_DATE(</c>). 감싸지 않으면 빈 글.</param>
/// <param name="BindSuffix">값 자리 뒤 글(<c>, 'YYYY-MM-DD HH24:MI:SS')</c>). 감싸지 않으면 빈 글.</param>
/// <param name="ArrayBind">배열 바인드(ArrayBindCount)로 보낼 수 있는가. BLOB·LONG RAW 는 아니다(실측 3.1: 이진 배열 NRE) → 그 표는 한 행씩.</param>
internal sealed record TiberoBulkShape(
    TiberoBulkType Type,
    bool IsSupported,
    BulkKind? Kind,
    BindDataType? KeyBind,
    TiberoParamKind? ParamKind,
    string BindPrefix,
    string BindSuffix,
    bool ArrayBind)
{
    /// <summary>값 자리를 변환 함수로 감싸는가(DATE·TIMESTAMP·RAW…). 서비스가 글에 감싼 글이 들어 있는지 확인하는 대상이다.</summary>
    public bool IsWrapped => BindPrefix.Length > 0;

    /// <summary>값 자리 글. <paramref name="bindName"/> 은 쌍점 없는 이름(<c>C1</c>) → <c>TO_DATE(:C1, …)</c> 또는 <c>:C1</c>.</summary>
    public string Wrap(string bindName)
    {
        ArgumentException.ThrowIfNullOrEmpty(bindName);
        return BindPrefix + ":" + bindName + BindSuffix;
    }
}

/// <summary>
/// Tibero 대량 경로의 형식표 정본(TB1 설계 2.1): 서버 형식 이름(<c>ColumnInfo.DataType</c>) → <see cref="TiberoBulkShape"/>, 리더 열(ProviderType) → 읽는 법.
/// 코덱·서비스·Tibero 복사 방언이 이 한 곳을 함께 쓴다.
/// </summary>
internal static partial class TiberoBulkTypes
{
    /// <summary>DATE 값 글자의 서버 서식(<c>yyyy-MM-dd HH:mm:ss</c> 와 짝).</summary>
    public const string DateFormat = "YYYY-MM-DD HH24:MI:SS";

    /// <summary>TIMESTAMP 값 글자의 서버 서식(나노초 9자리, Q3 = FF9).</summary>
    public const string TimestampFormat = "YYYY-MM-DD HH24:MI:SS.FF9";

    private static readonly TiberoBulkShape UnknownShape = new(TiberoBulkType.Unknown, false, null, null, null, "", "", false);

    // 실측(docs/design/tibero-driver-transfer.md 3.1·3.10)으로 정한 것만 IsSupported. 나머지는 후보만 든다.
    private static readonly TiberoBulkShape NumberShape =
        new(TiberoBulkType.Number, true, BulkKind.Other, BindDataType.Number, TiberoParamKind.Decimal, "", "", true);

    private static readonly TiberoBulkShape Varchar2Shape =
        new(TiberoBulkType.Varchar2, true, BulkKind.Text, BindDataType.Varchar2, TiberoParamKind.Varchar2, "", "", true);

    private static readonly TiberoBulkShape CharShape =
        new(TiberoBulkType.Char, true, BulkKind.Text, BindDataType.FixedChar, TiberoParamKind.Varchar2, "", "", true);

    private static readonly TiberoBulkShape NVarchar2Shape =
        new(TiberoBulkType.NVarchar2, true, BulkKind.Text, BindDataType.NVarchar2, TiberoParamKind.NVarchar2, "", "", true);

    // 키 바인드 = NVarchar2: FixedChar 는 DB 문자셋(CP949)으로 보내 그 밖의 글자가 바뀐다(NVARCHAR2 문자셋 실측). 값에 공백 채움이 들어 있어 같은 값으로 찾는다.
    private static readonly TiberoBulkShape NCharShape =
        new(TiberoBulkType.NChar, true, BulkKind.Text, BindDataType.NVarchar2, TiberoParamKind.NVarchar2, "", "", true);

    private static readonly TiberoBulkShape ClobShape =
        new(TiberoBulkType.Clob, true, BulkKind.Clob, null, TiberoParamKind.Clob, "", "", true);

    private static readonly TiberoBulkShape NClobShape =
        new(TiberoBulkType.NClob, true, BulkKind.Clob, null, TiberoParamKind.NClob, "", "", true);

    private static readonly TiberoBulkShape BlobShape =
        new(TiberoBulkType.Blob, true, BulkKind.Blob, null, TiberoParamKind.Blob, "", "", false);

    private static readonly TiberoBulkShape DateShape =
        new(TiberoBulkType.Date, true, BulkKind.Other, BindDataType.Date, TiberoParamKind.Varchar2, "TO_DATE(", ", '" + DateFormat + "')", true);

    private static readonly TiberoBulkShape TimestampShape =
        new(TiberoBulkType.Timestamp, true, BulkKind.Other, BindDataType.Timestamp, TiberoParamKind.Varchar2,
            "TO_TIMESTAMP(", ", '" + TimestampFormat + "')", true);

    private static readonly TiberoBulkShape RawShape =
        new(TiberoBulkType.Raw, true, BulkKind.Other, BindDataType.Raw, TiberoParamKind.Varchar2, "HEXTORAW(", ")", true);

    // INTERVAL 둘(TB1 커밋 9, win144 IT 실측 Tibero 6): TO_YMINTERVAL(:C1) / TO_DSINTERVAL(:C1) + Varchar2 종류로 배열·스칼라 모두 됨.
    // 키 되읽기는 쓰지 않는다(복사 키로 INTERVAL 을 쓰지 않음) → KeyBind null = BindTypeOf·ToKeyText·FromKeyText 는 NotSupported.
    private static readonly TiberoBulkShape IntervalYmShape =
        new(TiberoBulkType.IntervalYm, true, BulkKind.Other, null, TiberoParamKind.Varchar2, "TO_YMINTERVAL(", ")", true);

    private static readonly TiberoBulkShape IntervalDsShape =
        new(TiberoBulkType.IntervalDs, true, BulkKind.Other, null, TiberoParamKind.Varchar2, "TO_DSINTERVAL(", ")", true);

    // ---- 꺼진 형식(Q2): 읽기만 하고 쓰기는 IT 뒤에 켠다. 감싸기·종류는 후보(미실측) ----

    private static readonly TiberoBulkShape BinaryFloatShape =
        new(TiberoBulkType.BinaryFloat, false, BulkKind.Other, BindDataType.BinaryDouble, TiberoParamKind.Varchar2, "TO_BINARY_DOUBLE(", ")", true);

    private static readonly TiberoBulkShape BinaryDoubleShape =
        new(TiberoBulkType.BinaryDouble, false, BulkKind.Other, BindDataType.BinaryDouble, TiberoParamKind.Varchar2, "TO_BINARY_DOUBLE(", ")", true);

    // 교차 복사(XC4)는 이 후보 감싸기를 TiberoBulkValueCodec.CrossBindExpression·CrossBindText 우회로 쓴다(IsSupported 는 꺼진 채).
    // XC5(실측 IT)가 서식을 검증하고 녹색이면 그때 IsSupported 를 켠다.
    private static readonly TiberoBulkShape TimestampTzShape =
        new(TiberoBulkType.TimestampTz, false, BulkKind.Other, BindDataType.TimestampTz, TiberoParamKind.Varchar2,
            "TO_TIMESTAMP_TZ(", ", '" + TimestampFormat + " TZH:TZM')", true);

    private static readonly TiberoBulkShape TimestampLtzShape =
        new(TiberoBulkType.TimestampLtz, false, BulkKind.Other, BindDataType.TimestampLtz, null,
            "FROM_TZ(TO_TIMESTAMP(", ", '" + TimestampFormat + "'), DBTIMEZONE)", true);

    private static readonly TiberoBulkShape LongShape =
        new(TiberoBulkType.Long, false, BulkKind.Text, null, TiberoParamKind.Long, "", "", true);

    private static readonly TiberoBulkShape LongRawShape =
        new(TiberoBulkType.LongRaw, false, BulkKind.Blob, null, TiberoParamKind.LongRaw, "", "", false);

    /// <summary>
    /// 정밀도·길이 괄호(<c>TIMESTAMP(6)</c>, <c>VARCHAR2(10 CHAR)</c>, <c>INTERVAL DAY(2) TO SECOND(6)</c>)를 지우고 공백을 하나로, 대문자로
    /// (Oracle <c>BulkBinds.ToDbType</c> 과 같은 규칙).
    /// </summary>
    public static string Normalize(string serverType)
    {
        ArgumentNullException.ThrowIfNull(serverType);
        return Spaces().Replace(Precision().Replace(serverType, string.Empty), " ").Trim().ToUpperInvariant();
    }

    /// <summary>서버 형식 이름 → 모양. 모르는 형식이면 <see cref="TiberoBulkType.Unknown"/>(지원 아님, 갈래 없음).</summary>
    public static TiberoBulkShape ShapeOf(string serverType) => Normalize(serverType) switch
    {
        "NUMBER" or "FLOAT" => NumberShape,
        "VARCHAR2" or "VARCHAR" => Varchar2Shape,
        "CHAR" => CharShape,
        "NVARCHAR2" or "NVARCHAR" => NVarchar2Shape,
        "NCHAR" => NCharShape,
        "CLOB" => ClobShape,
        "NCLOB" => NClobShape,
        "BLOB" => BlobShape,
        "DATE" => DateShape,
        "TIMESTAMP" => TimestampShape,
        "RAW" => RawShape,
        "BINARY_FLOAT" => BinaryFloatShape,
        "BINARY_DOUBLE" => BinaryDoubleShape,
        "TIMESTAMP WITH TIME ZONE" => TimestampTzShape,
        "TIMESTAMP WITH LOCAL TIME ZONE" => TimestampLtzShape,
        "INTERVAL YEAR TO MONTH" => IntervalYmShape,
        "INTERVAL DAY TO SECOND" => IntervalDsShape,
        "LONG" => LongShape,
        "LONG RAW" => LongRawShape,
        _ => UnknownShape,
    };

    /// <summary>
    /// 리더 열(스키마 표 <c>ProviderType</c> + <c>IsRowID</c>) → 읽는 법(TB1 설계 1.2). ROWID·BFILE·XMLTYPE·BOOLEAN·객체·REF CURSOR 와 모르는 번호는
    /// <see cref="NotSupportedException"/>(열 이름·형식). 숫자 열을 문자로 가정하지 않는다(숫자 열 GetString 은 빈 글자).
    /// </summary>
    public static TiberoBulkReadKind ReadKindOf(int providerType, bool isRowId, string columnName)
    {
        if (isRowId)
        {
            throw Unreadable(columnName, TiberoColumnTypes.RowIdTypeName);
        }

        return providerType switch
        {
            TiberoColumnTypes.PtDecimal or TiberoColumnTypes.PtByte or TiberoColumnTypes.PtInt16 or TiberoColumnTypes.PtInt32
                or TiberoColumnTypes.PtInt64 or TiberoColumnTypes.PtDouble or TiberoColumnTypes.PtSingle => TiberoBulkReadKind.Number,
            TiberoColumnTypes.PtBinaryFloat => TiberoBulkReadKind.BinaryFloat,
            TiberoColumnTypes.PtBinaryDouble => TiberoBulkReadKind.BinaryDouble,
            TiberoColumnTypes.PtVarchar2 or TiberoColumnTypes.PtChar or TiberoColumnTypes.PtNVarchar2 or TiberoColumnTypes.PtNChar
                => TiberoBulkReadKind.Text,
            TiberoColumnTypes.PtLong => TiberoBulkReadKind.Long,
            TiberoColumnTypes.PtClob or TiberoColumnTypes.PtNClob => TiberoBulkReadKind.Clob,
            TiberoColumnTypes.PtBlob => TiberoBulkReadKind.Blob,
            TiberoColumnTypes.PtRaw => TiberoBulkReadKind.Raw,
            TiberoColumnTypes.PtLongRaw => TiberoBulkReadKind.LongRaw,
            TiberoColumnTypes.PtDate => TiberoBulkReadKind.Date,
            TiberoColumnTypes.PtTimeStamp => TiberoBulkReadKind.Timestamp,
            TiberoColumnTypes.PtTimeStampTZ => TiberoBulkReadKind.TimestampTz,
            TiberoColumnTypes.PtTimeStampLTZ => TiberoBulkReadKind.TimestampLtz,
            TiberoColumnTypes.PtIntervalYM => TiberoBulkReadKind.IntervalYm,
            TiberoColumnTypes.PtIntervalDS => TiberoBulkReadKind.IntervalDs,
            _ => throw Unreadable(columnName, TiberoColumnTypes.Describe(providerType).DbTypeName),
        };
    }

    /// <summary>
    /// 스키마 표를 못 읽을 때의 대안: <c>GetDataTypeName</c> 이름(docs/design/tibero-driver-values.md 1장: <c>Decimal</c>·<c>Varchar2</c>·<c>TimeStamp</c>…) → 읽는 법.
    /// 서버 형식 이름(<c>BINARY_FLOAT</c>·<c>TIMESTAMP WITH TIME ZONE</c>)도 받는다. 모르거나 <c>RowID</c> 면 <see cref="NotSupportedException"/>.
    /// </summary>
    public static TiberoBulkReadKind ReadKindOf(string dataTypeName, string columnName)
    {
        ArgumentNullException.ThrowIfNull(dataTypeName);
        return Spaces().Replace(dataTypeName, " ").Trim().ToUpperInvariant() switch
        {
            "DECIMAL" or "NUMBER" or "FLOAT" or "BYTE" or "INT16" or "INT32" or "INT64" or "SINGLE" or "DOUBLE" => TiberoBulkReadKind.Number,
            "BINARYFLOAT" or "BINARY_FLOAT" => TiberoBulkReadKind.BinaryFloat,
            "BINARYDOUBLE" or "BINARY_DOUBLE" => TiberoBulkReadKind.BinaryDouble,
            "VARCHAR2" or "VARCHAR" or "CHAR" or "NVARCHAR2" or "NVARCHAR" or "NCHAR" => TiberoBulkReadKind.Text,
            "LONG" => TiberoBulkReadKind.Long,
            "CLOB" or "NCLOB" => TiberoBulkReadKind.Clob,
            "BLOB" => TiberoBulkReadKind.Blob,
            "RAW" => TiberoBulkReadKind.Raw,
            "LONGRAW" or "LONG RAW" => TiberoBulkReadKind.LongRaw,
            "DATE" => TiberoBulkReadKind.Date,
            "TIMESTAMP" => TiberoBulkReadKind.Timestamp,
            "TIMESTAMPTZ" or "TIMESTAMP WITH TIME ZONE" => TiberoBulkReadKind.TimestampTz,
            "TIMESTAMPLTZ" or "TIMESTAMP WITH LOCAL TIME ZONE" => TiberoBulkReadKind.TimestampLtz,
            "INTERVALYM" or "INTERVAL YEAR TO MONTH" => TiberoBulkReadKind.IntervalYm,
            "INTERVALDS" or "INTERVAL DAY TO SECOND" => TiberoBulkReadKind.IntervalDs,
            _ => throw Unreadable(columnName, dataTypeName),
        };
    }

    // Oracle 대량 읽기(OracleBulkExecutionService)의 「지원하지 않는 열 형식」 글과 같은 모양.
    private static NotSupportedException Unreadable(string columnName, string typeName)
        => new($"데이터 복사가 지원하지 않는 열 형식입니다: {columnName} ({typeName})");

    [GeneratedRegex(@"\([^)]*\)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Precision();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Spaces();
}

/// <summary>대량 읽기에서 열 값 하나를 읽는 법(<see cref="TiberoBulkTypes.ReadKindOf(int, bool, string)"/>, TB1 설계 1.2 표).</summary>
internal enum TiberoBulkReadKind
{
    /// <summary>원시 바이트 → <see cref="TiberoBulkNumber"/>(정수·실수 형식 열도).</summary>
    Number,

    /// <summary>원시 바이트(4바이트) → float(드라이버 GetValue 는 음수가 틀림, R1x).</summary>
    BinaryFloat,

    /// <summary>원시 바이트(8바이트) → double(드라이버 GetValue 는 음수가 틀림, R1x).</summary>
    BinaryDouble,

    /// <summary>GetString → string(CHAR 끝 공백 그대로).</summary>
    Text,

    /// <summary>GetValue → string(GetString 은 빈 글자, R1x. 쓰기 꺼짐).</summary>
    Long,

    /// <summary>LOB 끝까지 → string.</summary>
    Clob,

    /// <summary>LOB 끝까지 → byte[].</summary>
    Blob,

    /// <summary>원시 바이트 → byte[].</summary>
    Raw,

    /// <summary>GetValue → byte[](ReadRawBytes 는 72바이트 서술자, R1x).</summary>
    LongRaw,

    /// <summary>GetValue → DateTime(초).</summary>
    Date,

    /// <summary>원시 바이트 → <see cref="TiberoBulkTimestamp"/>.</summary>
    Timestamp,

    /// <summary>원시 바이트 → <see cref="TiberoBulkTimestampTz"/>.</summary>
    TimestampTz,

    /// <summary>원시 바이트 → <see cref="TiberoBulkTimestampLtz"/>.</summary>
    TimestampLtz,

    /// <summary>원시 바이트 → <see cref="TiberoBulkIntervalYm"/>.</summary>
    IntervalYm,

    /// <summary>원시 바이트 → <see cref="TiberoBulkIntervalDs"/>.</summary>
    IntervalDs,
}
