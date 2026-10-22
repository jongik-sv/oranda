using Oranda.Core.Sessions;

namespace Oranda.Core.Metadata;

/// <summary>
/// 객체 상세의 인덱스·제약 줄을 고를 때 그 키의 열을 따로 읽는다(TSK-13-34). 상세를 열 때는 읽지 않는다(왕복을 늘리지 않는다).
/// 뷰·권한 문제(ORA-00942·ORA-01031)로 읽지 못하면 빈 목록과 <see cref="SchemaFolderResult{T}.Denied"/>. 읽기 전용이다.
/// 구현: Oranda.Oracle <c>OracleTableKeyDetailProvider</c>.
/// </summary>
public interface ITableKeyDetailProvider
{
    /// <summary>표 <paramref name="table"/> 의 인덱스 <paramref name="indexName"/> 가 쓰는 열(위치 순). 함수 기반 인덱스는 식을, 내림차순 열은 <see cref="IndexColumnDetail.Descending"/>.</summary>
    Task<SchemaFolderResult<IndexColumnDetail>> GetIndexColumnsAsync(ISession s, DbObjectRef table, string indexName, CancellationToken ct);

    /// <summary>표 <paramref name="table"/> 의 제약 <paramref name="constraintName"/> 이 쓰는 열(위치 순). 외래키면 같은 위치의 참조 표·열도.</summary>
    Task<SchemaFolderResult<ConstraintColumnDetail>> GetConstraintColumnsAsync(ISession s, DbObjectRef table, string constraintName, CancellationToken ct);
}

/// <summary>
/// 인덱스 열 하나. <paramref name="Column"/> 은 표의 열 이름(내림차순 열의 숨은 이름 <c>SYS_NC…$</c> 는 식에서 꺼낸 열 이름으로 바꾼다),
/// <paramref name="Expression"/> 은 함수 기반 인덱스의 식(일반 열·단순 내림차순 열은 null).
/// </summary>
public sealed record IndexColumnDetail(int Position, string Column, string? Expression, bool Descending);

/// <summary>제약 열 하나. 외래키면 <see cref="ReferencedOwner"/>·<see cref="ReferencedTable"/>·<see cref="ReferencedColumn"/> 이 같은 위치의 참조 키 열.</summary>
public sealed record ConstraintColumnDetail(int Position, string Column)
{
    public string? ReferencedOwner { get; init; }

    public string? ReferencedTable { get; init; }

    public string? ReferencedColumn { get; init; }
}
