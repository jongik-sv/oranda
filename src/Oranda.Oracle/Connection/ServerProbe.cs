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
        var canExecuteDbmsMonitor = await CanRunAsync(connection,
            "SELECT 1 FROM all_tab_privs WHERE table_name = 'DBMS_MONITOR' AND privilege = 'EXECUTE' AND ROWNUM <= 1", ct);

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
            Capabilities: ServerCapabilities.From(privileges, roles, canReadVSession, canExecuteDbmsMonitor));
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
