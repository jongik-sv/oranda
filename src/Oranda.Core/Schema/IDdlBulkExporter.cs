using Oranda.Core.Connection;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;

namespace Oranda.Core.Schema;

public enum DdlExtractLayout
{
    /// <summary>스키마 전체를 파일 하나로.</summary>
    SingleFile,

    /// <summary><c>유형/이름.sql</c> 로 객체마다 파일 하나.</summary>
    FilePerObject,
}

/// <summary>이름 바꾸기 한 쌍. DDL 안의 따옴표 식별자(<c>"FROM"</c>)를 <c>"TO"</c> 로 바꾼다(스키마 이름도 같다).</summary>
public sealed record DdlRename(string From, string To);

/// <summary>DDL 일괄 추출 요청. 저장 속성(저장·세그먼트·테이블스페이스·종결자·종속 객체)은 <see cref="Ddl"/>.</summary>
public sealed record DdlExtractRequest
{
    public required string Owner { get; init; }

    /// <summary>빈 목록 = <see cref="IDdlBulkExporter.DefaultTypes"/>.</summary>
    public IReadOnlyList<string> ObjectTypes { get; init; } = [];

    /// <summary>SQL LIKE(이스케이프 <c>\</c>) 이름 패턴. null = 전부.</summary>
    public string? NamePattern { get; init; }

    public bool InvalidOnly { get; init; }

    /// <summary>
    /// 추출할 객체를 화면이 직접 정한 목록(대상 미리보기에서 체크한 것). null 이면 위 조건(스키마·유형·이름·INVALID)으로 목록을 조회하고,
    /// 값이 있으면 조회하지 않고 이 목록만 추출한다(빈 목록 = 추출할 것 없음).
    /// </summary>
    public IReadOnlyList<DbObjectRef>? Objects { get; init; }

    public DdlOptions Ddl { get; init; } = new();

    public IReadOnlyList<DdlRename> Renames { get; init; } = [];

    public required string OutputDirectory { get; init; }

    public DdlExtractLayout Layout { get; init; } = DdlExtractLayout.SingleFile;
}

public sealed record DdlExtractItem(DbObjectRef Ref, bool Succeeded, string? Error);

public sealed record DdlExtractResult(IReadOnlyList<DdlExtractItem> Items, IReadOnlyList<string> Files)
{
    public int SucceededCount => Items.Count(i => i.Succeeded);

    public int FailedCount => Items.Count - SucceededCount;
}

/// <summary>
/// DDL 일괄 추출(SCH-10). 객체 목록은 <see cref="IMetadataProvider.ListObjectsAsync"/>, DDL 은 <see cref="IMetadataProvider.GetDdlAsync"/> 를
/// 객체마다 부른다. 한 객체가 실패해도 나머지는 계속하고 결과 목록에 오류를 남긴다. 상태를 바꾸지 않는 읽기 작업이다.
/// DBMS 마다 하나씩 구현한다(Oracle = Oranda.Oracle, Tibero = Oranda.Tibero).
/// 구현은 <c>[OrandaService(typeof(IDdlBulkExporter), Dbms = …)]</c> 로 등록하고, 쓰는 쪽은
/// DBMS 별 구현 고르기(<c>IDbmsServiceSelector</c>)로 세션의 DBMS 에 맞는 것을 꺼낸다.
/// </summary>
public interface IDdlBulkExporter
{
    /// <summary>이 구현이 맡는 DBMS.</summary>
    Dbms Dbms { get; }

    /// <summary>의존 순서(앞 유형이 먼저 만들어져야 하는 순). 파일에도 이 순서로 쓴다.</summary>
    IReadOnlyList<string> DefaultTypes { get; }

    Task<DdlExtractResult> ExtractAsync(IMetadataProvider meta, ISession session, DdlExtractRequest request,
                                        IProgress<(int Done, int Total)>? progress, CancellationToken ct);
}
