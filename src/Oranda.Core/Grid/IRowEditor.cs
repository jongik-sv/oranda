using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;

namespace Oranda.Core.Grid;

/// <summary>
/// 그리드 편집(GRID-04, TRD 4.4 「편집」). 반영은 세이브포인트 → 실행 → 영향 행 수 ≠ 1 이면 그 행만 롤백.
/// 생성 DML 은 실행 엔진(정책 관문)을 거친다. 구현: Oranda.Language(TSK-06-04).
/// </summary>
public interface IRowEditor
{
    /// <summary>편집 가능 여부와 이유. <paramref name="keys"/> 는 메타데이터 제공자가 구한 대상 테이블 키.</summary>
    EditCapability Analyze(SqlStatement query, IReadOnlyList<ResultColumn> cols, TableKeyInfo keys);

    /// <summary>반영 전 미리보기용 DML(GRID-05).</summary>
    IReadOnlyList<SqlStatement> BuildDml(IReadOnlyList<RowChange> changes);

    Task<ApplyResult> ApplyAsync(ISession s, IReadOnlyList<RowChange> changes, bool commit, CancellationToken ct);
}

public enum EditBlockReason
{
    None,
    NoKeyColumns,
    Aggregate,
    Distinct,
    GroupBy,
    SetOperation,
    NotKeyPreserved,
    MultipleTables,
    ReadOnlyProfile,
    NotAnalyzable,
}

/// <summary>편집 가능 여부. <see cref="CanAddRowId"/> 면 「ROWID 추가 후 다시 실행」 단추를 보인다.</summary>
public sealed record EditCapability(bool CanEdit, EditBlockReason Reason = EditBlockReason.None)
{
    public string? Message { get; init; }

    public DbObjectRef? Target { get; init; }

    /// <summary>키(PK·NOT NULL 유니크 또는 ROWID) 열의 결과 열 번호.</summary>
    public IReadOnlyList<int> KeyColumnOrdinals { get; init; } = [];

    /// <summary>편집할 수 있는 결과 열 번호(대상 테이블 열만).</summary>
    public IReadOnlyList<int> EditableColumnOrdinals { get; init; } = [];

    public bool CanAddRowId { get; init; }

    /// <summary>ROWID 를 덧붙인 다시 실행용 문장(<see cref="CanAddRowId"/> 일 때).</summary>
    public SqlStatement? QueryWithRowId { get; init; }
}

public enum TableKeyKind
{
    None,
    PrimaryKey,
    UniqueNotNull,
}

/// <summary>대상 테이블과 행 식별 키.</summary>
public sealed record TableKeyInfo(DbObjectRef Table, TableKeyKind Kind, IReadOnlyList<string> KeyColumns);

public enum RowChangeKind
{
    Insert,
    Update,
    Delete,
}

/// <summary>
/// 행 변경 1건. <see cref="Key"/> 는 키 열(또는 <c>ROWID</c>) → 값.
/// <see cref="OriginalValues"/> 가 비어 있지 않으면 낙관적 잠금(WHERE 에 원래 값 비교 추가).
/// </summary>
public sealed record RowChange(RowChangeKind Kind, long RowIndex, DbObjectRef Table)
{
    public IReadOnlyDictionary<string, object?> Key { get; init; } = new Dictionary<string, object?>();

    public IReadOnlyDictionary<string, object?> NewValues { get; init; } = new Dictionary<string, object?>();

    public IReadOnlyDictionary<string, object?> OriginalValues { get; init; } = new Dictionary<string, object?>();
}

/// <summary>반영 결과. 실패한 행은 롤백되어 <see cref="Failures"/> 에 남는다.</summary>
public sealed record ApplyResult(int AppliedCount, bool Committed)
{
    public IReadOnlyList<RowApplyFailure> Failures { get; init; } = [];
}

/// <param name="RowIndex">그리드 행 번호</param>
/// <param name="AffectedRows">실제 영향 행 수(1 이 아니어서 실패한 경우)</param>
/// <param name="Error">서버 오류(있으면)</param>
public sealed record RowApplyFailure(long RowIndex, long AffectedRows, DbError? Error);
