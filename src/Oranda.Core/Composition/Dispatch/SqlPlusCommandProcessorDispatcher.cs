using Oranda.Core.Execution;
using Oranda.Core.Sessions;

namespace Oranda.Core.Composition.Dispatch;

/// <summary>세션의 DBMS 로 구현을 골라 넘긴다(D-046). 구현 등록 = <c>[OrandaService(typeof(ISqlPlusCommandProcessor), Dbms = …)]</c>.</summary>
[OrandaService(typeof(ISqlPlusCommandProcessor))]
public sealed class SqlPlusCommandProcessorDispatcher : ISqlPlusCommandProcessor
{
    private readonly IDbmsServiceSelector _selector;

    public SqlPlusCommandProcessorDispatcher(IDbmsServiceSelector selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        _selector = selector;
    }

    public Task<ExecutionResult> ExecuteAsync(ISession s, SqlStatement stmt, CancellationToken ct) =>
        _selector.For<ISqlPlusCommandProcessor>(s).ExecuteAsync(s, stmt, ct);
}
