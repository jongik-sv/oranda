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

/// <summary>접속 프로필 관리와 현재 접속(작업 세션). 접속 관리자 화면·명령·다른 기능이 쓴다.</summary>
public interface IConnectionService
{
    /// <summary>현재 작업 세션. 없으면 null.</summary>
    ISession? ActiveSession { get; }

    /// <summary>마지막으로 접속한 프로필(끊겨도 남는다. 「다시 접속」용).</summary>
    ConnectionProfile? LastProfile { get; }

    /// <summary>현재 세션이 끊긴 상태.</summary>
    bool IsBroken { get; }

    /// <summary>현재 세션·끊김 상태가 바뀌었다.</summary>
    event EventHandler? ActiveSessionChanged;

    Task<StoredDocument<ConnectionsDocument>> LoadAsync(CancellationToken ct);

    /// <summary>프로필을 추가·수정한다(같은 <c>Id</c> 면 수정). 비밀번호는 안전 저장소에만 둔다.</summary>
    /// <param name="profile">저장할 프로필. <c>Id</c> 가 비어 있으면 새로 발급한다.</param>
    /// <param name="password">새 비밀번호. null·빈 값이면 저장된 것을 그대로 둔다.</param>
    /// <param name="savePassword">false 면 저장된 비밀번호를 지우고 접속 때마다 묻는다.</param>
    /// <param name="ct">취소.</param>
    Task<ConnectionProfile> SaveProfileAsync(ConnectionProfile profile, string? password, bool savePassword, CancellationToken ct);

    /// <summary>프로필과 저장된 비밀번호·풀어 놓은 지갑을 지운다.</summary>
    Task DeleteProfileAsync(Guid id, CancellationToken ct);

    /// <summary>접속한다. 성공하면 이전 작업 세션은 닫는다.</summary>
    Task<ConnectOutcome> ConnectAsync(ConnectionProfile profile, string? password, CancellationToken ct);

    Task DisconnectAsync(CancellationToken ct);

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
}

/// <summary>비밀번호 변경 도구(ODP.NET <c>OpenWithNewPassword</c>). 시험은 가짜로 바꾼다.</summary>
public interface IPasswordChanger
{
    Task ChangeAsync(ConnectionProfile profile, string oldPassword, string newPassword, CancellationToken ct);
}

/// <summary>접속 시험 도구(ODP.NET 연결 하나를 열고 닫는다). 시험은 가짜로 바꾼다.</summary>
public interface IConnectionTester
{
    /// <summary>드라이버용 프로필(<see cref="ConnectionService.ToDriverProfile"/> 를 거친 값)로 연결을 열어 본다. 취소되면 <see cref="OperationCanceledException"/>.</summary>
    Task<ConnectionProbeResult> ProbeAsync(ConnectionProfile driverProfile, string password, CancellationToken ct);
}

/// <inheritdoc />
public sealed class OdpConnectionTester : IConnectionTester
{
    public async Task<ConnectionProbeResult> ProbeAsync(ConnectionProfile driverProfile, string password, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(driverProfile);
        ct.ThrowIfCancellationRequested();
        driverProfile.ApplyProcessSettings();

        // 접속 제한 시간은 연결 문자열(Connection Timeout)에 들어간다. 취소 단추는 기다림만 끊고,
        // 이미 시작한 연결 시도는 끝난 뒤에 닫는다(열리는 중인 연결을 다른 스레드에서 닫지 않는다).
        var connection = new OracleConnection(driverProfile.BuildConnectionString(password));
        var open = connection.OpenAsync(ct);
        try
        {
            await open.WaitAsync(ct).ConfigureAwait(false);
            var version = connection.ServerVersion;
            return SupportedServerPolicy.IsSupported(version, out var reason)
                ? ConnectionProbeResult.Ok(version)
                : new ConnectionProbeResult(false, version, null, reason);
        }
        catch (OracleException ex)
        {
            return ConnectionProbeResult.Fail(ex.Number, ex.Message);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException or TimeoutException)
        {
            return ConnectionProbeResult.Fail(null, ex.Message);
        }
        finally
        {
            if (open.IsCompleted)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                _ = open.ContinueWith(
                    t =>
                    {
                        _ = t.Exception;
                        connection.Dispose();
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default);
            }
        }
    }
}

/// <inheritdoc />
public sealed class OdpPasswordChanger : IPasswordChanger
{
    public async Task ChangeAsync(ConnectionProfile profile, string oldPassword, string newPassword, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ct.ThrowIfCancellationRequested();
        profile.ApplyProcessSettings();

        // 프록시 접속이어도 바꾸는 비밀번호는 프록시 사용자의 것이다(data-model 2.2 proxyUser).
        var own = profile with { User = string.IsNullOrWhiteSpace(profile.ProxyUser) ? profile.User : profile.ProxyUser, ProxyUser = null };
        await using var connection = new OracleConnection(own.BuildConnectionString(oldPassword));
        connection.OpenWithNewPassword(newPassword);
        await connection.CloseAsync().ConfigureAwait(false);
    }
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
    private ISession? _active;
    private bool _broken;

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

    public void Dispose()
    {
        foreach (var s in _subscriptions)
        {
            s.Dispose();
        }

        _gate.Dispose();
    }

    public ISession? ActiveSession => Volatile.Read(ref _active);

    public ConnectionProfile? LastProfile { get; private set; }

    public bool IsBroken => Volatile.Read(ref _broken);

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

        var previous = Interlocked.Exchange(ref _active, session);
        Volatile.Write(ref _broken, false);
        LastProfile = profile;
        if (previous is not null)
        {
            await previous.DisposeAsync().ConfigureAwait(false);
        }

        UpdateKeys();
        ActiveSessionChanged?.Invoke(this, EventArgs.Empty);
        await RecordConnectedAsync(profile, ct).ConfigureAwait(false);
        return new ConnectOutcome(ConnectStatus.Connected, $"{profile.Name} 에 접속했습니다({VersionText(session.Server)}).", session, session.Server.Version);
    }

    public async Task DisconnectAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var previous = Interlocked.Exchange(ref _active, null);
        Volatile.Write(ref _broken, false);
        if (previous is not null)
        {
            await previous.DisposeAsync().ConfigureAwait(false);
        }

        UpdateKeys();
        ActiveSessionChanged?.Invoke(this, EventArgs.Empty);
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

    /// <summary>서버 버전 문자열의 짧은 이름(23 이상 = 26ai, 19 = 19c). 읽을 수 없으면 null.</summary>
    internal static string? ServerLabel(string? version)
    {
        var head = version?.Split('.', 2)[0];
        return int.TryParse(head, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var major)
            ? major >= 23 ? "26ai" : major + "c"
            : null;
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
        if (Volatile.Read(ref _active) is { } active && active.Id == e.SessionId)
        {
            Volatile.Write(ref _broken, true);
            UpdateKeys();
            ActiveSessionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnClosed(SessionClosed e)
    {
        if (Volatile.Read(ref _active) is { } active && active.Id == e.SessionId)
        {
            // 「다시 접속」 은 LastProfile 로 하므로 세션만 비운다.
            Interlocked.CompareExchange(ref _active, null, active);
            Volatile.Write(ref _broken, false);
            UpdateKeys();
            ActiveSessionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void UpdateKeys()
    {
        _keys?.Set("hasSession", ActiveSession is not null && !IsBroken);
        _keys?.Set("sessionBroken", IsBroken);
    }

    private static string VersionText(ServerInfo server) =>
        server.IsAutonomous ? "ADB" : server.MajorVersion >= 23 ? "26ai" : server.MajorVersion + "c";

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
