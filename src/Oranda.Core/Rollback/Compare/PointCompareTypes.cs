namespace Oranda.Core.Rollback.Compare;

/// <summary>시점 비교 분류(설계 8.3).</summary>
public enum CompareKind
{
    /// <summary>B ≠ C, C = N(커밋 시점이 없으면 B ≠ N): 이전 값으로 UPDATE.</summary>
    Revert,

    /// <summary>B 에 있고 C·N 에 없음: INSERT 로 되살림.</summary>
    Restore,

    /// <summary>B 에 없고 C 에 있고 N = C: DELETE.</summary>
    Remove,

    /// <summary>커밋 뒤 다른 세션이 다시 바꿈(C ≠ N). 기본 제외.</summary>
    Conflict,

    /// <summary>B = C(또는 B = N): 할 일 없음.</summary>
    Same,
}

/// <summary>반영할 때 만드는 문장 종류. 충돌 행도 이것으로 무엇을 만들지 정한다.</summary>
public enum CompareAction { None, Update, Insert, Delete }

/// <summary>
/// 한 시점에서 읽은 행 하나. <see cref="Values"/> 는 <see cref="RollbackTable.Columns"/> 순서다.
/// 키가 ROWID 인 표(<see cref="RollbackTable.KeyColumns"/> 가 빔)는 <see cref="RowId"/> 로 짝짓는다.
/// </summary>
public sealed record CompareSnapshotRow(IReadOnlyList<object?> Values)
{
    public string? RowId { get; init; }
}

/// <summary>키로 짝지은 행 하나와 그 분류. 없는 시점의 값은 null.</summary>
public sealed record CompareRow(CompareKind Kind, CompareAction Action, IReadOnlyList<object?> Key)
{
    public string? RowId { get; init; }
    public IReadOnlyList<object?>? Base { get; init; }
    public IReadOnlyList<object?>? Commit { get; init; }
    public IReadOnlyList<object?>? Now { get; init; }

    /// <summary>되돌릴 열(<see cref="RollbackTable.Columns"/> 안의 자리). UPDATE 일 때만 찬다.</summary>
    public IReadOnlyList<int> ChangedColumns { get; init; } = [];
}

/// <summary>비교 결과. <see cref="Warnings"/> 는 화면 위쪽 안내 줄(PK 없음, 커밋 시점 없음 등).</summary>
public sealed record ComparePlan(RollbackTable Table, IReadOnlyList<CompareRow> Rows, bool HasCommitPoint)
{
    public IReadOnlyList<string> Warnings { get; init; } = [];

    public int Count(CompareKind kind) => Rows.Count(r => r.Kind == kind);
}

/// <summary>만들어진 DML 한 문장. <see cref="Row"/> 는 근거 행, <see cref="Note"/> 는 문장 뒤 주석(충돌 등).</summary>
public sealed record CompareStatement(CompareRow Row, string Sql)
{
    public string? Note { get; init; }
}

/// <summary>DML 묶음. 리터럴로 쓸 수 없어 빠진 행은 <see cref="Skipped"/> 에 이유와 함께 남는다.</summary>
public sealed record CompareScript(IReadOnlyList<CompareStatement> Statements, IReadOnlyList<(CompareRow Row, string Reason)> Skipped);
