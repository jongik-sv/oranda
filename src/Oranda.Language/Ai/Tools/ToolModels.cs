namespace Oranda.Language.Ai.Tools;

// 도구 응답 모양(설계 §5.2·§5.3). 직렬화 때 snake_case 로 바뀐다. 날짜는 yyyy-MM-dd.
// 행 값(히스토그램 끝값·바인드 값)은 어떤 응답에도 싣지 않는다. 값을 줄지는 AgentGate 의 허용 수준이 따로 정한다.

/// <summary>도구가 거부하거나 입력이 잘못됐을 때. 메시지는 LLM 이 읽는다(무엇을 하면 되는지 포함).</summary>
public sealed class AiToolException(string message) : InvalidOperationException(message);

public sealed record ColumnDescription(string Name, string Type, bool Nullable, string? Default, string? Comment);

public sealed record ConstraintDescription(string Name, string Kind, IReadOnlyList<string> Columns, string? References);

public sealed record IndexSummary(string Name, bool Unique, string? Kind, IReadOnlyList<string> Columns);

/// <summary><c>describe_object</c> 응답.</summary>
public sealed record ObjectDescription(
    string Name, string Kind, string? Comment,
    IReadOnlyList<ColumnDescription> Columns, IReadOnlyList<ConstraintDescription> Constraints, IReadOnlyList<IndexSummary> Indexes);

public sealed record IndexColumnDescription(string Column, bool Descending, string? Expression);

/// <summary><c>get_index_info</c> 의 인덱스 하나. 모르는 값(보이기·분할 여부)은 null.</summary>
public sealed record IndexDescription(
    string Name, bool Unique, string? Kind, IReadOnlyList<IndexColumnDescription> Columns,
    string? Visibility, bool? Partitioned, int? Blevel, long? LeafBlocks, long? DistinctKeys, long? ClusteringFactor,
    string? LastAnalyzed, bool Stale);

public sealed record ColumnStatsDescription(string Name, long? NumDistinct, long? NumNulls, double? Density, string Histogram, long? NumBuckets);

/// <summary><c>get_table_stats</c> 응답. 통계가 없으면 <see cref="HasStats"/> 가 거짓.</summary>
public sealed record TableStatsDescription(
    string Name, bool HasStats, long? NumRows, long? Blocks, long? AvgRowLen, string? LastAnalyzed, bool Stale,
    IReadOnlyList<ColumnStatsDescription> Columns);

public sealed record HistogramBucketCount(int Bucket, long Count, string? Endpoint = null);

/// <summary>
/// <c>get_histogram</c> 응답: 버킷별 개수. 끝값(<c>endpoint</c>, 실제 열 값)은 호출이 요청했을 때만 싣고 그 전에는 null 이다. 버킷이 <c>maxBuckets</c> 를 넘으면 개수가 큰 순으로 줄이고 <see cref="OmittedBuckets"/> 에 적는다.
/// </summary>
public sealed record HistogramDescription(
    string Column, string Kind, long BucketCount, long TotalCount, IReadOnlyList<HistogramBucketCount> Buckets, int OmittedBuckets);

public sealed record PlanStepDescription(
    int Id, int Depth, string Operation, string? ObjectName, long? Cost, long? ERows, long? ARows, long? Starts,
    string? Access, string? Filter, string? Risk);

/// <summary><c>get_cursor_plan</c>·<c>explain_plan</c> 응답.</summary>
public sealed record PlanDescription(string? SqlId, int? Child, long? PlanHash, IReadOnlyList<PlanStepDescription> Steps);

public sealed record ChildCursorDescription(
    int Child, long PlanHash, bool BindSensitive, bool BindAware, bool Shareable, long Executions, long BufferGets, double ElapsedMs,
    IReadOnlyList<string> MismatchReasons);

/// <summary><c>get_sql_stats</c> 응답. 공유 풀에 없으면 <see cref="Found"/> 가 거짓이다. 실행이 아직 한 번도 끝나지 않았으면(executions 0) 실행당 값은 null.</summary>
public sealed record SqlStatsDescription(
    bool Found, int Children, long Executions, double? ElapsedMsPerExec, double? CpuMsPerExec, double? BufferGetsPerExec,
    double? DiskReadsPerExec, double? RowsPerExec);

public sealed record BenchmarkVariant(string Label, string Sql);

public sealed record BenchmarkDescription(
    string Label, int Repeat, double ElapsedMs, long LogicalReads, long PhysicalReads, long Rows, long? PlanHash, string? SqlId = null);
