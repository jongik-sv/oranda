using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Sessions;
using Oranda.Oracle.Execution;
using Oranda.Oracle.Sessions;

namespace Oranda.Oracle.Connection;

// O4: 접속 쪽 Core 인터페이스(IConnectionStringFactory·IConnectionProbe)의 Oracle 구현. 드라이버 변환(프록시 표기·tnsnames.ora 펼치기)은 여기서 한다.

/// <inheritdoc cref="IConnectionStringFactory"/>
[OrandaService(typeof(IConnectionStringFactory), Dbms = Dbms.Oracle)]
internal sealed class OracleConnectionStringFactory : IConnectionStringFactory
{
    public Dbms Dbms => Dbms.Oracle;

    public string Build(ConnectionProfile profile, string password) => profile.BuildConnectionString(password);
}

/// <summary>
/// 저장된 프로필 → 드라이버용 프로필(<c>ConnectionService.ToDriverProfile</c> 의 Oracle 쪽 사본, TnsResolver 도 함께 옮김).
/// 프록시 접속이면 사용자를 ODP.NET 형식 <c>proxy[user]</c> 로 바꾸고, TNS 디렉터리의 별칭이면 tnsnames.ora 에서 기술자를 찾아 접속 문자열로 넘긴다
/// (ODP.NET 의 TNS_ADMIN 은 프로세스 전체 설정이라 연결을 연 뒤에는 바꿀 수 없다). 지갑 폴더는 기술자의 <c>MY_WALLET_DIRECTORY</c> 로도 알린다.
/// 저장된 프로필은 바꾸지 않는다.
/// </summary>
internal static class OracleDriverProfile
{
    private static readonly object Gate = new();

    /// <summary>이 프로세스에서 드라이버 전역 지갑 위치로 이미 넘긴 폴더(없으면 null).</summary>
    private static string? _appliedWallet;

    public static ConnectionProfile From(ConnectionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
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
            bool keepWallet;
            lock (Gate)
            {
                keepWallet = _appliedWallet is null || string.Equals(_appliedWallet, profile.WalletLocation, StringComparison.OrdinalIgnoreCase);
                if (keepWallet && !string.IsNullOrWhiteSpace(profile.WalletLocation))
                {
                    _appliedWallet = profile.WalletLocation;
                }
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
}

/// <inheritdoc cref="IConnectionProbe"/>
[OrandaService(typeof(IConnectionProbe), Dbms = Dbms.Oracle)]
internal sealed class OracleConnectionProbe : IConnectionProbe
{
    public Dbms Dbms => Dbms.Oracle;

    public async Task<ServerProbeResult> ProbeAsync(ConnectionProfile profile, string password, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ct.ThrowIfCancellationRequested();

        // 접속 제한 시간은 연결 문자열(Connection Timeout)에 들어간다. 취소 단추는 기다림만 끊고,
        // 이미 시작한 연결 시도는 끝난 뒤에 닫는다(열리는 중인 연결을 다른 스레드에서 닫지 않는다).
        var connection = OracleDriverProfile.From(profile).CreateConnection(password);
        var open = connection.OpenAsync(ct);
        try
        {
            await open.WaitAsync(ct).ConfigureAwait(false);
            var version = connection.ServerVersion;
            return SupportedServerPolicy.IsSupported(version, out var reason)
                ? ServerProbeResult.Ok(version)
                : ServerProbeResult.Unsupported(version, reason);
        }
        catch (OracleException ex)
        {
            return ServerProbeResult.Fail(OracleErrorClassifier.ToDbError(ex), ex.Message);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException or TimeoutException)
        {
            return ServerProbeResult.Fail(null, ex.Message);
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

    public async Task ChangePasswordAsync(ConnectionProfile profile, string oldPassword, string newPassword, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ct.ThrowIfCancellationRequested();

        // 프록시 접속이어도 바꾸는 비밀번호는 프록시 사용자의 것이다(data-model 2.2 proxyUser).
        var own = OracleDriverProfile.From(profile with { User = string.IsNullOrWhiteSpace(profile.ProxyUser) ? profile.User : profile.ProxyUser, ProxyUser = null });
        await using var connection = own.CreateConnection(oldPassword);
        connection.OpenWithNewPassword(newPassword);
        await connection.CloseAsync().ConfigureAwait(false);
    }
}

/// <inheritdoc cref="IServerVersionPolicy"/>
[OrandaService(typeof(IServerVersionPolicy), Dbms = Dbms.Oracle)]
internal sealed class OracleServerVersionPolicy : IServerVersionPolicy
{
    public Dbms Dbms => Dbms.Oracle;

    public string MinimumText => SupportedServerPolicy.MinimumText;

    public IReadOnlyList<string> TestedVersions => SupportedServerPolicy.TestedVersions;

    public bool IsSupported(string? serverVersion, out string reason) => SupportedServerPolicy.IsSupported(serverVersion, out reason);
}
