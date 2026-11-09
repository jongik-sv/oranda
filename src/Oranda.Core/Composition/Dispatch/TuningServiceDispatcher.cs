using Oranda.Core.Monitoring;
using Oranda.Core.Plans;
using Oranda.Core.Sessions;
using Oranda.Core.Tuning;

namespace Oranda.Core.Composition.Dispatch;

/// <summary>
/// 세션의 DBMS 로 구현을 골라 넘긴다(D-046). 구현 등록 = <c>[OrandaService(typeof(ITuningService), Dbms = …)]</c>.
/// 멤버마다 기본 구현(지원하지 않음)이 있다 — 고른 구현이 재정의했으면 그것, 아니면 인터페이스 기본을 그대로 부른다.
/// </summary>
[OrandaService(typeof(ITuningService))]
public sealed class TuningServiceDispatcher : ITuningService
{
    private readonly IDbmsServiceSelector _selector;

    public TuningServiceDispatcher(IDbmsServiceSelector selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        _selector = selector;
    }

    public Task<TableStatsRow?> GetTableStatsAsync(ISession s, string owner, string table, CancellationToken ct) =>
        _selector.For<ITuningService>(s).GetTableStatsAsync(s, owner, table, ct);

    public Task<IReadOnlyList<ColumnStatsRow>> GetColumnStatsAsync(ISession s, string owner, string table, CancellationToken ct) =>
        _selector.For<ITuningService>(s).GetColumnStatsAsync(s, owner, table, ct);

    public Task<IReadOnlyList<HistogramBucket>> GetHistogramAsync(ISession s, string owner, string table, string column, CancellationToken ct) =>
        _selector.For<ITuningService>(s).GetHistogramAsync(s, owner, table, column, ct);

    public Task<IReadOnlyList<IndexStatsRow>> GetIndexStatsAsync(ISession s, string owner, string table, CancellationToken ct) =>
        _selector.For<ITuningService>(s).GetIndexStatsAsync(s, owner, table, ct);

    public Task<IReadOnlyList<ChildCursorRow>> GetChildCursorsAsync(ISession s, string sqlId, CancellationToken ct) =>
        _selector.For<ITuningService>(s).GetChildCursorsAsync(s, sqlId, ct);

    public Task<IReadOnlyList<SharedCursorReason>> GetSharedCursorReasonsAsync(ISession s, string sqlId, CancellationToken ct) =>
        _selector.For<ITuningService>(s).GetSharedCursorReasonsAsync(s, sqlId, ct);

    public Task<ExecutionPlan> GetChildPlanAsync(ISession s, CursorRef cursor, string format, CancellationToken ct) =>
        _selector.For<ITuningService>(s).GetChildPlanAsync(s, cursor, format, ct);

    public Task<IReadOnlyList<WaitSample>> SampleWaitsAsync(ISession s, string? sqlId, CancellationToken ct) =>
        _selector.For<ITuningService>(s).SampleWaitsAsync(s, sqlId, ct);

    public Task<TraceFileInfo> GetTraceFileAsync(ISession s, SessionKey target, CancellationToken ct) =>
        _selector.For<ITuningService>(s).GetTraceFileAsync(s, target, ct);

    public Task<IReadOnlyList<string>> ReadTraceLinesAsync(ISession s, TraceFileInfo file, CancellationToken ct) =>
        _selector.For<ITuningService>(s).ReadTraceLinesAsync(s, file, ct);

    public Task<IReadOnlyList<IndexColumnsRow>> GetIndexColumnsAsync(ISession s, string owner, string table, CancellationToken ct) =>
        _selector.For<ITuningService>(s).GetIndexColumnsAsync(s, owner, table, ct);

    public Task<BenchmarkResult> RunBenchmarkAsync(ISession s, BenchmarkCase benchmark, int iterations, CancellationToken ct) =>
        _selector.For<ITuningService>(s).RunBenchmarkAsync(s, benchmark, iterations, ct);

    public Task<IReadOnlyList<AwrPlanRow>> GetAwrPlanHistoryAsync(ISession s, string sqlId, CancellationToken ct) =>
        _selector.For<ITuningService>(s).GetAwrPlanHistoryAsync(s, sqlId, ct);

    public Task<ExecutionPlan> GetAwrPlanAsync(ISession s, string sqlId, long planHashValue, CancellationToken ct) =>
        _selector.For<ITuningService>(s).GetAwrPlanAsync(s, sqlId, planHashValue, ct);

    public Task<IReadOnlyList<AshWaitRow>> GetAshWaitsAsync(ISession s, string sqlId, DateTime since, CancellationToken ct) =>
        _selector.For<ITuningService>(s).GetAshWaitsAsync(s, sqlId, since, ct);

    public Task<IReadOnlyList<SqlPlanControlRow>> GetSqlPlanControlsAsync(ISession s, string sqlId, CancellationToken ct) =>
        _selector.For<ITuningService>(s).GetSqlPlanControlsAsync(s, sqlId, ct);

    public Task<SqlStatsRow?> GetSqlStatsAsync(ISession s, string sqlId, CancellationToken ct) =>
        _selector.For<ITuningService>(s).GetSqlStatsAsync(s, sqlId, ct);

    public Task<IReadOnlyList<TuningEnvRow>> GetTuningEnvironmentAsync(ISession s, CancellationToken ct) =>
        _selector.For<ITuningService>(s).GetTuningEnvironmentAsync(s, ct);

    public Task<TuningCandidateSet> GetTuningCandidatesAsync(ISession s, TuningCandidateQuery query, CancellationToken ct) =>
        _selector.For<ITuningService>(s).GetTuningCandidatesAsync(s, query, ct);

    public Task<IReadOnlyList<PlanFindingLine>> GetPlanFindingsAsync(ISession s, int? instanceId, string sqlId, long planHashValue, CancellationToken ct) =>
        _selector.For<ITuningService>(s).GetPlanFindingsAsync(s, instanceId, sqlId, planHashValue, ct);
}
