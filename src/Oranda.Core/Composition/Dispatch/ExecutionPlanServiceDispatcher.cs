using Oranda.Core.Execution;
using Oranda.Core.Plans;
using Oranda.Core.Sessions;

namespace Oranda.Core.Composition.Dispatch;

/// <summary>
/// 세션의 DBMS 로 구현을 골라 넘긴다(D-046). 구현 등록 = <c>[OrandaService(typeof(IExecutionPlanService), Dbms = …)]</c>.
/// 기본 구현이 있는 멤버도 고른 구현의 것(재정의했으면 그것, 아니면 인터페이스 기본)을 그대로 부른다.
/// </summary>
[OrandaService(typeof(IExecutionPlanService))]
public sealed class ExecutionPlanServiceDispatcher : IExecutionPlanService
{
    private readonly IDbmsServiceSelector _selector;

    public ExecutionPlanServiceDispatcher(IDbmsServiceSelector selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        _selector = selector;
    }

    public Task<ExecutionPlan> ExplainAsync(ISession s, SqlStatement stmt, CancellationToken ct) =>
        _selector.For<IExecutionPlanService>(s).ExplainAsync(s, stmt, ct);

    public Task<ExecutionPlan> GetCursorPlanAsync(ISession s, CursorRef cursor, string format, CancellationToken ct) =>
        _selector.For<IExecutionPlanService>(s).GetCursorPlanAsync(s, cursor, format, ct);

    public Task<CursorRef?> GetLastCursorAsync(ISession s, CancellationToken ct) =>
        _selector.For<IExecutionPlanService>(s).GetLastCursorAsync(s, ct);

    public Task<string> GetSqlMonitorReportAsync(ISession s, CursorRef cursor, bool html, CancellationToken ct) =>
        _selector.For<IExecutionPlanService>(s).GetSqlMonitorReportAsync(s, cursor, html, ct);
}
