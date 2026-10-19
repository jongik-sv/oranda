using Oranda.Core.Composition;
using Oranda.Core.Execution;
using Oranda.Core.Plans;
using Oranda.Core.Sessions;

namespace Oranda.Oracle.Plans;

/// <summary>실행 계획(EXPLAIN PLAN·DISPLAY_CURSOR, 위험 단계 표시). 소유: TSK-07-01. 빈 틀 — 기능 Task 가 구현한다.</summary>
[OrandaService(typeof(IExecutionPlanService))]
internal sealed class OracleExecutionPlanService : IExecutionPlanService
{
    public Task<ExecutionPlan> ExplainAsync(ISession s, SqlStatement stmt, CancellationToken ct)
        => throw new NotImplementedException("TSK-07-01");

    public Task<ExecutionPlan> GetCursorPlanAsync(ISession s, CursorRef cursor, string format, CancellationToken ct)
        => throw new NotImplementedException("TSK-07-01");
}
