using System.Data.Common;
using System.Globalization;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Tibero.Execution;

namespace Oranda.Tibero.Connection;

/// <summary>
/// 접속 직후 한 번 서버 버전·세션·권한을 읽어 <see cref="ServerInfo"/> 를 만든다(Oracle <c>ServerProbe</c> 대응, 실측 = docs/design/tibero-driver-session.md 3·4장).
/// 권한이 모자라 실패하는 질의는 예외로 두지 않고 「못 한다」로 기록한다. 드라이버 형식을 쓰지 않는다(<see cref="DbConnection"/> 만).
/// 세션의 명령 잠금은 호출자(세션 관리자)가 쥔다 — 연결을 연 직후, 세션을 내보내기 전에만 부른다.
/// </summary>
internal static class TiberoServerProbe
{
    /// <summary>현재 세션 SID. <c>SYS_CONTEXT('USERENV','SID')</c> 는 Tibero 에서 ODP-11006 이다.</summary>
    internal const string SidSql = "SELECT SID FROM V$MYSTAT WHERE ROWNUM = 1";

    /// <summary>되는 USERENV 항목만(DB_UNIQUE_NAME·SERVICE_NAME·CON_NAME 은 ODP-11006).</summary>
    internal const string IdentitySql =
        "SELECT SYS_CONTEXT('USERENV','INSTANCE_NAME'), SYS_CONTEXT('USERENV','SERVER_HOST'), " +
        "SYS_CONTEXT('USERENV','DB_NAME'), SYS_CONTEXT('USERENV','SESSION_USER') FROM DUAL";

    internal const string VersionSql = "SELECT NAME, VALUE FROM V$VERSION";

    /// <summary>Tibero 의 파라미터 뷰는 <c>V$PARAMETERS</c>(끝에 S)다. <c>V$PARAMETER</c> 는 없다.</summary>
    internal const string ClusterSql = "SELECT VALUE FROM V$PARAMETERS WHERE NAME = 'CLUSTER_DATABASE'";

    public static async Task<ServerInfo> ProbeAsync(DbConnection connection, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var rows = new TiberoProbeRows
        {
            Sid = (await QueryRowAsync(connection, SidSql, ct).ConfigureAwait(false))?[0],
            Identity = await QueryRowAsync(connection, IdentitySql, ct).ConfigureAwait(false),
            Version = await QueryPairsAsync(connection, VersionSql, ct).ConfigureAwait(false),
            DriverServerVersion = connection.ServerVersion,
            Privileges = await QuerySetAsync(connection, "SELECT PRIVILEGE FROM SESSION_PRIVS", ct).ConfigureAwait(false),
            Roles = await QuerySetAsync(connection, "SELECT ROLE FROM SESSION_ROLES", ct).ConfigureAwait(false),
            CanReadVSession = await CanRunAsync(connection, "SELECT 1 FROM V$SESSION WHERE ROWNUM <= 1", ct).ConfigureAwait(false),
            ClusterDatabase = (await QueryRowAsync(connection, ClusterSql, ct).ConfigureAwait(false))?[0],
        };
        return Assemble(rows);
    }

    /// <summary>읽은 행 → <see cref="ServerInfo"/>. 서버 없이 시험한다.</summary>
    internal static ServerInfo Assemble(TiberoProbeRows rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var identity = rows.Identity ?? [];
        string? At(int i) => i < identity.Count && !string.IsNullOrWhiteSpace(identity[i]) ? identity[i] : null;
        var dbName = At(2);

        var version = TiberoServerVersionPolicy.FromVersionRows(rows.Version ?? [])
            ?? (string.IsNullOrWhiteSpace(rows.DriverServerVersion) ? null : rows.DriverServerVersion.Trim())
            ?? "0";

        return new ServerInfo(
            Version: version,
            InstanceName: At(0),
            HostName: At(1),
            ServiceName: null,
            DatabaseName: dbName,
            ContainerName: null,
            IsMultitenant: false,
            SessionId: long.TryParse(rows.Sid, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sid) ? sid : 0,
            // DBMS_MONITOR 패키지가 없다(실측) → 세션 트레이스 권한은 없음. DbmsSupport 도 Tibero 의 SessionTrace 를 끈다.
            Capabilities: ServerCapabilities.From(rows.Privileges ?? EmptySet, rows.Roles ?? EmptySet, rows.CanReadVSession, false, Dbms.Tibero),
            // DB_UNIQUE_NAME 이 없으므로 그 자리(메타데이터 캐시 키)에 DB_NAME 을 둔다.
            DbUniqueName: dbName,
            SessionUser: At(3),
            IsAutonomous: false,
            Edition: null,
            ManagementPackAccess: null,
            IsRac: rows.ClusterDatabase?.Trim() is { } cluster
                && (cluster.Equals("YES", StringComparison.OrdinalIgnoreCase) || cluster.Equals("TRUE", StringComparison.OrdinalIgnoreCase)),
            Dbms: Dbms.Tibero);
    }

    /// <summary>
    /// 메타데이터 캐시 키 <c>{DB_UNIQUE_NAME}/{CON_NAME}/{SESSION_USER}</c> 대문자(data-model 3.3, Oracle 과 같은 모양).
    /// Tibero 는 DB_UNIQUE_NAME 자리에 DB_NAME, CON_NAME 자리에 <c>-</c> 가 들어간다(예: <c>TIBERO/-/SCOTT</c>).
    /// </summary>
    public static string BuildDbKey(ServerInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        var db = info.DbUniqueName ?? info.DatabaseName ?? "?";
        var con = info.ContainerName ?? "-";
        var user = info.SessionUser ?? "?";
        return $"{db}/{con}/{user}".ToUpperInvariant();
    }

    private static readonly TiberoErrorClassifier Classifier = new();

    private static readonly IReadOnlySet<string> EmptySet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>드라이버 서버 오류(볼 수 없음 등)만 「못 읽음」으로 삼킨다. 끊김·취소는 호출자에게 그대로 던진다.</summary>
    private static bool IsSwallowable(Exception ex) =>
        TiberoErrorClassifier.IsDriverException(ex)
        && Classifier.FromException(ex)?.Kind is not (DbErrorKind.Disconnected or DbErrorKind.Cancelled);

    private static async Task<IReadOnlyList<string?>?> QueryRowAsync(DbConnection connection, string sql, CancellationToken ct)
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
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
        catch (Exception ex) when (IsSwallowable(ex))
        {
            return null;
        }
    }

    private static async Task<IReadOnlyList<(string? Name, string? Value)>> QueryPairsAsync(DbConnection connection, string sql, CancellationToken ct)
    {
        var rows = new List<(string?, string?)>();
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                rows.Add((
                    reader.IsDBNull(0) ? null : Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture),
                    reader.FieldCount > 1 && !reader.IsDBNull(1) ? Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture) : null));
            }
        }
        catch (Exception ex) when (IsSwallowable(ex))
        {
            // 버전을 못 읽으면 드라이버의 ServerVersion 으로 넘어간다.
        }

        return rows;
    }

    private static async Task<IReadOnlySet<string>> QuerySetAsync(DbConnection connection, string sql, CancellationToken ct)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (!reader.IsDBNull(0))
                {
                    set.Add(Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture)!);
                }
            }
        }
        catch (Exception ex) when (IsSwallowable(ex))
        {
            // 보지 못하면 빈 집합 — 권한이 없는 것으로 취급한다.
        }

        return set;
    }

    private static async Task<bool> CanRunAsync(DbConnection connection, string sql, CancellationToken ct)
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (IsSwallowable(ex))
        {
            return false;
        }
    }
}

/// <summary><see cref="TiberoServerProbe"/> 가 읽은 원재료(행 입력 단위 시험용).</summary>
internal sealed record TiberoProbeRows
{
    /// <summary><c>V$MYSTAT</c> 의 SID 글.</summary>
    public string? Sid { get; init; }

    /// <summary>(INSTANCE_NAME, SERVER_HOST, DB_NAME, SESSION_USER). 못 읽으면 null.</summary>
    public IReadOnlyList<string?>? Identity { get; init; }

    /// <summary><c>V$VERSION</c> 의 (NAME, VALUE).</summary>
    public IReadOnlyList<(string? Name, string? Value)>? Version { get; init; }

    /// <summary>드라이버의 <c>ServerVersion</c>(실측 = 주 버전만, 예 <c>6</c>). <see cref="Version"/> 을 못 읽을 때 쓴다.</summary>
    public string? DriverServerVersion { get; init; }

    public IReadOnlySet<string>? Privileges { get; init; }

    public IReadOnlySet<string>? Roles { get; init; }

    public bool CanReadVSession { get; init; }

    /// <summary><c>V$PARAMETERS</c> 의 CLUSTER_DATABASE 값(실측 = <c>NO</c>).</summary>
    public string? ClusterDatabase { get; init; }
}
