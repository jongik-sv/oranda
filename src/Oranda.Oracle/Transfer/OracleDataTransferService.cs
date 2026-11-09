using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Transfer;

namespace Oranda.Oracle.Transfer;

/// <summary>
/// Oracle 의 내보내기·가져오기. 본문은 Core <see cref="DataTransferService"/> 에 있고 여기서는 Oracle 방언(<see cref="OracleDataTransferDialect"/>)과
/// 값 변환기(<see cref="OracleImportValueConverter"/>)만 넘긴다. 소유: TSK-06-07.
/// </summary>
[OrandaService(typeof(IDataTransferService), Dbms = Dbms.Oracle)]
internal sealed class OracleDataTransferService(IExecutionService execution, IMetadataProvider metadata)
    : DataTransferService(execution, metadata, OracleDataTransferDialect.Instance, new OracleImportValueConverter());

/// <summary>Oracle 가져오기 문장 조각: 한 행 SELECT 는 <c>FROM DUAL</c> 이 필요하고, 세이브포인트 문법은 <c>SAVEPOINT</c> / <c>ROLLBACK TO SAVEPOINT</c>.</summary>
internal sealed class OracleDataTransferDialect : IDataTransferDialect
{
    internal static readonly OracleDataTransferDialect Instance = new();

    public string SelectRow(IReadOnlyList<string> literals)
    {
        ArgumentNullException.ThrowIfNull(literals);
        return "SELECT " + string.Join(", ", literals) + " FROM DUAL";
    }

    public string SavepointStatement(string name) => "SAVEPOINT " + name;

    public string RollbackToSavepointStatement(string name) => "ROLLBACK TO SAVEPOINT " + name;
}
