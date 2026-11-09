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
}

/// <summary>
/// DBMS 별 <see cref="DbmsFeature"/> 지원 표. 라이선스 팩 같은 Oracle 안쪽 조건은 여기서 보지 않는다(다른 곳에서 점검한다).
/// Tibero 쪽은 첫 판 값이며 실측 뒤에 표를 고친다.
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
        _ => throw new ArgumentOutOfRangeException(nameof(feature), feature, null),
    };

    // Tibero: docs/design/tibero.md 3장 첫 판 값. 실측 뒤 행마다 고친다.
    private static bool TiberoSupports(DbmsFeature feature) => feature switch
    {
        // 3장 "SQL Monitor 보고서·AWR·ASH" = 안 됨(첫 판, Oracle 팩 기능)
        DbmsFeature.SqlMonitorReport => false,
        DbmsFeature.AwrReport => false,
        DbmsFeature.Ash => false,

        // 3장 "트레이스·TKPROF·10053" = 안 됨(첫 판)
        DbmsFeature.SessionTrace => false,
        DbmsFeature.Tkprof => false,
        DbmsFeature.OptimizerTrace => false,

        // 3장 "Java 저장 소스" = 안 됨
        DbmsFeature.JavaSource => false,
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
