using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Connection;

namespace Oranda.Oracle.Connection;

/// <summary><see cref="ConnectionProfile"/> 의 ODP.NET 의존 부분: 연결 문자열 조립과 프로세스 전역 설정.</summary>
public static class ConnectionProfileExtensions
{
    /// <summary>비밀번호를 넣은 연결 문자열. 로그나 화면에 내보내지 않는다.</summary>
    public static string BuildConnectionString(this ConnectionProfile profile, string password)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var builder = new OracleConnectionStringBuilder
        {
            DataSource = profile.DataSource,
            UserID = profile.User,
            Password = password,
            ConnectionTimeout = profile.ConnectTimeoutSeconds,
            // 모니터링 도구는 도구마다 전용 세션을 쓰므로 풀링을 끈다. 세션 하나 = 연결 하나.
            Pooling = false,
        };

        if (profile.Auth != AuthMode.Default)
        {
            builder.DBAPrivilege = profile.Auth switch
            {
                AuthMode.SysDba => "SYSDBA",
                AuthMode.SysOper => "SYSOPER",
                AuthMode.SysBackup => "SYSBACKUP",
                AuthMode.SysDg => "SYSDG",
                AuthMode.SysKm => "SYSKM",
                _ => throw new ArgumentOutOfRangeException(nameof(profile), profile.Auth, "알 수 없는 인증 모드"),
            };
        }

        return builder.ConnectionString;
    }

    /// <summary>비밀번호를 넣은 연결(열지 않음). 제품의 모든 접속은 이 길로 만든다.</summary>
    public static OracleConnection CreateConnection(this ConnectionProfile profile, string password) =>
        CreateConnection(profile.BuildConnectionString(password));

    /// <summary>
    /// 연결 문자열로 연결을 만든다(열지 않음). ODP.NET 은 접속 때 PC 시간대 지역 이름(예 Etc/UTC)을 세션 시간대로 보내는데,
    /// 시간대 파일이 오래된 서버(11.2 XE 등)는 그 이름을 몰라 ORA-00604·ORA-01882 로 접속 자체를 거부한다. 그때만 시차(+00:00)로 대신 보낸다.
    /// </summary>
    public static OracleConnection CreateConnection(string connectionString) =>
        new(connectionString) { UseHourOffsetForUnsupportedTimezone = true };

    /// <summary>
    /// TNS_ADMIN 과 지갑 위치는 ODP.NET 이 프로세스 전체 설정으로만 받는다.
    /// 프로필마다 다르면 연결을 열기 직전에 한 번 적용한다.
    /// </summary>
    public static void ApplyProcessSettings(this ConnectionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (!string.IsNullOrWhiteSpace(profile.TnsAdmin))
        {
            OracleConfiguration.TnsAdmin = profile.TnsAdmin;
        }

        if (!string.IsNullOrWhiteSpace(profile.WalletLocation))
        {
            OracleConfiguration.WalletLocation = profile.WalletLocation;
        }
    }
}
