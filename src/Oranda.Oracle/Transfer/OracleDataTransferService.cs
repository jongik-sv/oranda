using Oranda.Core.Composition;
using Oranda.Core.Execution;
using Oranda.Core.Sessions;
using Oranda.Core.Transfer;

namespace Oranda.Oracle.Transfer;

/// <summary>내보내기(서버 재스트리밍)·가져오기. 소유: TSK-06-07. 빈 틀 — 기능 Task 가 구현한다.</summary>
[OrandaService(typeof(IDataTransferService))]
internal sealed class OracleDataTransferService : IDataTransferService
{
    public Task<TransferSummary> ExportAsync(ISession s, SqlStatement query, IReadOnlyDictionary<string, BindValue> binds, ExportOptions options, Stream destination, IProgress<long>? progress, CancellationToken ct)
        => throw new NotImplementedException("TSK-06-07");

    public Task<TransferSummary> ImportAsync(ISession s, ImportOptions options, Stream source, IProgress<long>? progress, CancellationToken ct)
        => throw new NotImplementedException("TSK-06-07");
}
