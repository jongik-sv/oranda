using Oranda.Core.Execution;
using Oranda.Core.Sessions;

namespace Oranda.Core.Composition.Dispatch;

/// <summary>세션의 DBMS 로 구현을 골라 넘긴다(D-046). 구현 등록 = <c>[OrandaService(typeof(IBulkExecutionService), Dbms = …)]</c>.</summary>
[OrandaService(typeof(IBulkExecutionService))]
public sealed class BulkExecutionServiceDispatcher : IBulkExecutionService
{
    private readonly IDbmsServiceSelector _selector;

    public BulkExecutionServiceDispatcher(IDbmsServiceSelector selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        _selector = selector;
    }

    public IAsyncEnumerable<object?[]> StreamRowsAsync(ISession s, SqlStatement query, IReadOnlyDictionary<string, BindValue> binds,
                                                       ExecutionOptions options, CancellationToken ct) =>
        DispatchRules.Deferred(() => _selector.For<IBulkExecutionService>(s).StreamRowsAsync(s, query, binds, options, ct), ct);

    public Task<BulkResult> ExecuteArrayAsync(ISession s, SqlStatement stmt, IReadOnlyList<BulkColumn> columns,
                                              IReadOnlyList<object?[]> rows, ExecutionOptions options, CancellationToken ct) =>
        _selector.For<IBulkExecutionService>(s).ExecuteArrayAsync(s, stmt, columns, rows, options, ct);
}
