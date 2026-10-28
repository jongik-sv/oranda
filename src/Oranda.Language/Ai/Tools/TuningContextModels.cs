namespace Oranda.Language.Ai.Tools;

/// <summary><c>get_tuning_context</c> 요청. <see cref="Sql"/> 과 <see cref="SqlId"/> 중 하나는 있어야 한다.</summary>
/// <param name="Sql">튜닝할 문장(없으면 <paramref name="SqlId"/> 의 계획에서 표를 찾는다)</param>
/// <param name="SqlId">공유 풀의 sql_id</param>
/// <param name="BudgetTokens">응답이 넘지 않을 토큰 어림값</param>
/// <param name="AllowExplainPlan">접속의 EXPLAIN PLAN 실효 허용(sql_id 가 없을 때만 쓴다)</param>
/// <param name="DefaultOwner">소유자를 적지 않은 이름을 풀 스키마</param>
/// <param name="CursorSchema">커서를 볼 수 있는 파싱 스키마(실행 범위의 스키마). null 이면 스키마를 따지지 않는다</param>
public sealed record TuningContextRequest(
    string? Sql, string? SqlId, int BudgetTokens, bool AllowExplainPlan, string DefaultOwner, string? CursorSchema = null);

public sealed record ContextStatement(string? SqlId, long? PlanHash, int TextChars);

public sealed record ContextStats(long? NumRows, long? Blocks, string? LastAnalyzed, bool Stale);

public sealed record ContextColumn(
    string Name, string? Type, bool? Nullable, string? Comment, long? NumDistinct, long? NumNulls, string? Histogram);

public sealed record ContextIndex(
    string Name, bool Unique, IReadOnlyList<string> Columns, int? Blevel, long? LeafBlocks, long? DistinctKeys, long? ClusteringFactor, bool? Stale);

/// <summary>
/// 참조 객체 하나. 풀 수 없거나 읽지 못한 객체는 <see cref="Problem"/> 에 이유가 있고 나머지는 비어 있다.
/// </summary>
public sealed record ContextObject(
    string Name, string Kind, ContextStats? Stats, IReadOnlyList<ContextColumn> UsedColumns, IReadOnlyList<ContextIndex> Indexes, string? Problem);

public sealed record ContextPlanFinding(int Id, string Operation, string? ObjectName, string Note);

/// <summary><c>get_tuning_context</c> 응답(설계 §5.3). 줄인 것은 <see cref="Omitted"/> 에 적는다.</summary>
public sealed record TuningContext(
    ContextStatement Statement, IReadOnlyList<ContextObject> Objects, IReadOnlyList<ContextPlanFinding> PlanFindings, IReadOnlyList<string> Omitted);
