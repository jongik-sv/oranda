namespace Oranda.Core.Connection;

/// <summary>
/// 접속 계정으로 어떤 도구를 쓸 수 있는지. 접속 직후에 한 번 계산해서 메뉴·탭을 켜고 끈다.
/// 구형 Orange 도 접속 때 session_privs / session_roles 를 읽어 같은 일을 했다.
/// </summary>
public sealed record ServerCapabilities(
    bool IsDba,
    bool CanQueryDictionary,
    bool CanQueryPerformanceViews,
    bool CanKillSession,
    bool CanTraceSessions)
{
    /// <param name="privileges">session_privs 의 privilege 값</param>
    /// <param name="roles">session_roles 의 role 값</param>
    /// <param name="canReadVSession">실제로 v$session 을 읽어 본 결과 (권한 이름만으로는 알 수 없다)</param>
    /// <param name="canExecuteDbmsMonitor">dbms_monitor 실행 가능 여부 (보통 EXECUTE 를 따로 받아야 한다)</param>
    public static ServerCapabilities From(
        IReadOnlySet<string> privileges,
        IReadOnlySet<string> roles,
        bool canReadVSession,
        bool canExecuteDbmsMonitor)
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
            CanTraceSessions: canExecuteDbmsMonitor);
    }
}
