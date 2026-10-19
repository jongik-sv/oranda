using System.Runtime.CompilerServices;
using Oranda.Core.Execution;
using Oranda.Core.Sessions;

namespace Oranda.Testing;

/// <summary>
/// 받은 요청을 기록하고 <see cref="Responder"/> 의 결과를 돌려주는 실행 엔진. 기본 응답은 성공(영향 행 없음).
/// </summary>
public sealed class FakeExecutionService : IExecutionService
{
    private readonly List<ExecutionCall> _calls = [];

    public IReadOnlyList<ExecutionCall> Calls => _calls;

    public Func<ExecutionCall, ExecutionResult> Responder { get; set; } =
        call => new ExecutionResult(call.Statement, ExecutionStatus.Succeeded);

    public Task<ExecutionResult> ExecuteAsync(ISession s, SqlStatement stmt, IReadOnlyDictionary<string, BindValue> binds,
                                              ExecutionOptions? options, CancellationToken ct)
    {
        var call = new ExecutionCall(s, stmt, binds, options ?? ExecutionOptions.Default);
        _calls.Add(call);
        return Task.FromResult(Responder(call));
    }

    public async IAsyncEnumerable<ExecutionResult> ExecuteScriptAsync(ISession s, IReadOnlyList<SqlStatement> stmts,
        ScriptErrorMode mode, ExecutionOptions? options, [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stmts);
        var empty = new Dictionary<string, BindValue>();
        foreach (var stmt in stmts)
        {
            ct.ThrowIfCancellationRequested();
            var result = await ExecuteAsync(s, stmt, empty, options, ct).ConfigureAwait(false);
            yield return result;
            if (result.Status == ExecutionStatus.Failed && mode == ScriptErrorMode.Stop)
            {
                yield break;
            }
        }
    }
}

/// <summary><see cref="FakeExecutionService"/> 가 받은 요청 1건.</summary>
public sealed record ExecutionCall(ISession Session, SqlStatement Statement,
                                   IReadOnlyDictionary<string, BindValue> Binds, ExecutionOptions Options);
