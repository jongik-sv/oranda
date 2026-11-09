namespace Oranda.Core.Connection;

/// <summary>
/// 접속한 서버와 세션의 신원. 화면 제목줄, 기능 분기, 진단 로그에 쓴다.
/// TSK-02-03 이 끝에 덧붙인 매개변수(<see cref="DbUniqueName"/> 이후)는 접속 점검(TSK-03-03)이 채운다.
/// <see cref="Dbms"/> 는 접속한 서버의 DBMS 로 공급자가 채우며 기본은 Oracle 이다.
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
    bool IsRac = false,
    Dbms Dbms = Dbms.Oracle)
{
    private readonly ServerCapabilities _capabilities = Capabilities;

    /// <summary>
    /// 접속 계정의 도구 권한. 늘 이 서버의 <see cref="Dbms"/> 를 따른다: 공급자가 권한 계산에 DBMS 를 넘기지 않았어도
    /// (<see cref="ServerCapabilities.Dbms"/> 가 다르면) <see cref="ServerCapabilities.WithDbms"/> 로 맞춘 값을 돌려준다.
    /// </summary>
    public ServerCapabilities Capabilities
    {
        get => _capabilities is null || _capabilities.Dbms == Dbms ? _capabilities! : _capabilities.WithDbms(Dbms);
        init => _capabilities = value;
    }

    /// <summary>Oracle 12c 이상이면 true. 12c 부터 CDB/PDB, DBMS_DEBUG_JDWP, blocking_session 컬럼 등이 달라진다. Oracle 버전 기준이라 Tibero 는 false.</summary>
    public bool IsAtLeast12c => Dbms == Dbms.Oracle && MajorVersion >= 12;

    /// <summary>서버 버전과 DBMS 로 정해지는 기능 켜짐(11g 에서 12c 이후 기능 끄기, TSK-03-07).</summary>
    public ServerFeatures Features => ServerFeatures.For(MajorVersion, Dbms);

    public int MajorVersion =>
        int.TryParse(Version.Split('.')[0], out var major) ? major : 0;
}
