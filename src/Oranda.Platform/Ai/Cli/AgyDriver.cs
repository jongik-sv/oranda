using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Ai.Profiles;
using Oranda.Core.Ai.Protocol;
using Oranda.Core.Composition;
using Oranda.Core.Storage;
using Oranda.Platform.Ai.Bridge;

namespace Oranda.Platform.Ai.Cli;

/// <summary>
/// Antigravity CLI(<c>agy</c>) 드라이버. 실행마다 전용 폴더에 잠근 사용자 정의 에이전트 파일(<see cref="AgyAgentFile"/>)을 쓰고
/// <c>agy</c> 를 그 폴더에서 stream-json 입출력으로 띄운다. 도구는 에이전트 파일이 내장 도구를 없애고, 남은 MCP 서버(orandactl)만
/// 게이트(AgentGate)로 이어진다. 스트림 감시는 허용 목록 밖 도구·모르는 단계를 보면 곧바로 프로세스를 끝낸다.
/// 모델·노력은 넘기지 않는다(agy 기본값). 설정의 Claude 실행 파일 경로(<c>AiSettings.ExecutablePath</c>)도 쓰지 않는다.
/// </summary>
[OrandaService(typeof(ISystemCliAiDriver))]
internal sealed class AgyDriver : ISystemCliAiDriver
{
    /// <summary>표준 입력 프롬프트 한도(UTF-8 바이트). agy 는 약 192,000 을 넘으면 경고 없이 잘라 버리므로 여유를 두고 그 전에 닫는다.</summary>
    internal const int MaxPromptBytes = 180_000;

    /// <summary>
    /// 한 차례 실행의 호출 측 시간 제한: 6시간. 호출 1건의 시간 제한(<c>timeoutSeconds</c>, 6시간)과 같은 값이다. 벤치마크 제한이 0(무제한)이어도 이 제한은 적용된다.
    /// 이 제한은 안전망이다: 멈춘 CLI 는 사용자 중지로 끝내는 것이 정상 경로이고(사용자 중지는 이 제한보다 먼저 같은 경로로 프로세스를 끝낸다),
    /// agy 자체의 <c>--print-timeout</c> 은 기본 0(차례가 끝날 때까지 기다림)이라 넘기지 않는다.
    /// 오래된 실행 폴더 정리 기준(<see cref="SystemCliRunner.StaleRunAge"/>)은 이 값보다 커야 한다: 긴 실행의 폴더를 새 실행이 지우면 안 된다.
    /// </summary>
    internal static readonly TimeSpan RunTimeLimit = TimeSpan.FromHours(6);

    /// <summary>Oranda 가 잠금을 검증한 agy 버전. 다른 버전이면 새 대화 첫 차례에 안내만 한다(막지 않는다). agy 를 올리면 ORANDA_AI_E2E_AGY=1 시험을 다시 돌리고 이 목록을 고친다.</summary>
    internal static readonly IReadOnlyCollection<string> VerifiedVersions = ["1.4.12"];

    /// <summary>
    /// agy 의 자동 업데이트 끄기. agy 1.4.12 에서 <c>true</c> 일 때만 백그라운드 갱신기(<c>agy --bg-updater</c>)가 뜨지 않음을 측정했다
    /// (<c>1</c>·<c>0</c> 은 효과가 없었다). 끄면 실행 중 버전이 바뀌는 경로와 갱신기의 토큰 환경 상속이 함께 사라진다.
    /// </summary>
    internal const string AutoUpdateVariable = "AGY_CLI_DISABLE_AUTO_UPDATE";

    private const string CliName = "agy";
    private const string CliDisplayName = "Antigravity CLI";

    private static readonly string[] GeminiRoots = ["config", "antigravity", "antigravity-cli"];
    private const int MaxArgumentSummaryChars = 4000;

    private readonly SystemCliRunner _runner;
    private readonly IAgentGate _gate;
    private readonly IClaudeProcessLauncher _launcher;
    private readonly Func<string?> _pathVariable;
    private readonly Func<string, bool> _fileExists;
    private readonly string? _home;
    private readonly TimeSpan _timeLimit;
    private readonly SystemCliVersion.Reader _readVersion;

    public AgyDriver(IAgentRunTokens tokens, IAgentGate gate, IAgentBridgeEndpoint bridge, IAppDataPaths paths)
        : this(
            tokens,
            gate,
            bridge,
            paths,
            new SystemClaudeProcessLauncher(),
            CliDriver.OrandactlPath(AppContext.BaseDirectory, File.Exists),
            () => Environment.GetEnvironmentVariable("PATH"),
            File.Exists,
            home: null,
            RunTimeLimit,
            (executable, limit, ct) => SystemCliVersion.ReadAsync(executable, limit, LaunchEnvironment(null), ct))
    {
    }

    /// <summary>시험용: 프로세스 실행·경로 찾기·홈 폴더·시간 제한을 바꿔 넣는다.</summary>
    internal AgyDriver(
        IAgentRunTokens tokens,
        IAgentGate gate,
        IAgentBridgeEndpoint bridge,
        IAppDataPaths paths,
        IClaudeProcessLauncher launcher,
        string orandactlPath,
        Func<string?> pathVariable,
        Func<string, bool> fileExists,
        string? home,
        TimeSpan timeLimit,
        SystemCliVersion.Reader readVersion)
    {
        _runner = new SystemCliRunner(tokens, gate, bridge, paths, orandactlPath, fileExists);
        _gate = gate;
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _pathVariable = pathVariable;
        _fileExists = fileExists;
        _home = home;
        _timeLimit = timeLimit;
        _readVersion = readVersion ?? throw new ArgumentNullException(nameof(readVersion));
    }

    /// <summary>
    /// agy 프로세스 환경 덮어쓰기. 실행 토큰(환경 변수로만 넘긴다)과 자동 업데이트 끄기. 토큰이 없으면(버전 읽기 등) 부모에게서 물려받은 토큰 변수를 지운다.
    /// </summary>
    internal static Dictionary<string, string?> LaunchEnvironment(string? token)
        => new() { [AgentRunToken.EnvironmentVariable] = token, [AutoUpdateVariable] = "true" };

    /// <summary>MCP 도구 호출 하나의 제한(초). 시험이 짧게 줄여 CLI 가 호출을 포기하는 경로를 본다.</summary>
    internal int ToolTimeoutSeconds { get; init; } = AgyAgentFile.DefaultToolTimeoutSeconds;

    public string CliId => AiProfilePresets.AntigravityCliId;

    public async IAsyncEnumerable<AiEvent> RunAsync(AiRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 라우터가 비운 값이지만 직접 호출에서도 새어 나가지 않게 막는다.
        if (request.Model is not null || request.Effort is not null)
        {
            yield return new AiError("이 CLI 는 모델·노력을 지정하지 않습니다. 기본 모델로 실행됩니다.", "cli_option_unsupported");
            yield break;
        }

        if (request.Images is { Count: > 0 })
        {
            yield return new AiError("Antigravity CLI 는 이미지를 보낼 수 없습니다.", "image_unsupported");
            yield break;
        }

        if (string.IsNullOrWhiteSpace(request.Prompt))
        {
            yield return new AiError("보낼 내용이 없습니다.", "prompt_empty");
            yield break;
        }

        if (Encoding.UTF8.GetByteCount(request.Prompt) > MaxPromptBytes)
        {
            yield return new AiError($"요청이 너무 깁니다(최대 약 {MaxPromptBytes / 1000}KB). agy 는 이보다 긴 입력을 조용히 잘라 버려 보내지 않았습니다.", "input_too_large");
            yield break;
        }

        var (preconditionError, pipeName) = _runner.CheckPreconditions(request);
        if (preconditionError is not null)
        {
            yield return preconditionError;
            yield break;
        }

        // 사용자 전역 MCP 설정에 서버가 있으면 잠금 상태에서도 그 프로세스가 떠서 실행 토큰(환경 변수)을 물려받는다. 띄우기 전에 닫는다.
        if (GlobalMcpConfigWithServers() is { } mcpConfigPath)
        {
            yield return new AiError(
                $"agy 의 전역 MCP 설정에 서버가 있어 실행하지 않았습니다. 잠금 상태에서도 그 서버 프로세스가 시작되어 이 실행의 토큰을 물려받을 수 있습니다. 설정 파일의 서버를 비운 뒤 다시 시도하세요: {mcpConfigPath}",
                "mcp_config_present");
            yield break;
        }

        // 사이드카: 사용자가 대시보드에서 켠 백그라운드 프로세스를 agy(언어 서버)가 띄울 수 있다. 실제 기동은 측정하지 못했다(대조군도 뜨지 않음).
        // 그래서 켜짐 여부와 상관없이 항목이 있으면 닫는다. 읽지 못하거나 형식을 모르면 닫는다.
        if (GlobalSidecarConfig() is { } sidecarPath)
        {
            yield return new AiError(
                $"agy 의 사이드카(백그라운드 자동화) 설정이 있어 실행하지 않았습니다. 사용자가 켠 사이드카는 잠금 상태에서도 시작되어 이 실행의 토큰을 물려받을 수 있는데 Oranda 는 이를 막을 수 없습니다. 사이드카 폴더나 설정을 비우거나 옮긴 뒤 다시 시도하세요: {sidecarPath}",
                "sidecar_config_present");
            yield break;
        }

        var (executable, locateError) = SystemCliExecutable.Locate("agy", CliName, _pathVariable(), _fileExists, _home);
        if (locateError is not null)
        {
            yield return locateError;
            yield break;
        }

        // 새 대화의 첫 차례에만 검증하지 않은 버전을 알린다(이어 말하기 차례의 AI 글은 튜닝 레포트의 답변으로 모이므로 섞이면 안 된다).
        // 버전 읽기는 토큰 발급 전에 하고 토큰을 주지 않는다. 먼저 닫히는 경우(위 거부들)에는 읽지도 않는다.
        var versionNotice = string.IsNullOrEmpty(request.ResumeId)
            ? await SystemCliVersion.UnverifiedNoticeAsync(_readVersion, executable!, VerifiedVersions, CliDisplayName, ct).ConfigureAwait(false)
            : null;

        var session = request.Session!;
        var schema = request.OutputSchema is { } element ? JsonSerializer.Serialize(element) : null;
        var resumeId = string.IsNullOrEmpty(request.ResumeId) ? null : request.ResumeId;

        var run = _runner.Begin(session, pipeName!, ct, _timeLimit);
        try
        {
            var (_, prepareError) = SystemCliRun.Prepare(() =>
            {
                AgyAgentFile.Write(run.RunDirectory, run.OrandactlPath, run.PipeName, withFinishTool: schema is not null, _gate.ListTools(session), ToolTimeoutSeconds);
                return 0;
            });
            if (prepareError is not null)
            {
                yield return prepareError;
                yield break;
            }

            var launch = new ClaudeLaunch(
                executable!,
                AgyCliArguments.Build(new AgyInvocation(resumeId, schema)),
                run.RunDirectory,
                AgyCliArguments.BuildStandardInput(request.Prompt),
                // 토큰은 명령줄 인자·에이전트 파일에 넣지 않고 프로세스 환경 변수로만 넘긴다(MCP 서버가 상속). 자동 업데이트는 끈다.
                LaunchEnvironment(run.Token),
                Secret: run.Token); // 표준 오류 끝부분에 토큰이 나와도 가린다(Codex 와 같다)

            var (process, startError) = run.Start(_launcher, launch, CliName);
            if (startError is not null)
            {
                yield return startError;
                yield break;
            }

            var steps = new Dictionary<int, AgyToolStep>();
            var usage = new SystemCliToolUsage();
            string? conversationId = null;
            var finished = false;
            var lines = process!.ReadLinesAsync(run.CancellationToken).GetAsyncEnumerator(run.CancellationToken);
            try
            {
                while (true)
                {
                    var (hasLine, line, timedOut) = await NextLineAsync(lines, run).ConfigureAwait(false);
                    if (timedOut)
                    {
                        yield return SystemCliRun.TimeoutError(CliName, _timeLimit);
                        yield break;
                    }

                    if (!hasLine)
                    {
                        break;
                    }

                    foreach (var streamEvent in AgyStreamParser.Parse(line!))
                    {
                        foreach (var aiEvent in Handle(streamEvent, run, resumeId, schema is not null, steps, usage, ref conversationId, ref finished))
                        {
                            usage.Observe(aiEvent);
                            yield return aiEvent;
                            if (aiEvent is AiError)
                            {
                                yield break;
                            }

                            if (aiEvent is AiStarted && versionNotice is not null)
                            {
                                yield return new AiTextDelta(versionNotice);
                            }
                        }
                    }
                }
            }
            finally
            {
                await lines.DisposeAsync().ConfigureAwait(false);
            }

            var (exitCode, exitTimedOut) = await WaitForExitAsync(process, run).ConfigureAwait(false);
            if (exitTimedOut)
            {
                yield return SystemCliRun.TimeoutError(CliName, _timeLimit);
                yield break;
            }

            if (!finished)
            {
                yield return FailedWithoutResult(process.StandardErrorTail, exitCode);
            }
        }
        finally
        {
            await run.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 스트림 사건 하나를 화면 사건으로 옮긴다. 감시에 걸리면(허용 밖 도구·모르는 단계) 프로세스를 끝내고 오류 하나만 낸다.
    /// <paramref name="finished"/> 는 결과(<c>result</c>)를 받았다는 표시다.
    /// </summary>
    private static List<AiEvent> Handle(
        AgyStreamEvent streamEvent,
        SystemCliRun run,
        string? resumeId,
        bool schemaRun,
        Dictionary<int, AgyToolStep> steps,
        SystemCliToolUsage usage,
        ref string? conversationId,
        ref bool finished)
    {
        var events = new List<AiEvent>();
        switch (streamEvent)
        {
            case AgySessionStarted started:
                conversationId = started.ConversationId;
                if (resumeId is not null && !string.Equals(resumeId, started.ConversationId, StringComparison.Ordinal))
                {
                    // 없는 대화 id 를 주면 agy 는 rc=0 으로 새 대화를 시작한다. 이어 말하기가 아니므로 닫는다.
                    return Fail(run, "이어 말하려던 대화를 찾지 못해 새 대화가 시작되어 중단했습니다.", "conversation_mismatch");
                }

                events.Add(new AiStarted(started.ConversationId));
                break;

            case AgyUnknownStep unknown:
                return Reject(run, $"단계 {unknown.StepType}");

            case AgyText text:
                if (conversationId is null)
                {
                    return Fail(run, "agy 가 시작 정보를 보내기 전에 응답을 냈습니다.", "init_missing");
                }

                events.Add(new AiTextDelta(text.Text));
                break;

            case AgyFinishStep:
                if (conversationId is null)
                {
                    return Fail(run, "agy 가 시작 정보를 보내기 전에 도구를 불렀습니다.", "init_missing");
                }

                // 구조 결과를 내는 마지막 단계. 스키마가 없는 실행에는 finish 도구가 없으므로 나오면 닫는다.
                return schemaRun ? events : Reject(run, "단계 finish");

            case AgyToolStep step:
                if (conversationId is null)
                {
                    return Fail(run, "agy 가 시작 정보를 보내기 전에 도구를 불렀습니다.", "init_missing");
                }

                if (run.RejectUnexpectedTool(DisplayName(step), tool => IsAllowedTool(step, schemaRun)) is { } rejected)
                {
                    return [rejected];
                }

                if (step.ToolName == "finish")
                {
                    break; // 구조 결과를 내는 내장 도구. 카드로 보이지 않는다.
                }

                events.AddRange(ToolEvents(step, run, steps));
                break;

            case AgyResult result:
                if (conversationId is null)
                {
                    return Fail(run, "agy 가 시작 정보를 보내기 전에 결과를 냈습니다.", "init_missing");
                }

                finished = true;
                var closing = Finish(result, conversationId, resumeId);
                if (closing.Any(e => e is AiFinal))
                {
                    // 도구를 불렀는데 성공 없이 실패가 있으면 결과를 내보내지 않는다. 모두 거부됐거나 한 번도 안 불렀으면 결과 앞에 근거 없음을 알린다(새 대화 첫 차례만).
                    if (usage.AllFailedError(CliDisplayName) is { } allFailed)
                    {
                        return [allFailed];
                    }

                    if (usage.Notice(schemaRun, string.IsNullOrEmpty(resumeId)) is { } noTools)
                    {
                        closing.Insert(0, new AiTextDelta(noTools));
                    }
                }

                return closing;
        }

        return events;
    }

    private static List<AiEvent> Finish(AgyResult result, string initConversationId, string? resumeId)
    {
        if (result.FinishedReason is { } reason)
        {
            // print_timeout 은 rc=0·status SUCCESS 로 온다. 끝난 이유가 있으면 정상 완료가 아니다.
            return reason == "print_timeout"
                ? [new AiError("agy 의 출력 제한 시간이 지나 중단되었습니다.", "cli_timeout")]
                : [new AiError($"agy 가 정상으로 끝나지 않았습니다({reason}){Detail(result.Error)}", "cli_failed")];
        }

        if (!string.Equals(result.Status, "SUCCESS", StringComparison.Ordinal))
        {
            return [new AiError(string.IsNullOrWhiteSpace(result.Error) ? "agy 실행 오류" : result.Error, "cli_error")];
        }

        if (result.ConversationId is { Length: > 0 } id && !string.Equals(id, initConversationId, StringComparison.Ordinal))
        {
            return [new AiError("agy 응답의 대화 id 가 시작 때와 달라 중단했습니다.", "conversation_mismatch")];
        }

        if (resumeId is not null && !string.Equals(initConversationId, resumeId, StringComparison.Ordinal))
        {
            return [new AiError("이어 말하려던 대화가 아닌 대화의 결과여서 중단했습니다.", "conversation_mismatch")];
        }

        JsonElement? structured = null;
        if (result.StructuredOutputJson is { } json)
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                structured = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                return [new AiError("agy 의 구조 결과를 읽지 못했습니다.", "cli_error")];
            }
        }

        return
        [
            new AiUsage(result.InputTokens, result.OutputTokens),
            new AiFinal(structured is null ? result.Response : null, structured, initConversationId),
        ];
    }

    private static List<AiEvent> ToolEvents(AgyToolStep step, SystemCliRun run, Dictionary<int, AgyToolStep> steps)
    {
        var callId = step.StepIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
        switch (step.State)
        {
            case "ACTIVE":
                steps[step.StepIndex] = step;
                return [new AiToolCall(callId, step.Tool, Summarize(step.ArgumentsJson))];

            case "DONE" or "ERROR":
                var name = steps.TryGetValue(step.StepIndex, out var started) ? started.Tool : step.Tool;
                steps.Remove(step.StepIndex);
                if (run.Judgements.TryTake(name, out var completed))
                {
                    return [new AiToolResult(callId, name, completed.Size, completed.Judgement, completed.Note)];
                }

                // 게이트를 거치지 않았다(권한 거부·연결 실패·브리지의 busy·내부 오류). orandactl 호출은 모두 게이트를 지나므로 판단 기록이 없는 호출은
                // agy 가 DONE 으로 보내도 성공이 아니다. 상태와 상관없이 실패로 알리고 센다(성공은 게이트 판단이 있을 때만).
                return [new AiToolResult(callId, name, step.Output?.Length ?? 0, AiToolJudgement.Failed)];

            default:
                return [];
        }
    }

    /// <summary>허용: Oranda MCP 서버(orandactl)의 도구 호출, 그리고 구조 결과 실행의 <c>finish</c>. 그 밖은 모두 닫는다.</summary>
    private static bool IsAllowedTool(AgyToolStep step, bool schemaRun)
        => step.ToolName switch
        {
            "call_mcp_tool" => string.Equals(step.Server, ClaudeCliArguments.McpServerName, StringComparison.Ordinal),
            "finish" => schemaRun,
            _ => false,
        };

    private static string DisplayName(AgyToolStep step)
        => step.ToolName == "call_mcp_tool" ? $"call_mcp_tool({step.Server}/{step.Tool})" : step.ToolName;

    private static List<AiEvent> Reject(SystemCliRun run, string what)
    {
        var error = run.RejectUnexpectedTool(what, _ => false);
        return [error!];
    }

    private static List<AiEvent> Fail(SystemCliRun run, string message, string code)
    {
        try
        {
            run.Process?.Kill();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException or AggregateException)
        {
            // 정리 단계가 다시 끝낸다.
        }

        return [new AiError(message, code)];
    }

    private static AiError FailedWithoutResult(string stderrTail, int exitCode)
    {
        var detail = stderrTail.Trim();
        if (exitCode == 3 && AgyError(detail) is { } agyError)
        {
            return new AiError($"agy 오류: {agyError}", "cli_error");
        }

        return new AiError(
            detail.Length == 0 ? $"agy 가 결과 없이 끝났습니다(종료 코드 {exitCode})." : $"agy 가 결과 없이 끝났습니다(종료 코드 {exitCode}): {detail}",
            "cli_failed");
    }

    /// <summary>rc=3 의 표준 오류 <c>AGY_ERROR: {json}</c> 에서 짧은 오류 글을 꺼낸다.</summary>
    internal static string? AgyError(string stderr)
    {
        const string Mark = "AGY_ERROR:";
        var index = stderr.IndexOf(Mark, StringComparison.Ordinal);
        if (index < 0)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(stderr[(index + Mark.Length)..].Trim());
            return document.RootElement.TryGetProperty("short_error", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Detail(string? error) => string.IsNullOrWhiteSpace(error) ? "." : ": " + error;

    private static string Summarize(string inputJson)
        => inputJson.Length <= MaxArgumentSummaryChars ? inputJson : inputJson[..MaxArgumentSummaryChars] + "…";

    /// <summary>다음 줄을 읽는다. 시간 제한으로 끊기면 timedOut, 호출 측 취소면 예외를 그대로 낸다.</summary>
    private static async Task<(bool HasLine, string? Line, bool TimedOut)> NextLineAsync(IAsyncEnumerator<string> lines, SystemCliRun run)
    {
        try
        {
            return await lines.MoveNextAsync().ConfigureAwait(false) ? (true, lines.Current, false) : (false, null, false);
        }
        catch (OperationCanceledException) when (run.TimedOut)
        {
            return (false, null, true);
        }
    }

    private static async Task<(int ExitCode, bool TimedOut)> WaitForExitAsync(IClaudeProcess process, SystemCliRun run)
    {
        try
        {
            return (await process.WaitForExitAsync(run.CancellationToken).ConfigureAwait(false), false);
        }
        catch (OperationCanceledException) when (run.TimedOut)
        {
            return (0, true);
        }
    }

    /// <summary>
    /// 사용자의 agy 전역 설정에 MCP 서버가 하나라도 있으면 그 파일 경로를 돌려준다(없으면 null).
    /// 잠금 상태(<c>excludeDefaultComponents</c>)에서도 서버 프로세스가 뜨는 것을 agy 1.4.12 로 측정한 위치는
    /// <c>~/.gemini/config/mcp_config.json</c>, <c>~/.gemini/config/plugins/&lt;이름&gt;/mcp_config.json</c>,
    /// <c>~/.gemini/antigravity-cli/plugins/&lt;이름&gt;/mcp_config.json</c> 이다(AgyEndToEndTests 가 업데이트 때 다시 측정한다).
    /// 측정에서 뜨지 않은 옛 위치(<c>antigravity</c>·<c>antigravity-cli</c> 의 <c>mcp_config.json</c>)와 플러그인 등록 파일(<c>plugins.json</c>)도 막는 쪽으로 함께 본다.
    /// 읽을 수 없거나 형식을 알 수 없는 비어 있지 않은 파일은 서버가 있는 것으로 본다(닫는 쪽).
    /// </summary>
    internal string? GlobalMcpConfigWithServers()
    {
        var home = _home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
        {
            return null;
        }

        foreach (var root in GeminiRoots.Select(name => Path.Combine(home, ".gemini", name)))
        {
            if (HasServers(Path.Combine(root, "mcp_config.json")))
            {
                return Path.Combine(root, "mcp_config.json");
            }

            if (HasRegistrations(Path.Combine(root, "plugins.json")))
            {
                return Path.Combine(root, "plugins.json");
            }

            var pluginsRoot = Path.Combine(root, "plugins");
            if (PluginDirectories(pluginsRoot) is not { } plugins)
            {
                return pluginsRoot; // 읽지 못하면 플러그인에 서버가 없다고 확인할 수 없다.
            }

            foreach (var plugin in plugins)
            {
                if (HasServers(Path.Combine(plugin, "mcp_config.json")))
                {
                    return Path.Combine(plugin, "mcp_config.json");
                }
            }
        }

        return null;
    }

    private static string[]? PluginDirectories(string pluginsRoot)
    {
        try
        {
            return Directory.Exists(pluginsRoot) ? Directory.GetDirectories(pluginsRoot) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>플러그인 등록 파일(<c>plugins.json</c>)에 등록이 하나라도 있는지(값이 모두 비어 있으면 없음).</summary>
    private static bool HasRegistrations(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            var text = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            using var document = JsonDocument.Parse(text);
            return !IsEmptyValue(document.RootElement);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return true;
        }
    }

    private static bool IsEmptyValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => value.EnumerateObject().All(p => IsEmptyValue(p.Value)),
        JsonValueKind.Array => value.EnumerateArray().All(IsEmptyValue),
        JsonValueKind.String => string.IsNullOrEmpty(value.GetString()),
        JsonValueKind.Null or JsonValueKind.Undefined => true,
        _ => false,
    };

    /// <summary>
    /// 사용자의 agy 전역 설정에 사이드카가 하나라도 있으면 그 폴더·파일 경로를 돌려준다(없으면 null).
    /// 세 루트(config·antigravity·antigravity-cli) 각각에서 <c>sidecars/</c> 폴더의 항목, 비어 있지 않은 <c>sidecars.json</c>,
    /// <c>plugins/&lt;이름&gt;/sidecars/</c> 의 항목, 비어 있지 않은 <c>plugins/&lt;이름&gt;/sidecars.json</c> 을 본다.
    /// 사이드카의 실제 기동은 측정하지 못했다(사용자가 대시보드에서 켜야 하고 대조군도 뜨지 않았다). 그래서 켜짐 여부는 보지 않고 항목이 있으면 닫으며,
    /// 읽지 못하거나 형식을 모르는 경우도 닫는다.
    /// </summary>
    internal string? GlobalSidecarConfig()
    {
        var home = _home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
        {
            return null;
        }

        foreach (var root in GeminiRoots.Select(name => Path.Combine(home, ".gemini", name)))
        {
            if (HasEntries(Path.Combine(root, "sidecars")) is { } folder)
            {
                return folder;
            }

            if (HasRegistrations(Path.Combine(root, "sidecars.json")))
            {
                return Path.Combine(root, "sidecars.json");
            }

            var pluginsRoot = Path.Combine(root, "plugins");
            if (PluginDirectories(pluginsRoot) is not { } plugins)
            {
                return pluginsRoot;
            }

            foreach (var plugin in plugins)
            {
                if (HasEntries(Path.Combine(plugin, "sidecars")) is { } pluginFolder)
                {
                    return pluginFolder;
                }

                if (HasRegistrations(Path.Combine(plugin, "sidecars.json")))
                {
                    return Path.Combine(plugin, "sidecars.json");
                }
            }
        }

        return null;
    }

    /// <summary>폴더에 항목(파일·하위 폴더)이 하나라도 있으면 그 경로를 돌려준다. 폴더가 없거나 비어 있으면 null. 읽지 못하면 폴더 경로(닫는 쪽).</summary>
    private static string? HasEntries(string folder)
    {
        try
        {
            return Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any() ? folder : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return folder;
        }
    }

    private static bool HasServers(string path)
    {
        string text;
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true; // 읽지 못하면 비어 있다고 확인할 수 없다.
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return true;
            }

            // 비어 있는 서버 목록({"mcpServers":{}})만 있으면 서버가 없다. 그 밖의 항목이 하나라도 있으면 서버가 있는 것으로 본다.
            foreach (var property in root.EnumerateObject())
            {
                if (property.Name == "mcpServers" && property.Value.ValueKind == JsonValueKind.Object && !property.Value.EnumerateObject().Any())
                {
                    continue;
                }

                return true;
            }

            return false;
        }
        catch (JsonException)
        {
            return true;
        }
    }
}
