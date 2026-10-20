namespace Oranda.Core.Execution;

/// <summary>바인드 값 형식(EXEC-03 대화상자의 타입 목록).</summary>
public enum BindDataType
{
    Varchar2,
    NVarchar2,
    FixedChar,
    Number,
    BinaryDouble,
    Date,
    Timestamp,
    TimestampTz,
    Clob,
    Blob,
    Raw,
    Boolean,
    Json,
    RefCursor,
}

public enum BindDirection
{
    In,
    Out,
    InOut,
    ReturnValue,
}

/// <summary>
/// 바인드 값 하나. <see cref="Value"/> 는 .NET 기본 형식(string, decimal, DateTime, DateTimeOffset, byte[], bool) 또는 null.
/// 이 값 자체는 디스크에 저장하지 않는다. 바인드 변수 창의 입력(값 글자·형식·방향)만 탭과 함께 저장된다(<c>WorkspaceBind</c>, EXEC-03).
/// </summary>
public sealed record BindValue(BindDataType Type, object? Value, BindDirection Direction = BindDirection.In)
{
    /// <summary>OUT 문자열·RAW 의 최대 크기. null = 형식 기본값.</summary>
    public int? Size { get; init; }
}
