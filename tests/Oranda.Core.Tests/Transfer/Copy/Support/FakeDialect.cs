using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Transfer.Copy;

namespace Oranda.Core.Tests.Transfer.Copy;

/// <summary>문장 글만 만드는 방언(값 자리 = <c>:이름</c>).</summary>
internal sealed class FakeDialect : ICopyDialect
{
    public bool IsServerName(string name, string owner) => CreateTableDdl.IsServerName(name);

    private static SqlStatement Ddl(string text) => new(text, default, StatementKind.Ddl);

    public string BindPlaceholder(string bindName, string serverType) => ":" + bindName;

    public SqlStatement DisableTrigger(string owner, string trigger) => Ddl($"ALTER TRIGGER \"{owner}\".\"{trigger}\" DISABLE");

    public SqlStatement DisableConstraint(DisabledObject fk) => Ddl($"ALTER TABLE \"{fk.Owner}\".\"{fk.Table}\" DISABLE CONSTRAINT \"{fk.Name}\"");

    public SqlStatement DropTable(string owner, string table) => Ddl($"DROP TABLE \"{owner}\".\"{table}\"");

    public SqlStatement NextValues(string owner, string sequence) => throw new NotSupportedException();

    public SqlStatement SetTransactionReadOnly() => new("SET TRANSACTION READ ONLY", default, StatementKind.TransactionControl);

    public SqlStatement Savepoint(string name) => new("SAVEPOINT " + name, default, StatementKind.TransactionControl);

    public SqlStatement RollbackToSavepoint(string name) => new("ROLLBACK TO SAVEPOINT " + name, default, StatementKind.TransactionControl);

    public SqlStatement EnableTrigger(string owner, string trigger) => Ddl($"ALTER TRIGGER \"{owner}\".\"{trigger}\" ENABLE");

    public SqlStatement DropConstraint(DisabledObject fk) => Ddl($"ALTER TABLE \"{fk.Owner}\".\"{fk.Table}\" DROP CONSTRAINT \"{fk.Name}\"");

    public SqlStatement EnableConstraint(DisabledObject fk, bool validate) => Ddl($"ALTER TABLE \"{fk.Owner}\".\"{fk.Table}\" ENABLE CONSTRAINT \"{fk.Name}\"");

    public SqlStatement RecycleBinOn() => throw new NotSupportedException();

    public string RestartIdentity(string owner, string table, string column) => throw new NotSupportedException();

    public string AddForeignKeyWithoutValidation(string addForeignKeyDdl) => addForeignKeyDdl;

    public bool IsParentKeysNotFound(DbError? dbError) => false;

    public bool IsReferencedByForeignKeys(DbError? dbError) => false;
}
