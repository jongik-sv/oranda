using Oranda.Core.Execution;
using Oranda.Core.Transfer.Copy;

namespace Oranda.Oracle.Transfer.Copy;

/// <summary>Oracle 복사 문장(<see cref="ICopyDialect"/>). 실행기와 같은 출처(<see cref="CopyStatements"/>)를 감싼다.</summary>
internal sealed class OracleCopyDialect : ICopyDialect
{
    public static readonly OracleCopyDialect Instance = new();

    /// <summary>ORA-02298: 부모 키가 없는 자식 행이 있어 FK 를 VALIDATE 로 켜지 못함.</summary>
    internal const int ParentKeysNotFound = 2298;

    /// <summary>ORA-02449: 다른 표의 FK 가 참조하는 PK·UK 가 있어 DROP 을 거부(대상 계정에 안 보이는 FK).</summary>
    internal const int ReferencedByForeignKeys = 2449;

    /// <summary>값 자리는 바인드 이름 그대로(<c>:C1</c>) — 형식은 ODP.NET 매개변수가 맡는다. 형식과 무관하다.</summary>
    public string BindPlaceholder(string bindName, string serverType) => ":" + bindName;

    public SqlStatement DisableTrigger(string owner, string trigger) => CopyStatements.DisableTrigger(owner, trigger);

    public SqlStatement DisableConstraint(DisabledObject fk) => CopyStatements.DisableConstraint(fk);

    public SqlStatement DropTable(string owner, string table) => CopyStatements.DropTable(owner, table);

    public SqlStatement NextValues(string owner, string sequence) => CopyStatements.NextValues(owner, sequence);

    public SqlStatement SetTransactionReadOnly() => CopyStatements.SetTransactionReadOnly();

    /// <summary>
    /// 같은 시점 읽기 + 동시 복사(PF2b): <c>AS OF SCN :ORANDA_SCN</c>. 지금 SCN 은 먼저 <c>DBMS_FLASHBACK.GET_SYSTEM_CHANGE_NUMBER</c>(정확한 지금 SCN, 그 패키지의
    /// EXECUTE 권한이 있을 때), 안 되면 <c>TIMESTAMP_TO_SCN(SYSTIMESTAMP)</c>(권한이 필요 없는 SQL 함수, SCN-시각 대응 간격만큼 — 몇 초 — 앞 SCN 일 수 있다).
    /// <c>V$DATABASE.CURRENT_SCN</c> 은 V$ 조회 권한이 필요해 쓰지 않는다. 어느 쪽이든 모든 테이블이 같은 SCN 으로 읽으므로 테이블 사이는 맞는다.
    /// 다른 계정의 표는 FLASHBACK 객체 권한이나 FLASHBACK ANY TABLE 이 있어야 한다(없으면 ORA-01031 — 사전 점검이 먼저 본다).
    /// </summary>
    public CopyReadPointSupport ReadPoint => Point;

    /// <summary><see cref="ReadPoint"/> 의 값(한 번만 만든다).</summary>
    internal static readonly CopyReadPointSupport Point = new(
        "SCN",
        "AS OF SCN :{0}",
        [
            new CopyReadPointQuery("SELECT DBMS_FLASHBACK.GET_SYSTEM_CHANGE_NUMBER FROM DUAL", "DBMS_FLASHBACK.GET_SYSTEM_CHANGE_NUMBER"),
            new CopyReadPointQuery("SELECT TIMESTAMP_TO_SCN(SYSTIMESTAMP) FROM DUAL", "TIMESTAMP_TO_SCN(SYSTIMESTAMP)"),
        ],
        "FLASHBACK",
        "FLASHBACK ANY TABLE")
    {
        SnapshotTooOldErrors = "ORA-01555·ORA-08180",
        DefinitionChangedNumbers = [1466], // ORA-01466: unable to read data - table definition has changed
        InvalidPointNumbers = [8181], // ORA-08181: specified number is not a valid system change number
    };

    public SqlStatement Savepoint(string name) => CopyStatements.Savepoint(name);

    public SqlStatement RollbackToSavepoint(string name) => CopyStatements.RollbackTo(name);

    public SqlStatement EnableTrigger(string owner, string trigger) => CopyStatements.EnableTrigger(owner, trigger);

    public SqlStatement DropConstraint(DisabledObject fk) => CopyStatements.DropConstraint(fk);

    public SqlStatement EnableConstraint(DisabledObject fk, bool validate) => CopyStatements.EnableConstraint(fk, validate);

    public SqlStatement RecycleBinOn() => CopyStatements.RecycleBinOn();

    public string RestartIdentity(string owner, string table, string column) => CopyStatements.RestartIdentity(owner, table, column);

    public string AddForeignKeyWithoutValidation(string addForeignKeyDdl) => CopyStatements.AddForeignKeyWithoutValidation(addForeignKeyDdl);

    public bool IsParentKeysNotFound(DbError? dbError) => dbError?.Number == ParentKeysNotFound;

    public bool IsReferencedByForeignKeys(DbError? dbError) => dbError?.Number == ReferencedByForeignKeys;
}
