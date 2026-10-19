using System.Globalization;
using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Connection;

namespace Oranda.Oracle.Connection;

/// <summary>
/// 접속 직후 한 번 서버 버전·세션·권한을 읽는다. 권한이 모자라서 실패하는 쿼리는 예외로 두지 않고
/// "못 한다"로 기록해, 호출한 쪽이 그 도구를 끄게 한다.
/// </summary>
public static class ServerProbe
{
    public static async Task<ServerInfo> ProbeAsync(OracleConnection connection, CancellationToken ct = default)
    {
        var basic = await QueryRowAsync(connection,
            "SELECT SYS_CONTEXT('USERENV','INSTANCE_NAME'), SYS_CONTEXT('USERENV','SERVER_HOST'), " +
            "SYS_CONTEXT('USERENV','SERVICE_NAME'), SYS_CONTEXT('USERENV','DB_NAME'), " +
            "SYS_CONTEXT('USERENV','SID') FROM dual", ct)
            ?? throw new InvalidOperationException("SYS_CONTEXT 조회에 실패했다.");

        // 12c 이상 전용 항목은 구버전에서 ORA-02003 이 나므로 따로 묻는다.
        var multitenant = await QueryRowAsync(connection,
            "SELECT SYS_CONTEXT('USERENV','CON_NAME'), SYS_CONTEXT('USERENV','CDB_NAME') FROM dual", ct);

        var privileges = await QuerySetAsync(connection, "SELECT privilege FROM session_privs", ct);
        var roles = await QuerySetAsync(connection, "SELECT role FROM session_roles", ct);
        var canReadVSession = await CanRunAsync(connection, "SELECT 1 FROM v$session WHERE ROWNUM <= 1", ct);
        var hasMonitorGrant = await CountAtLeastOneAsync(connection,
            "SELECT COUNT(*) FROM all_tab_privs WHERE table_name = 'DBMS_MONITOR' AND privilege = 'EXECUTE'", ct);
        var canExecuteDbmsMonitor = CanExecuteDbmsMonitor(hasMonitorGrant, privileges);

        // 확장 점검(CONN-12). 못 읽으면 null/false 로 두고 예외로 만들지 않는다.
        var ident = await QueryRowAsync(connection,
            "SELECT SYS_CONTEXT('USERENV','DB_UNIQUE_NAME'), SYS_CONTEXT('USERENV','SESSION_USER') FROM dual", ct);
        var cloudService = await QueryRowAsync(connection,
            "SELECT SYS_CONTEXT('USERENV','CLOUD_SERVICE') FROM dual", ct);
        var banner = (await QueryRowAsync(connection, "SELECT banner FROM v$version WHERE ROWNUM = 1", ct))?[0]
            ?? (await QueryRowAsync(connection, "SELECT product FROM product_component_version WHERE ROWNUM = 1", ct))?[0];
        var packAccess = (await QueryRowAsync(connection,
            "SELECT value FROM v$parameter WHERE name = 'control_management_pack_access'", ct))?[0];
        var clusterDb = (await QueryRowAsync(connection,
            "SELECT value FROM v$parameter WHERE name = 'cluster_database'", ct))?[0];

        var conName = multitenant?[0];
        var cdbName = multitenant?[1];

        return new ServerInfo(
            Version: connection.ServerVersion,
            InstanceName: basic[0],
            HostName: basic[1],
            ServiceName: basic[2],
            DatabaseName: basic[3],
            ContainerName: conName,
            IsMultitenant: !string.IsNullOrEmpty(cdbName),
            SessionId: long.TryParse(basic[4], out var sid) ? sid : 0,
            Capabilities: ServerCapabilities.From(privileges, roles, canReadVSession, canExecuteDbmsMonitor),
            DbUniqueName: ident?[0],
            SessionUser: ident?[1],
            IsAutonomous: IsAutonomousService(cloudService?[0]),
            Edition: ParseEdition(banner),
            ManagementPackAccess: packAccess,
            IsRac: string.Equals(clusterDb, "TRUE", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>CLOUD_SERVICE 컨텍스트는 ADB 에서만 값이 있다(OLTP, DWCS, JDCS, ...).</summary>
    public static bool IsAutonomousService(string? cloudService) => !string.IsNullOrWhiteSpace(cloudService);

    /// <summary>v$version 배너에서 에디션을 뽑는다(Enterprise, Standard, Free, Express). 모르면 null.</summary>
    public static string? ParseEdition(string? banner)
    {
        if (string.IsNullOrWhiteSpace(banner))
        {
            return null;
        }

        foreach (var name in new[] { "Enterprise", "Standard", "Free", "Express", "Personal" })
        {
            if (banner.Contains(name, StringComparison.OrdinalIgnoreCase))
            {
                return name;
            }
        }

        return null;
    }

    /// <summary>
    /// all_tab_privs 는 본인·PUBLIC·활성 역할 경유의 객체 권한 행을 보여 주므로 행이 있으면 실행 가능이다.
    /// 한계: EXECUTE ANY PROCEDURE 시스템 권한은 이 뷰에 안 나오므로 session_privs 로 따로 인정하고, 그 외 경로는 보지 못한다.
    /// </summary>
    public static bool CanExecuteDbmsMonitor(bool hasExecuteGrantRow, IReadOnlySet<string> privileges)
    {
        ArgumentNullException.ThrowIfNull(privileges);
        return hasExecuteGrantRow || privileges.Contains("EXECUTE ANY PROCEDURE");
    }

    private static async Task<bool> CountAtLeastOneAsync(OracleConnection connection, string sql, CancellationToken ct)
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            var value = await command.ExecuteScalarAsync(ct);
            return Convert.ToInt64(value, CultureInfo.InvariantCulture) > 0;
        }
        catch (OracleException)
        {
            return false;
        }
    }

    private static async Task<string?[]?> QueryRowAsync(OracleConnection connection, string sql, CancellationToken ct)
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
            {
                return null;
            }

            var row = new string?[reader.FieldCount];
            for (var i = 0; i < row.Length; i++)
            {
                row[i] = reader.IsDBNull(i) ? null : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture);
            }

            return row;
        }
        catch (OracleException)
        {
            return null;
        }
    }

    private static async Task<HashSet<string>> QuerySetAsync(OracleConnection connection, string sql, CancellationToken ct)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                set.Add(reader.GetString(0));
            }
        }
        catch (OracleException)
        {
            // 보지 못하면 빈 집합 — 권한이 없는 것으로 취급한다.
        }

        return set;
    }

    private static async Task<bool> CanRunAsync(OracleConnection connection, string sql, CancellationToken ct)
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteScalarAsync(ct);
            return true;
        }
        catch (OracleException)
        {
            return false;
        }
    }
}
