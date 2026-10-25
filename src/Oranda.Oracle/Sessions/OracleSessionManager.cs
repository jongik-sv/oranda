using System.Collections.Concurrent;
using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Security;
using Oranda.Core.Sessions;
using Oranda.Oracle.Connection;

namespace Oranda.Oracle.Sessions;

/// <summary>ODP.NET 세션 관리자(작업·전용 세션, Pooling=false, 지원 버전 관문, Keep Alive, 끊김 감지). 소유: TSK-03-03.</summary>
[OrandaService(typeof(ISessionManager))]
internal sealed class OracleSessionManager : ISessionManager
{
    public const string ModuleName = "Oranda";

    private readonly IEventBus _events;
    private readonly ConcurrentDictionary<Guid, OracleSession> _sessions = new();

    public OracleSessionManager(IEventBus events)
    {
        _events = events;
    }

    public IReadOnlyList<ISession> Sessions => [.. _sessions.Values];

    public async Task<ISession> OpenAsync(ConnectionProfile profile, SecureCredential credential, SessionPurpose purpose, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(credential);

        profile.ApplyProcessSettings();
        var connection = profile.CreateConnection(credential.Secret.ToString());
        try
        {
            await connection.OpenAsync(ct);

            // 지원 버전 관문: 서버가 알려 준 버전으로 먼저 거른다. 거부면 아래 catch 에서 연결을 닫는다.
            SupportedServerPolicy.EnsureSupported(connection.ServerVersion);

            var info = await ServerProbe.ProbeAsync(connection, ct);
            var id = Guid.NewGuid();
            await SetSessionIdentityAsync(connection, id, purpose, ct);
            await ApplyNlsAsync(connection, profile.Nls, ct);

            var session = new OracleSession(id, profile, info, purpose, BuildDbKey(info), connection, _events,
                id => _sessions.TryRemove(id, out _));
            _sessions[id] = session;
            _events.Publish(new SessionOpened(id, profile, info, purpose));
            return session;
        }
        catch
        {
            // 열다 실패한 연결도 UI 스레드에서 직접 닫지 않는다(응답 없는 서버면 Dispose 가 끝나지 않는다).
            await SessionCloser.RunAsync(connection.DisposeAsync, "열다 실패한 연결").ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>메타데이터 캐시 키 {DB_UNIQUE_NAME}/{CON_NAME}/{SESSION_USER} 대문자(data-model 3.3).</summary>
    public static string BuildDbKey(ServerInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        var db = info.DbUniqueName ?? info.DatabaseName ?? "?";
        var con = info.ContainerName ?? "-";
        var user = info.SessionUser ?? "?";
        return $"{db}/{con}/{user}".ToUpperInvariant();
    }

    /// <summary>v$session 의 module·action·client_identifier 를 채운다. 서버 쪽에서 Oranda 세션을 알아보게 한다.</summary>
    internal static async Task SetSessionIdentityAsync(OracleConnection connection, Guid id, SessionPurpose purpose, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.BindByName = true;
        cmd.CommandText =
            "BEGIN DBMS_APPLICATION_INFO.SET_MODULE(:m, :a); DBMS_SESSION.SET_IDENTIFIER(:c); END;";
        cmd.Parameters.Add("m", ModuleName);
        cmd.Parameters.Add("a", purpose.ToString());
        cmd.Parameters.Add("c", "oranda:" + id.ToString("N")[..8]);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task ApplyNlsAsync(OracleConnection connection, NlsSettings? nls, CancellationToken ct)
    {
        if (nls is null)
        {
            return;
        }

        var items = new (string Name, string? Value)[]
        {
            ("NLS_DATE_FORMAT", nls.DateFormat),
            ("NLS_TIMESTAMP_FORMAT", nls.TimestampFormat),
            ("NLS_LANGUAGE", nls.Language),
            ("NLS_TERRITORY", nls.Territory),
            ("NLS_SORT", nls.Sort),
        };
        foreach (var (name, value) in items)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = $"ALTER SESSION SET {name} = '{value.Replace("'", "''", StringComparison.Ordinal)}'";
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }
}
