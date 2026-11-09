namespace Oranda.Core.Connection;

/// <summary>
/// DBMS 에 따라 지원 여부가 갈리는 서버 쪽 기능(docs/design/tibero.md 2장 "기능 지원 표시", 3장 표).
/// 3장 표에서 첫 판에 "안 됨" 으로 적힌 행이며, 지원하지 않는 DBMS 에서도 항목은 보이되 꺼 두고 이유를 보인다.
/// 3장의 "TLS·지갑·Autonomous" 는 서버 기능이 아니라 접속 설정 단위라 이 표에 넣지 않는다(접속 편집기가 DBMS 로 막는다).
/// </summary>
public enum DbmsFeature
{
    /// <summary>SQL 모니터 보고서(DBMS_SQLTUNE.REPORT_SQL_MONITOR).</summary>
    SqlMonitorReport,

    /// <summary>AWR 보고서(DBMS_WORKLOAD_REPOSITORY).</summary>
    AwrReport,

    /// <summary>ASH(액티브 세션 히스토리, V$ACTIVE_SESSION_HISTORY).</summary>
    Ash,

    /// <summary>DBMS_MONITOR 를 쓰는 세션 트레이스.</summary>
    SessionTrace,

    /// <summary>TKPROF 로 트레이스 파일 해석.</summary>
    Tkprof,

    /// <summary>옵티마이저 트레이스(이벤트 10053).</summary>
    OptimizerTrace,

    /// <summary>자바 소스 객체(JAVA SOURCE) 조회·컴파일.</summary>
    JavaSource,

    /// <summary>자식 커서가 따로 생긴 사유(V$SQL_SHARED_CURSOR, 튜닝 TN-04).</summary>
    SharedCursorReasons,

    /// <summary>SQL 프로필·기준선·패치 보기(튜닝 TN-15).</summary>
    PlanControls,

    /// <summary>최근 데드락 기록(알림 로그, 잠금 LK-12).</summary>
    Deadlocks,

    /// <summary>리터럴만 다른 SQL 묶음(FORCE_MATCHING_SIGNATURE, 튜닝 추천).</summary>
    LiteralGroups,

    /// <summary>SQL 모니터 보고서 HTML 형식(REPORT_SQL_MONITOR 의 type 인수).</summary>
    SqlMonitorReportHtml,
}

/// <summary>
/// DBMS 별 <see cref="DbmsFeature"/> 지원 표. 라이선스 팩 같은 Oracle 안쪽 조건은 여기서 보지 않는다(다른 곳에서 점검한다).
/// Tibero 쪽은 6.2.15 실측(tibero-catalog-map.md)까지 반영한 값이며, 실측이 켤 근거를 주지 못한 행은 끈 채로 둔다(행마다 주석).
/// </summary>
public static class DbmsSupport
{
    /// <summary>
    /// 이 DBMS 가 해당 기능을 지원하는지. 기능마다 따로 적는다(한 행을 실측으로 고쳐도 다른 행이 같이 바뀌지 않게).
    /// 표에 없는 기능 값은 <see cref="ArgumentOutOfRangeException"/> — 새 기능을 더하면 DBMS 마다 행을 적어야 한다.
    /// </summary>
    public static bool Supports(Dbms dbms, DbmsFeature feature) => dbms switch
    {
        Dbms.Oracle => OracleSupports(feature),
        Dbms.Tibero => TiberoSupports(feature),
        _ => throw new ArgumentOutOfRangeException(nameof(dbms), dbms, null),
    };

    // Oracle: 모두 지원(팩 라이선스는 다른 곳에서 본다)
    private static bool OracleSupports(DbmsFeature feature) => feature switch
    {
        DbmsFeature.SqlMonitorReport => true,
        DbmsFeature.AwrReport => true,
        DbmsFeature.Ash => true,
        DbmsFeature.SessionTrace => true,
        DbmsFeature.Tkprof => true,
        DbmsFeature.OptimizerTrace => true,
        DbmsFeature.JavaSource => true,
        DbmsFeature.SharedCursorReasons => true,
        DbmsFeature.PlanControls => true,
        DbmsFeature.Deadlocks => true,
        DbmsFeature.LiteralGroups => true,
        DbmsFeature.SqlMonitorReportHtml => true,
        _ => throw new ArgumentOutOfRangeException(nameof(feature), feature, null),
    };

    // Tibero: docs/design/tibero.md 3장 + tibero-catalog-map.md(시험 서버 6.2.15 실측, 읽기 전용). 처음 7행은 끔 유지 — 실측이 켤 근거를 주지 못했다. 뒤 5행(P0-b)은 원본이 없음을 실측으로 확인.
    private static bool TiberoSupports(DbmsFeature feature) => feature switch
    {
        // DBMS_SQLTUNE.REPORT_SQL_MONITOR 는 있으나 인수가 달라(sql_id => …, type => … 호출이 ODP-11029) Oracle 호출 형태로는 안 된다(catalog-map 요약·5장).
        // 실측(P1 M5, win144 #35, 6.2.15): Tibero 카탈로그 plan.monitor_report 로 방금 끝낸 문장의 보고서를 받으면 빈 글(길이 0) → 끈 채로 둔다.
        DbmsFeature.SqlMonitorReport => false,

        // 이 키의 소비자 = 계획 이력 명령(oranda.tuning.awrHistory). TPR(DBA_HIST_SQLSTAT·SNAPSHOT·SQL_PLAN)로 tuning.awr_plan_history·awr_plan_steps 가 된다
        // (SQL_ID 대신 SQL_HASH_VALUE, *_DELTA 없음 — 스냅숏 행 값 그대로). 실측(P5b H1, win144 #49·#50, 6.2.15): 이력 행·계획 단계·해시가 나옴 → 켬.
        // TPR 이 꺼진 서버는 이력이 빈 목록일 뿐 오류가 아니다.
        DbmsFeature.AwrReport => true,

        // V$ACTIVE_SESSION_HISTORY 는 있으나 SESSION_STATE·WAIT_CLASS 열이 없고 EVENT → WAIT_EVENT 다(catalog-map 4장).
        // 실측(P5b H0·H2, win144 #49): 시험 서버는 ACTIVE_SESSION_HISTORY = NO 라 표본 0행 → 값의 뜻을 잴 수 없어 끈 채로 둔다(ASH 를 켠 서버에서 다시 잼).
        DbmsFeature.Ash => false,

        // DBMS_MONITOR 패키지가 없다(catalog-map 5.1·요약) → 실측으로 확정된 「안 됨」.
        DbmsFeature.SessionTrace => false,

        // 외부 TKPROF 도구·10053 이벤트는 사전 조사 대상이 아니었다. 실측 필요(근거 없음 → 안전값).
        DbmsFeature.Tkprof => false,
        DbmsFeature.OptimizerTrace => false,

        // DBMS_JAVA 와 *_JAVA_CLASSES 등은 있으나 *_JAVA_DERIVATIONS·*_JAVA_IMPLEMENTS·*_JAVA_INNERS 가 없고 DBMS_JAVA.SHORTNAME 은 빈 값(catalog-map 3·5장).
        // 실측 필요: JAVA SOURCE 객체를 만들고 컴파일할 수 있는지.
        DbmsFeature.JavaSource => false,

        // 아래 다섯 행은 원본 뷰·인수가 없어 실측으로 확정된 「안 됨」(t4-decision Q2, tibero-monitor-catalog.md 2·3장 「없음」 표).
        // V$SQL_SHARED_CURSOR 없음 → tuning.shared_cursor 없음(자식 수만 child_cursors).
        DbmsFeature.SharedCursorReasons => false,

        // SQL 프로필·기준선·패치 뷰와 signature 열 없음 → tuning.plan_controls 없음.
        DbmsFeature.PlanControls => false,

        // V$DIAG_ALERT_EXT 없음 → lock.deadlocks 없음(알림 로그는 서버 파일).
        DbmsFeature.Deadlocks => false,

        // FORCE_MATCHING_SIGNATURE 없음 → tuning.recommend_literals 없음.
        DbmsFeature.LiteralGroups => false,

        // REPORT_SQL_MONITOR 에 형식(type) 인수가 없다(텍스트 CLOB 만) → plan.monitor_report_html 없음.
        DbmsFeature.SqlMonitorReportHtml => false,
        _ => throw new ArgumentOutOfRangeException(nameof(feature), feature, null),
    };

    /// <summary>지원하지 않을 때 항목 옆에 보일 이유. 지원하면 null.</summary>
    public static string? UnsupportedReason(Dbms dbms, DbmsFeature feature)
        => Supports(dbms, feature) ? null : NotSupportedText(dbms);

    /// <summary>"{표시 이름} 에서는 지원하지 않음" 안내문.</summary>
    public static string NotSupportedText(Dbms dbms) => dbms switch
    {
        Dbms.Oracle => "Oracle 에서는 지원하지 않음",
        Dbms.Tibero => "Tibero 에서는 지원하지 않음",
        _ => throw new ArgumentOutOfRangeException(nameof(dbms), dbms, null),
    };
}
