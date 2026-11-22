using System.Diagnostics;
using System.Text.RegularExpressions;
using Oranda.Core.Ai.Profiles;
using Oranda.Core.Ai.Setup;
using Oranda.Platform.Ai.Cli;

namespace Oranda.Platform.Ai.Setup;

/// <summary>찾은 claude. <paramref name="Version"/> 은 <c>claude --version</c> 으로 읽은 판(읽지 못하면 null).</summary>
internal sealed record ClaudeFound(string Path, string? Version);

/// <summary>설치 결과. 실패면 <paramref name="Reason"/> 에 쉬운 까닭이나 마지막 오류 줄(비밀은 가림), <paramref name="ErrorKind"/> 에 종류.</summary>
internal sealed record ClaudeInstallOutcome(bool Success, ClaudeFound? Found, string? Reason, AiSetupErrorKind? ErrorKind = null);

internal enum ClaudeLoginCheckKind
{
    /// <summary>claude 를 찾지 못했다.</summary>
    NotFound,

    LoggedIn,

    LoggedOut,

    /// <summary>이 판은 상태를 JSON 으로 알려 주지 않는다(「로그인 끝났어요」 단추로).</summary>
    Unsupported,

    /// <summary>이번 확인만 실패했다(시간 초과·띄우지 못함). 다음 확인은 다시 해 본다.</summary>
    Failed,
}

/// <summary>로그인 상태. <paramref name="Email"/> 은 로그인했고 claude 가 알려 줄 때만.</summary>
/// <param name="Error"><see cref="ClaudeLoginCheckKind.Failed"/> 의 까닭.</param>
internal sealed record ClaudeLoginCheck(ClaudeLoginCheckKind Kind, string? Email = null, AiSetupErrorKind? Error = null);

internal enum ClaudeTestFailure
{
    None,
    NotFound,
    NotLoggedIn,
    Network,
    Timeout,
    ServerRefused,
    Unknown,
}

/// <summary>연결 시험 결과. <paramref name="Detail"/> 은 claude 가 준 글(비밀은 가림, 성공이면 답 글).</summary>
internal sealed record ClaudeTestOutcome(ClaudeTestFailure Failure, TimeSpan Elapsed, string? Detail);

/// <summary>엔진이 바깥에서 받는 값(시험이 바꿔 넣는다).</summary>
/// <param name="ConfiguredPath">설정에 적힌 claude 경로(없으면 null).</param>
/// <param name="PathVariable">PATH 값.</param>
/// <param name="FileExists">파일이 있는지.</param>
/// <param name="Home">사용자 홈 폴더(<c>~/.local/bin</c> 찾기).</param>
/// <param name="WorkDirectory">연결 시험을 돌릴 빈 폴더(프로젝트 설정을 읽지 않게).</param>
internal sealed record ClaudeSetupHost(
    Func<CancellationToken, Task<string?>> ConfiguredPath,
    Func<string?> PathVariable,
    Func<string, bool> FileExists,
    string Home,
    string WorkDirectory);

/// <summary>
/// claude 준비(찾기·판·설치·로그인·상태·연결 시험). 모든 프로세스는 <see cref="ISetupProcessRunner"/> 로 띄우고,
/// 끝나거나 취소되면 프로세스 트리를 끝낸다. 실패는 예외가 아니라 결과로 돌려준다(취소만 예외).
/// 출력 모양은 실측 문서(<c>docs/oranda1/research/ai-setup-claude-measure.md</c>)를 따른다.
/// </summary>
internal sealed partial class ClaudeSetupEngine
{
    /// <summary>연결 시험 상한. 프로필 연결 시험(<c>AiProfileProbe</c>)과 같은 60초.</summary>
    internal static readonly TimeSpan DefaultTestTimeout = TimeSpan.FromSeconds(60);

    internal const string TestPrompt = "OK 라고만 답하세요.";

    /// <summary>
    /// bash 로 돌리는 설치 한 줄. 파이프의 종료 코드는 마지막 명령(안쪽 bash)의 것이라, curl 이 내려받지 못해도 0 으로 끝난다 →
    /// <c>pipefail</c> 로 curl 의 실패가 드러나게 한다.
    /// </summary>
    internal const string UnixInstallScript = "set -o pipefail; " + ClaudeInstallCommands.Unix;

    /// <summary>
    /// PowerShell 로 돌리는 설치 한 줄. 숨은 창의 Windows PowerShell 5.1 은 출력이 OEM 코드 페이지(949)라 한글·✅ 가 깨진다 →
    /// 출력을 UTF-8 로 바꾼 뒤 돌린다(W2 실측). 실패는 install.ps1 이 Write-Error 뒤 exit 1.
    /// </summary>
    internal const string WindowsInstallScript = "[Console]::OutputEncoding=[Text.Encoding]::UTF8; " + ClaudeInstallCommands.Windows;

    private static readonly TimeSpan QuickTimeout = TimeSpan.FromSeconds(15);

    // 연결 시험에서 결과 줄을 받은 뒤 프로세스가 끝나기를 기다리는 시간. 손자가 출력 관을 쥐고 있어도 결과를 버리지 않는다.
    private static readonly TimeSpan ExitGrace = TimeSpan.FromSeconds(5);

    private readonly ISetupProcessRunner _runner;
    private readonly ClaudeSetupHost _host;
    private readonly TimeSpan _testTimeout;

    public ClaudeSetupEngine(ISetupProcessRunner runner, ClaudeSetupHost host, TimeSpan? testTimeout = null)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _testTimeout = testTimeout ?? DefaultTestTimeout;
    }

    /// <summary>자동 설치가 안 될 때 직원이 검은 창에 붙여 넣을 한 줄.</summary>
    public static string ManualInstallCommand => ClaudeInstallCommands.ManualFor(AiProfilePresets.CurrentPlatform);

    /// <summary>설정 경로 → PATH → 흔한 설치 위치 순으로 찾고 판을 읽는다. 못 찾으면 null.</summary>
    public async Task<ClaudeFound?> FindAsync(CancellationToken ct)
    {
        var path = await LocateAsync(ct).ConfigureAwait(false);
        if (path is null)
        {
            return null;
        }

        var (exitCode, lines, _) = await RunQuickAsync(path, ["--version"], ct).ConfigureAwait(false);
        var version = exitCode == 0 ? SystemCliVersion.Parse(string.Join('\n', lines.Where(l => !l.IsError).Select(l => l.Text))) : null;
        return new ClaudeFound(path, version);
    }

    /// <summary>
    /// 공식 설치 명령을 숨은 창에서 돌린다. 줄마다 <paramref name="progress"/> 로 알리고(꾸밈 글자를 빼고 비밀은 가림),
    /// 끝나면 다시 찾는다. 취소하면 프로세스 트리를 끝내고 <see cref="OperationCanceledException"/> 을 낸다.
    /// </summary>
    public async Task<ClaudeInstallOutcome> InstallAsync(IProgress<string>? progress, CancellationToken ct)
    {
        var launch = OperatingSystem.IsWindows()
            ? new SetupLaunch("powershell", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", WindowsInstallScript], ChildEnvironment(null))
            : new SetupLaunch("/bin/bash", ["-c", UnixInstallScript], ChildEnvironment(null));

        ISetupProcess process;
        try
        {
            process = _runner.Start(launch);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return new ClaudeInstallOutcome(false, null, SetupText.Redact(ex.Message), AiSetupErrorKind.Unknown);
        }

        string? lastLine = null;
        string? lastError = null;
        int exitCode;
        await using (process.ConfigureAwait(false))
        {
            try
            {
                await foreach (var line in process.ReadLinesAsync(ct).ConfigureAwait(false))
                {
                    var text = SetupText.Redact(ClaudeSetupOutput.Plain(line.Text)).Trim();
                    if (text.Length == 0)
                    {
                        continue;
                    }

                    lastLine = text;
                    if (line.IsError)
                    {
                        lastError = text;
                    }

                    progress?.Report(text);
                }

                exitCode = await process.WaitForExitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                process.Kill();
                throw;
            }
        }

        if (exitCode != 0)
        {
            var reason = lastError ?? lastLine ?? $"설치가 끝나지 못했습니다(종료 코드 {exitCode}).";
            return new ClaudeInstallOutcome(false, null, reason, InstallNetworkError().IsMatch(reason) ? AiSetupErrorKind.Network : AiSetupErrorKind.Unknown);
        }

        var found = await FindAsync(ct).ConfigureAwait(false);
        if (found is not null)
        {
            return new ClaudeInstallOutcome(true, found, null);
        }

        // 종료 코드가 0 이어도 내려받기 실패 글이 있었으면 망 문제로 본다(pipefail 이 없는 셸 대비).
        return lastError is not null && InstallNetworkError().IsMatch(lastError)
            ? new ClaudeInstallOutcome(false, null, lastError, AiSetupErrorKind.Network)
            : new ClaudeInstallOutcome(false, null, "설치는 끝났지만 claude 를 찾지 못했습니다.", AiSetupErrorKind.CliNotFound);
    }

    /// <summary>
    /// <c>claude auth login</c> 을 뒤에서 띄운다(브라우저는 claude 가 연다). claude 를 못 찾으면 null.
    /// 받은 것을 버리면(Dispose) 프로세스 트리를 끝낸다 — 표준 입력이 닫혀도 claude 는 스스로 끝나지 않는다.
    /// </summary>
    public async Task<ClaudeLoginSession?> StartLoginAsync(CancellationToken ct)
    {
        var path = await LocateAsync(ct).ConfigureAwait(false);
        if (path is null)
        {
            return null;
        }

        try
        {
            return new ClaudeLoginSession(_runner.Start(new SetupLaunch(path, ["auth", "login"], ChildEnvironment(path))));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary><c>claude auth status --json</c> 을 읽는다. 모르는 칸은 버리고, JSON 이 아니면 <see cref="ClaudeLoginCheckKind.Unsupported"/>.</summary>
    public async Task<ClaudeLoginCheck> StatusAsync(CancellationToken ct)
    {
        var path = await LocateAsync(ct).ConfigureAwait(false);
        if (path is null)
        {
            return new ClaudeLoginCheck(ClaudeLoginCheckKind.NotFound);
        }

        var (exitCode, lines, timedOut) = await RunQuickAsync(path, ["auth", "status", "--json"], ct).ConfigureAwait(false);
        if (timedOut)
        {
            return new ClaudeLoginCheck(ClaudeLoginCheckKind.Failed, Error: AiSetupErrorKind.Timeout);
        }

        if (exitCode == -1 && lines.Count == 0)
        {
            return new ClaudeLoginCheck(ClaudeLoginCheckKind.Failed, Error: AiSetupErrorKind.Unknown); // 띄우지 못했다
        }

        var state = ClaudeSetupOutput.AuthStatus(string.Join('\n', lines.Where(l => !l.IsError).Select(l => l.Text)));
        return state switch
        {
            null => new ClaudeLoginCheck(ClaudeLoginCheckKind.Unsupported),
            { LoggedIn: true } => new ClaudeLoginCheck(ClaudeLoginCheckKind.LoggedIn, state.Email),
            _ => new ClaudeLoginCheck(ClaudeLoginCheckKind.LoggedOut),
        };
    }

    /// <summary>
    /// 짧은 질문 한 번으로 연결을 시험한다. 도구·MCP 를 모두 끄고 빈 폴더에서 돌린다(DB 를 건드리지 않는다).
    /// 상한(<see cref="DefaultTestTimeout"/>)을 넘으면 끝내고 <see cref="ClaudeTestFailure.Timeout"/>.
    /// </summary>
    public async Task<ClaudeTestOutcome> TestAsync(CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        var path = await LocateAsync(ct).ConfigureAwait(false);
        if (path is null)
        {
            return new ClaudeTestOutcome(ClaudeTestFailure.NotFound, clock.Elapsed, null);
        }

        try
        {
            Directory.CreateDirectory(_host.WorkDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ClaudeTestOutcome(ClaudeTestFailure.Unknown, clock.Elapsed, SetupText.Redact(ex.Message));
        }

        string[] arguments =
        [
            "-p", TestPrompt,
            "--output-format", "stream-json",
            "--verbose",
            "--tools", string.Empty,
            "--strict-mcp-config",
            "--setting-sources", "project",
        ];

        ISetupProcess process;
        try
        {
            process = _runner.Start(new SetupLaunch(path, arguments, ChildEnvironment(path), _host.WorkDirectory));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return new ClaudeTestOutcome(ClaudeTestFailure.NotFound, clock.Elapsed, SetupText.Redact(ex.Message));
        }

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(_testTimeout);
        var parser = new ClaudeStreamParser();
        ClaudeResult? result = null;
        string? lastError = null;
        await using (process.ConfigureAwait(false))
        {
            try
            {
                await foreach (var line in process.ReadLinesAsync(limit.Token).ConfigureAwait(false))
                {
                    if (line.IsError)
                    {
                        lastError = line.Text;
                        continue;
                    }

                    result = parser.Parse(line.Text).OfType<ClaudeResult>().LastOrDefault() ?? result;
                    if (result is not null)
                    {
                        break; // 결과가 마지막 줄이다. 출력 관이 닫히기를 기다리지 않는다.
                    }
                }

                if (result is null)
                {
                    await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
                }
                else
                {
                    // 결과는 받았다. 잠깐만 기다리고, 그래도 안 끝나면 트리를 끝낸다(아래 Dispose 도 끝낸다).
                    using var grace = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    grace.CancelAfter(ExitGrace);
                    try
                    {
                        await process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        process.Kill();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                process.Kill();
                if (ct.IsCancellationRequested)
                {
                    throw;
                }

                return new ClaudeTestOutcome(ClaudeTestFailure.Timeout, clock.Elapsed, null);
            }
        }

        var elapsed = clock.Elapsed;
        if (result is null)
        {
            return new ClaudeTestOutcome(ClaudeTestFailure.Unknown, elapsed, lastError is null ? null : SetupText.Redact(lastError));
        }

        var detail = result.Text is null ? null : SetupText.Redact(result.Text);
        return new ClaudeTestOutcome(result.IsError ? Classify(result) : ClaudeTestFailure.None, elapsed, detail);
    }

    /// <summary>오류 결과를 종류로 나눈다. 글은 판마다 바뀔 수 있어 맞지 않으면 「모름」.</summary>
    internal static ClaudeTestFailure Classify(ClaudeResult result)
    {
        var text = result.Text ?? string.Empty;
        if (result.NotLoggedIn || Unauthorized().IsMatch(text))
        {
            return ClaudeTestFailure.NotLoggedIn;
        }

        // 서버가 상태 번호로 답했으면 닿은 것이다: 글에 network 같은 낱말이 섞여도 거절로 본다.
        if (ServerStatus().IsMatch(text))
        {
            return ClaudeTestFailure.ServerRefused;
        }

        return NetworkError().IsMatch(text) ? ClaudeTestFailure.Network : ClaudeTestFailure.Unknown;
    }

    private async Task<string?> LocateAsync(CancellationToken ct)
    {
        var configured = await _host.ConfiguredPath(ct).ConfigureAwait(false);
        var path = ClaudeExecutable.Find(configured, _host.PathVariable(), _host.FileExists, _host.Home);
        return path is null || ClaudeExecutable.IsBatchFile(path) ? null : path;
    }

    /// <summary>짧게 끝나는 명령(판·상태). 상한을 넘거나 띄우지 못하면 종료 코드 -1.</summary>
    private async Task<(int ExitCode, List<SetupLine> Lines, bool TimedOut)> RunQuickAsync(string path, string[] arguments, CancellationToken ct)
    {
        ISetupProcess process;
        try
        {
            process = _runner.Start(new SetupLaunch(path, arguments, ChildEnvironment(path)));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return (-1, [], false);
        }

        var lines = new List<SetupLine>();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(QuickTimeout);
        await using (process.ConfigureAwait(false))
        {
            try
            {
                await foreach (var line in process.ReadLinesAsync(limit.Token).ConfigureAwait(false))
                {
                    lines.Add(line);
                }

                return (await process.WaitForExitAsync(limit.Token).ConfigureAwait(false), lines, false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                process.Kill();
                return (-1, lines, true);
            }
            catch (OperationCanceledException)
            {
                process.Kill();
                throw;
            }
        }
    }

    /// <summary>
    /// 자식 환경: claude 안에서 띄운 Oranda 라도 「중첩 실행」 으로 보지 않게 표시 변수를 지우고,
    /// 실행 파일 폴더를 PATH 앞에 둔다(npm 설치의 claude 는 옆의 node 를 찾는다).
    /// </summary>
    private Dictionary<string, string?> ChildEnvironment(string? executable)
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["CLAUDECODE"] = null,
            ["CLAUDE_CODE_ENTRYPOINT"] = null,
        };
        if (executable is not null && Path.GetDirectoryName(executable) is { Length: > 0 } directory)
        {
            var path = _host.PathVariable() ?? string.Empty;
            if (!path.Split(Path.PathSeparator).Contains(directory, StringComparer.Ordinal))
            {
                environment["PATH"] = path.Length == 0 ? directory : directory + Path.PathSeparator + path;
            }
        }

        return environment;
    }

    [GeneratedRegex(@"\bAPI Error: 401\b|authentication_error|invalid x-api-key|OAuth token has expired", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex Unauthorized();

    [GeneratedRegex(@"Connection error|ENOTFOUND|ECONNREFUSED|ECONNRESET|ETIMEDOUT|EAI_AGAIN|getaddrinfo|fetch failed|Unable to connect|socket hang up|network error|network is unreachable", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex NetworkError();

    // 설치 스크립트(install.sh·install.ps1)가 내려받지 못했을 때의 글과 curl·PowerShell 의 망 오류.
    [GeneratedRegex(@"Download failed|Failed to download|Failed to get latest version|Failed to get a valid version|downloads\.claude\.ai|Could not resolve host|Failed to connect|Connection timed out|\bSSL\b|unable to connect|remote name could not be resolved|curl: \(\d+\)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex InstallNetworkError();

    [GeneratedRegex(@"\bAPI Error: [45]\d\d\b", RegexOptions.CultureInvariant)]
    private static partial Regex ServerStatus();
}
