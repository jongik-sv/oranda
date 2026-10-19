using Oranda.Core.Composition;
using Oranda.Core.Execution;
using Oranda.Core.Sessions;

namespace Oranda.Oracle.Execution;

/// <summary>실행 엔진(치환 변수·바인드·정책 관문·취소·스크립트·트랜잭션 추적·DBMS_OUTPUT). internal(TC-05). 소유: TSK-06-01. 빈 틀 — 기능 Task 가 구현한다.</summary>
[OrandaService(typeof(IExecutionService))]
internal sealed class OracleExecutionService : IExecutionService
{
    public Task<ExecutionResult> ExecuteAsync(ISession s, SqlStatement stmt, IReadOnlyDictionary<string, BindValue> binds, ExecutionOptions? options, CancellationToken ct)
        => throw new NotImplementedException("TSK-06-01");

    public IAsyncEnumerable<ExecutionResult> ExecuteScriptAsync(ISession s, IReadOnlyList<SqlStatement> stmts, ScriptErrorMode mode, ExecutionOptions? options, CancellationToken ct)
        => throw new NotImplementedException("TSK-06-01");
}
