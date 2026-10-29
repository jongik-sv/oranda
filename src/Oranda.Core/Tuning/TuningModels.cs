using Oranda.Core.History;

namespace Oranda.Core.Tuning;

// 튜닝 조회 계약(TSK-06-01, PRD 11장 TN-01~16). 값 형만 둔다 — 조회·파서 구현은 TSK-06-02 이후.
// 각 필드의 Oracle 출처 열은 XML 주석에 적는다(@PFX@ = dba_/all_, 11g 변형은 TSK-06-02).

/// <summary>표 통계(TN-03). 출처: @PFX@_tab_statistics.</summary>
/// <param name="Owner">owner</param>
/// <param name="TableName">table_name</param>
/// <param name="NumRows">num_rows</param>
/// <param name="Blocks">blocks</param>
/// <param name="AvgRowLen">avg_row_len</param>
/// <param name="LastAnalyzed">last_analyzed(수집 안 됐으면 null)</param>
/// <param name="Stale">stale_stats = 'YES'</param>
public sealed record TableStatsRow(
    string Owner, string TableName, long? NumRows, long? Blocks, long? AvgRowLen, DateTime? LastAnalyzed, bool Stale);

/// <summary>열 통계(TN-03). 출처: @PFX@_tab_col_statistics.</summary>
/// <param name="ColumnName">column_name</param>
/// <param name="NumDistinct">num_distinct</param>
/// <param name="NumNulls">num_nulls</param>
/// <param name="Density">density</param>
/// <param name="Histogram">histogram(NONE·FREQUENCY·HEIGHT BALANCED·TOP-FREQUENCY·HYBRID)</param>
/// <param name="NumBuckets">num_buckets</param>
/// <param name="LastAnalyzed">last_analyzed</param>
public sealed record ColumnStatsRow(
    string ColumnName, long? NumDistinct, long? NumNulls, double? Density, string Histogram, long? NumBuckets, DateTime? LastAnalyzed);

/// <summary>히스토그램 버킷 하나(TN-03). 출처: @PFX@_tab_histograms.</summary>
/// <param name="EndpointNumber">endpoint_number(누적 개수 또는 버킷 번호)</param>
/// <param name="EndpointValue">endpoint_actual_value, 없으면 endpoint_value 를 문자열로</param>
/// <param name="Frequency">endpoint_number 의 앞 버킷과의 차(막대 높이)</param>
public sealed record HistogramBucket(long EndpointNumber, string? EndpointValue, long Frequency);

/// <summary>인덱스 통계(TN-03). 출처: @PFX@_ind_statistics.</summary>
/// <param name="IndexName">index_name</param>
/// <param name="Uniqueness">uniqueness</param>
/// <param name="ClusteringFactor">clustering_factor</param>
/// <param name="LeafBlocks">leaf_blocks</param>
/// <param name="DistinctKeys">distinct_keys</param>
/// <param name="BLevel">blevel</param>
/// <param name="LastAnalyzed">last_analyzed</param>
/// <param name="Stale">stale_stats = 'YES'</param>
public sealed record IndexStatsRow(
    string IndexName, string Uniqueness, long? ClusteringFactor, long? LeafBlocks, long? DistinctKeys, int? BLevel, DateTime? LastAnalyzed, bool Stale);

/// <summary>자식 커서 한 줄(TN-04). 출처: gv$sql(sql_id 로 거른 자식).</summary>
/// <param name="ChildNumber">child_number</param>
/// <param name="PlanHashValue">plan_hash_value</param>
/// <param name="IsBindSensitive">is_bind_sensitive = 'Y'</param>
/// <param name="IsBindAware">is_bind_aware = 'Y'</param>
/// <param name="IsShareable">is_shareable = 'Y'</param>
/// <param name="Executions">executions</param>
/// <param name="BufferGets">buffer_gets</param>
/// <param name="ElapsedMicros">elapsed_time(마이크로초)</param>
public sealed record ChildCursorRow(
    int ChildNumber, long PlanHashValue, bool IsBindSensitive, bool IsBindAware, bool IsShareable, long Executions, long BufferGets, long ElapsedMicros)
{
    /// <summary>RAC 인스턴스. 출처: inst_id.</summary>
    public int? InstanceId { get; init; }

    /// <summary>실행당 buffer gets(0 실행이면 null).</summary>
    public double? GetsPerExec => Executions > 0 ? (double)BufferGets / Executions : null;

    /// <summary>실행당 경과 마이크로초(0 실행이면 null).</summary>
    public double? ElapsedPerExecMicros => Executions > 0 ? (double)ElapsedMicros / Executions : null;
}

/// <summary>자식 커서가 따로 생긴 사유 하나(TN-04). 출처: gv$sql_shared_cursor 의 'Y' 인 열 하나가 한 행.</summary>
/// <param name="ChildNumber">child_number</param>
/// <param name="Reason">'Y' 인 열 이름(예: BIND_MISMATCH, ROLL_INVALID_MISMATCH)</param>
public sealed record SharedCursorReason(int ChildNumber, string Reason)
{
    /// <summary>12c+ reason 열의 XML 상세(없으면 null).</summary>
    public string? Detail { get; init; }
}

/// <summary>세션 대기 표본 한 건(TN-12, 팩 없음). 출처: gv$session.</summary>
/// <param name="SampledAt">표본 시각(클라이언트)</param>
/// <param name="Sid">sid</param>
/// <param name="SqlId">sql_id</param>
/// <param name="Event">event(대기 아니면 null)</param>
/// <param name="WaitClass">wait_class</param>
public sealed record WaitSample(DateTime SampledAt, long Sid, string? SqlId, string? Event, string? WaitClass);

/// <summary>트레이스 파일 정보(TN-07). 출처: v$diag_info 의 Default Trace File 경로.</summary>
/// <param name="Path">서버 경로</param>
/// <param name="Readable">본문을 SQL 로 읽을 수 있는가(12.2+ v$diag_trace_file_contents)</param>
/// <param name="UnavailableReason">읽을 수 없을 때 이유(11g 등), 읽을 수 있으면 null</param>
public sealed record TraceFileInfo(string Path, bool Readable, string? UnavailableReason);

/// <summary>TKPROF 식 요약의 문장 하나(TN-08). 출처: 트레이스 본문의 PARSING IN CURSOR·PARSE·EXEC·FETCH 줄.</summary>
public sealed record TraceStatement(string SqlText, string? SqlId)
{
    /// <summary>재귀 깊이(PARSING IN CURSOR 의 dep=, 0 = 사용자 문장). TSK-06-02 에서 더함.</summary>
    public int Depth { get; init; }

    public long ParseCount { get; init; }

    public long ExecuteCount { get; init; }

    public long FetchCount { get; init; }

    /// <summary>c= 의 합(마이크로초).</summary>
    public long CpuMicros { get; init; }

    /// <summary>e= 의 합(마이크로초).</summary>
    public long ElapsedMicros { get; init; }

    /// <summary>p= 물리 읽기 블록.</summary>
    public long Disk { get; init; }

    /// <summary>cr= 일관 읽기 블록.</summary>
    public long Query { get; init; }

    /// <summary>cu= 현재 읽기 블록.</summary>
    public long Current { get; init; }

    /// <summary>r= 행 수.</summary>
    public long Rows { get; init; }
}

/// <summary>대기 이벤트별 합계(트레이스의 WAIT 줄). ela= 의 합.</summary>
public sealed record TraceWaitTotal(string Event, long Count, long ElapsedMicros);

/// <summary>TKPROF 식 요약(TN-08). 문장 정렬은 화면이 한다.</summary>
public sealed record TraceSummary(IReadOnlyList<TraceStatement> Statements, IReadOnlyList<TraceWaitTotal> Waits);

/// <summary>인덱스 후보(TN-09). 계획의 FULL SCAN 단계·술어 열에서 근거를 얻는다.</summary>
/// <param name="Owner">표 소유자</param>
/// <param name="TableName">표</param>
/// <param name="Columns">후보 열(순서대로)</param>
/// <param name="BasisStepId">근거가 된 계획 단계 Id</param>
/// <param name="OverlappingIndex">앞 열이 겹치는 기존 인덱스(@PFX@_ind_columns), 없으면 null</param>
/// <param name="Ddl">CREATE INDEX 문안(보여주기만, 실행 안 함)</param>
public sealed record IndexCandidate(
    string Owner, string TableName, IReadOnlyList<string> Columns, int BasisStepId, string? OverlappingIndex, string Ddl);

/// <summary>기존 인덱스의 열 구성(겹침 확인용). 출처: @PFX@_ind_columns.</summary>
/// <param name="IndexName">index_name</param>
/// <param name="Columns">column_name 을 column_position 순으로</param>
public sealed record IndexColumnsRow(string IndexName, IReadOnlyList<string> Columns);

/// <summary>벤치마크 대상 한 개(TN-11). SELECT 만 허용한다.</summary>
/// <param name="Label">표시 이름(원본·변형 1…)</param>
/// <param name="Sql">실행할 SELECT</param>
public sealed record BenchmarkCase(string Label, string Sql)
{
    /// <summary>실행 출처(이력 <c>source</c>·정책 판단). 사람이 튜닝 도우미에서 돌리면 <see cref="HistorySource.Script"/>, AI 에이전트가 쓴 SQL 이면 <see cref="HistorySource.Agent"/>.</summary>
    public HistorySource Source { get; init; } = HistorySource.Script;

    /// <summary>
    /// true 면 반복 전체를 <c>SET TRANSACTION READ ONLY</c> 안에서 돌리고 끝나면 롤백한다(사용자 함수가 든 문장을 에이전트가 돌릴 때: 함수 안 DML 을 DB 가 막는다).
    /// 이미 열린 트랜잭션이 있어 시작하지 못하면 실행하지 않고 실패한다.
    /// </summary>
    public bool ReadOnly { get; init; }
}

/// <summary>벤치마크 결과(TN-11). 값은 반복 평균.</summary>
/// <param name="Case">대상</param>
/// <param name="Iterations">반복 수</param>
/// <param name="Elapsed">경과 평균</param>
/// <param name="LogicalReads">논리 읽기 평균. 출처: v$mystat 'session logical reads' 전후 차</param>
/// <param name="PhysicalReads">물리 읽기 평균. 출처: v$mystat 'physical reads' 전후 차</param>
/// <param name="Rows">가져온 행 수</param>
/// <param name="PlanHashValue">plan_hash_value(gv$sql), 모르면 null</param>
public sealed record BenchmarkResult(
    BenchmarkCase Case, int Iterations, TimeSpan Elapsed, long LogicalReads, long PhysicalReads, long Rows, long? PlanHashValue);

/// <summary>재작성 후보(TN-10). 규칙 기반이라 DB 조회가 없다.</summary>
/// <param name="Rule">규칙 ID(예: not-in-to-not-exists)</param>
/// <param name="Description">설명</param>
/// <param name="Sql">고친 SQL</param>
public sealed record RewriteCandidate(string Rule, string Description, string Sql);

/// <summary>AWR 계획 이력 한 줄(TN-13, license diagnostics). 출처: dba_hist_sql_plan·dba_hist_sqlstat·dba_hist_snapshot.</summary>
/// <param name="SnapId">snap_id</param>
/// <param name="SnapTime">end_interval_time</param>
/// <param name="PlanHashValue">plan_hash_value</param>
/// <param name="Executions">executions_delta</param>
/// <param name="ElapsedMicros">elapsed_time_delta</param>
/// <param name="BufferGets">buffer_gets_delta</param>
public sealed record AwrPlanRow(long SnapId, DateTime SnapTime, long PlanHashValue, long Executions, long ElapsedMicros, long BufferGets);

/// <summary>ASH 대기 분해 한 줄(TN-14, license diagnostics). 출처: gv$active_session_history 를 event 로 묶음.</summary>
/// <param name="Event">event(CPU 이면 "ON CPU")</param>
/// <param name="WaitClass">wait_class</param>
/// <param name="Samples">표본 수(대략 초)</param>
public sealed record AshWaitRow(string Event, string? WaitClass, long Samples)
{
    /// <summary>같은 질의의 전체 표본 대비 비율(0~1).</summary>
    public double Share { get; init; }
}

/// <summary>SQL Profile·Baseline·Patch 의 종류(TN-15).</summary>
public enum SqlPlanControlKind
{
    Profile,
    Baseline,
    Patch,
}

/// <summary>SQL Profile·Baseline·Patch 보기 한 줄(TN-15, 보기만). 출처: dba_sql_profiles·dba_sql_plan_baselines·dba_sql_patches.</summary>
/// <param name="Kind">종류</param>
/// <param name="Name">name / plan_name</param>
/// <param name="Status">status(ENABLED·DISABLED)</param>
/// <param name="Created">created</param>
/// <param name="SqlText">sql_text</param>
public sealed record SqlPlanControlRow(SqlPlanControlKind Kind, string Name, string Status, DateTime Created, string? SqlText)
{
    /// <summary>baseline 의 accepted 표시(다른 종류는 null).</summary>
    public bool? Accepted { get; init; }
}

/// <summary>만들 스크립트의 종류(TN-16).</summary>
public enum TuningScriptKind
{
    DbmsStats,
    TuningAdvisor,
    Baseline,
}

/// <summary>만든 스크립트(TN-16). 새 편집기 탭에 넣기만 하고 DB 로 보내지 않는다.</summary>
public sealed record TuningScript(TuningScriptKind Kind, string Body);
