using Oranda.Core.Execution;
using Oranda.Core.Transfer.Copy;

namespace Oranda.Oracle.Transfer.Copy;

/// <summary>Oracle 복사 문장(<see cref="ICopyDialect"/>). 실행기와 같은 출처(<see cref="CopyStatements"/>)를 감싼다.</summary>
internal sealed class OracleCopyDialect : ICopyDialect
{
    public static readonly OracleCopyDialect Instance = new();

    public SqlStatement DisableTrigger(string owner, string trigger) => CopyStatements.DisableTrigger(owner, trigger);

    public SqlStatement DisableConstraint(DisabledObject fk) => CopyStatements.DisableConstraint(fk);

    public SqlStatement DropTable(string owner, string table) => CopyStatements.DropTable(owner, table);

    public SqlStatement NextValues(string owner, string sequence) => CopyStatements.NextValues(owner, sequence);
}
