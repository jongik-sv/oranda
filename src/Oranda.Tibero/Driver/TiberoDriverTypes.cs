namespace Oranda.Tibero.Driver;

/// <summary>매개변수 형식(드라이버 TiberoDbType 의 같은 이름 값으로 묶인다). 정수는 드라이버 값과 무관하다.</summary>
internal enum TiberoParamKind
{
    Varchar2,
    NVarchar2,
    Char,
    NChar,
    Long,
    Clob,
    NClob,
    Blob,
    Raw,
    LongRaw,
    Byte,
    Int16,
    Int32,
    Int64,
    Decimal,
    Single,
    Double,
    BinaryFloat,
    BinaryDouble,
    Boolean,
    Date,
    TimeStamp,
    TimeStampTZ,
    TimeStampLTZ,
    IntervalDS,
    IntervalYM,
    RefCursor,
    XmlType,
    BFile,
}

/// <summary>컬렉션 바인드 종류.</summary>
internal enum TiberoCollectionKind
{
    None,
    AssociativeArray,
}

/// <summary>LOB 종류.</summary>
internal enum TiberoLobKind
{
    Clob,
    NClob,
    Blob,
}

/// <summary>공급자 값(OUT 매개변수·드라이버 값)의 종류.</summary>
internal enum TiberoValueKind
{
    Null,
    String,
    Decimal,
    Date,
    TimeStamp,
    TimeStampLtz,
    Binary,
    Lob,
    RefCursor,
    Clr,
    Other,
}

/// <summary>
/// 공급자 값을 어댑터 쪽 값으로 바꾼 결과. <see cref="Value"/> 에는 드라이버 형식 객체가 들어가지 않는다
/// (.NET 기본 형식·<see cref="TiberoLob"/>·<see cref="System.Data.Common.DbDataReader"/> 만).
/// </summary>
/// <param name="Kind">값의 종류.</param>
/// <param name="ProviderTypeName">드라이버 형식의 짧은 이름(진단용). 드라이버 형식이 아니면 null.</param>
/// <param name="IsNull">NULL 인지.</param>
/// <param name="Value">변환된 값.</param>
/// <param name="Nanosecond">시각 종류의 나노초(그 밖 0).</param>
internal readonly record struct TiberoOutValue(
    TiberoValueKind Kind,
    string? ProviderTypeName,
    bool IsNull,
    object? Value,
    int Nanosecond = 0);

/// <summary>오류가 나온 곳.</summary>
internal enum TiberoErrorOrigin
{
    /// <summary>서버 오류(드라이버 TiberoException).</summary>
    Server,

    /// <summary>드라이버 쪽 오류(TbOdpException).</summary>
    Client,
}

/// <summary>드라이버 예외에서 읽은 오류. <paramref name="Number"/> 는 드라이버 값 그대로(보통 음수).</summary>
internal readonly record struct TiberoErrorInfo(int Number, string Message, TiberoErrorOrigin Origin);
