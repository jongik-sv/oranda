using Oranda.Core.Metadata;
using Oranda.Core.Sessions;

namespace Oranda.Core.Transfer.Copy;

/// <summary>
/// 「테이블 다시 만들기」(설계 6.2.3)가 지우기 전에 대상·원본 카탈로그에서 받아 두는 것. DBMS 마다 트리거 DDL 을 얻는 길과 휴지통이 달라
/// (T5 결정 Q5) 구현은 DBMS 어셈블리에 두고 그 DBMS 의 복사 실행기 하위 형식이 생성자에서 넘긴다. 키 서비스가 아니다.
/// 조회는 모두 대량 읽기 경로(정책 1회, 이력 없음)로 하고, 하나씩 끝까지 읽은 뒤 다음을 보낸다.
/// </summary>
public interface ICopyRecreateStep
{
    /// <summary>대상 표의 트리거·다른 계정에 준 객체 권한·이 표를 참조하는 FK 의 DDL.</summary>
    Task<PreservedDdl> CaptureAsync(ISession target, string owner, string table, CancellationToken ct);

    /// <summary>
    /// 원본 표의 FK 가운데 부모가 <paramref name="parents"/>(함께 다시 만드는 표)에 있는 것을, 대상 소유자 이름으로 만든 <c>ADD CONSTRAINT</c> 문장.
    /// 서버가 지은 이름은 쓰지 않는다(<c>Name</c> = null). 원본은 읽기 전용 세션이고 조회만 한다.
    /// </summary>
    Task<IReadOnlyList<(string Parent, string Ddl, string? Name)>> SourceForeignKeysAsync(ISession source, string sourceOwner, string table,
                                                                                      IReadOnlySet<string> parents, string targetOwner, CancellationToken ct);

    /// <summary>
    /// 휴지통에 이 원래 이름으로 있는 표의 휴지통 이름. DROP 전후를 비교하면 이번 DROP 이 만든 이름을 안다. 비면 휴지통에 남지 않았다(영구 삭제).
    /// </summary>
    Task<HashSet<string>> RecycleBinAsync(ISession target, string table, CancellationToken ct);

    /// <summary>
    /// 원본 표 인덱스 하나의 열(위치 순, 함수 기반 인덱스는 식)을 원본 세션에서 읽는다(SQ2: 새 표에 함수 기반 인덱스를 만들 때). 기본 = 읽지 못함(null).
    /// DBMS 어셈블리는 그 DBMS 의 <see cref="ITableKeyDetailProvider"/>(Oracle 은 LONG 식을 끝까지 읽는다)로 구현한다.
    /// </summary>
    Task<IReadOnlyList<IndexColumnDetail>?> IndexColumnsAsync(ISession source, DbObjectRef table, string indexName, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<IndexColumnDetail>?>(null);
}

/// <summary>
/// 「테이블 다시 만들기」(설계 6.2.3)가 지우기 전에 받아 두는 대상 쪽 DDL. 적재가 끝난 뒤 되살린다.
/// </summary>
/// <param name="Triggers">트리거 생성 문장(<c>CREATE OR REPLACE TRIGGER …</c>). 꺼져 있던 트리거는 뒤에 <c>ALTER TRIGGER … DISABLE</c> 이 따른다</param>
/// <param name="Grants">다른 계정에 준 객체 권한(<c>GRANT … ON … TO …</c>)</param>
/// <param name="ReferencingForeignKeys">
/// 다른 표가 이 표를 참조하던 FK(<c>ALTER TABLE … ADD CONSTRAINT … FOREIGN KEY … REFERENCES …</c>, DEFERRABLE·꺼짐은 원래대로).
/// <c>DROP TABLE … CASCADE CONSTRAINTS</c> 가 함께 지우므로 되살린다. 켜져 있던 FK 는 기본(ENABLE VALIDATE)으로 보내고,
/// 부모 없는 행 때문에 실패하면(ORA-02298) 실행기가 끝에 <c>ENABLE NOVALIDATE</c> 를 붙여 다시 보낸다
/// </param>
public sealed record PreservedDdl(IReadOnlyList<string> Triggers, IReadOnlyList<string> Grants, IReadOnlyList<string> ReferencingForeignKeys)
{
    public static PreservedDdl Empty { get; } = new([], [], []);

    /// <summary>
    /// <see cref="Triggers"/> 와 같은 자리의 트리거(생성 문장과 그 뒤 DISABLE 문장 모두 같은 트리거).
    /// 되살린 트리거가 컴파일 오류(ORA-24344)로 INVALID 이면 실행기가 이것으로 끈다.
    /// </summary>
    public IReadOnlyList<DisabledObject> TriggerObjects { get; init; } = [];

    /// <summary>
    /// <see cref="ReferencingForeignKeys"/> 와 같은 자리의 FK(자식 소유자·표·이름). 실행기가 작업 안에서 함께 다시 만드는 자식 표의 FK 를 거르고
    /// (그 FK 는 <see cref="CreateTableDdl"/> 이 만든다), 부모 없는 행 수를 셀 때 쓴다. 두 목록은 같은 위치로 짝짓는다.
    /// </summary>
    public IReadOnlyList<DisabledObject> ReferencingConstraints { get; init; } = [];
}
