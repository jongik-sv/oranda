using Oranda.Core.Sessions;

namespace Oranda.Core.Metadata;

/// <summary>
/// 스키마·객체·열·제약·인덱스·주석·패키지 멤버·DDL 읽기(TRD 4.7, AI 확장 지점 4.10).
/// 뷰 계열(dba_/all_/user_)은 <c>ServerCapabilities</c> 로 고르고, 쿼리는 카탈로그 <c>meta.*</c> 에서 꺼낸다.
/// 구현: Oranda.Oracle <c>OracleMetadataProvider</c>(partial, 파일별 소유 Task 는 docs/design/design.md).
/// TRD 6장의 세 메서드 외 나머지는 TSK-02-03 이 덧붙였다.
/// </summary>
public interface IMetadataProvider
{
    /// <summary>스키마(사용자) 이름 목록. TSK-04-04.</summary>
    Task<IReadOnlyList<string>> ListSchemasAsync(ISession s, CancellationToken ct);

    /// <summary>객체 이름 목록(유형·이름 패턴·상태 필터). TSK-04-04.</summary>
    Task<IReadOnlyList<DbObjectRef>> ListObjectsAsync(ISession s, ObjectQuery q, CancellationToken ct);

    /// <summary>테이블·뷰의 열·제약·인덱스·주석. TSK-04-04.</summary>
    Task<TableInfo> GetTableAsync(ISession s, DbObjectRef table, CancellationToken ct);

    /// <summary>
    /// 프로시저·함수·패키지의 멤버와 인자(all_arguments). 단독 프로시저면 원소 1개. TSK-04-04.
    /// </summary>
    Task<IReadOnlyList<ProcedureInfo>> GetProceduresAsync(ISession s, DbObjectRef obj, CancellationToken ct);

    /// <summary>DBMS_METADATA DDL. TSK-07-03.</summary>
    Task<string> GetDdlAsync(ISession s, DbObjectRef obj, DdlOptions o, CancellationToken ct);

    /// <summary>객체 상세 탭(파티션·권한·의존·통계·크기 등). TSK-07-03.</summary>
    Task<ObjectDetail> GetObjectDetailAsync(ISession s, DbObjectRef obj, CancellationToken ct);

    /// <summary>PL/SQL 소스(all_source). 명세/본문은 <see cref="DbObjectRef.ObjectType"/> 로 구분. TSK-08-01.</summary>
    Task<string> GetSourceAsync(ISession s, DbObjectRef obj, CancellationToken ct);

    /// <summary>서버 컴파일 오류·경고(all_errors). TSK-08-01.</summary>
    Task<IReadOnlyList<CompileError>> GetCompileErrorsAsync(ISession s, DbObjectRef obj, CancellationToken ct);

    /// <summary>
    /// 이름 풀이 한 단계: <c>owner.name</c> 이 그 스키마의 표·뷰인지, 시노님이면 가리키는 곳(<see cref="SynonymResolver"/> 가 따라간다).
    /// 사전에 없거나 읽지 못하면 null. 기본 구현은 늘 null(시노님을 풀지 않는다).
    /// </summary>
    Task<NameStep?> GetNameStepAsync(ISession s, string owner, string name, CancellationToken ct) => Task.FromResult<NameStep?>(null);
}

/// <summary>
/// 객체 참조. <see cref="ObjectType"/> 은 Oracle OBJECT_TYPE 그대로(<c>TABLE</c>, <c>PACKAGE BODY</c>).
/// <see cref="SubName"/> 은 패키지 멤버·파티션 이름 등(없으면 null).
/// </summary>
public sealed record DbObjectRef(string Owner, string Name, string ObjectType, string? SubName = null);

/// <summary>객체 목록 조건. 이름 패턴은 SQL LIKE 형식이며 서버 쿼리에 넣는다(TRD 4.7).</summary>
public sealed record ObjectQuery
{
    public string? Owner { get; init; }

    /// <summary>빈 목록 = 모든 유형.</summary>
    public IReadOnlyList<string> ObjectTypes { get; init; } = [];

    public string? NamePattern { get; init; }

    public bool InvalidOnly { get; init; }

    public DateTimeOffset? CreatedAfter { get; init; }

    public DateTimeOffset? ModifiedAfter { get; init; }

    /// <summary>null = 제한 없음.</summary>
    public int? Limit { get; init; }
}

public sealed record TableInfo(DbObjectRef Table, IReadOnlyList<ColumnInfo> Columns)
{
    public IReadOnlyList<ConstraintInfo> Constraints { get; init; } = [];

    public IReadOnlyList<IndexInfo> Indexes { get; init; } = [];

    public string? Comment { get; init; }
}

/// <summary>열 1개. <see cref="DataType"/> 은 서버 타입 이름(<c>VARCHAR2</c>, <c>VECTOR</c> …).</summary>
public sealed record ColumnInfo(string Name, int Position, string DataType)
{
    public int? Length { get; init; }

    public int? Precision { get; init; }

    public int? Scale { get; init; }

    public bool IsNullable { get; init; } = true;

    public string? DefaultValue { get; init; }

    public string? Comment { get; init; }

    public bool IsVirtual { get; init; }

    public bool IsIdentity { get; init; }

    public bool IsInvisible { get; init; }

    /// <summary>23ai 이상 Data Use Case Domain 이름.</summary>
    public string? Domain { get; init; }
}

public enum ConstraintKind
{
    PrimaryKey,
    Unique,
    ForeignKey,
    Check,
}

public sealed record ConstraintInfo(string Name, ConstraintKind Kind, IReadOnlyList<string> Columns)
{
    public DbObjectRef? ReferencedTable { get; init; }

    /// <summary>
    /// 외래키가 참조하는 부모 테이블의 기본키·유일 키 이름(r_constraint_name). 외래키가 아니거나 모르면 null.
    /// 부모 키 열은 <see cref="Columns"/> 와 위치(position) 순서로 짝을 이룬다.
    /// </summary>
    public string? ReferencedConstraint { get; init; }

    public string? Condition { get; init; }

    public bool IsEnabled { get; init; } = true;
}

public sealed record IndexInfo(string Name, bool IsUnique, IReadOnlyList<string> Columns)
{
    public string? IndexType { get; init; }
}

/// <summary>DBMS_METADATA 변환 옵션(SCH DDL 보기).</summary>
public sealed record DdlOptions
{
    public bool IncludeStorage { get; init; }

    public bool IncludeSegmentAttributes { get; init; }

    public bool IncludeTablespace { get; init; }

    public bool SqlTerminator { get; init; } = true;

    public bool IncludeDependentObjects { get; init; }
}

public enum ArgumentDirection
{
    In,
    Out,
    InOut,
}

public sealed record ArgumentInfo(string Name, int Position, string DataType, ArgumentDirection Direction)
{
    public bool HasDefault { get; init; }

    /// <summary>사용자 정의 형식이면 <c>OWNER.TYPE</c>.</summary>
    public string? TypeName { get; init; }
}

/// <summary>프로시저·함수 1개(패키지 멤버면 <see cref="Overload"/> 로 구분).</summary>
public sealed record ProcedureInfo(string Name, bool IsFunction, IReadOnlyList<ArgumentInfo> Arguments)
{
    public int? Overload { get; init; }

    public string? ReturnType { get; init; }
}

/// <summary>표 형태 상세 한 묶음(예: 「권한」, 「파티션」).</summary>
public sealed record DetailSection(string Title, IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<object?>> Rows);

public sealed record ObjectDetail(DbObjectRef Target, IReadOnlyList<DetailSection> Sections);

/// <summary>서버 컴파일 오류(all_errors). 줄·열은 1부터.</summary>
public sealed record CompileError(int Line, int Column, string Message, bool IsWarning)
{
    public int MessageNumber { get; init; }
}
