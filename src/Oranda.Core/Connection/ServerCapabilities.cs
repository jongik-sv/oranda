namespace Oranda.Core.Connection;

/// <summary>
/// 접속 계정으로 어떤 도구를 쓸 수 있는지. 접속 직후에 한 번 계산해서 메뉴·탭을 켜고 끈다.
/// 구형 Orange 도 접속 때 session_privs / session_roles 를 읽어 같은 일을 했다.
/// Tibero 도 DBA 롤·SELECT ANY DICTIONARY·ALTER SYSTEM 이름을 같게 본다(T2 실측에서 확인).
/// </summary>
/// <param name="Dbms">접속한 서버의 DBMS. 기본은 Oracle 이다.</param>
public sealed record ServerCapabilities(
    bool IsDba,
    bool CanQueryDictionary,
    bool CanQueryPerformanceViews,
    bool CanKillSession,
    bool CanTraceSessions,
    Dbms Dbms = Dbms.Oracle)
{
    /// <param name="privileges">session_privs 의 privilege 값</param>
    /// <param name="roles">session_roles 의 role 값</param>
    /// <param name="canReadVSession">실제로 v$session 을 읽어 본 결과 (권한 이름만으로는 알 수 없다)</param>
    /// <param name="canExecuteDbmsMonitor">dbms_monitor 실행 가능 여부 (보통 EXECUTE 를 따로 받아야 한다)</param>
    /// <param name="dbms">접속한 서버의 DBMS. 세션 트레이스를 지원하지 않는 DBMS 면 <see cref="CanTraceSessions"/> 는 false.</param>
    public static ServerCapabilities From(
        IReadOnlySet<string> privileges,
        IReadOnlySet<string> roles,
        bool canReadVSession,
        bool canExecuteDbmsMonitor,
        Dbms dbms = Dbms.Oracle)
    {
        var isDba = roles.Contains("DBA");
        var canQueryDictionary = isDba
            || roles.Contains("SELECT_CATALOG_ROLE")
            || privileges.Contains("SELECT ANY DICTIONARY");

        return new ServerCapabilities(
            IsDba: isDba,
            CanQueryDictionary: canQueryDictionary,
            CanQueryPerformanceViews: canReadVSession,
            CanKillSession: privileges.Contains("ALTER SYSTEM"),
            CanTraceSessions: canExecuteDbmsMonitor && DbmsSupport.Supports(dbms, DbmsFeature.SessionTrace),
            Dbms: dbms);
    }

    /// <summary>
    /// DBMS 를 바꾼 사본. 새 DBMS 가 세션 트레이스를 지원하지 않으면 <see cref="CanTraceSessions"/> 를 끈다(켜지는 쪽으로는 바꾸지 않는다 —
    /// 권한 점검 결과를 모르기 때문이다). 같은 DBMS 면 자기 자신.
    /// </summary>
    public ServerCapabilities WithDbms(Dbms dbms) => dbms == Dbms
        ? this
        : this with { Dbms = dbms, CanTraceSessions = CanTraceSessions && DbmsSupport.Supports(dbms, DbmsFeature.SessionTrace) };

    /// <summary>이 DBMS 가 해당 기능을 지원하지 않을 때 보일 이유. 지원하면 null.</summary>
    public string? UnsupportedReason(DbmsFeature feature) => DbmsSupport.UnsupportedReason(Dbms, feature);
}
