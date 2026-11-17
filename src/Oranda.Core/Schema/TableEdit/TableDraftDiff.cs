namespace Oranda.Core.Schema.TableEdit;

/// <summary>편집 줄의 상태(화면 색: 노랑·초록·빨강, 설계 2장).</summary>
public enum DraftRowState
{
    Unchanged,
    Modified,
    Added,
    Dropped,
}

/// <summary>열에서 바뀐 칸.</summary>
[Flags]
public enum ColumnChange
{
    None = 0,
    Name = 1,

    /// <summary>형식·길이·길이 기준·정밀도·소수.</summary>
    Type = 2,
    Nullable = 4,
    Default = 8,
    VirtualExpression = 16,
    Comment = 32,
    Identity = 64,
    Invisible = 128,
    Domain = 256,
}

/// <summary>제약에서 바뀐 칸.</summary>
[Flags]
public enum ConstraintChange
{
    None = 0,
    Name = 1,

    /// <summary>열·조건·참조·삭제 규칙(DROP + ADD 두 문장).</summary>
    Definition = 2,
    Enabled = 4,
}

/// <summary>인덱스에서 바뀐 칸.</summary>
[Flags]
public enum IndexChange
{
    None = 0,
    Name = 1,

    /// <summary>열 구성·순서·DESC·유일·종류·테이블스페이스(DROP + CREATE 두 문장).</summary>
    Definition = 2,
}

/// <summary>열 한 줄의 차이. 원본에만 있으면 <see cref="Edited"/> 는 null(지움), 편집에만 있으면 <see cref="Original"/> 이 null(추가).</summary>
public sealed record ColumnDiff(string RowKey, DraftRowState State, ColumnChange Changes, ColumnDraft? Original, ColumnDraft? Edited);

/// <summary>제약 한 줄의 차이.</summary>
public sealed record ConstraintDiff(string RowKey, DraftRowState State, ConstraintChange Changes, ConstraintDraft? Original, ConstraintDraft? Edited);

/// <summary>인덱스 한 줄의 차이.</summary>
public sealed record IndexDiff(string RowKey, DraftRowState State, IndexChange Changes, IndexDraft? Original, IndexDraft? Edited);

/// <summary>
/// 원본과 편집 모델의 차이(정규화한 뒤 비교, 설계 8.4). 바뀌지 않은 줄은 넣지 않는다. 화면은 줄 상태·칸 표시·페이지별 건수를,
/// 문장 빌더는 문장을 여기서 얻는다.
/// </summary>
public sealed record TableDraftChanges
{
    public IReadOnlyList<ColumnDiff> Columns { get; init; } = [];

    public IReadOnlyList<ConstraintDiff> Constraints { get; init; } = [];

    public IReadOnlyList<IndexDiff> Indexes { get; init; } = [];

    /// <summary>테이블 주석이 바뀌었나.</summary>
    public bool CommentChanged { get; init; }

    /// <summary>테이블 이름이 바뀌었나(기존 테이블, 설계 18.1).</summary>
    public bool Renamed { get; init; }

    /// <summary>바뀐 것이 없다(편집 켜고 안 고침 또는 고쳤다 되돌림 = 문장 0개).</summary>
    public bool IsEmpty => Columns.Count == 0 && Constraints.Count == 0 && Indexes.Count == 0 && !CommentChanged && !Renamed;

    /// <summary>페이지 이름 옆 바뀐 건수(설계 7.1).</summary>
    public int CountFor(TableEditPage page) => page switch
    {
        TableEditPage.Columns => Columns.Count,
        TableEditPage.Constraints => Constraints.Count,
        TableEditPage.Indexes => Indexes.Count,
        TableEditPage.Description => (CommentChanged ? 1 : 0) + (Renamed ? 1 : 0),
        _ => 0,
    };

    /// <summary>줄 키의 상태(차이에 없으면 <see cref="DraftRowState.Unchanged"/>).</summary>
    public DraftRowState StateOf(string rowKey) =>
        Columns.FirstOrDefault(d => d.RowKey == rowKey)?.State
        ?? Constraints.FirstOrDefault(d => d.RowKey == rowKey)?.State
        ?? Indexes.FirstOrDefault(d => d.RowKey == rowKey)?.State
        ?? DraftRowState.Unchanged;
}

/// <summary>차이 계산(설계 8.1: 줄 키로 맞춤, 8.4: 정규화 뒤 비교).</summary>
public static class TableDraftDiff
{
    /// <summary>원본 <paramref name="original"/> 과 편집 <paramref name="edited"/> 의 차이.</summary>
    public static TableDraftChanges Compute(TableDraft original, TableDraft edited, ITableEditDialect dialect) =>
        throw new NotImplementedException("T1b");
}
