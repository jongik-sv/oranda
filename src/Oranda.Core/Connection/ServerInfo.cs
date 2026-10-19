namespace Oranda.Core.Connection;

/// <summary>
/// 접속한 서버와 세션의 신원. 화면 제목줄, 기능 분기, 진단 로그에 쓴다.
/// TSK-02-03 이 끝에 덧붙인 매개변수(<see cref="DbUniqueName"/> 이후)는 접속 점검(TSK-03-03)이 채운다.
/// </summary>
public sealed record ServerInfo(
    string Version,
    string? InstanceName,
    string? HostName,
    string? ServiceName,
    string? DatabaseName,
    string? ContainerName,
    bool IsMultitenant,
    long SessionId,
    ServerCapabilities Capabilities,
    string? DbUniqueName = null,
    string? SessionUser = null,
    bool IsAutonomous = false,
    string? Edition = null,
    string? ManagementPackAccess = null,
    bool IsRac = false)
{
    /// <summary>12c 이상이면 true. 12c 부터 CDB/PDB, DBMS_DEBUG_JDWP, blocking_session 컬럼 등이 달라진다.</summary>
    public bool IsAtLeast12c => MajorVersion >= 12;

    public int MajorVersion =>
        int.TryParse(Version.Split('.')[0], out var major) ? major : 0;
}
