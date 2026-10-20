using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Oracle.ManagedDataAccess.Client;
using Oranda.App.Shell;
using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Security;
using Oranda.Core.Sessions;
using Oranda.Core.Storage;
using Oranda.Oracle.Connection;
using Oranda.Oracle.Sessions;
using Oranda.Platform.Storage;

namespace Oranda.App.Features.ConnectionManager;

/// <summary>접속을 시도한 결과 종류.</summary>
public enum ConnectStatus
{
    Connected,

    /// <summary>저장된 비밀번호가 없다(저장 안 함). 물어서 다시 시도한다.</summary>
    PasswordRequired,

    /// <summary>ORA-28001. 비밀번호 변경 대화상자를 띄운다(CONN-04).</summary>
    PasswordExpired,

    /// <summary>지원하지 않는 서버 버전. <see cref="ConnectOutcome.ServerVersion"/>·<see cref="ConnectOutcome.Reason"/> 를 보인다.</summary>
    Unsupported,

    Failed,
}

/// <summary>접속 시도 결과. 비밀번호는 담지 않는다.</summary>
public sealed record ConnectOutcome(ConnectStatus Status, string Message, ISession? Session = null, string? ServerVersion = null, string? Reason = null)
{
    public bool IsConnected => Status == ConnectStatus.Connected;
}

/// <summary>비밀번호 변경 시도 결과.</summary>
public sealed record PasswordChangeOutcome(bool Succeeded, string Message);

/// <summary>
/// 접속 시험 결과(SQL Developer 의 Test, DBeaver 의 Test Connection). 비밀번호는 담지 않는다.
/// <see cref="ConnectStatus.Connected"/> 면 서버 버전과 걸린 시간을, 실패면 ORA 번호와 메시지를 담는다.
/// </summary>
public sealed record ConnectionTestOutcome(ConnectStatus Status, string Message, string? ServerVersion = null, TimeSpan Elapsed = default, int? ErrorNumber = null)
{
    public bool Succeeded => Status == ConnectStatus.Connected;
}

/// <summary>접속 시험 도구가 돌려주는 날 결과(서버 버전 또는 오류 번호·메시지).</summary>
public sealed record ConnectionProbeResult(bool Succeeded, string? ServerVersion, int? ErrorNumber, string? Message)
{
    public static ConnectionProbeResult Ok(string? serverVersion) => new(true, serverVersion, null, null);

    public static ConnectionProbeResult Fail(int? errorNumber, string message) => new(false, null, errorNumber, message);
}

/// <summary>
/// 접속 프로필 관리와 열린 접속(작업 세션, CONN-06). 접속은 여러 개를 동시에 열고, 그중 하나가 「지금 접속」(<see cref="ActiveSession"/>)이다.
/// 탭·문서가 쓸 세션은 직접 고르지 않고 <see cref="SessionRouter"/> 에 묻는다. 접속 관리자 화면·명령·다른 기능이 쓴다.
/// </summary>
public interface IConnectionService
{
    /// <summary>지금 접속(활성 문서의 접속). 없으면 null.</summary>
    ISession? ActiveSession { get; }

    /// <summary>열린 작업 세션(접속). 연 순서이고 툴바 콤보 번호는 위치 + 1 이다. 프로필마다 하나뿐이다.</summary>
    IReadOnlyList<ISession> Sessions { get; }

    /// <summary>지금 접속의 프로필, 지금 접속이 없으면 마지막으로 접속한 프로필(끊겨도 남는다. 「다시 접속」용).</summary>
    ConnectionProfile? LastProfile { get; }

    /// <summary>지금 접속이 끊긴 상태.</summary>
    bool IsBroken { get; }

    /// <summary>지금 접속·열린 접속 목록·끊김 상태가 바뀌었다. 아무 스레드에서나 온다.</summary>
    event EventHandler? ActiveSessionChanged;

    /// <summary>접속에 성공했다(새로 열었거나 이미 열린 접속을 지금 접속으로 바꿨다). 활성 SQL 탭이 이 접속을 따른다.</summary>
    event EventHandler<ISession>? Connected;

    /// <summary>프로필의 열린 작업 세션(끊긴 것 포함). 없으면 null.</summary>
    ISession? SessionOf(Guid profileId);

    /// <summary>열린 작업 세션 중 끊긴 것인지.</summary>
    bool IsSessionBroken(Guid sessionId);

    /// <summary>열린 작업 세션 하나를 지금 접속으로 바꾼다. 목록에 없으면 false.</summary>
    bool Switch(ISession session);

    Task<StoredDocument<ConnectionsDocument>> LoadAsync(CancellationToken ct);

    /// <summary>프로필을 추가·수정한다(같은 <c>Id</c> 면 수정). 비밀번호는 안전 저장소에만 둔다.</summary>
    /// <param name="profile">저장할 프로필. <c>Id</c> 가 비어 있으면 새로 발급한다.</param>
    /// <param name="password">새 비밀번호. null·빈 값이면 저장된 것을 그대로 둔다.</param>
    /// <param name="savePassword">false 면 저장된 비밀번호를 지우고 접속 때마다 묻는다.</param>
    /// <param name="ct">취소.</param>
    Task<ConnectionProfile> SaveProfileAsync(ConnectionProfile profile, string? password, bool savePassword, CancellationToken ct);

    /// <summary>프로필과 저장된 비밀번호·풀어 놓은 지갑을 지운다.</summary>
    Task DeleteProfileAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// 접속한다. 다른 접속은 닫지 않고 목록에 더하며 새 접속이 지금 접속이 된다. 같은 프로필이 이미 열려 있으면 새로 열지 않고
    /// 그 접속을 지금 접속으로 바꾼다(끊긴 것이면 닫고 같은 자리에 다시 연다). 열린 접속이 <see cref="ConnectionService.MaxSessions"/> 개면 실패.
    /// </summary>
    Task<ConnectOutcome> ConnectAsync(ConnectionProfile profile, string? password, CancellationToken ct);

    /// <summary>지금 접속을 끊는다. 다른 접속은 그대로이고 마지막에 연 접속이 지금 접속이 된다.</summary>
    Task DisconnectAsync(CancellationToken ct);

    /// <summary>열린 작업 세션 하나를 끊는다(목록에 없으면 아무것도 안 한다).</summary>
    Task DisconnectAsync(ISession session, CancellationToken ct);

    /// <summary>마지막 프로필로 다시 접속한다(저장된 비밀번호가 있을 때).</summary>
    Task<ConnectOutcome> ReconnectAsync(string? password, CancellationToken ct);

    /// <summary>비밀번호를 바꾼다. 비밀번호를 저장하는 프로필이면 저장소의 값도 새 값으로 바꾼다.</summary>
    Task<PasswordChangeOutcome> ChangePasswordAsync(ConnectionProfile profile, string? oldPassword, string newPassword, CancellationToken ct);

    /// <summary>저장된 비밀번호가 있는지.</summary>
    Task<bool> HasStoredPasswordAsync(ConnectionProfile profile, CancellationToken ct);

    /// <summary>ADB 지갑 zip 으로 프로필을 만들어 저장한다(지갑은 데이터 폴더 <c>wallets/{id}</c> 에 푼다). 비밀번호는 비워 둔다.</summary>
    Task<ConnectionProfile> ImportWalletAsync(string zipPath, string? walletAlias, CancellationToken ct);

    /// <summary>
    /// 지금 값(저장 안 한 편집 값 포함)으로 임시 연결을 열어 서버 버전을 읽고 바로 닫는다.
    /// 세션 관리자를 거치지 않으므로 앱의 활성 접속·세션 목록·마지막 접속 시각은 바뀌지 않고, 비밀번호도 저장하지 않는다.
    /// <paramref name="password"/> 가 비면 저장된 비밀번호를 쓰고, 그것도 없으면 <see cref="ConnectStatus.PasswordRequired"/>.
    /// </summary>
    Task<ConnectionTestOutcome> TestConnectionAsync(ConnectionProfile profile, string? password, CancellationToken ct);

    /// <summary>
    /// 프로필을 복제해 저장한다. 이름 뒤에 「 (사본)」 을 붙이고(겹치면 「 (사본) (2)」 …), 비밀번호·마지막 접속 시각은 옮기지 않는다.
    /// 앱이 풀어 놓은 지갑 폴더(<c>wallets/{id}</c>)를 쓰면 사본의 폴더로 복사한다(원본을 지워도 사본이 접속되게).
    /// </summary>
    Task<ConnectionProfile> DuplicateProfileAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// 폴더 목록·접속의 폴더(<c>Group</c>)를 한 번에 바꿔 저장한다(<see cref="ConnectionFolders"/> 의 작업을 넘긴다).
    /// 비밀번호·안전 저장소 항목은 건드리지 않는다. 읽기 전용 파일이면 <see cref="ConnectionValidationException"/>.
    /// </summary>
    Task UpdateFoldersAsync(Func<ConnectionsDocument, ConnectionsDocument> edit, CancellationToken ct);
}

/// <summary>비밀번호 변경 도구(ODP.NET <c>OpenWithNewPassword</c>). 시험은 가짜로 바꾼다.</summary>
public interface IPasswordChanger
{
    Task ChangeAsync(ConnectionProfile profile, string oldPassword, string newPassword, CancellationToken ct);
}

/// <summary>
/// <see cref="IConnectionService"/> 구현. 비밀번호는 <see cref="ICredentialStore"/> 에만 두고(data-model 5.1),
/// <c>connections.json</c> 에는 <c>credentialRef</c> 문자열만 남긴다.
/// </summary>
public sealed class ConnectionService : IConnectionService, IDisposable
{
    private readonly IConnectionStore _store;
    private readonly ICredentialStore _credentials;
    private readonly ISessionManager _sessions;
    private readonly IAppDataPaths _paths;
    private readonly IPasswordChanger _passwordChanger;
    private readonly IConnectionTester _tester;
    private readonly ContextKeyService? _keys;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<IDisposable> _subscriptions = [];

    // 열린 작업 세션(연 순서)·끊긴 세션·지금 접속. 세션 이벤트는 아무 스레드에서나 오므로 _sync 안에서만 바꾼다.
    private readonly Lock _sync = new();
    private readonly List<ISession> _open = [];
    private readonly HashSet<Guid> _brokenIds = [];
    private ISession? _active;
    private ConnectionProfile? _lastProfile;

    /// <summary>동시에 열 수 있는 접속(공유 작업 세션) 수. 전용 세션(D-017)·도구 세션(CONN-07)은 세지 않는다.</summary>
    public const int MaxSessions = 10;

    public ConnectionService(
        IConnectionStore store,
        ICredentialStore credentials,
        ISessionManager sessions,
        IAppDataPaths paths,
        IEventBus events,
        IPasswordChanger? passwordChanger = null,
        ContextKeyService? keys = null,
        IConnectionTester? tester = null)
    {
        ArgumentNullException.ThrowIfNull(events);
        _store = store;
        _credentials = credentials;
        _sessions = sessions;
        _paths = paths;
        _passwordChanger = passwordChanger ?? new OdpPasswordChanger();
        _tester = tester ?? new OdpConnectionTester();
        _keys = keys;
        _subscriptions.Add(events.Subscribe<SessionBroken>(this, OnBroken));
        _subscriptions.Add(events.Subscribe<SessionClosed>(this, OnClosed));
    }

    public event EventHandler? ActiveSessionChanged;

    public event EventHandler<ISession>? Connected;

    public void Dispose()
    {
        foreach (var s in _subscriptions)
        {
            s.Dispose();
        }

        _gate.Dispose();
    }

    public ISession? ActiveSession
    {
        get
        {
            lock (_sync)
            {
                return _active;
            }
        }
    }

    public IReadOnlyList<ISession> Sessions
    {
        get
        {
            lock (_sync)
            {
                return [.. _open];
            }
        }
    }

    public ConnectionProfile? LastProfile
    {
        get
        {
            lock (_sync)
            {
                return _active?.Profile ?? _lastProfile;
            }
        }
    }

    public bool IsBroken
    {
        get
        {
            lock (_sync)
            {
                return _active is { } a && _brokenIds.Contains(a.Id);
            }
        }
    }

    public ISession? SessionOf(Guid profileId)
    {
        if (profileId == Guid.Empty)
        {
            return null;
        }

        lock (_sync)
        {
            return _open.FirstOrDefault(s => s.Profile.Id == profileId);
        }
    }

    public bool IsSessionBroken(Guid sessionId)
    {
        lock (_sync)
        {
            return _brokenIds.Contains(sessionId);
        }
    }

    public bool Switch(ISession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (_sync)
        {
            if (!_open.Contains(session))
            {
                return false;
            }

            if (ReferenceEquals(_active, session))
            {
                return true;
            }

            _active = session;
            _lastProfile = session.Profile;
        }

        RaiseChanged();
        return true;
    }

    /// <summary>비밀번호 저장 키 형식(data-model 5.1).</summary>
    public static string CredentialRefFor(Guid profileId) => "oranda:conn:" + profileId.ToString("D").ToLowerInvariant();

    public Task<StoredDocument<ConnectionsDocument>> LoadAsync(CancellationToken ct) => _store.LoadAsync(ct);

    public async Task<ConnectionProfile> SaveProfileAsync(ConnectionProfile profile, string? password, bool savePassword, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var id = profile.Id == Guid.Empty ? Guid.NewGuid() : profile.Id;
        var previousRef = profile.CredentialRef;
        var next = profile with
        {
            Id = id,
            CredentialRef = savePassword ? (profile.CredentialRef ?? CredentialRefFor(id)) : null,
        };

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var doc = await _store.LoadAsync(ct).ConfigureAwait(false);
            if (doc.IsReadOnly)
            {
                throw new ConnectionValidationException("connections.json 이(가) 읽기 전용이라 저장하지 않는다. " + doc.Notice);
            }

            var list = doc.Value.Profiles.ToList();
            var index = list.FindIndex(p => p.Id == id);
            if (index >= 0)
            {
                // 편집기가 모르는 값(마지막 접속 시각)은 이어 받는다.
                next = next with { LastConnectedAt = next.LastConnectedAt ?? list[index].LastConnectedAt };
                list[index] = next;
            }
            else
            {
                list.Add(next);
            }

            // 검증(비밀 문자열·이름 중복)을 통과해야 비밀번호도 저장한다.
            await _store.SaveAsync(doc.Value with { Profiles = list }, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        if (!savePassword)
        {
            if (previousRef is not null)
            {
                await _credentials.DeleteAsync(previousRef).ConfigureAwait(false);
            }
        }
        else if (!string.IsNullOrEmpty(password))
        {
            await _credentials.SaveAsync(next.CredentialRef!, Encoding.UTF8.GetBytes(password)).ConfigureAwait(false);
        }

        return next;
    }

    public async Task DeleteProfileAsync(Guid id, CancellationToken ct)
    {
        ConnectionProfile? removed;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var doc = await _store.LoadAsync(ct).ConfigureAwait(false);
            removed = doc.Value.Profiles.FirstOrDefault(p => p.Id == id);
            if (removed is null)
            {
                return;
            }

            if (doc.IsReadOnly)
            {
                throw new ConnectionValidationException("connections.json 이(가) 읽기 전용이라 지우지 않는다.");
            }

            await _store.SaveAsync(doc.Value with { Profiles = [.. doc.Value.Profiles.Where(p => p.Id != id)] }, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        if (removed.CredentialRef is not null)
        {
            await _credentials.DeleteAsync(removed.CredentialRef).ConfigureAwait(false);
        }

        DeleteOwnWalletFolder(removed);
    }

    public async Task<ConnectOutcome> ConnectAsync(ConnectionProfile profile, string? password, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);

        // 프로필마다 공유 작업 세션은 하나(CONN-06): 살아 있는 접속이 있으면 새로 열지 않고 지금 접속으로 바꾼다.
        if (LiveSessionOf(profile.Id) is { } existing)
        {
            Switch(existing);
            Connected?.Invoke(this, existing);
            return new ConnectOutcome(ConnectStatus.Connected, $"{profile.Name} 은(는) 이미 접속되어 있어 지금 접속으로 바꿨습니다.", existing, existing.Server.Version);
        }

        if (!HasRoomFor(profile.Id))
        {
            return new ConnectOutcome(ConnectStatus.Failed, $"접속은 {MaxSessions}개까지 열 수 있습니다. 쓰지 않는 접속을 끊고 다시 하세요.");
        }

        var secret = password;
        string? storeProblem = null;
        if (string.IsNullOrEmpty(secret))
        {
            (secret, storeProblem) = await TryLoadStoredAsync(profile).ConfigureAwait(false);
        }

        if (string.IsNullOrEmpty(secret))
        {
            var ask = $"'{profile.Name}' 의 비밀번호를 입력하세요.";
            return new ConnectOutcome(ConnectStatus.PasswordRequired, storeProblem is null ? ask : storeProblem + " " + ask);
        }

        using var credential = new SecureCredential(secret);
        ISession session;
        try
        {
            session = await _sessions.OpenAsync(ToDriverProfile(profile), credential, SessionPurpose.Work, ct).ConfigureAwait(false);
        }
        catch (UnsupportedServerException ex)
        {
            var version = ex.ServerVersion ?? "(알 수 없음)";
            var reason = ex.Reason ?? ex.Message;
            return new ConnectOutcome(ConnectStatus.Unsupported, $"지원하지 않는 서버 버전 {version}: {reason}", null, version, reason);
        }
        catch (Exception ex) when (IsPasswordExpired(ex))
        {
            return new ConnectOutcome(ConnectStatus.PasswordExpired, "비밀번호가 만료되었습니다(ORA-28001). 새 비밀번호를 입력하세요.");
        }
        catch (OracleException ex)
        {
            return new ConnectOutcome(ConnectStatus.Failed, FirstLine(ex.Message));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException or TimeoutException)
        {
            return new ConnectOutcome(ConnectStatus.Failed, FirstLine(ex.Message));
        }

        ISession? replaced = null;
        ISession? duplicate = null;
        var full = false;
        lock (_sync)
        {
            if (_open.Count >= MaxSessions && !_open.Any(s => profile.Id != Guid.Empty && s.Profile.Id == profile.Id))
            {
                full = true; // 여는 동안 다른 접속이 자리를 채웠다
            }
            else if (_open.FirstOrDefault(s => profile.Id != Guid.Empty && s.Profile.Id == profile.Id) is { } same)
            {
                if (!_brokenIds.Contains(same.Id) && same.State != SessionState.Broken)
                {
                    // 같은 프로필을 동시에 두 번 접속했다: 먼저 열린 것을 쓰고 이것은 닫는다.
                    duplicate = session;
                    session = same;
                }
                else
                {
                    // 끊긴 접속을 다시 열었다: 같은 자리(콤보 번호)에 새 세션을 둔다.
                    replaced = same;
                    _open[_open.IndexOf(same)] = session;
                    _brokenIds.Remove(same.Id);
                }
            }
            else
            {
                _open.Add(session);
            }

            if (!full)
            {
                _active = session;
                _lastProfile = profile;
            }
        }

        if (full)
        {
            await CloseAsync(session).ConfigureAwait(false);
            RaiseChanged(); // 실행 상태가 닫은 세션을 지금 접속으로 잡았을 수 있다
            return new ConnectOutcome(ConnectStatus.Failed, $"접속은 {MaxSessions}개까지 열 수 있습니다. 쓰지 않는 접속을 끊고 다시 하세요.");
        }

        RaiseChanged();
        Connected?.Invoke(this, session);
        if (duplicate is not null)
        {
            await CloseAsync(duplicate).ConfigureAwait(false);
            return new ConnectOutcome(ConnectStatus.Connected, $"{profile.Name} 은(는) 이미 접속되어 있어 지금 접속으로 바꿨습니다.", session, session.Server.Version);
        }

        if (replaced is not null)
        {
            await CloseAsync(replaced).ConfigureAwait(false);
        }

        await RecordConnectedAsync(profile, ct).ConfigureAwait(false);
        return new ConnectOutcome(ConnectStatus.Connected, $"{profile.Name} 에 접속했습니다({VersionText(session.Server)}).", session, session.Server.Version);
    }

    public Task DisconnectAsync(CancellationToken ct) =>
        ActiveSession is { } active ? DisconnectAsync(active, ct) : Task.CompletedTask;

    public async Task DisconnectAsync(ISession session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        ct.ThrowIfCancellationRequested();
        if (!Remove(session))
        {
            return;
        }

        // 접속 끊기는 화면에 먼저 반영하고 세션은 그 뒤에 닫는다. 닫기는 UI 스레드 밖에서 시간 제한을 두고 기다린다.
        RaiseChanged();
        await CloseAsync(session).ConfigureAwait(false);
    }

    /// <summary>
    /// 목록에서 뺀다. 지금 접속이었으면 남은 것 중 마지막에 연 접속이 지금 접속이 된다(없으면 null, 프로필은 「다시 접속」용으로 남긴다).
    /// 빼지 않았으면(목록에 없음) false.
    /// </summary>
    private bool Remove(ISession session)
    {
        lock (_sync)
        {
            if (!_open.Remove(session))
            {
                return false;
            }

            _brokenIds.Remove(session.Id);
            if (ReferenceEquals(_active, session))
            {
                _lastProfile = session.Profile;
                _active = _open.Count == 0 ? null : _open[^1];
            }

            return true;
        }
    }

    /// <summary>프로필의 살아 있는(끊기지 않은) 작업 세션.</summary>
    private ISession? LiveSessionOf(Guid profileId)
    {
        lock (_sync)
        {
            return profileId == Guid.Empty
                ? null
                : _open.FirstOrDefault(s => s.Profile.Id == profileId && !_brokenIds.Contains(s.Id) && s.State != SessionState.Broken);
        }
    }

    /// <summary>새 접속을 열 자리가 있는지(같은 프로필의 끊긴 접속을 바꿔 여는 것은 자리를 더 쓰지 않는다).</summary>
    private bool HasRoomFor(Guid profileId)
    {
        lock (_sync)
        {
            return _open.Count < MaxSessions || (profileId != Guid.Empty && _open.Any(s => s.Profile.Id == profileId));
        }
    }

    /// <summary>
    /// 세션 닫기를 호출한 스레드(UI)에서 떼어 내고 <see cref="SessionCloser.Timeout"/> 까지만 기다린다.
    /// ODP.NET 의 Dispose 는 서버가 응답하지 않으면 끝나지 않으므로 UI 스레드에서 직접 기다리지 않는다.
    /// </summary>
    internal static Task CloseAsync(ISession session, Action<string>? warn = null, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        return SessionCloser.RunAsync(session.DisposeAsync, "세션 " + session.Id.ToString("N")[..8], warn, timeout);
    }

    public async Task<ConnectOutcome> ReconnectAsync(string? password, CancellationToken ct)
    {
        var profile = LastProfile;
        if (profile is null)
        {
            return new ConnectOutcome(ConnectStatus.Failed, "다시 접속할 접속 정보가 없습니다.");
        }

        // 저장소의 최신 값(이름·주소를 고쳤을 수 있다)을 우선한다.
        var doc = await _store.LoadAsync(ct).ConfigureAwait(false);
        var fresh = doc.Value.Profiles.FirstOrDefault(p => p.Id == profile.Id) ?? profile;
        return await ConnectAsync(fresh, password, ct).ConfigureAwait(false);
    }

    public async Task<PasswordChangeOutcome> ChangePasswordAsync(ConnectionProfile profile, string? oldPassword, string newPassword, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (string.IsNullOrEmpty(newPassword))
        {
            return new PasswordChangeOutcome(false, "새 비밀번호를 입력하세요.");
        }

        var old = string.IsNullOrEmpty(oldPassword) ? await LoadStoredAsync(profile).ConfigureAwait(false) : oldPassword;
        if (string.IsNullOrEmpty(old))
        {
            return new PasswordChangeOutcome(false, "현재 비밀번호를 입력하세요.");
        }

        try
        {
            await _passwordChanger.ChangeAsync(profile, old, newPassword, ct).ConfigureAwait(false);
        }
        catch (OracleException ex)
        {
            return new PasswordChangeOutcome(false, FirstLine(ex.Message));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException or TimeoutException)
        {
            return new PasswordChangeOutcome(false, FirstLine(ex.Message));
        }

        // 새 비밀번호는 접속 후 저장소에만 갱신한다(data-model 5.1).
        if (profile.CredentialRef is not null)
        {
            await _credentials.SaveAsync(profile.CredentialRef, Encoding.UTF8.GetBytes(newPassword)).ConfigureAwait(false);
        }

        return new PasswordChangeOutcome(true, "비밀번호를 바꿨습니다.");
    }

    public async Task<bool> HasStoredPasswordAsync(ConnectionProfile profile, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ct.ThrowIfCancellationRequested();
        return !string.IsNullOrEmpty(await LoadStoredAsync(profile).ConfigureAwait(false));
    }

    public async Task<ConnectionProfile> ImportWalletAsync(string zipPath, string? walletAlias, CancellationToken ct)
    {
        var info = WalletImporter.Inspect(zipPath);
        if (!info.HasAutoLoginWallet)
        {
            throw new WalletFormatException("자동 로그인 지갑(cwallet.sso)이 없는 지갑은 지원하지 않는다.");
        }

        var chosen = walletAlias ?? info.DefaultAlias ?? throw new WalletFormatException("접속 별칭이 없다.");
        if (!info.Aliases.Contains(chosen, StringComparer.OrdinalIgnoreCase))
        {
            throw new WalletFormatException($"지갑에 '{chosen}' 별칭이 없다.");
        }

        var id = Guid.NewGuid();
        var folder = WalletFolder(id);
        WalletImporter.Extract(zipPath, folder);

        var doc = await _store.LoadAsync(ct).ConfigureAwait(false);
        var name = UniqueName(info.SuggestedName, doc.Value.Profiles.Select(p => p.Name));
        var profile = new ConnectionProfile(name, "ADMIN", chosen, TnsAdmin: folder, WalletLocation: folder, Id: id, Group: "ADB");
        return await SaveProfileAsync(profile, null, savePassword: false, ct).ConfigureAwait(false);
    }

    public async Task<ConnectionTestOutcome> TestConnectionAsync(ConnectionProfile profile, string? password, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var secret = string.IsNullOrEmpty(password) ? await LoadStoredAsync(profile).ConfigureAwait(false) : password;
        if (string.IsNullOrEmpty(secret))
        {
            return new ConnectionTestOutcome(ConnectStatus.PasswordRequired, $"'{profile.Name}' 의 비밀번호를 입력하세요.");
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var probe = await _tester.ProbeAsync(ToDriverProfile(profile), secret, ct).ConfigureAwait(false);
        watch.Stop();
        if (probe.Succeeded)
        {
            var version = probe.ServerVersion ?? "(알 수 없음)";
            var label = ServerLabel(version);
            return new ConnectionTestOutcome(
                ConnectStatus.Connected,
                $"접속 시험 성공: 서버 {version}{(label is null ? string.Empty : $"({label})")}, {watch.Elapsed.TotalMilliseconds:0} ms 걸림.",
                version,
                watch.Elapsed);
        }

        // 드라이버 메시지에 비밀번호가 섞여 나오는 일은 없어야 하지만, 상태 줄로 나가기 전에 한 번 더 지운다.
        var message = FirstLine(probe.Message ?? string.Empty).Replace(secret, "***", StringComparison.Ordinal);
        if (probe.ErrorNumber is { } number && !message.StartsWith("ORA-", StringComparison.OrdinalIgnoreCase))
        {
            message = $"ORA-{number:00000}: {message}";
        }

        var status = probe.ServerVersion is null ? ConnectStatus.Failed : ConnectStatus.Unsupported;
        var prefix = status == ConnectStatus.Unsupported ? $"지원하지 않는 서버 버전 {probe.ServerVersion}" : "접속 시험 실패";
        return new ConnectionTestOutcome(status, $"{prefix}: {message}", probe.ServerVersion, watch.Elapsed, probe.ErrorNumber);
    }

    /// <summary>서버 버전 문자열의 짧은 이름(23 이상 = 26ai, 19 = 19c, 11 = 11g). 읽을 수 없으면 null.</summary>
    internal static string? ServerLabel(string? version)
    {
        var head = version?.Split('.', 2)[0];
        return int.TryParse(head, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var major)
            ? major >= 23 ? "26ai" : major < 12 ? major + "g" : major + "c"
            : null;
    }

    public async Task UpdateFoldersAsync(Func<ConnectionsDocument, ConnectionsDocument> edit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(edit);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var doc = await _store.LoadAsync(ct).ConfigureAwait(false);
            if (doc.IsReadOnly)
            {
                throw new ConnectionValidationException("connections.json 이(가) 읽기 전용이라 폴더를 바꾸지 않는다. " + doc.Notice);
            }

            await _store.SaveAsync(edit(doc.Value), ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ConnectionProfile> DuplicateProfileAsync(Guid id, CancellationToken ct)
    {
        var doc = await _store.LoadAsync(ct).ConfigureAwait(false);
        var original = doc.Value.Profiles.FirstOrDefault(p => p.Id == id)
            ?? throw new ConnectionValidationException("복제할 접속이 없다(이미 지워졌을 수 있다).");

        var copyId = Guid.NewGuid();
        var copy = original with
        {
            Id = copyId,
            Name = UniqueName(original.Name + " (사본)", doc.Value.Profiles.Select(p => p.Name)),
            CredentialRef = null,
            LastConnectedAt = null,
        };

        var ownWallet = WalletFolder(original.Id);
        if (SamePath(original.WalletLocation, ownWallet) && Directory.Exists(ownWallet))
        {
            var target = WalletFolder(copyId);
            CopyWalletFolder(ownWallet, target);
            copy = copy with
            {
                WalletLocation = target,
                TnsAdmin = SamePath(original.TnsAdmin, ownWallet) ? target : original.TnsAdmin,
            };
        }

        return await SaveProfileAsync(copy, null, savePassword: false, ct).ConfigureAwait(false);
    }

    /// <summary>ORA-28001(비밀번호 만료). 드라이버 예외 번호 또는 메시지로 판별한다.</summary>
    internal static bool IsPasswordExpired(Exception ex) =>
        ex is OracleException { Number: 28001 } || ex.Message.Contains("ORA-28001", StringComparison.Ordinal);

    /// <summary>
    /// 세션 관리자에 넘길 사본을 만든다(저장된 프로필은 바꾸지 않는다).
    /// 프록시 접속이면 사용자를 ODP.NET 형식 <c>proxy[user]</c> 로 바꾸고,
    /// TNS 디렉터리의 별칭이면 tnsnames.ora 에서 기술자를 찾아 접속 문자열로 넘긴다
    /// (ODP.NET 의 TNS_ADMIN 은 프로세스 전체 설정이라 연결을 연 뒤에는 바꿀 수 없다).
    /// 지갑 폴더는 기술자의 <c>MY_WALLET_DIRECTORY</c> 로도 알린다.
    /// </summary>
    internal static ConnectionProfile ToDriverProfile(ConnectionProfile profile)
    {
        var result = profile;
        if (!string.IsNullOrWhiteSpace(profile.ProxyUser))
        {
            result = result with { User = $"{profile.ProxyUser}[{profile.User}]", ProxyUser = null };
        }

        if (!string.IsNullOrWhiteSpace(profile.TnsAdmin)
            && TnsResolver.TryResolve(profile.TnsAdmin, profile.DataSource, out var descriptor))
        {
            if (!string.IsNullOrWhiteSpace(profile.WalletLocation))
            {
                descriptor = TnsResolver.WithWalletDirectory(descriptor, profile.WalletLocation);
            }

            // 기술자를 직접 넘기므로 TNS 디렉터리는 필요 없다. 지갑 위치는 프로세스에서 처음 쓰는 폴더일 때만 함께 남겨
            // 드라이버의 전역 설정이 한 번 맞게 하고(이전 방식), 다른 폴더면 기술자의 값에 맡긴다.
            var keepWallet = AppliedWallet is null || string.Equals(AppliedWallet, profile.WalletLocation, StringComparison.OrdinalIgnoreCase);
            if (keepWallet && !string.IsNullOrWhiteSpace(profile.WalletLocation))
            {
                AppliedWallet = profile.WalletLocation;
            }

            result = result with
            {
                DataSource = descriptor,
                TnsAdmin = null,
                WalletLocation = keepWallet ? profile.WalletLocation : null,
            };
        }

        return result;
    }

    /// <summary>이 프로세스에서 드라이버 전역 지갑 위치로 이미 넘긴 폴더(없으면 null).</summary>
    private static string? AppliedWallet { get; set; }

    internal string WalletFolder(Guid id) => Path.Combine(_paths.DataDirectory, "wallets", id.ToString("D").ToLowerInvariant());

    private async Task<string?> LoadStoredAsync(ConnectionProfile profile) =>
        (await TryLoadStoredAsync(profile).ConfigureAwait(false)).Secret;

    /// <summary>
    /// 저장된 비밀번호. 없으면 null. 저장소를 읽지 못하면(키체인 접근을 거부함 등) null 과 그 이유를 돌려준다:
    /// 예외를 그대로 올리면 접속 명령이 처리되지 않은 예외로 앱을 끝내므로, 비밀번호를 묻는 쪽으로 넘긴다.
    /// </summary>
    private async Task<(string? Secret, string? Problem)> TryLoadStoredAsync(ConnectionProfile profile)
    {
        if (profile.CredentialRef is null)
        {
            return (null, null);
        }

        byte[]? bytes;
        try
        {
            bytes = await _credentials.LoadAsync(profile.CredentialRef).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (null, "저장된 비밀번호를 읽지 못했습니다(" + ex.Message + ").");
        }

        if (bytes is null)
        {
            return (null, null);
        }

        var text = Encoding.UTF8.GetString(bytes);
        Array.Clear(bytes);
        return (text, null);
    }

    private static bool SamePath(string? a, string b) =>
        !string.IsNullOrWhiteSpace(a)
        && string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>풀어 놓은 지갑 파일을 새 폴더로 복사하고 sqlnet.ora 의 지갑 위치를 새 폴더로 고쳐 쓴다.</summary>
    private static void CopyWalletFolder(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            var name = Path.GetFileName(file);
            var destination = Path.Combine(target, name);
            if (string.Equals(name, "sqlnet.ora", StringComparison.OrdinalIgnoreCase))
            {
                File.WriteAllText(destination, WalletImporter.RewriteWalletDirectory(File.ReadAllText(file), target));
            }
            else
            {
                File.Copy(file, destination, overwrite: true);
            }
        }
    }

    private void DeleteOwnWalletFolder(ConnectionProfile removed)
    {
        // 우리가 풀어 놓은 폴더(데이터 폴더 아래 wallets/{id})만 지운다. 사용자가 고른 폴더는 건드리지 않는다.
        var own = Path.GetFullPath(WalletFolder(removed.Id)).TrimEnd('\\', '/');
        if (removed.WalletLocation is { Length: > 0 } wallet
            && string.Equals(Path.GetFullPath(wallet).TrimEnd('\\', '/'), own, StringComparison.OrdinalIgnoreCase)
            && Directory.Exists(own))
        {
            try
            {
                Directory.Delete(own, recursive: true);
            }
            catch (IOException)
            {
                // 지우지 못해도 프로필 삭제는 끝났다.
            }
            catch (UnauthorizedAccessException)
            {
                // 같은 이유.
            }
        }
    }

    private async Task RecordConnectedAsync(ConnectionProfile profile, CancellationToken ct)
    {
        try
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var doc = await _store.LoadAsync(ct).ConfigureAwait(false);
                if (doc.IsReadOnly || doc.Value.Profiles.All(p => p.Id != profile.Id))
                {
                    return;
                }

                var now = DateTimeOffset.UtcNow;
                await _store.SaveAsync(
                    doc.Value with { Profiles = [.. doc.Value.Profiles.Select(p => p.Id == profile.Id ? p with { LastConnectedAt = now } : p)] },
                    ct).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception ex) when (ex is ConnectionValidationException or IOException or UnauthorizedAccessException)
        {
            // 최근 접속 시각을 못 적어도 접속은 성공이다.
        }
    }

    private void OnBroken(SessionBroken e)
    {
        lock (_sync)
        {
            if (!_open.Any(s => s.Id == e.SessionId) || !_brokenIds.Add(e.SessionId))
            {
                return;
            }
        }

        RaiseChanged();
    }

    private void OnClosed(SessionClosed e)
    {
        // 밖에서 닫힌 접속(세션 관리자 정리 등)은 목록에서 뺀다. 「다시 접속」 은 LastProfile 로 한다.
        ISession? closed;
        lock (_sync)
        {
            closed = _open.FirstOrDefault(s => s.Id == e.SessionId);
        }

        if (closed is not null && Remove(closed))
        {
            RaiseChanged();
        }
    }

    private void RaiseChanged()
    {
        UpdateKeys();
        ActiveSessionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateKeys()
    {
        var broken = IsBroken;
        _keys?.Set("hasSession", ActiveSession is not null && !broken);
        _keys?.Set("sessionBroken", broken);
    }

    private static string VersionText(ServerInfo server) =>
        server.IsAutonomous ? "ADB" : ServerLabel(server.Version) ?? server.Version;

    private static string UniqueName(string wanted, IEnumerable<string> existing)
    {
        var names = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        if (!names.Contains(wanted))
        {
            return wanted;
        }

        for (var i = 2; ; i++)
        {
            var candidate = $"{wanted} ({i})";
            if (!names.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    private static string FirstLine(string message)
    {
        var line = message.Split('\n', 2)[0].Trim();
        return line.Length == 0 ? message : line;
    }
}
