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
