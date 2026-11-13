using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Security;
using Oranda.Core.Rollback;
using Oranda.Core.Sessions;
using Oranda.Tibero.Connection;
using Oranda.Tibero.Driver;
using Oranda.Tibero.Rollback;

namespace Oranda.Tibero.Sessions;

/// <summary>
/// Tibero 세션 관리자(Oracle <c>OracleSessionManager</c> 대응): 작업·모니터·전용 세션, Pooling=false, 지원 버전 관문, Keep Alive, 끊김 감지.
/// 실측 근거 = docs/design/tibero-driver-session.md. 늘 등록한다(결정25, D1 대체). 드라이버를 지정하지 않았으면 열 때 <see cref="TiberoDriverUnavailableException"/>(지정 안내 문장).
/// </summary>
[OrandaService(typeof(ISessionManager), Dbms = Dbms.Tibero)]
internal sealed class TiberoSessionManager : ISessionManager
{
    public const string ModuleName = "Oranda";

    private readonly IEventBus _events;
    private readonly TiberoConnectionStringFactory _strings = new();
    private readonly TiberoServerVersionPolicy _policy = new();
    private readonly ConcurrentDictionary<Guid, TiberoSession> _sessions = new();
    private readonly IDriverPathSource? _paths;
    private readonly TiberoRollbackHooks _rollback;

    /// <param name="events">이벤트 버스.</param>
    /// <param name="paths">드라이버 위치(설정). App 이 등록하면 주입되고, 없으면 환경 변수 <c>ORANDA_TIBERO_DRIVER</c> 만 본다.</param>
    /// <param name="ledger">되돌리기 원장(앱 조립이 등록). 없으면 끔 구현.</param>
    /// <param name="capture">되돌리기 엔진(커밋 시점 읽기).</param>
    public TiberoSessionManager(IEventBus events, IDriverPathSource? paths = null, IRollbackLedger? ledger = null, IRollbackCapture? capture = null)
    {
        _events = events;
        _paths = paths;
        _rollback = new TiberoRollbackHooks(capture, ledger, events);
    }

    public IReadOnlyList<ISession> Sessions => [.. _sessions.Values];

    public async Task<ISession> OpenAsync(ConnectionProfile profile, SecureCredential credential, SessionPurpose purpose, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(credential);

        // DBMS 별 고르기(디스패처)가 다른 DBMS 프로필을 여기로 보내면 연결을 만들기 전에 거부한다.
        if (profile.Dbms != Dbms.Tibero)
        {
            throw new ArgumentException($"TiberoSessionManager 는 Tibero 프로필만 받는다(받은 DBMS: {profile.Dbms}).", nameof(profile));
        }

        var connectionString = _strings.Build(profile, credential.Secret.ToString());

        // 드라이버는 첫 Tibero 접속 때 읽는다(설정의 위치 → 검사 → 로드). 못 쓰면 TiberoDriverUnavailableException(사용자에게 보일 문장).
        var driver = TiberoDriver.Require(_paths);
        var connection = await TiberoConnectionOpener.OpenAsync(driver, connectionString, profile.ConnectTimeoutSeconds, ct).ConfigureAwait(false);
        try
        {
            // 드라이버의 *Async 는 동기로 돈다(tb-exec 실측) → 준비 문장들을 스레드 풀에서 보낸다(호출한 UI 스레드를 막지 않게).
            var (info, transaction, id) = await Task.Run(() => PrepareAsync(driver, connection, profile, purpose, ct), CancellationToken.None).ConfigureAwait(false);

            var session = new TiberoSession(id, profile, info, purpose, TiberoServerProbe.BuildDbKey(info), connection, transaction, _events,
                closed => _sessions.TryRemove(closed, out _), driver: driver, rollback: _rollback);
            _sessions[id] = session;
            _events.Publish(new SessionOpened(id, profile, info, purpose));
            return session;
        }
        catch
        {
            // 열다 실패한 연결도 호출 스레드(UI)에서 직접 닫지 않는다(응답 없는 서버면 Dispose 가 끝나지 않을 수 있다).
            await TimedClose.RunAsync(() => TiberoConnectionOpener.CloseAsync(connection), "열다 실패한 Tibero 연결").ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// 연 연결의 준비: 서버 정보 → 지원 버전 관문 → 세션 식별 → NLS → 수동 커밋 트랜잭션.
    /// Tibero 는 트랜잭션 객체가 없으면 문장마다 자동 커밋하므로(tb-exec 실측) 마지막에 <see cref="DbConnection.BeginTransaction()"/> 을 잡아
    /// 세션이 닫힐 때까지 둔다(SAFE-04 수동 커밋 기본). 실패하면 던지고 호출자가 연결을 닫는다.
    /// </summary>
    private async Task<(ServerInfo Info, DbTransaction Transaction, Guid Id)> PrepareAsync(
        TiberoDriver driver, DbConnection connection, ConnectionProfile profile, SessionPurpose purpose, CancellationToken ct)
    {
        var info = await TiberoServerProbe.ProbeAsync(connection, ct).ConfigureAwait(false);

        // 지원 버전 관문: V$VERSION 으로 조립한 버전(실측 6.2.15 = "6")으로 거른다.
        _policy.EnsureSupported(info.Version);

        var id = Guid.NewGuid();
        await SetSessionIdentityAsync(driver, connection, id, purpose, ct).ConfigureAwait(false);
        await ApplyNlsAsync(driver, connection, profile.Nls, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        return (info, transaction, id);
    }

    /// <summary>
    /// <c>V$SESSION</c> 의 MODULE·ACTION·CLIENT_IDENTIFIER 를 채운다(실측: 이 PL/SQL 블록은 반영되고, 연결 속성 <c>ModuleName</c> 은 반영되지 않는다).
    /// 서버 쪽에서 Oranda 세션을 알아보게 한다(Oracle 과 같은 값).
    /// </summary>
    internal static async Task SetSessionIdentityAsync(TiberoDriver driver, DbConnection connection, Guid id, SessionPurpose purpose, CancellationToken ct)
    {
        await using var cmd = CreateCommand(driver, connection);
        cmd.CommandText = "BEGIN DBMS_APPLICATION_INFO.SET_MODULE(:m, :a); DBMS_SESSION.SET_IDENTIFIER(:c); END;";
        AddParameter(cmd, "m", ModuleName);
        AddParameter(cmd, "a", purpose.ToString());
        AddParameter(cmd, "c", "oranda:" + id.ToString("N")[..8]);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 프로필의 NLS 설정을 <c>ALTER SESSION</c> 으로 적용할 문장들. <c>NLS_LANGUAGE</c>·<c>NLS_TERRITORY</c> 는 Tibero 가 받지 않으므로
    /// (실측 ODP-7197 Invalid initialization parameter name) 빼고, 뺀 이름을 <paramref name="skipped"/> 에 담는다.
    /// </summary>
    internal static IReadOnlyList<string> NlsStatements(NlsSettings? nls, out IReadOnlyList<string> skipped)
    {
        var list = new List<string>();
        var notApplied = new List<string>();
        skipped = notApplied;
        if (nls is null)
        {
            return list;
        }

        var items = new (string Name, string? Value, bool Supported)[]
        {
            ("NLS_DATE_FORMAT", nls.DateFormat, true),
            ("NLS_TIMESTAMP_FORMAT", nls.TimestampFormat, true),
            ("NLS_LANGUAGE", nls.Language, false),
            ("NLS_TERRITORY", nls.Territory, false),
            ("NLS_SORT", nls.Sort, true),
        };
        foreach (var (name, value, supported) in items)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (!supported)
            {
                notApplied.Add(name);
                continue;
            }

            list.Add($"ALTER SESSION SET {name} = '{value.Replace("'", "''", StringComparison.Ordinal)}'");
        }

        return list;
    }

    private static async Task ApplyNlsAsync(TiberoDriver driver, DbConnection connection, NlsSettings? nls, CancellationToken ct)
    {
        var statements = NlsStatements(nls, out var skipped);
        foreach (var sql in statements)
        {
            await using var cmd = CreateCommand(driver, connection);
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        if (skipped.Count > 0)
        {
            Trace.TraceWarning("Tibero 는 세션 단위 {0} 설정을 지원하지 않아 적용하지 않았습니다(ALTER SESSION 이 ODP-7197 로 거부됨).", string.Join(", ", skipped));
        }
    }

    /// <summary>세션을 만들기 전 준비 문장용 명령(이름 바인드 켬).</summary>
    private static DbCommand CreateCommand(TiberoDriver driver, DbConnection connection)
    {
        var command = connection.CreateCommand();
        driver.SetBindByName(command);
        return command;
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
