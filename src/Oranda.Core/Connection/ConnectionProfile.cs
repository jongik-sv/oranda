using Oracle.ManagedDataAccess.Client;

namespace Oranda.Core.Connection;

/// <summary>Oracle 에서 SYSDBA/SYSOPER 로 붙는지. ODP.NET Managed 가 지원하는 값만 둔다.</summary>
public enum AuthMode
{
    Default,
    SysDba,
    SysOper,
}

/// <summary>
/// 접속 정보. <see cref="DataSource"/> 는 Easy Connect Plus 문자열
/// (예: <c>tcps://host:1522/svc?retry_count=3</c>, <c>host:1521/svc</c>) 또는 tnsnames.ora 별칭.
/// 비밀번호는 보관하지 않는다 — 연결을 만들 때 따로 넘긴다.
/// </summary>
public sealed record ConnectionProfile(
    string Name,
    string User,
    string DataSource,
    AuthMode Auth = AuthMode.Default,
    string? TnsAdmin = null,
    string? WalletLocation = null,
    int ConnectTimeoutSeconds = 15)
{
    /// <summary>비밀번호를 넣은 연결 문자열. 로그나 화면에 내보내지 않는다.</summary>
    public string BuildConnectionString(string password)
    {
        var builder = new OracleConnectionStringBuilder
        {
            DataSource = DataSource,
            UserID = User,
            Password = password,
            ConnectionTimeout = ConnectTimeoutSeconds,
            // 모니터링 도구는 도구마다 전용 세션을 쓰므로 풀링을 끈다. 세션 하나 = 연결 하나.
            Pooling = false,
        };

        if (Auth != AuthMode.Default)
        {
            builder.DBAPrivilege = Auth == AuthMode.SysDba ? "SYSDBA" : "SYSOPER";
        }

        return builder.ConnectionString;
    }

    /// <summary>
    /// TNS_ADMIN 과 지갑 위치는 ODP.NET 이 프로세스 전체 설정으로만 받는다.
    /// 프로필마다 다르면 연결을 열기 직전에 한 번 적용한다.
    /// </summary>
    public void ApplyProcessSettings()
    {
        if (!string.IsNullOrWhiteSpace(TnsAdmin))
        {
            OracleConfiguration.TnsAdmin = TnsAdmin;
        }

        if (!string.IsNullOrWhiteSpace(WalletLocation))
        {
            OracleConfiguration.WalletLocation = WalletLocation;
        }
    }
}
