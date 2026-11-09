using Oranda.Core.Catalog.Providers;
using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Language;
using Oranda.Core.Monitoring;
using Oranda.Core.Plans;
using Oranda.Core.Sessions;
using Oranda.Core.Tuning;
using Oranda.Core.Tuning.Providers;
using Oranda.Tibero.Catalog;
using Oranda.Tibero.Execution;
using Oranda.Tibero.Sessions;
using static Oranda.Core.Catalog.Providers.CatalogValues;

namespace Oranda.Tibero.Tuning;

/// <summary>
/// Tibero 튜닝 조회(PRD 11장, P5): DBMS 공통 본문 <see cref="CatalogTuningService"/> 에 Tibero 질의 포트(<see cref="TiberoCatalogPort"/>)와 고리를 붙여 등록한다. Oracle <c>OracleTuningService</c> 대응.
/// 세션 통계 = <c>v$mystat</c>(<see cref="TiberoSessionStatistics.Source"/>), 직전 계획 해시 = <c>tuning.prev_cursor</c>.
/// 읽기 전용 벤치마크(<c>BenchmarkCase.ReadOnly</c>)는 거절한다: Tibero 의 READ ONLY 트랜잭션은 쓰기를 막지 않는다(<see cref="ReadOnlyBenchmarkUnavailable"/>).
/// 트레이스 본문은 SQL 로 읽을 수 없고(V$DIAG_* 없음, P5c), 자식 커서 사유·SQL 계획 고정은 원본이 없다(<c>DbmsFeature.SharedCursorReasons</c>·<c>PlanControls</c> = false).
/// <para>
/// P5a 가 실측하는 것 = 통계·커서·대기·SQL 통계·환경·벤치마크. 이력·추천(awr_plan_*·ash_waits·recommend_*)은 기본 본문을 그대로 상속하고 P5b 가 실측한다.
/// </para>
/// </summary>
[OrandaService(typeof(ITuningService), Dbms = Dbms.Tibero)]
internal sealed class TiberoTuningService : CatalogTuningService
{
    /// <summary>트레이스 본문을 SQL 로 못 읽는 이유(Tibero 는 V$DIAG_TRACE_FILE_CONTENTS 같은 뷰가 없고 서버 파일만 있다).</summary>
    internal const string TraceUnavailable = "Tibero 에서는 트레이스 파일을 아직 지원하지 않습니다(SQL 로 읽을 수 있는 뷰가 없습니다). 서버의 파일을 직접 여세요.";

    internal const string SharedCursorReasonsUnavailable = "Tibero 에는 자식 커서 사유(V$SQL_SHARED_CURSOR)가 없어 지원하지 않습니다.";

    internal const string PlanControlsUnavailable = "Tibero 에는 SQL Profile·Baseline·Patch 가 없어 지원하지 않습니다.";

    /// <summary>
    /// Tibero <c>v$mystat</c> 의 논리 읽기 이름(<see cref="TiberoSessionStatistics.Interesting"/> 의 첫 항목). Oracle 의 「session logical reads」 가 아니다 —
    /// 기본 이름을 쓰면 벤치마크 논리 읽기가 늘 0 이다. 물리 읽기 이름(「physical reads」)은 Oracle 과 같다.
    /// </summary>
    internal const string LogicalReadsName = "logical reads";

    public TiberoTuningService(IExecutionService execution, IExecutionPlanService plans, ILanguageService language)
        : base(execution, plans, language, TiberoSessionStatistics.Source)
    {
    }

    /// <summary>카탈로그 <c>tuning.*</c> 를 세션 연결로 읽는다(열 이름 포함). 명령 잠금·라이선스 관문·끊김 보고는 포트가 맡는다.</summary>
    protected override Task<CatalogRows> QueryAsync(ISession s, string id, IReadOnlyList<CatalogBind> binds, CatalogQueryOptions options, CancellationToken ct) =>
        TiberoCatalogPort.QueryAsync(s, id, binds, options, ct);

    protected override string LogicalReadsStatistic => LogicalReadsName;

    protected override bool IsNoAccess(Exception ex) => TiberoCatalogPort.IsNoAccess(ex);

    protected override string? TraceUnavailableReason(ISession s) => TraceUnavailable;

    /// <summary>Tibero 에는 자식 커서 사유 뷰가 없다. 화면은 <c>DbmsFeature.SharedCursorReasons</c> 가 막고, 이것은 마지막 방어선(실패한 작업으로 돌려준다).</summary>
    public override Task<IReadOnlyList<SharedCursorReason>> GetSharedCursorReasonsAsync(ISession s, string sqlId, CancellationToken ct) =>
        Task.FromException<IReadOnlyList<SharedCursorReason>>(new NotSupportedException(SharedCursorReasonsUnavailable));

    /// <summary>Tibero 에는 SQL Profile·Baseline·Patch 가 없다. 화면은 <c>DbmsFeature.PlanControls</c> 가 막고, 이것은 마지막 방어선(실패한 작업으로 돌려준다).</summary>
    public override Task<IReadOnlyList<SqlPlanControlRow>> GetSqlPlanControlsAsync(ISession s, string sqlId, CancellationToken ct) =>
        Task.FromException<IReadOnlyList<SqlPlanControlRow>>(new NotSupportedException(PlanControlsUnavailable));

    /// <summary>트레이스 파일은 Tibero 에서 지원하지 않는다(V$DIAG_* 없음, P5c). 질의를 보내지 않고 이유와 함께 실패한 작업으로 돌려준다.</summary>
    public override Task<TraceFileInfo> GetTraceFileAsync(ISession s, SessionKey target, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        return Task.FromException<TraceFileInfo>(new NotSupportedException(TraceUnavailable));
    }

    protected override Task<long?> PreviousPlanHashAsync(ISession s, CancellationToken ct) => ReadPreviousPlanHashAsync(s, ct);

    /// <summary>직전 문장(벤치마크 첫 회)의 계획 해시 = tuning.prev_cursor 의 세 번째 열. 행이 없거나 드라이버 오류(권한 없음 포함)면 null.</summary>
    internal static async Task<long?> ReadPreviousPlanHashAsync(ISession s, CancellationToken ct)
    {
        try
        {
            var rows = (await TiberoCatalogPort.QueryAsync(s, "tuning.prev_cursor", CatalogBinds.None, TuningQueryOptions, ct).ConfigureAwait(false)).Rows;
            return rows.Count == 0 ? null : Long(rows[0][2]);
        }
        catch (Exception ex) when (TiberoErrorClassifier.IsDriverException(ex))
        {
            return null; // v$session·v$sql 을 볼 권한이 없으면 계획 해시는 모른다.
        }
    }

    /// <summary>
    /// 읽기 전용 벤치마크를 거절하는 이유. Oracle 은 읽기 전용 트랜잭션 안에서 사용자 함수의 DML 을 DB 가 막지만(ORA-01456),
    /// Tibero 의 READ ONLY 트랜잭션은 쓰기를 막지 않는다(INSERT·DDL·FOR UPDATE 가 성공, 쓰기 IT #28, main a0140964) — 막아 줄 장치가 없으니 사용자 함수가 든 문장은 벤치마크하지 않는다.
    /// </summary>
    internal const string ReadOnlyBenchmarkUnavailable =
        "Tibero 의 읽기 전용 트랜잭션은 쓰기를 막지 않아, 사용자 함수가 든 문장은 벤치마크할 수 없습니다(함수 안에서 데이터가 바뀔 수 있습니다).";

    /// <summary>
    /// 사용자 함수가 든 문장의 읽기 전용 벤치마크를 거절한다. 기본 본문은 이 고리를 문장을 보내기 전에 부르므로(<c>benchmark.ReadOnly</c>) 문장이 서버로 가지 않는다.
    /// 서버로도 아무것도 보내지 않는다(읽기 전용 블록을 열어 봐야 쓰기를 막지 못한다).
    /// </summary>
    protected override Task BeginReadOnlyAsync(ISession s, CancellationToken ct) =>
        Task.FromException(new NotSupportedException(ReadOnlyBenchmarkUnavailable));
}
