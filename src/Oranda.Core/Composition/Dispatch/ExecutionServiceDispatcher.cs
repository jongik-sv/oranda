using Oranda.Core.Execution;
using Oranda.Core.Sessions;

namespace Oranda.Core.Composition.Dispatch;

/// <summary>세션의 DBMS 로 구현을 골라 넘긴다(D-046). 구현 등록 = <c>[OrandaService(typeof(IExecutionService), Dbms = …)]</c>.</summary>
[OrandaService(typeof(IExecutionService))]
public sealed class ExecutionServiceDispatcher : IExecutionService
{
    private readonly IDbmsServiceSelector _selector;

    public ExecutionServiceDispatcher(IDbmsServiceSelector selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        _selector = selector;
    }

    public Task<ExecutionResult> ExecuteAsync(ISession s, SqlStatement stmt, IReadOnlyDictionary<string, BindValue> binds,
                                              ExecutionOptions? options, CancellationToken ct) =>
        _selector.For<IExecutionService>(s).ExecuteAsync(s, stmt, binds, options, ct);

    public IAsyncEnumerable<ExecutionResult> ExecuteScriptAsync(ISession s, IReadOnlyList<SqlStatement> stmts, ScriptErrorMode mode,
                                                                ExecutionOptions? options, CancellationToken ct) =>
        DispatchRules.Deferred(() => _selector.For<IExecutionService>(s).ExecuteScriptAsync(s, stmts, mode, options, ct), ct);
}
