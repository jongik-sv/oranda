using System;
using System.Threading;
using System.Threading.Tasks;
using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Connection;
using Oranda.Oracle.Connection;
using Oranda.Oracle.Sessions;

namespace Oranda.App.Features.ConnectionManager;

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
        var connection = driverProfile.CreateConnection(password);
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
        await using var connection = own.CreateConnection(oldPassword);
        connection.OpenWithNewPassword(newPassword);
        await connection.CloseAsync().ConfigureAwait(false);
    }
}
