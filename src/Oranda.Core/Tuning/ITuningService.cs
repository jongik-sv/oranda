using Oranda.Core.Monitoring;
using Oranda.Core.Plans;
using Oranda.Core.Sessions;

namespace Oranda.Core.Tuning;

/// <summary>
/// 튜닝 조회(PRD 11장, TSK-06-01 계약). 모든 조회는 SELECT 다(DML 금지). 쿼리는 카탈로그 <c>tuning.*</c>.
/// 구현: Oranda.Oracle <c>OracleTuningService</c>(TSK-06-02). 기본 구현은 지원하지 않음 — 시험용 가짜가 쓰는 것만 구현하면 된다.
/// </summary>
public interface ITuningService
{
    /// <summary>표 통계. 통계가 없는 표는 null. TN-03.</summary>
    Task<TableStatsRow?> GetTableStatsAsync(ISession s, string owner, string table, CancellationToken ct) =>
        throw new NotSupportedException();

    /// <summary>열 통계. TN-03.</summary>
    Task<IReadOnlyList<ColumnStatsRow>> GetColumnStatsAsync(ISession s, string owner, string table, CancellationToken ct) =>
        throw new NotSupportedException();

    /// <summary>한 열의 히스토그램 버킷(endpoint 순). TN-03.</summary>
    Task<IReadOnlyList<HistogramBucket>> GetHistogramAsync(ISession s, string owner, string table, string column, CancellationToken ct) =>
        throw new NotSupportedException();

    /// <summary>표의 인덱스 통계. TN-03.</summary>
    Task<IReadOnlyList<IndexStatsRow>> GetIndexStatsAsync(ISession s, string owner, string table, CancellationToken ct) =>
        throw new NotSupportedException();

    /// <summary>sql_id 의 자식 커서 목록. TN-04.</summary>
    Task<IReadOnlyList<ChildCursorRow>> GetChildCursorsAsync(ISession s, string sqlId, CancellationToken ct) =>
        throw new NotSupportedException();

    /// <summary>자식 커서가 따로 생긴 사유(자식 번호 순). TN-04.</summary>
    Task<IReadOnlyList<SharedCursorReason>> GetSharedCursorReasonsAsync(ISession s, string sqlId, CancellationToken ct) =>
        throw new NotSupportedException();

    /// <summary>자식 커서 하나의 계획(<paramref name="cursor"/> 의 ChildNumber 필수). 기존 계획 형 <see cref="ExecutionPlan"/> 을 재사용한다. TN-04.</summary>
    Task<ExecutionPlan> GetChildPlanAsync(ISession s, CursorRef cursor, string format, CancellationToken ct) =>
        throw new NotSupportedException();

    /// <summary>대기 표본 한 번(<paramref name="sqlId"/> 가 null 이면 내 세션). 팩 없음. TN-12.</summary>
    Task<IReadOnlyList<WaitSample>> SampleWaitsAsync(ISession s, string? sqlId, CancellationToken ct) =>
        throw new NotSupportedException();

    /// <summary>세션의 트레이스 파일 경로와 읽기 가능 여부. <paramref name="target"/> 가 <c>default</c> 면 이 세션(v$diag_info). TN-07.</summary>
    Task<TraceFileInfo> GetTraceFileAsync(ISession s, SessionKey target, CancellationToken ct) =>
        throw new NotSupportedException();

    /// <summary>트레이스 본문 줄(12.2+). 읽을 수 없으면 <see cref="TraceFileInfo.Readable"/> 이 거짓이다. TN-07.</summary>
    Task<IReadOnlyList<string>> ReadTraceLinesAsync(ISession s, TraceFileInfo file, CancellationToken ct) =>
        throw new NotSupportedException();

    /// <summary>표의 기존 인덱스 열 구성(후보와 겹침 확인). TN-09.</summary>
    Task<IReadOnlyList<IndexColumnsRow>> GetIndexColumnsAsync(ISession s, string owner, string table, CancellationToken ct) =>
        throw new NotSupportedException();

    /// <summary>SELECT 를 <paramref name="iterations"/> 번 실행해 전후 차를 잰다. SELECT 아니면 거부. TN-11.</summary>
    Task<BenchmarkResult> RunBenchmarkAsync(ISession s, BenchmarkCase benchmark, int iterations, CancellationToken ct) =>
        throw new NotSupportedException();

    /// <summary>AWR 계획 이력(license diagnostics). TN-13.</summary>
    Task<IReadOnlyList<AwrPlanRow>> GetAwrPlanHistoryAsync(ISession s, string sqlId, CancellationToken ct) =>
        throw new NotSupportedException();

    /// <summary>
    /// AWR 에 남은 한 plan_hash 의 계획 단계(license diagnostics, 카탈로그 <c>tuning.awr_plan_steps</c>). 두 plan_hash 비교용(TSK-06-06).
    /// <see cref="ExecutionPlan.Text"/> 는 단계를 들여쓴 글, <see cref="ExecutionPlan.PlanHashValue"/> 는 <paramref name="planHashValue"/>. 단계가 없으면 빈 계획.
    /// </summary>
    Task<ExecutionPlan> GetAwrPlanAsync(ISession s, string sqlId, long planHashValue, CancellationToken ct) =>
        throw new NotSupportedException();

    /// <summary>ASH 대기 분해(license diagnostics). <paramref name="since"/> 이후. TN-14.</summary>
    Task<IReadOnlyList<AshWaitRow>> GetAshWaitsAsync(ISession s, string sqlId, DateTime since, CancellationToken ct) =>
        throw new NotSupportedException();

    /// <summary>SQL Profile·Baseline·Patch 보기(<paramref name="sqlId"/> 의 것). 권한이 없으면 빈 목록. TN-15.</summary>
    Task<IReadOnlyList<SqlPlanControlRow>> GetSqlPlanControlsAsync(ISession s, string sqlId, CancellationToken ct) =>
        throw new NotSupportedException();

    /// <summary>sql_id 의 누적 실행 통계(gv$sql 합). 공유 풀에 없으면 null. TN-17(TSK-06-07).</summary>
    Task<SqlStatsRow?> GetSqlStatsAsync(ISession s, string sqlId, CancellationToken ct) =>
        throw new NotSupportedException();

    /// <summary>보고서 환경 절: 버전 배너·optimizer 매개변수 일부·NLS 일부. TN-17(TSK-06-07).</summary>
    Task<IReadOnlyList<TuningEnvRow>> GetTuningEnvironmentAsync(ISession s, CancellationToken ct) =>
        throw new NotSupportedException();
}
