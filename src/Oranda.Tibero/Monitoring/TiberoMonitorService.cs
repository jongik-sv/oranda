using System.Globalization;
using Oranda.Core.Catalog;
using Oranda.Core.Catalog.Providers;
using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Monitoring;
using Oranda.Core.Monitoring.Providers;
using Oranda.Core.Sessions;
using Oranda.Tibero.Catalog;
using Oranda.Tibero.Execution;

namespace Oranda.Tibero.Monitoring;

/// <summary>
/// Tibero 세션·Lock·SQL 모니터(P2): DBMS 공통 본문 <see cref="CatalogMonitorService"/> 에 Tibero 질의 포트(<see cref="TiberoCatalogPort"/>)와 고리를 붙여 등록한다.
/// Oracle <c>OracleMonitorService</c> 대응.
/// <para>
/// Tibero 차이 중 SQL 이 맡는 것: 세션 상태 RUNNING/READY → ACTIVE/INACTIVE, 차단자 = <c>V$WAITER_SESSION</c> 의 <c>MIN(HOLD_SID)</c>,
/// 잠금 모드 +1(Tibero 0 기반 → Oracle 1 기반). 단일 인스턴스라 인스턴스 번호는 늘 같다(Kill 문장에도 붙이지 않는다).
/// </para>
/// <para>
/// 이 클래스가 맡는 것: Kill(<see cref="KillStatement(SessionKey)"/>, 음수 SERIAL# 교정), 없는 원본의 이유(<see cref="UnavailableReason"/>), 잠긴 행 없음.
/// 세션 트레이스는 지원하지 않는다(<c>TraceStatement</c> 를 재정의하지 않아 null → <see cref="NotSupportedException"/>, 화면은 <c>DbmsFeature.SessionTrace</c> = false 로 숨긴다).
/// 데드락 기록은 원본(<c>lock.deadlocks</c>)이 없어 기본 본문이 빈 목록을 준다(<c>DbmsFeature.Deadlocks</c> = false).
/// </para>
/// </summary>
[OrandaService(typeof(IMonitorService), Dbms = Dbms.Tibero)]
internal sealed class TiberoMonitorService : CatalogMonitorService
{
    /// <summary>Tibero 에는 세션 대기 이력 뷰(V$SESSION_WAIT_HISTORY)가 없다. 상세의 「대기 이력」 구획이 비는 이유.</summary>
    internal const string WaitHistoryUnavailable = "Tibero 에는 세션 대기 이력(V$SESSION_WAIT_HISTORY)이 없어 지원하지 않습니다.";

    /// <summary>카탈로그에 없는 그 밖의 구획 원본의 이유.</summary>
    internal const string SectionViewMissing = "이 Tibero 판에는 이 정보를 읽을 뷰가 없습니다.";

    /// <summary>Tibero 에는 잠긴 행 위치를 알려 주는 뷰가 없다(<c>lock.locked_row</c> 없음).</summary>
    internal const string LockedRowUnavailable = "Tibero 에는 대기 세션이 기다리는 행의 위치를 알려 주는 뷰가 없어 잠긴 행 값을 읽을 수 없습니다.";

    /// <summary>SERIAL# 은 부호 없는 32비트로 저장되는데 V$SESSION 은 int 최대값을 넘으면 음수로 보여 준다. 되돌리는 값.</summary>
    private const long SerialWrap = 1L << 32;

    /// <summary>Tibero 판이 없는 잠긴 행 위치 질의 id.</summary>
    private const string LockedRowQuery = "lock.locked_row";

    private static readonly Lazy<SqlCatalog> Catalog = new(TiberoEmbeddedCatalog.Load);

    public TiberoMonitorService(IExecutionService execution)
        : base(execution)
    {
    }

    /// <summary>
    /// <c>ALTER SYSTEM KILL SESSION (sid, serial)</c>. 위험 명령 확인 대상(재입력 이름 = <c>sid,serial</c>).
    /// SERIAL# 은 <see cref="KillSerial"/> 로 교정한다(목록·상세는 V$SESSION 원값으로 비교하므로 교정은 여기서만).
    /// 일반 계정은 서버가 TBR-17004(권한 없음)로 거절한다.
    /// </summary>
    internal static SqlStatement KillStatement(SessionKey target)
    {
        var sid = target.Sid.ToString(CultureInfo.InvariantCulture);
        var serial = KillSerial(target.Serial).ToString(CultureInfo.InvariantCulture);
        return new SqlStatement($"ALTER SYSTEM KILL SESSION ({sid}, {serial})", default, StatementKind.Other)
        {
            Verb = "ALTER SYSTEM",
            DangerousTarget = $"{sid},{serial}",
        };
    }

    /// <summary>
    /// V$SESSION 이 음수로 보여 준 SERIAL# 을 실제 값으로 되돌린다(음수 + 2^32, 예: -2138093902 → 2156873394).
    /// Tibero 기술 노트 «Issue of SERIAL Value Displayed as Negative When Querying V$SESSION»(support.tibero.com 13751750295823).
    /// </summary>
    internal static long KillSerial(long serial) => serial < 0 ? serial + SerialWrap : serial;

    /// <summary>
    /// 「볼 수 없음」: 뷰 없음·권한 부족(<see cref="TiberoCatalogPort.IsNoAccess"/>) 또는 카탈로그에 <c>lock.locked_row</c> 가 없음.
    /// 뒤쪽이 필요한 이유: Tibero 에는 행 위치 뷰가 없어, 기본 본문의 잠긴 행 조회가 던지지 않고 null(행 위치 모름)로 돌아야 한다.
    /// 다른 id 가 없는 것은 카탈로그 결함이라 삼키지 않는다.
    /// </summary>
    internal static bool IsNoAccessError(Exception ex) =>
        TiberoCatalogPort.IsNoAccess(ex) || ex is CatalogQueryNotFoundException { Id: LockedRowQuery };

    /// <summary>이 서버 판 카탈로그에 <paramref name="id"/> 가 있는가.</summary>
    internal static bool CatalogHas(ServerInfo server, string id)
    {
        try
        {
            Catalog.Value.Get(id, server);
            return true;
        }
        catch (CatalogQueryNotFoundException)
        {
            return false;
        }
    }

    /// <summary>상세 구획 원본이 없을 때의 이유. 있으면 null(질의한다). 대기 이력만 전용 문구.</summary>
    internal static string? UnavailableReason(ServerInfo server, string id)
    {
        if (CatalogHas(server, id))
        {
            return null;
        }

        return string.Equals(id, "session.wait_history", StringComparison.Ordinal) ? WaitHistoryUnavailable : SectionViewMissing;
    }

    /// <summary>카탈로그 <c>session.*</c>·<c>lock.*</c>·<c>sql.*</c> 쿼리를 모니터 세션 연결로 읽는다. 세션 명령 잠금(D-041)은 포트가 잡는다.</summary>
    protected override Task<CatalogRows> QueryAsync(ISession s, string id, IReadOnlyList<CatalogBind> binds, CatalogQueryOptions options, CancellationToken ct) =>
        TiberoCatalogPort.QueryAsync(s, id, binds, options, ct);

    protected override bool IsServerError(Exception ex) => TiberoErrorClassifier.IsDriverException(ex);

    protected override bool IsNoAccess(Exception ex) => IsNoAccessError(ex);

    protected override bool HasQuery(ISession monitor, string id) => CatalogHas(monitor.Server, id);

    /// <summary>Tibero 의 KILL SESSION 에는 IMMEDIATE 가 없다. <paramref name="immediate"/> 는 무시한다. 인스턴스 접미사도 없다(단일 인스턴스).</summary>
    protected override SqlStatement KillStatement(ISession monitor, SessionKey target, bool immediate) => KillStatement(target);

    protected override string? SectionUnavailableReason(ISession monitor, string id) => UnavailableReason(monitor.Server, id);

    /// <summary><c>lock.locked_row</c> 가 없어 이 길에 오지 않는다(<see cref="IsNoAccess"/> 가 null 로 돌린다). 늘 null.</summary>
    protected override string? LockedRowSelect(ISession monitor, string owner, string table) => null;

    /// <summary>마지막 방어선: 잠긴 행 위치를 모르니 값을 읽지 못한다(실패한 작업으로 돌려준다).</summary>
    protected override Task<(IReadOnlyList<string> Names, IReadOnlyList<string?>? Values)> ReadLockedRowValuesAsync(
        ISession monitor, string sql, string rowId, CancellationToken ct) =>
        Task.FromException<(IReadOnlyList<string> Names, IReadOnlyList<string?>? Values)>(new NotSupportedException(LockedRowUnavailable));
}
