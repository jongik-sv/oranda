using System.ComponentModel;
using System.Diagnostics;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Storage;
using Oranda.Platform.Ai.Bridge;

namespace Oranda.Platform.Ai.Cli;

/// <summary>
/// 시스템 CLI 드라이버가 함께 쓰는 실행 골격(CLI 종류와 무관한 부분). 사전 조건(세션·브리지·orandactl)을 확인하고,
/// 실행마다 <see cref="SystemCliRun"/> 을 시작해 실행 토큰·실행 폴더·게이트 판단 큐·취소 때 프로세스 종료·정리를 맡는다.
/// 인자·출력 해석·초기 검증 같은 CLI 고유 부분은 드라이버가 맡는다. 이 골격은 도구 호출 경로를 만들지 않는다:
/// 모든 도구 호출은 CLI → orandactl → 파이프 → AgentGate 로만 지난다.
/// </summary>
internal sealed class SystemCliRunner
{
    /// <summary>
    /// 오래된 실행 폴더로 보는 나이: 7시간. 시스템 CLI 한 차례 실행 제한(agy 는 6시간)보다 커야 한다 — 새 실행이 아직 도는 긴 실행의 폴더를 지우면 안 된다.
    /// 다른 CLI 드라이버가 자기 실행 제한과 비교할 수 있게 공개 범위 안에 둔다.
    /// </summary>
    internal static readonly TimeSpan StaleRunAge = TimeSpan.FromHours(7);

    private readonly IAgentRunTokens _tokens;
    private readonly IAgentGate _gate;
    private readonly IAgentBridgeEndpoint _bridge;
    private readonly string _aiDirectory;
    private readonly string _orandactlPath;
    private readonly Func<string, bool> _fileExists;

    public SystemCliRunner(
        IAgentRunTokens tokens,
        IAgentGate gate,
        IAgentBridgeEndpoint bridge,
        IAppDataPaths paths,
        string orandactlPath,
        Func<string, bool> fileExists)
    {
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        ArgumentNullException.ThrowIfNull(paths);
        _aiDirectory = Path.Combine(paths.DataDirectory, "ai");
        _orandactlPath = orandactlPath;
        _fileExists = fileExists ?? throw new ArgumentNullException(nameof(fileExists));
    }

    /// <summary>앱 데이터 폴더의 <c>ai</c> 폴더(세션 소속·실행 폴더·작업 폴더의 부모).</summary>
    public string AiDirectory => _aiDirectory;

    /// <summary>orandactl 실행 파일 경로(mcp-config 에 넣는다).</summary>
    public string OrandactlPath => _orandactlPath;

    /// <summary>실행 범위(세션)가 있고 폐기되지 않았는지, 브리지가 열려 있는지, orandactl 이 있는지 확인한다.</summary>
    public (AiError? Error, string? PipeName) CheckPreconditions(AiRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Session is null)
        {
            return (new AiError("AI 요청에 실행 범위(Session)가 없습니다.", "session_required"), null);
        }

        if (request.Session.IsRevoked)
        {
            return (new AiError("이 AI 요청의 실행 범위가 이미 폐기되었습니다. 새 요청으로 다시 시작하세요.", "session_revoked"), null);
        }

        var pipeName = _bridge.PipeName;
        if (pipeName is null)
        {
            return (new AiError("AI 연결이 열려 있지 않습니다. AI 튜닝 기능이 켜져 있는지 확인하세요.", "bridge_not_ready"), null);
        }

        return !_fileExists(_orandactlPath)
            ? (new AiError("orandactl 실행 파일을 찾지 못했습니다.", "orandactl_missing"), null)
            : (null, pipeName);
    }

    /// <summary>
    /// 실행 하나를 시작한다: 오래된 실행 폴더를 정리하고, 새 실행 토큰을 발급하며, 게이트 판단 구독을 건다.
    /// 반환값은 반드시 <see cref="SystemCliRun.DisposeAsync"/> 해야 한다(토큰 폐기·실행 폴더 삭제·구독 해제).
    /// </summary>
    /// <param name="timeout">호출 측 시간 제한. null 이면 없다(CLI 가 재연결로 오래 걸릴 수 있어 CLI 별로 정한다).</param>
    public SystemCliRun Begin(AgentSession session, string pipeName, CancellationToken ct, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        var runsDirectory = Path.Combine(_aiDirectory, "runs");
        var runDirectory = Path.Combine(runsDirectory, $"{session.RunId}-{Guid.NewGuid():N}");
        SystemCliRun.DeleteStaleRuns(runsDirectory, StaleRunAge);

        if (timeout is { } limit && limit <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "시간 제한은 0 보다 커야 합니다.");
        }

        var token = _tokens.Issue(session);
        SystemCliJudgements? judgements = null;
        try
        {
            judgements = new SystemCliJudgements(_gate, session.RunId);
            return new SystemCliRun(_tokens, token, runDirectory, _aiDirectory, pipeName, _orandactlPath, judgements, timeout, ct);
        }
        catch
        {
            try
            {
                judgements?.Dispose();
            }
            finally
            {
                _tokens.Revoke(token);
            }

            throw;
        }
    }
}

/// <summary>
/// 시스템 CLI 실행 하나. 실행 토큰·실행 폴더·게이트 판단 큐를 들고, 취소(또는 시간 제한)가 오면 프로세스를 끝내며,
/// <see cref="DisposeAsync"/> 가 한 단계가 실패해도 나머지(프로세스 종료·구독 해제·토큰 폐기·폴더 삭제)를 끝까지 한다.
/// </summary>
internal sealed class SystemCliRun : IAsyncDisposable
{
    private readonly IAgentRunTokens _tokens;
    private readonly CancellationTokenSource? _timeoutSource;
    private readonly CancellationToken _outerToken;
    private CancellationTokenRegistration _killOnCancel;

    internal SystemCliRun(
        IAgentRunTokens tokens,
        string token,
        string runDirectory,
        string aiDirectory,
        string pipeName,
        string orandactlPath,
        SystemCliJudgements judgements,
        TimeSpan? timeout,
        CancellationToken ct)
    {
        _tokens = tokens;
        Token = token;
        RunDirectory = runDirectory;
        AiDirectory = aiDirectory;
        PipeName = pipeName;
        OrandactlPath = orandactlPath;
        Judgements = judgements;
        _outerToken = ct;
        if (timeout is { } limit)
        {
            _timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _timeoutSource.CancelAfter(limit);
            CancellationToken = _timeoutSource.Token;
        }
        else
        {
            CancellationToken = ct;
        }
    }

    /// <summary>이 실행의 실행 토큰. 명령줄 인자에 넣지 않고 프로세스 환경 변수(<see cref="TokenEnvironment"/>)로만 넘긴다.</summary>
    public string Token { get; }

    /// <summary>이 실행 전용 폴더(토큰이 든 파일을 만드는 곳). 끝나면 지운다.</summary>
    public string RunDirectory { get; }

    public string AiDirectory { get; }

    public string PipeName { get; }

    public string OrandactlPath { get; }

    public SystemCliJudgements Judgements { get; }

    /// <summary>호출 측 취소와 시간 제한을 합친 토큰. 시간 제한이 없으면 호출 측 토큰 그대로다.</summary>
    public CancellationToken CancellationToken { get; }

    /// <summary>시간 제한이 지나서 취소됐는지(호출 측이 직접 취소한 경우는 false).</summary>
    public bool TimedOut => _timeoutSource is { IsCancellationRequested: true } && !_outerToken.IsCancellationRequested;

    public IClaudeProcess? Process { get; private set; }

    /// <summary>실행 토큰을 환경 변수 하나로 만든다(<see cref="ClaudeLaunch.Environment"/> 에 합친다).</summary>
    public KeyValuePair<string, string?> TokenEnvironment(string variableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(variableName);
        return new(variableName, Token);
    }

    /// <summary>
    /// 준비 단계(실행 폴더에 파일 만들기 등). 디스크 오류는 예외 대신 AiError 로 알리고, 그러면 호출 측은 프로세스를 띄우지 않고 닫아야 한다(fail-closed).
    /// </summary>
    public static (T Value, AiError? Error) Prepare<T>(Func<T> step)
    {
        ArgumentNullException.ThrowIfNull(step);
        try
        {
            return (step(), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (default!, new AiError($"AI 실행 준비에 실패했습니다: {ex.Message}", "cli_start_failed"));
        }
    }

    /// <summary>
    /// 프로세스를 띄운다. 실행 토큰이 명령줄 인자에 들어 있으면(프로세스 목록에 노출된다) 띄우지 않고 닫는다.
    /// 표준 입력의 글은 <see cref="ClaudeLaunch.StandardInput"/> 으로 쓰고 닫는다. 띄운 뒤에는 취소·시간 제한이 오면 곧바로 프로세스를 끝낸다.
    /// </summary>
    /// <param name="cliName">오류 글에 쓸 CLI 이름.</param>
    public (IClaudeProcess? Process, AiError? Error) Start(IClaudeProcessLauncher launcher, ClaudeLaunch launch, string cliName)
    {
        ArgumentNullException.ThrowIfNull(launcher);
        ArgumentNullException.ThrowIfNull(launch);
        if (launch.Arguments.Any(a => a.Contains(Token, StringComparison.Ordinal)))
        {
            return (null, new AiError($"{cliName} 의 명령줄 인자에 실행 토큰이 들어 있어 시작하지 않았습니다.", "cli_start_failed"));
        }

        IClaudeProcess started;
        try
        {
            started = launcher.Start(launch);
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            return (null, new AiError($"{cliName} 를 시작하지 못했습니다: {ex.Message}", "cli_start_failed"));
        }

        Process = started;

        // 읽기 취소가 늦게 반영되는 경우에도 취소하면 곧바로 프로세스를 끝낸다.
        _killOnCancel = CancellationToken.Register(started.Kill);
        return (started, null);
    }

    /// <summary>
    /// 스트림 감시: 허용 목록 밖 도구가 보이면 곧바로 프로세스를 끝내고 <c>unexpected_tool</c> 오류를 돌려준다. 허용이면 null.
    /// </summary>
    public AiError? RejectUnexpectedTool(string toolName, Func<string, bool> isAllowed)
    {
        ArgumentNullException.ThrowIfNull(isAllowed);
        if (isAllowed(toolName))
        {
            return null;
        }

        try
        {
            Process?.Kill();
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException or IOException or UnauthorizedAccessException or AggregateException)
        {
            // 종료가 실패해도 오류는 돌려준다: 호출 측이 이 오류로 닫으면 정리 단계(DisposeAsync)가 한 번 더 끝내려 한다.
        }

        return new AiError($"예상하지 못한 도구가 열렸습니다: {toolName}", "unexpected_tool");
    }

    /// <summary>시간 제한 오류. 호출 측이 <see cref="TimedOut"/> 일 때 이 오류로 닫는다.</summary>
    public static AiError TimeoutError(string cliName, TimeSpan limit)
        => new($"{cliName} 가 제한 시간({limit.TotalSeconds:0}초) 안에 끝나지 않아 중단했습니다.", "cli_timeout");

    /// <summary>한 단계가 실패해도 나머지(프로세스 종료·구독 해제·토큰 폐기·폴더 삭제)는 반드시 한다.</summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            await _killOnCancel.DisposeAsync().ConfigureAwait(false);
            if (Process is not null)
            {
                try
                {
                    Process.Kill();
                }
                finally
                {
                    await Process.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
        finally
        {
            try
            {
                Judgements.Dispose();
            }
            finally
            {
                try
                {
                    _tokens.Revoke(Token);
                }
                finally
                {
                    try
                    {
                        DeleteQuietly(RunDirectory);
                    }
                    finally
                    {
                        _timeoutSource?.Dispose();
                    }
                }
            }
        }
    }

    internal static void DeleteQuietly(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 다음 실행이 오래된 실행 폴더를 정리한다.
        }
    }

    /// <summary>앱이 죽었거나 삭제가 실패해 남은 오래된 실행 폴더(토큰이 든 파일)를 지운다. 진행 중인 다른 실행은 건드리지 않는다.</summary>
    internal static void DeleteStaleRuns(string runsDirectory, TimeSpan staleAge)
    {
        try
        {
            if (!Directory.Exists(runsDirectory))
            {
                return;
            }

            foreach (var directory in Directory.EnumerateDirectories(runsDirectory))
            {
                if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(directory) > staleAge)
                {
                    DeleteQuietly(directory);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 정리는 최선을 다하는 일이다.
        }
    }
}

/// <summary>
/// 시스템 CLI 의 버전을 읽어, Oranda 가 잠금을 검증하지 않은 버전이면 알리는 공용 부분. CLI 는 스스로 업데이트되므로 잠금(도구 제거·설정 유입 차단)이
/// 업데이트로 깨질 수 있다. 이 안내는 상태를 알리는 일만 하고 실행을 막지 않는다: 막는 일은 스트림 감시와 거부가 그대로 맡는다.
/// 버전 읽기 프로세스에는 실행 토큰을 주지 않고(토큰 발급 전에 실행한다), 시간 상한을 짧게 둔다.
/// </summary>
internal static class SystemCliVersion
{
    /// <summary>버전 읽기 시간 상한. 넘으면 읽지 못한 것으로 보고 안내를 낸다.</summary>
    internal static readonly TimeSpan ReadTimeLimit = TimeSpan.FromSeconds(5);

    /// <summary>버전 읽기: 실행 파일과 시간 상한을 받아 버전 글(첫 줄)을 돌려준다. 실행 실패·시간 초과면 null.</summary>
    internal delegate Task<string?> Reader(string executable, TimeSpan limit, CancellationToken ct);

    /// <summary>
    /// 안내 글 한 줄(끝에 빈 줄). 화면의 마지막 AI 글 줄에 이어 붙는 조각이므로 뒤따르는 응답과 줄이 갈리게 빈 줄로 끝낸다.
    /// </summary>
    internal static string Message(string displayName)
        => $"[안내] 이 버전의 {displayName} 는 Oranda 에서 잠금을 검증하지 않았습니다.\n\n";

    /// <summary>
    /// 검증한 버전이 아니거나 버전을 읽지 못하면 안내 글을, 검증한 버전이면 null 을 돌려준다. 호출 측 취소만 예외로 낸다.
    /// </summary>
    internal static async Task<string?> UnverifiedNoticeAsync(
        Reader read, string executable, IReadOnlyCollection<string> verifiedVersions, string displayName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(read);
        string? output;
        try
        {
            output = await read(executable, ReadTimeLimit, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is IOException or InvalidOperationException or Win32Exception or UnauthorizedAccessException)
        {
            output = null;
        }

        return Parse(output) is { } version && verifiedVersions.Contains(version) ? null : Message(displayName);
    }

    /// <summary>출력에서 첫 번째 버전 모양(숫자와 점 둘 이상 이어진 것)을 꺼낸다. 없으면 null.</summary>
    internal static string? Parse(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        foreach (var word in output.Split([' ', '\t', '\r', '\n', 'v', ','], StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = word.Split('.');
            if (parts.Length >= 2 && parts.All(p => p.Length > 0 && p.All(char.IsAsciiDigit)))
            {
                return word;
            }
        }

        return null;
    }

    /// <summary>
    /// 실제 프로세스로 <c>실행파일 --version</c> 을 읽는다. 실행 토큰 변수는 부모 환경에서 물려받았더라도 지운다.
    /// <paramref name="environment"/> 로 환경 변수를 더할 수 있다(예: 자동 업데이트 끄기). 토큰 변수는 항상 지운다.
    /// 시간 상한을 넘으면 프로세스 트리를 끝내고 null. 호출 측 취소는 예외로 낸다.
    /// </summary>
    internal static Task<string?> ReadAsync(string executable, TimeSpan limit, CancellationToken ct)
        => ReadAsync(executable, limit, environment: null, ct);

    /// <summary>
    /// <see cref="ReadAsync(string, TimeSpan, CancellationToken)"/> 에 환경 변수를 더한 형태. <see cref="Reader"/> 로 바로 바꿀 수 있게 3인자 형태를 따로 둔다.
    /// </summary>
    internal static async Task<string?> ReadAsync(string executable, TimeSpan limit, IReadOnlyDictionary<string, string?>? environment, CancellationToken ct)
    {
        var launch = new ClaudeLaunch(
            executable,
            ["--version"],
            Path.GetTempPath(),
            string.Empty,
            new Dictionary<string, string?>(environment ?? new Dictionary<string, string?>()) { [Oranda.Core.Ai.Protocol.AgentRunToken.EnvironmentVariable] = null });
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(limit);
        Process? process = null;
        try
        {
            process = Process.Start(SystemClaudeProcessLauncher.CreateStartInfo(launch));
            if (process is null)
            {
                return null;
            }

            process.StandardInput.Close();
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            var output = await process.StandardOutput.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            _ = await stderr.ConfigureAwait(false);
            return output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null; // 시간 상한
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
        {
            return null;
        }
        finally
        {
            if (process is not null)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException or AggregateException)
                {
                    // 이미 끝났거나 끝나는 중이다.
                }

                process.Dispose();
            }
        }
    }
}

/// <summary>
/// 한 실행의 도구 사용 집계(시스템 CLI 드라이버 공용). 드라이버가 화면에 내는 도구 사건(<see cref="AiToolCall"/>·<see cref="AiToolResult"/>)을 세어,
/// 모델이 도구가 모두 실패했는데도 그럴듯한 결과를 지어내 정상 종료하는 경우(실제 왕복에서 관찰)를 드라이버가 알게 한다. 판정표:
/// <list type="table">
/// <listheader><term>성공 · 실행 뒤 실패 · 게이트 거부</term><description>처리</description></listheader>
/// <item><term>1건 이상 · 무관 · 무관</term><description>통과</description></item>
/// <item><term>0 · 1건 이상 · 무관</term><description><c>all_tools_failed</c> 로 닫는다(거부와 섞여도). 결과는 내보내지 않는다.</description></item>
/// <item><term>0 · 0 · 1건 이상</term><description>닫지 않고 결과 앞에 「모든 도구 호출이 거부되어 실측 근거가 없습니다」 한 줄(새 대화 첫 차례만).</description></item>
/// <item><term>0 · 0 · 0</term><description>구조 결과를 쓰는 새 대화 첫 차례에만 「도구를 쓰지 않고 답했으므로 실측 근거가 없습니다」 한 줄.</description></item>
/// </list>
/// 거부는 게이트가 거부로 판단한 호출(<see cref="AiToolJudgement.Denied"/>)만이다. 게이트 판단이 없는 오류 단계와 결과가 오지 않은 호출은 실행 뒤 실패로 센다.
/// 안내 줄은 새 대화 첫 차례에만 낸다: 이어 말하기 차례의 AI 글은 튜닝 레포트의 답변으로 모이므로 섞이면 안 되고, 거기서는 안내 없이 통과시킨다(한계).
/// </summary>
internal sealed class SystemCliToolUsage
{
    private int _calls;
    private int _succeeded;
    private int _denied;

    /// <summary>도구 호출 수.</summary>
    public int Calls => _calls;

    /// <summary>성공(허용·가려서 돌려줌)한 호출 수.</summary>
    public int Succeeded => _succeeded;

    /// <summary>게이트가 거부한 호출 수.</summary>
    public int Denied => _denied;

    /// <summary>실행 뒤 실패(게이트 판단이 실패이거나 없는 호출·결과가 오지 않은 호출) 수.</summary>
    public int Failed => Math.Max(0, _calls - _succeeded - _denied);

    /// <summary>도구 사건 하나를 센다. 다른 사건은 무시한다.</summary>
    public void Observe(AiEvent aiEvent)
    {
        switch (aiEvent)
        {
            case AiToolCall:
                _calls++;
                break;

            case AiToolResult result when result.Judgement is AiToolJudgement.Allowed or AiToolJudgement.Masked:
                _succeeded++;
                break;

            case AiToolResult { Judgement: AiToolJudgement.Denied }:
                _denied++;
                break;
        }
    }

    /// <summary>성공 없이 실행 뒤 실패가 1건 이상이면 실행을 닫는 오류(<c>all_tools_failed</c>), 아니면 null. 거부가 섞여도 닫는다.</summary>
    public AiError? AllFailedError(string displayName)
    {
        if (_calls == 0 || _succeeded > 0 || Failed == 0)
        {
            return null;
        }

        var reason = _denied == 0 ? "모두 실패" : "모두 실패하거나 거부";
        return new AiError(
            $"{displayName} 가 부른 도구 호출 {_calls}건이 {reason}되어(실패 {Failed}건, 거부 {_denied}건) 근거 없는 결과를 내보내지 않았습니다. 앱의 AI 연결 상태와 접속의 AI 허용 수준을 확인하세요.",
            "all_tools_failed");
    }

    /// <summary>
    /// 닫지 않고 결과 앞에 낼 안내 글(끝에 빈 줄), 없으면 null. 새 대화 첫 차례에만 낸다.
    /// 모든 호출이 거부되었으면 스키마 여부와 상관없이, 호출이 하나도 없으면 구조 결과를 쓰는 실행에서만 낸다.
    /// </summary>
    public string? Notice(bool structuredResult, bool firstTurn)
    {
        if (!firstTurn)
        {
            return null;
        }

        if (_calls > 0 && _succeeded == 0 && Failed == 0 && _denied > 0)
        {
            return "[안내] 모든 도구 호출이 거부되어 실측 근거가 없습니다.\n\n";
        }

        return _calls == 0 && structuredResult ? "[안내] AI 가 도구를 쓰지 않고 답했으므로 실측 근거가 없습니다.\n\n" : null;
    }
}
