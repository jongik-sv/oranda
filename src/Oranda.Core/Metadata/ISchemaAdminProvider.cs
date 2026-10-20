using Oranda.Core.Sessions;

namespace Oranda.Core.Metadata;

/// <summary>
/// 객체 검색(이름·유형·크기·생성일·변경일)·소스 본문 검색·열 이름 검색·참조 관계·휴지통 목록(SCH-09~11). 읽기 전용이다 —
/// 컴파일·Purge·Flashback 같은 변경은 <c>IExecutionService</c> 로 보낸다(정책 관문). 구현: Oranda.Oracle(TSK-07-04).
/// </summary>
public interface ISchemaAdminProvider
{
    Task<IReadOnlyList<ObjectSearchRow>> SearchObjectsAsync(ISession s, ObjectSearchQuery q, CancellationToken ct);

    /// <summary>
    /// PL/SQL 소스 본문을 줄 단위로 찾는다(<c>all_source</c>/<c>dba_source</c>). 검색어는 바인드로만 보낸다.
    /// 정규식이 틀리면 <see cref="SearchPatternException"/>.
    /// </summary>
    Task<SearchHits<SourceSearchHit>> SearchSourceAsync(ISession s, SourceSearchQuery q, CancellationToken ct);

    /// <summary>테이블·뷰의 열을 이름으로 찾는다(<c>all_tab_columns</c>/<c>dba_tab_columns</c>, 대소문자 무시).</summary>
    Task<SearchHits<ColumnSearchHit>> SearchColumnsAsync(ISession s, ColumnSearchQuery q, CancellationToken ct);

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

/// <summary>
/// 소스 본문 검색 조건. 기본은 「글자 포함」, <see cref="WholeWord"/> 는 앞뒤가 식별자 글자(영숫자·<c>_</c>·<c>$</c>·<c>#</c>)가 아닌 곳만,
/// <see cref="UseRegex"/> 는 Oracle 정규식(<c>REGEXP_LIKE</c>, POSIX ERE). 소스를 가진 7개 유형(패키지·본문·프로시저·함수·트리거·타입·타입 본문)만 찾는다.
/// </summary>
public sealed record SourceSearchQuery
{
    public string? Owner { get; init; }

    /// <summary>빈 목록 = 소스 유형 모두.</summary>
    public IReadOnlyList<string> ObjectTypes { get; init; } = [];

    public string Text { get; init; } = string.Empty;

    public bool MatchCase { get; init; }

    public bool WholeWord { get; init; }

    public bool UseRegex { get; init; }

    /// <summary>돌려줄 최대 줄 수. 더 있으면 <see cref="SearchHits{T}.Capped"/>.</summary>
    public int Limit { get; init; } = 1000;
}

/// <summary>소스 검색 결과 한 줄. <see cref="Line"/> 은 서버 소스 줄 번호(1부터), <see cref="Text"/> 는 줄 끝 줄바꿈을 뗀 원문.</summary>
public sealed record SourceSearchHit(DbObjectRef Ref, int Line, string Text);

/// <summary>열 이름 검색 조건. 이름 패턴은 SQL LIKE(이스케이프 <c>\</c>), 대소문자를 가리지 않는다.</summary>
public sealed record ColumnSearchQuery
{
    public string? Owner { get; init; }

    public string? NamePattern { get; init; }

    /// <summary>돌려줄 최대 줄 수. 더 있으면 <see cref="SearchHits{T}.Capped"/>.</summary>
    public int Limit { get; init; } = 1000;
}

/// <summary>열 검색 결과 한 줄. <see cref="Table"/> 의 유형은 TABLE·VIEW·MATERIALIZED VIEW·CLUSTER 중 하나. <see cref="DataType"/> 은 길이·정밀도를 붙인 표시용.</summary>
public sealed record ColumnSearchHit(DbObjectRef Table, string ColumnName, string DataType, bool IsNullable);

/// <summary>상한이 있는 검색 결과. <see cref="Capped"/> = 상한보다 더 있어 잘랐다.</summary>
public sealed record SearchHits<T>(IReadOnlyList<T> Rows, bool Capped);

/// <summary>검색 패턴(정규식)을 서버가 받아들이지 않았거나 보내기 전에 틀린 것을 알았다. 메시지는 사람이 읽는 설명.</summary>
public sealed class SearchPatternException : Exception
{
    public SearchPatternException(string message)
        : base(message)
    {
    }

    public SearchPatternException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>검색 결과 한 줄. 크기는 세그먼트가 있고 볼 권한이 있을 때만 채운다.</summary>
public sealed record ObjectSearchRow(DbObjectRef Ref, string Status, DateTime? Created, DateTime? LastDdlTime, long? SizeBytes);

/// <summary>참조 관계 한 줄. <see cref="IsUsedBy"/> = true 면 이 객체를 쓰는 쪽, false 면 이 객체가 쓰는 쪽.</summary>
public sealed record ObjectReference(bool IsUsedBy, DbObjectRef Ref);

/// <summary>휴지통 항목. <see cref="ObjectName"/> 은 <c>BIN$…</c> 이름.</summary>
public sealed record RecycleBinItem(string ObjectName, string OriginalName, string ObjectType, string? Tablespace,
                                    DateTime? DropTime, bool CanUndrop, bool CanPurge, long? SpaceBlocks);
