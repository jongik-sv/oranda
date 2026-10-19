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
            builder.DBAPrivilege = profile.Auth == AuthMode.SysDba ? "SYSDBA" : "SYSOPER";
        }

        return builder.ConnectionString;
    }

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
