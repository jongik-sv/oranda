using Oranda.Core.Sessions;

namespace Oranda.Core.Metadata;

/// <summary>
/// 객체 검색(이름·유형·크기·생성일·변경일)·참조 관계·휴지통 목록(SCH-09~11). 읽기 전용이다 —
/// 컴파일·Purge·Flashback 같은 변경은 <c>IExecutionService</c> 로 보낸다(정책 관문). 구현: Oranda.Oracle(TSK-07-04).
/// </summary>
public interface ISchemaAdminProvider
{
    Task<IReadOnlyList<ObjectSearchRow>> SearchObjectsAsync(ISession s, ObjectSearchQuery q, CancellationToken ct);

    /// <summary>객체가 쓰는 것·객체를 쓰는 것.</summary>
    Task<IReadOnlyList<ObjectReference>> GetReferencesAsync(ISession s, DbObjectRef obj, CancellationToken ct);

    /// <summary>현재 스키마의 휴지통.</summary>
    Task<IReadOnlyList<RecycleBinItem>> ListRecycleBinAsync(ISession s, CancellationToken ct);
}

/// <summary>객체 검색 조건. 이름 패턴은 SQL LIKE(이스케이프 <c>\</c>).</summary>
public sealed record ObjectSearchQuery
{
    public string? Owner { get; init; }

    /// <summary>빈 목록 = 모든 유형.</summary>
    public IReadOnlyList<string> ObjectTypes { get; init; } = [];

    public string? NamePattern { get; init; }

    public bool InvalidOnly { get; init; }

    public DateTime? CreatedAfter { get; init; }

    public DateTime? ModifiedAfter { get; init; }

    public long? MinSizeBytes { get; init; }

    public long? MaxSizeBytes { get; init; }

    public int Limit { get; init; } = 500;
}

/// <summary>검색 결과 한 줄. 크기는 세그먼트가 있고 볼 권한이 있을 때만 채운다.</summary>
public sealed record ObjectSearchRow(DbObjectRef Ref, string Status, DateTime? Created, DateTime? LastDdlTime, long? SizeBytes);

/// <summary>참조 관계 한 줄. <see cref="IsUsedBy"/> = true 면 이 객체를 쓰는 쪽, false 면 이 객체가 쓰는 쪽.</summary>
public sealed record ObjectReference(bool IsUsedBy, DbObjectRef Ref);

/// <summary>휴지통 항목. <see cref="ObjectName"/> 은 <c>BIN$…</c> 이름.</summary>
public sealed record RecycleBinItem(string ObjectName, string OriginalName, string ObjectType, string? Tablespace,
                                    DateTime? DropTime, bool CanUndrop, bool CanPurge, long? SpaceBlocks);
