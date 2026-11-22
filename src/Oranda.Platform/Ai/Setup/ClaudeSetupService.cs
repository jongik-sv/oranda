using Oranda.Core.Ai.Setup;
using Oranda.Core.Composition;
using Oranda.Core.Settings;
using Oranda.Core.Storage;

namespace Oranda.Platform.Ai.Setup;

/// <summary>
/// AI 연결 도우미 갈래 A 의 claude 준비 서비스(계약 <see cref="IClaudeSetupService"/>). 일은 <see cref="ClaudeSetupEngine"/> 이 하고
/// 여기서는 계약 형태로 옮긴다. 실패는 결과로, 취소만 예외로 낸다.
/// </summary>
[OrandaService(typeof(IClaudeSetupService))]
internal sealed class ClaudeSetupService : IClaudeSetupService
{
    // 로그인을 시작하고 주소가 나오기를 기다리는 시간(실측: 시작 직후 나옴). 넘으면 주소는 progress 로 늦게 알린다.
    private static readonly TimeSpan DefaultLoginUrlWait = TimeSpan.FromSeconds(5);

    private readonly ClaudeSetupEngine _engine;
    private readonly TimeSpan _loginUrlWait;

    public ClaudeSetupService(ISettingsStore settings, IAppDataPaths paths)
        : this(
            new ClaudeSetupEngine(
                new SystemSetupProcessRunner(),
                new ClaudeSetupHost(
                    ct => ConfiguredPathAsync(settings, ct),
                    () => Environment.GetEnvironmentVariable("PATH"),
                    File.Exists,
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    Path.Combine(paths.DataDirectory, "ai", "setup-work"))),
            DefaultLoginUrlWait)
    {
    }

    /// <summary>시험용: 엔진(가짜 실행기)과 주소 기다림 시간을 바꿔 넣는다.</summary>
    internal ClaudeSetupService(ClaudeSetupEngine engine, TimeSpan loginUrlWait)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _loginUrlWait = loginUrlWait;
    }

    public async Task<ClaudeInstallation?> FindAsync(CancellationToken ct) =>
        await _engine.FindAsync(ct).ConfigureAwait(false) is { } found ? new ClaudeInstallation(found.Path, found.Version) : null;

    public async Task<ClaudeInstallResult> InstallAsync(IProgress<string>? progress, CancellationToken ct)
    {
        var outcome = await _engine.InstallAsync(progress, ct).ConfigureAwait(false);
        return new ClaudeInstallResult(
            outcome.Success,
            outcome.Found is { } found ? new ClaudeInstallation(found.Path, found.Version) : null,
            outcome.Success ? null : outcome.ErrorKind ?? AiSetupErrorKind.Unknown,
            ClaudeSetupEngine.ManualInstallCommand,
            outcome.Reason);
    }

    public async Task<ClaudeLoginStart> StartLoginAsync(IProgress<string>? browserUrl, CancellationToken ct)
    {
        var session = await _engine.StartLoginAsync(ct).ConfigureAwait(false);
        if (session is null)
        {
            // 못 찾았거나 띄우지 못했다. 다시 찾아 까닭을 가른다.
            var found = await _engine.FindAsync(ct).ConfigureAwait(false);
            return new ClaudeLoginStart(false, null, null, found is null ? AiSetupErrorKind.CliNotFound : AiSetupErrorKind.Unknown);
        }

        string? url = null;
        try
        {
            url = await session.Url.WaitAsync(_loginUrlWait, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // 기다리는 사이 막 나왔으면 바로 쓰고, 아니면 아래에서 progress 로 늦게 알린다.
            url = session.Url.IsCompletedSuccessfully ? session.Url.Result : null;
        }
        catch (OperationCanceledException)
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        if (url is null && browserUrl is not null)
        {
            _ = session.Url.ContinueWith(
                t =>
                {
                    if (t.Result is { } late)
                    {
                        browserUrl.Report(late);
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        return new ClaudeLoginStart(true, url, session, Exited: session.Exited);
    }

    public async Task<ClaudeLoginStatus> GetLoginStatusAsync(CancellationToken ct)
    {
        var check = await _engine.StatusAsync(ct).ConfigureAwait(false);
        return check.Kind switch
        {
            ClaudeLoginCheckKind.LoggedIn => new ClaudeLoginStatus(true, check.Email, StatusSupported: true),
            ClaudeLoginCheckKind.LoggedOut => new ClaudeLoginStatus(false, null, StatusSupported: true),
            ClaudeLoginCheckKind.NotFound => new ClaudeLoginStatus(false, null, StatusSupported: false, AiSetupErrorKind.CliNotFound),

            // 이번 확인만 실패했다: 지원 여부는 그대로 두어 화면이 몇 초 뒤 다시 묻게 한다.
            ClaudeLoginCheckKind.Failed => new ClaudeLoginStatus(false, null, StatusSupported: true, check.Error ?? AiSetupErrorKind.Unknown),
            _ => new ClaudeLoginStatus(false, null, StatusSupported: false),
        };
    }

    public async Task<AiSetupTestResult> TestConnectionAsync(CancellationToken ct)
    {
        var outcome = await _engine.TestAsync(ct).ConfigureAwait(false);
        var kind = outcome.Failure switch
        {
            ClaudeTestFailure.None => (AiSetupErrorKind?)null,
            ClaudeTestFailure.NotFound => AiSetupErrorKind.CliNotFound,
            ClaudeTestFailure.NotLoggedIn => AiSetupErrorKind.NotLoggedIn,
            ClaudeTestFailure.Network => AiSetupErrorKind.Network,
            ClaudeTestFailure.Timeout => AiSetupErrorKind.Timeout,
            ClaudeTestFailure.ServerRefused => AiSetupErrorKind.ServerRejected,
            _ => AiSetupErrorKind.Unknown,
        };
        return new AiSetupTestResult(kind is null, outcome.Elapsed, kind, kind is null ? null : outcome.Detail);
    }

    private static async Task<string?> ConfiguredPathAsync(ISettingsStore settings, CancellationToken ct)
    {
        try
        {
            return (await settings.LoadAsync(ct).ConfigureAwait(false)).Value.Ai.ExecutablePath;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null; // 설정을 못 읽으면 PATH·흔한 위치로 찾는다.
        }
    }
}
