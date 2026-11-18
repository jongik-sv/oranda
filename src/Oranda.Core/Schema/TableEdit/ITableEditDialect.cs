using Oranda.Core.Connection;
using Oranda.Core.Metadata;

namespace Oranda.Core.Schema.TableEdit;

/// <summary>
/// 테이블 편집기의 DBMS 차이(설계 8.1·12장·18.8). 문장 꼴(설계 8.2)은 Core <see cref="TableEditScriptBuilder"/> 가 만들고,
/// DBMS 가 다른 곳(형식 목록·이름 한도·거짓 차이 정규화·기능 지원)만 이 인터페이스로 뺀다. 구현은 Oracle·Tibero 공급자가
/// <c>[OrandaService(typeof(ITableEditDialect), Dbms = …)] internal sealed</c> 로 두고, 쓰는 쪽은 <c>DbmsServices.For&lt;T&gt;(services, session)</c> 로 꺼낸다
/// (<see cref="IColumnReorderScripts"/> 와 같은 꼴). 기본 구현이 있는 멤버는 Oracle 규칙이다(<see cref="TableEditDefaults"/>).
/// </summary>
public interface ITableEditDialect
{
    /// <summary>이 구현이 맡는 DBMS.</summary>
    Dbms Dbms { get; }

    /// <summary>형식 칸 목록(설계 7.2). LONG 은 넣지 않는다(기존 열 표시만).</summary>
    IReadOnlyList<string> TypeNames { get; }

    /// <summary>식별자 바이트 한도(30 또는 128). 서버를 모르면(null) 작은 쪽.</summary>
    int MaxIdentifierBytes(ServerInfo? server);

    /// <summary>가상 식 변경을 <c>MODIFY (c AS (expr))</c> 로 할 수 있나(설계 8.2). 아니면 DROP + ADD 와 ⚠.</summary>
    bool SupportsVirtualColumnModify { get; }

    /// <summary>DROP 이 휴지통을 거치지 않고 영구인가(Tibero 기본, 설계 12장). DROP ⚠ 문구가 달라진다.</summary>
    bool PurgesOnDrop { get; }

    /// <summary>
    /// 기능을 못 쓰는 이유의 문자열 키(<c>strings-*.json</c>). 쓸 수 있으면 null. 화면은 칸·단추를 흐리게 두고 이 이유를 보인다(설계 18.8).
    /// 버전·에디션에 따라 다르면 <paramref name="server"/> 로 가른다(모르면 null — 구현은 보수적으로 판단).
    /// </summary>
    string? UnsupportedReasonKey(TableEditFeature feature, ServerInfo? server);

    /// <summary>서버가 지은 이름인가(SYS_C… 등, TE-8). 기본 = Oracle 규칙.</summary>
    bool IsServerName(string name) => TableEditDefaults.IsServerName(name);

    /// <summary>따옴표 없이 쓰면 안 되는 예약어인가(대문자 이름을 받는다). 기본 = Oracle SQL 예약어.</summary>
    bool IsReservedWord(string upperName) => TableEditDefaults.IsReservedWord(upperName);

    /// <summary>비교용 형식 표기(거짓 차이 정규화, 설계 8.4: 대소문자·NUMBER 기본 정밀도·길이 기준). 문장에는 쓰지 않는다.</summary>
    string NormalizeType(ColumnDraft column) => TableEditDefaults.NormalizeType(column);

    /// <summary>비교용 기본값 표기(앞뒤 공백·줄바꿈, 빈 값 = null).</summary>
    string? NormalizeDefault(string? serverText) => TableEditDefaults.NormalizeDefault(serverText);

    /// <summary>비교용 가상 식·인덱스 식 표기(서버가 붙인 따옴표·대문자, 공백).</summary>
    string? NormalizeExpression(string? serverText) => TableEditDefaults.NormalizeExpression(serverText);

    /// <summary>비교용 CHECK 조건 표기.</summary>
    string? NormalizeCondition(string? serverText) => TableEditDefaults.NormalizeCondition(serverText);

    /// <summary>파티션 문장 하나를 못 쓰는 이유 키(설계 18.4·18.8). 쓸 수 있으면 null. 기본 = Oracle(모두 됨).</summary>
    string? UnsupportedPartitionReasonKey(Partitions.PartitionOperation operation, Partitions.PartitionMethod method, ServerInfo? server) => null;

    /// <summary>
    /// 나누기·합치기에 <c>UPDATE INDEXES</c> 를 붙이면 전역 인덱스도 쓸 수 있게 남나. 아니면 문장 빌더가 그 뒤에 전역 인덱스 <c>REBUILD</c> 를 붙인다
    /// (Tibero 실측 19: 로컬은 남고 전역은 UNUSABLE). 기본 = Oracle(남음).
    /// </summary>
    bool KeepsGlobalIndexesOnSplitMerge => true;

    /// <summary>
    /// 해시 하위 파티션을 개수(<c>SUBPARTITIONS n</c>) 대신 이름 템플릿(<c>SUBPARTITION TEMPLATE (SUBPARTITION SP1, …)</c>)으로 쓴다(PA-11).
    /// Tibero 실측(2026-11-18): <c>SUBPARTITIONS n</c> = TBR-7001, 템플릿은 됨. 기본 = Oracle(개수).
    /// </summary>
    bool HashSubpartitionsAsTemplate => false;

    /// <summary>일반 → 파티션을 <c>MODIFY PARTITION BY … ONLINE</c> 한 문장으로 하나(TE-41). 아니면 테이블 다시 만들기. 기본 = Oracle 12.2 이상.</summary>
    bool SupportsOnlinePartitionConversion(ServerInfo? server) => Partitions.PartitionDefaults.SupportsOnlinePartitionConversion(server);

    /// <summary>
    /// 테이블 압축 절(설계 18.2: 새 테이블 저장 절·MOVE). 기본 = Oracle(12c 이상 <c>ROW STORE COMPRESS BASIC|ADVANCED</c>, 11g·모름 <c>COMPRESS BASIC</c>·<c>COMPRESS FOR OLTP</c>),
    /// <see cref="TableCompression.None"/> = <c>NOCOMPRESS</c>. Tibero 는 <c>COMPRESS</c>.
    /// </summary>
    string CompressionClause(TableCompression compression, ServerInfo? server) => TableEditDefaults.CompressionClause(compression, server);
}

/// <summary>
/// 방언마다 지원이 다를 수 있는 편집 기능(설계 12장·18장). 1차 기본 편집(열 추가·지우기·MODIFY·주석·제약·인덱스)은 넣지 않는다 — 두 DBMS 모두 된다.
/// 값을 더할 때는 끝에 덧붙인다.
/// </summary>
public enum TableEditFeature
{
    /// <summary>가상 열 추가(12장 1).</summary>
    VirtualColumn,

    /// <summary>가상 식 변경 <c>MODIFY (c AS (expr))</c>(8.2).</summary>
    VirtualColumnModify,

    /// <summary><c>RENAME COLUMN</c>(12장 2).</summary>
    RenameColumn,

    /// <summary><c>ALTER INDEX … RENAME TO</c>(12장 2).</summary>
    RenameIndex,

    /// <summary><c>ENABLE/DISABLE CONSTRAINT</c>(12장 4).</summary>
    ConstraintEnableDisable,

    /// <summary>FK <c>ON DELETE SET NULL</c>(12장 4).</summary>
    ForeignKeyOnDeleteSetNull,

    /// <summary>인덱스 열 DESC(12장 5).</summary>
    DescendingIndex,

    /// <summary>테이블 이름 바꾸기(18.1).</summary>
    RenameTable,

    /// <summary>신원 열(18.5).</summary>
    IdentityColumn,

    /// <summary>숨김 열(18.5).</summary>
    InvisibleColumn,

    /// <summary>도메인 열(18.5, 23ai).</summary>
    DomainColumn,

    /// <summary>함수 기반 인덱스(18.6).</summary>
    FunctionBasedIndex,

    /// <summary>비트맵 인덱스(18.6).</summary>
    BitmapIndex,

    /// <summary>전역 임시 테이블 만들기(18.10).</summary>
    TemporaryTable,

    /// <summary>인덱스 구성 테이블 만들기(18.10).</summary>
    IndexOrganizedTable,

    /// <summary>저장 속성(18.2).</summary>
    StorageAttributes,

    /// <summary>LOB 저장 절(18.3).</summary>
    LobStorage,

    /// <summary>파티션 편집(18.4).</summary>
    Partitioning,

    /// <summary>일반 → 파티션 테이블 바꾸기(18.4, TE-41).</summary>
    PartitionConversion,

    /// <summary>형식 변경 자동 데이터 변환(18.9).</summary>
    ConvertingTypeChange,

    /// <summary><c>ALTER TABLE … MOVE ONLINE</c>(18.2, Oracle 12.2+ Enterprise). 안 되면 ONLINE 을 빼고 ⚠ 「그동안 DML 잠금」 — 막지 않는다.</summary>
    OnlineMove,

    /// <summary>일반 테이블 <c>MOVE … UPDATE INDEXES</c>(18.2, Oracle 12.2+). 안 되면 빼고 MOVE 뒤 인덱스마다 <c>ALTER INDEX … REBUILD</c> — 막지 않는다.</summary>
    MoveUpdateIndexes,

    /// <summary>LOB 저장 방식 낱말 <c>SECUREFILE</c>·<c>BASICFILE</c>(18.2, Oracle 11g+). Tibero 는 세그먼트 이름으로 읽혀 쓰지 않는다 — 방식 칸 흐림.</summary>
    SecureFileLob,

    /// <summary>고급 행 압축(<see cref="TableCompression.Advanced"/>, 18.2).</summary>
    AdvancedRowCompression,

    /// <summary>기본 테이블 압축(<see cref="TableCompression.Basic"/>, 18.2). Oracle 은 Enterprise 만(11g XE 실측 ORA-00439), Tibero 는 됨.</summary>
    BasicTableCompression,
}
