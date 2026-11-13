using Oranda.Core.Metadata;

namespace Oranda.Core.Rollback;

/// <summary>값 읽기 식과 리터럴을 고르는 열 형식 묶음(설계 6.6 표).</summary>
public enum RollbackValueKind
{
    Text,
    NationalText,
    Number,
    BinaryFloat,
    BinaryDouble,
    Date,
    Timestamp,
    TimestampTz,
    TimestampLtz,
    IntervalDaySecond,
    IntervalYearMonth,
    Raw,
    RowId,

    /// <summary>큰 열(LOB·LONG·XMLTYPE·JSON·VECTOR·객체 형식). 조건에 넣지 않고 NULL 여부만 읽는다.</summary>
    Large,
}

/// <summary>원복에 쓰는 열 하나. <paramref name="DataType"/> 은 사전의 형식 이름 그대로(예: <c>TIMESTAMP(6)</c>).</summary>
public sealed record RollbackColumn(string Name, string DataType, RollbackValueKind Kind)
{
    public int? Scale { get; init; }

    /// <summary>가상 열: 원복 INSERT 에서 뺀다(RD-12).</summary>
    public bool IsVirtual { get; init; }

    /// <summary>ID 열: GENERATED ALWAYS 면 원복 INSERT 가 ORA-32795 로 거부되므로 문장을 불완전으로 둔다(RD-12).</summary>
    public bool IsIdentity { get; init; }

    /// <summary>숨김 열: 원복 INSERT 에 이름을 적어 넣는다(RD-12).</summary>
    public bool IsInvisible { get; init; }
}

/// <summary>시노님을 푼 실제 표. <see cref="KeyColumns"/> 가 비면 ROWID 로 행을 찾는다.</summary>
public sealed record RollbackTable(DbObjectRef Table, IReadOnlyList<RollbackColumn> Columns, IReadOnlyList<string> KeyColumns)
{
    /// <summary>트리거·CASCADE FK 안내 글(확인 창 경고 줄, 설계 10.1).</summary>
    public IReadOnlyList<string> SideEffects { get; init; } = [];
}
