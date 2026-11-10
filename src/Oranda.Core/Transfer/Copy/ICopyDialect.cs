using Oranda.Core.Execution;

namespace Oranda.Core.Transfer.Copy;

/// <summary>
/// 복사가 보내는 문장 가운데 DBMS 마다 글이 다른 것(DDL·트랜잭션 제어·시퀀스 소비)과, 실행기가 결과를 보고 다음 문장을 고르는 그 DBMS 의 오류 판정.
/// 사전 점검의 미리 보기와 실행기가 같은 출처를 써야 한다: 화면이 미리 받은 확인은 글을 서수로 비교하므로 한 글자라도 다르면 확인이 묻힌다.
/// 조회·DELETE·배열 DML·COMMIT·ROLLBACK 은 <see cref="CopyCommonStatements"/>.
/// 문장만 만들고 실행하지 않는다. 구현은 DBMS 어셈블리에 두고 하위 형식이 생성자에서 넘긴다. 키 서비스가 아니다.
/// </summary>
public interface ICopyDialect
{
    /// <summary>
    /// 배열 DML(INSERT·MERGE)의 값 자리 글. <paramref name="bindName"/> 은 쌍점 없는 바인드 이름(<see cref="CopyCommonStatements.BindNames"/>, <c>C1</c>),
    /// <paramref name="serverType"/> 은 그 열의 원본 서버 형식(<c>ColumnInfo.DataType</c>, 괄호가 붙어도 된다 — 값이 원본 형식의 대량 값이다).
    /// 바인드 값이 글자로 가는 DBMS 는 여기서 변환 함수로 감싼다(Tibero <c>TO_DATE(:C1, …)</c>). Oracle 은 <c>:C1</c> 그대로다.
    /// </summary>
    string BindPlaceholder(string bindName, string serverType);

    /// <summary>
    /// 교차 복사(원본 DBMS ≠ 대상 DBMS)의 값 자리 글. <paramref name="bindType"/> 은 원본 형식을 이 DBMS 로 옮긴 바인드 형식(<c>CrossTypeMap</c>, D3)이다.
    /// 같은 DBMS 로는 쓰지 않는 형식을 교차 쓰기로만 보내는 DBMS(Tibero TIMESTAMP WITH TIME ZONE)가 덮는다. 기본 = <see cref="BindPlaceholder"/>.
    /// </summary>
    string CrossBindPlaceholder(string bindName, string bindType) => BindPlaceholder(bindName, bindType);

    /// <summary>대상 트리거 끄기.</summary>
    SqlStatement DisableTrigger(string owner, string trigger);

    /// <summary>대상 외래키 끄기.</summary>
    SqlStatement DisableConstraint(DisabledObject fk);

    /// <summary>「테이블 다시 만들기」의 DROP(위험 명령, 대상 이름 재입력 확인).</summary>
    SqlStatement DropTable(string owner, string table);

    /// <summary>
    /// 「없으면 만들기」로 방금 만든 표를 적재 실패 뒤 휴지통 없이 지우는 DROP(<c>DROP TABLE … PURGE</c>). 기본 구현 = Oracle·Tibero 공통 글
    /// (<see cref="CopyCommonStatements.DropCreatedTable"/>).
    /// </summary>
    SqlStatement DropCreatedTable(string owner, string table) => CopyCommonStatements.DropCreatedTable(owner, table);

    /// <summary>시퀀스 값을 한 번에 여러 개 소비하는 조회(바인드 <see cref="CopyCommonStatements.NextValuesBind"/> = 개수). 행을 끝까지 읽어야 그만큼 소비된다.</summary>
    SqlStatement NextValues(string owner, string sequence);

    /// <summary>원본 세션의 읽기 전용 트랜잭션 시작(같은 시점 기준 읽기).</summary>
    SqlStatement SetTransactionReadOnly();

    /// <summary>
    /// 원본으로 쓸 때 동시 복사 레인 여럿이 한 시점으로 읽는 시점 지정 읽기(PF2b, Oracle <c>AS OF SCN</c>). null = 쓰지 않음 — 같은 시점 읽기면 한 테이블씩 복사한다.
    /// <see cref="CopyReadPointDbms.Supports"/> 와 같아야 한다.
    /// </summary>
    CopyReadPointSupport? ReadPoint => null;

    /// <summary>세이브포인트 걸기(<see cref="CopyCommonStatements.BatchSavepoint"/>·<see cref="CopyCommonStatements.TableSavepoint"/>).</summary>
    SqlStatement Savepoint(string name);

    /// <summary>세이브포인트로 되돌리기.</summary>
    SqlStatement RollbackToSavepoint(string name);

    /// <summary>끈 트리거 다시 켜기.</summary>
    SqlStatement EnableTrigger(string owner, string trigger);

    /// <summary>다시 만들 표를 참조하는 FK 를 DROP 전에 떼어 낸다(적재 뒤 받아 둔 DDL 로 다시 붙인다).</summary>
    SqlStatement DropConstraint(DisabledObject fk);

    /// <summary>끈 FK 다시 켜기. <paramref name="validate"/> 가 거짓이면 부모 없는 자식 행이 있어도 켠다.</summary>
    SqlStatement EnableConstraint(DisabledObject fk, bool validate);

    /// <summary>이 세션에서 DROP 한 표가 휴지통에 남게 한다(커밋하지 않는 세션 설정).</summary>
    SqlStatement RecycleBinOn();

    /// <summary>다시 만든 표의 IDENTITY 열 다음 값을 적재한 최대값 뒤로 옮기는 DDL 글(실패하면 이 글을 안내에 그대로 넣는다).</summary>
    string RestartIdentity(string owner, string table, string column);

    /// <summary>
    /// FK 를 붙이는 DDL 글(<see cref="ICopyRecreateStep"/> 이 만든 <c>ALTER TABLE … ADD … FOREIGN KEY …</c>)을 부모 없는 행이 있어도 켜지는 글로.
    /// <see cref="IsParentKeysNotFound"/> 로 실패했을 때만 다시 보낸다.
    /// </summary>
    string AddForeignKeyWithoutValidation(string addForeignKeyDdl);

    /// <summary>FK 를 검증하며 켜다 부모 키가 없는 자식 행 때문에 실패했는가(Oracle ORA-02298). 오류가 없으면 false.</summary>
    bool IsParentKeysNotFound(DbError? dbError);

    /// <summary>다른 표의 FK 가 참조하는 PK·UK 가 있어 DROP 을 거부했는가(대상 계정에 안 보이는 FK, Oracle ORA-02449). 오류가 없으면 false.</summary>
    bool IsReferencedByForeignKeys(DbError? dbError);

    // ---- 안내 글에 들어가는 서버 오류 이름·휴지통 문장. 기본 구현 = Oracle 의 글(Oracle 방언은 따로 구현하지 않는다). ----
    // 번호를 실측으로 확인하지 못한 DBMS 는 이름을 null 로 두어 번호 없는 일반 문구를 낸다(번호를 추측해 넣지 않는다).

    /// <summary>DROP 이 「다른 계정에 보이지 않는 FK 가 참조함」 으로 거부됐을 때 안내에 붙는 오류 이름(Oracle <c>ORA-02449</c>). 모르면 null.</summary>
    string? ReferencedByForeignKeysErrorName => "ORA-02449";

    /// <summary>되살린 트리거가 컴파일 오류로 INVALID 일 때 안내에 붙는 오류 이름(Oracle <c>ORA-24344</c>). 이 오류가 없거나 모르면 null.</summary>
    string? TriggerCompileErrorName => "ORA-24344";

    /// <summary>원본 읽기가 이전 시점 데이터를 잃었을 때(<c>DbErrorKind.SnapshotTooOld</c>) 안내에 붙는 오류 이름(Oracle <c>ORA-01555</c>). 모르면 null.</summary>
    string? SnapshotTooOldErrorName => "ORA-01555";

    /// <summary>
    /// 지운 표를 휴지통에서 되돌릴 때(<c>FLASHBACK TABLE … TO BEFORE DROP</c>) 무엇이 함께 돌아오는지 말하는 괄호 문장(앞 공백 없음, 괄호 포함).
    /// Oracle 은 휴지통 이름이 <c>BIN$…</c> 이고 트리거·인덱스·권한이 함께 돌아오지만 이 표를 참조하던 FK 는 돌아오지 않는다.
    /// </summary>
    string FlashbackRestoreNote =>
        "(트리거는 BIN$ 이름으로, 인덱스·권한과 함께 돌아옵니다. 이 테이블을 참조하던 FK 는 돌아오지 않습니다)";

    /// <summary>
    /// 컴파일 오류가 있는 트리거 CREATE 를 오류 없이 받는 서버(받으면 INVALID 로 남고 켜진 채 있다)에서 되살린 트리거의 컴파일 상태를 읽는 조회 글.
    /// 글은 <c>SELECT</c> 로 시작하고 바인드는 <c>OWN</c>(트리거 소유자)·<c>NAME</c>, 결과 = 행 하나·열 하나(<c>VALID</c>/<c>INVALID</c>).
    /// 실행기는 되살린 트리거마다 이 조회로 INVALID 인지 보고 INVALID 이면 끈다. 기본 null = 조회하지 않음(Oracle 은 ORA-24344 오류로 알려 준다).
    /// </summary>
    string? TriggerStatusSql => null;

    /// <summary>
    /// 이름 없이 만든 제약·인덱스에 서버가 지어 준 이름인가(다시 만들 때 이름을 명시하지 않고 서버가 새로 짓게 둔다). <paramref name="owner"/> = 그 제약이 있던 표의 소유자.
    /// 기본 구현 = Oracle(<c>SYS_…</c>·<c>BIN$…</c>, 소유자와 무관).
    /// </summary>
    bool IsServerName(string name, string owner) => CreateTableDdl.IsServerName(name);

    /// <summary>
    /// 이 표를 참조하던 자식 표를 이 실행에서 먼저 지운 뒤 부모 DROP 이 「참조하는 FK 가 있음」 으로 거부됐을 때 붙이는 설명(앞 공백 없음). 기본 null = 덧붙일 말 없음(Oracle).
    /// 지운 자식 표가 휴지통에 FK 를 쥔 채 남는 DBMS 가 채운다. <paramref name="children"/> = 먼저 지운 자식 표 이름(<c>계정.표</c>).
    /// </summary>
    string? RecycledChildForeignKeyNote(IReadOnlyList<string> children) => null;
}
