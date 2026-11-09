using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Grid;
using Oranda.Core.Sessions;
using Oranda.Core.Transfer;

namespace Oranda.Core.Composition.Dispatch;

/// <summary>
/// 세션의 DBMS 로 구현을 골라 넘긴다(D-046). 구현 등록 = <c>[OrandaService(typeof(IDataTransferService), Dbms = …)]</c>.
/// 세션이 없는 <see cref="ExportRowsAsync"/>(손에 든 행을 파일로 쓰기)는 Oracle 구현에 맡긴다.
/// </summary>
[OrandaService(typeof(IDataTransferService))]
public sealed class DataTransferServiceDispatcher : IDataTransferService
{
    private readonly IDbmsServiceSelector _selector;

    public DataTransferServiceDispatcher(IDbmsServiceSelector selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        _selector = selector;
    }

    public Task<TransferSummary> ExportAsync(ISession s, SqlStatement query, IReadOnlyDictionary<string, BindValue> binds,
                                             ExportOptions options, Stream destination, IProgress<long>? progress,
                                             CancellationToken ct) =>
        _selector.For<IDataTransferService>(s).ExportAsync(s, query, binds, options, destination, progress, ct);

    // DBMS 무관: Oracle 구현에 맡긴다(T2 에서 재검토)
    public Task<TransferSummary> ExportRowsAsync(IReadOnlyList<ResultColumn> columns, IAsyncEnumerable<IReadOnlyList<object?>> rows,
                                                 ExportOptions options, Stream destination, IProgress<long>? progress,
                                                 CancellationToken ct) =>
        _selector.Get<IDataTransferService>(Dbms.Oracle).ExportRowsAsync(columns, rows, options, destination, progress, ct);

    public Task<TransferSummary> ImportAsync(ISession s, ImportOptions options, Stream source, IProgress<long>? progress,
                                             CancellationToken ct) =>
        _selector.For<IDataTransferService>(s).ImportAsync(s, options, source, progress, ct);
}
