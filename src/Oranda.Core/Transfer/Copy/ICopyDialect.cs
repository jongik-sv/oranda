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
    /// <summary>대상 트리거 끄기.</summary>
    SqlStatement DisableTrigger(string owner, string trigger);

    /// <summary>대상 외래키 끄기.</summary>
    SqlStatement DisableConstraint(DisabledObject fk);

    /// <summary>「테이블 다시 만들기」의 DROP(위험 명령, 대상 이름 재입력 확인).</summary>
    SqlStatement DropTable(string owner, string table);

    /// <summary>시퀀스 값을 한 번에 여러 개 소비하는 조회(바인드 <see cref="CopyCommonStatements.NextValuesBind"/> = 개수). 행을 끝까지 읽어야 그만큼 소비된다.</summary>
    SqlStatement NextValues(string owner, string sequence);

    /// <summary>원본 세션의 읽기 전용 트랜잭션 시작(같은 시점 기준 읽기).</summary>
    SqlStatement SetTransactionReadOnly();

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
}
