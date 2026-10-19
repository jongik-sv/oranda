using Oranda.Core.Execution;
using Oranda.Core.Sessions;

namespace Oranda.Core.Plans;

/// <summary>
/// 실행 계획(PLAN): EXPLAIN PLAN + DBMS_XPLAN.DISPLAY(예상), DISPLAY_CURSOR(실제). 쿼리는 카탈로그 <c>plan.*</c>.
/// EXPLAIN PLAN 도 실행 엔진(정책 관문)을 거친다. 구현: Oranda.Oracle(TSK-07-01). 세션 상세의 실제 계획도 이것을 쓴다.
/// </summary>
public interface IExecutionPlanService
{
    Task<ExecutionPlan> ExplainAsync(ISession s, SqlStatement stmt, CancellationToken ct);

    /// <param name="format">DBMS_XPLAN 형식 문자열(예: <c>ALLSTATS LAST</c>)</param>
    Task<ExecutionPlan> GetCursorPlanAsync(ISession s, CursorRef cursor, string format, CancellationToken ct);
}

/// <summary>서버 커서 식별(sql_id + child). <see cref="InstanceId"/> 는 RAC 일 때.</summary>
public sealed record CursorRef(string SqlId, int? ChildNumber = null, int? InstanceId = null);

/// <summary>위험 표시(PLAN 강조 대상).</summary>
[Flags]
public enum PlanRisk
{
    None = 0,
    FullScan = 1,
    Cartesian = 2,
    CardinalityMisestimate = 4,
}

public sealed record PlanStep(int Id, int? ParentId, int Depth, string Operation)
{
    public string? Options { get; init; }

    public string? ObjectOwner { get; init; }

    public string? ObjectName { get; init; }

    public long? Cost { get; init; }

    public long? EstimatedRows { get; init; }

    public long? ActualRows { get; init; }

    public long? Starts { get; init; }

    public TimeSpan? ActualTime { get; init; }

    public string? AccessPredicates { get; init; }

    public string? FilterPredicates { get; init; }

    public PlanRisk Risk { get; init; }
}

/// <summary>계획 트리(<see cref="Steps"/> 는 Id 순)와 DBMS_XPLAN 텍스트.</summary>
public sealed record ExecutionPlan(IReadOnlyList<PlanStep> Steps, string Text)
{
    public CursorRef? Cursor { get; init; }

    public long? PlanHashValue { get; init; }
}
