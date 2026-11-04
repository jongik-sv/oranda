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
/// opencode(<c>opencode run --format json</c>)로 AI 요청 한 번을 실행하는 시스템 CLI 드라이버. 도구 호출은
/// opencode → 플러그인(<see cref="OpencodePluginFile"/>) → 브리지 파이프 → AgentGate 로만 지난다(orandactl·MCP 는 쓰지 않는다: MCP 는 외부 HTTP 가 열린 <c>execute</c> 를 거친다).
/// 잠금은 <see cref="OpencodeArguments"/> 의 설정(전용 에이전트의 permissions)이 맡고, 스트림 감시가 마지막 장치다:
/// <c>part.tool</c> 이 허용(앱 도구 + 구조 결과 도구) 밖이면 곧바로 끝낸다. 단 opencode 는 도구가 끝난 뒤에만 <c>tool_use</c> 를 내므로 감시는 실행 뒤에 걸린다 —
/// 그래서 검증하지 않은 opencode 버전은 실행하지 않는다(<see cref="VerifiedVersions"/>).
/// 플러그인이 로드되지 않았거나 도구 목록이 기대와 다르면 조용히 진행하지 않고 닫는다.
/// <para>
/// 대화는 opencode 의 사용자 전역 저장소(<c>~/.local/share/opencode/opencode.db</c>)에 남는다(이어 말하기에 필요). 모델·노력·이미지는 넘기지 않는다.
/// </para>
/// </summary>
[OrandaService(typeof(ISystemCliAiDriver))]
internal sealed class OpencodeDriver : ISystemCliAiDriver
{
    /// <summary>한 차례 실행의 시간 제한: 6시간(Codex·agy 와 같다). 멈춘 CLI 는 사용자 중지로 끝내는 것이 일반 경로이고 이 제한은 안전망이다.</summary>
    internal static readonly TimeSpan RunTimeLimit = TimeSpan.FromHours(6);

    /// <summary>
    /// 잠금을 검증한 opencode 버전(<c>opencode --version</c> 의 숫자 부분). 다른 버전은 실행하지 않는다:
    /// 설정 키가 무시되면 도구가 되살아나는데 스트림 감시는 도구가 끝난 뒤에야 걸려, 열린 <c>execute</c> 의 <c>fetch</c> 한 번이 먼저 나갈 수 있다.
    /// opencode 를 올리면 ORANDA_AI_E2E_OPENCODE_REAL=1 시험을 다시 돌리고 이 목록을 고친다.
    /// </summary>
    internal static readonly IReadOnlyCollection<string> VerifiedVersions = ["2.0.32"];

    private const string CliName = "opencode";
    private const string CliDisplayName = "opencode";
    private const int MaxArgumentSummaryChars = 4000;

    /// <summary>도구를 부른 중간 단계의 끝 이유. 이 이유로 끝난 단계가 마지막이면 실행이 도중에 끊긴 것이다.</summary>
    private const string ToolCallsReason = "tool-calls";

    private readonly SystemCliRunner _runner;
    private readonly IAgentGate _gate;
    private readonly IAgentBridgeEndpoint _bridge;
    private readonly IClaudeProcessLauncher _launcher;
    private readonly Func<string?> _pathVariable;
    private readonly Func<string, bool> _fileExists;
    private readonly Func<string, string?> _readText;
    private readonly Func<string, string?> _environment;
    private readonly string _home;
    private readonly TimeSpan _timeLimit;
    private readonly SystemCliVersion.Reader _readVersion;

    public OpencodeDriver(IAgentRunTokens tokens, IAgentGate gate, IAgentBridgeEndpoint bridge, IAppDataPaths paths)
        : this(
            tokens,
            gate,
            bridge,
            paths,
            new SystemClaudeProcessLauncher(),
            () => Environment.GetEnvironmentVariable("PATH"),
            File.Exists,
            ReadTextIfExists,
            Environment.GetEnvironmentVariable,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            RunTimeLimit,
            (executable, limit, ct) => SystemCliVersion.ReadAsync(executable, limit, LaunchEnvironment(null), ct))
    {
    }

    /// <summary>시험용: 프로세스 실행·경로 찾기·파일 읽기·환경·홈 폴더·시간 제한·버전 읽기를 바꿔 넣는다.</summary>
    internal OpencodeDriver(
        IAgentRunTokens tokens,
        IAgentGate gate,
        IAgentBridgeEndpoint bridge,
        IAppDataPaths paths,
        IClaudeProcessLauncher launcher,
        Func<string?> pathVariable,
        Func<string, bool> fileExists,
        Func<string, string?> readText,
        Func<string, string?> environment,
        string home,
        TimeSpan timeLimit,
        SystemCliVersion.Reader readVersion)
    {
        // orandactl 을 쓰지 않으므로 경로는 비운다(실행 골격의 orandactl 확인은 쓰지 않고 이 드라이버가 사전 조건을 직접 확인한다).
        _runner = new SystemCliRunner(tokens, gate, bridge, paths, string.Empty, fileExists);
        _gate = gate;
        _bridge = bridge;
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _pathVariable = pathVariable;
        _fileExists = fileExists;
        _readText = readText ?? throw new ArgumentNullException(nameof(readText));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _home = home;
        _timeLimit = timeLimit;
        _readVersion = readVersion ?? throw new ArgumentNullException(nameof(readVersion));
    }

    public string CliId => AiProfilePresets.OpencodeCliId;

    /// <summary>
    /// opencode 프로세스 환경 덮어쓰기. 실행 토큰(환경 변수로만 넘긴다)·자동 업데이트 끄기, 그리고 부모의 <c>OPENCODE_CONFIG</c>(추가 설정 파일)를 지운다.
    /// 토큰이 없으면(버전 읽기) 부모에게서 물려받은 토큰 변수를 지운다.
    /// </summary>
    internal static Dictionary<string, string?> LaunchEnvironment(string? token)
        => new()
        {
            [AgentRunToken.EnvironmentVariable] = token,
            ["OPENCODE_DISABLE_AUTOUPDATE"] = "true",
            ["OPENCODE_CONFIG"] = null,
            // 상위 폴더의 .opencode(예: ~/.opencode)와 ~/.claude·~/.agents 호환 층을 읽지 않는다(2.0.32 에서 설정 폴더 플러그인이 그대로 로드되는 것을 확인).
            ["OPENCODE_DISABLE_PROJECT_CONFIG"] = "1",
            ["OPENCODE_CONFIG_PROJECT_DISABLE"] = "1",
            ["OPENCODE_DISABLE_CLAUDE_CODE"] = "1",
        };

    public async IAsyncEnumerable<AiEvent> RunAsync(AiRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Model is not null || request.Effort is not null)
        {
            yield return new AiError("이 CLI 는 모델·노력을 지정하지 않습니다. 기본 모델로 실행됩니다.", "cli_option_unsupported");
            yield break;
        }

        if (request.Images is { Count: > 0 })
        {
            yield return new AiError("opencode 는 이미지를 보낼 수 없습니다.", "image_unsupported");
            yield break;
        }

        if (string.IsNullOrWhiteSpace(request.Prompt))
        {
            yield return new AiError("보낼 내용이 없습니다.", "prompt_empty");
            yield break;
        }

        // 이어 말하기 id 는 opencode 가 낸 ses_ 모양만 받는다: 옵션처럼 보이는 글이 인자로 들어가는 일을 막는다.
        var resumeId = string.IsNullOrEmpty(request.ResumeId) ? null : request.ResumeId;
        if (resumeId is not null && !OpencodeArguments.SessionIdPattern().IsMatch(resumeId))
        {
            yield return new AiError("이어 말할 opencode 대화 id 의 형식이 올바르지 않습니다.", "resume_invalid");
            yield break;
        }

        if (CheckPreconditions(request) is { } preconditionError)
        {
            yield return preconditionError;
            yield break;
        }

        var session = request.Session!;
        var pipeName = _bridge.PipeName!;

        // 게이트가 이 실행에 내주는 도구. 이름은 허용 규칙(permission action)이 되므로 안전한 글자만 받는다.
        var tools = _gate.ListTools(session);
        if (ToolNameError(tools) is { } toolNameError)
        {
            yield return toolNameError;
            yield break;
        }

        JsonElement? finishSchema = null;
        if (request.OutputSchema is { } schema)
        {
            if (schema.ValueKind != JsonValueKind.Object || !schema.TryGetProperty("type", out var schemaType) || schemaType.GetString() != "object")
            {
                yield return new AiError("구조 결과 스키마의 최상위가 object 가 아니어서 opencode 로 실행하지 않았습니다.", "schema_unsupported");
                yield break;
            }

            finishSchema = schema;
        }

        var (executable, locateError) = LocateExecutable();
        if (locateError is not null)
        {
            yield return locateError;
            yield break;
        }

        // 사용자 설정의 MCP 서버를 끈다(자식 프로세스가 토큰을 상속한다). 읽지 못하면 닫는다.
        var (mcpNames, mcpProblem) = OpencodeGlobalConfig.ReadMcpServerNames(
            _environment, _home, Path.Combine(_runner.AiDirectory, "runs", "run"), _readText);
        if (mcpProblem is not null)
        {
            yield return new AiError($"opencode 의 사용자 설정을 확인하지 못해 실행하지 않았습니다. {mcpProblem}", "opencode_config_unsupported");
            yield break;
        }

        // 버전 읽기는 토큰 발급 전에 하고 토큰을 주지 않는다. 검증하지 않은 버전은 닫는다(스트림 감시가 도구 실행 뒤에야 걸리기 때문).
        var version = SystemCliVersion.Parse(await ReadVersionAsync(executable!, ct).ConfigureAwait(false));
        if (version is null || !VerifiedVersions.Contains(version))
        {
            yield return new AiError(
                $"이 버전의 opencode({version ?? "버전을 읽지 못함"})는 Oranda 에서 잠금을 검증하지 않아 실행하지 않았습니다. 검증한 버전: {string.Join(", ", VerifiedVersions)}.",
                "cli_version_unverified");
            yield break;
        }

        var allowed = new List<string>(tools.Select(t => t.Name));
        if (finishSchema is not null)
        {
            allowed.Add(OpencodeArguments.FinishToolName);
        }

        var run = _runner.Begin(session, pipeName, ct, _timeLimit);
        try
        {
            var configDirectory = Path.Combine(run.RunDirectory, "config");
            var workDirectory = Path.Combine(run.RunDirectory, "work");
            var (_, prepareError) = SystemCliRun.Prepare(() =>
            {
                if (Path.GetDirectoryName(run.RunDirectory) is { Length: > 0 } parent)
                {
                    PrivateDirectory.Ensure(parent);
                }

                PrivateDirectory.Ensure(run.RunDirectory);
                PrivateDirectory.Ensure(workDirectory);
                OpencodePluginFile.Write(configDirectory, pipeName, tools, finishSchema);
                return 0;
            });
            if (prepareError is not null)
            {
                yield return prepareError;
                yield break;
            }

            var environment = LaunchEnvironment(run.Token);
            environment["OPENCODE_CONFIG_DIR"] = configDirectory;
            environment["OPENCODE_CONFIG_CONTENT"] = OpencodeArguments.BuildConfigContent(allowed, mcpNames);
            var launch = new ClaudeLaunch(
                executable!,
                OpencodeArguments.Build(new OpencodeInvocation(resumeId)),
                workDirectory,
                request.Prompt,
                environment,
                Secret: run.Token); // 표준 오류 끝부분에 토큰이 나와도 가린다

            var (process, startError) = run.Start(_launcher, launch, CliName);
            if (startError is not null)
            {
                yield return startError;
                yield break;
            }

            var usage = new SystemCliToolUsage();
            var callIds = new HashSet<string>(StringComparer.Ordinal);
            string? sessionId = null;
            string? lastText = null;
            JsonElement? structured = null;
            long inputTokens = 0, outputTokens = 0;
            var ready = false;
            var finished = false;
            string? lastReason = null;
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

                    foreach (var streamEvent in OpencodeStreamParser.Parse(line!))
                    {
                        // 첫 사건에서 플러그인이 기대한 도구를 등록했는지 확인한다(없으면 도구 없이 모델이 지어내거나 잠금이 풀린 채 도는 것을 막는다).
                        if (!ready)
                        {
                            if (CheckReady(configDirectory, allowed) is { } notReady)
                            {
                                yield return Fail(run, notReady);
                                yield break;
                            }

                            ready = true;
                        }

                        if (streamEvent.SessionId is { Length: > 0 } id && sessionId is null)
                        {
                            sessionId = id;
                            if (resumeId is not null && !string.Equals(resumeId, id, StringComparison.Ordinal))
                            {
                                yield return Fail(run, new AiError("이어 말하려던 대화가 아닌 대화가 시작되어 중단했습니다.", "conversation_mismatch"));
                                yield break;
                            }

                            yield return new AiStarted(id);
                        }

                        switch (streamEvent)
                        {
                            case OpencodeUnexpected unexpected:
                                yield return run.RejectUnexpectedTool(unexpected.Description, _ => false)!;
                                yield break;

                            case OpencodeText text:
                                lastText = text.Text;
                                yield return new AiTextDelta(text.Text);
                                break;

                            case OpencodeTool tool:
                                if (run.RejectUnexpectedTool(tool.Tool, name => allowed.Contains(name, StringComparer.Ordinal)) is { } rejected)
                                {
                                    yield return rejected;
                                    yield break;
                                }

                                if (tool.Tool == OpencodeArguments.FinishToolName)
                                {
                                    // 구조 결과를 내는 도구: 인자가 결과다. 카드로 보이지 않는다. 실패한 호출(스키마 불일치)은 결과로 쓰지 않는다.
                                    if (tool.Status == "completed" && TryParseObject(tool.InputJson) is { } result)
                                    {
                                        structured = result;
                                    }

                                    break;
                                }

                                foreach (var toolEvent in ToolEvents(tool, run, callIds))
                                {
                                    usage.Observe(toolEvent);
                                    yield return toolEvent;
                                }

                                break;

                            case OpencodeStepFinish stepFinish:
                                inputTokens += stepFinish.InputTokens;
                                outputTokens += stepFinish.OutputTokens;
                                finished = true;
                                lastReason = stepFinish.Reason;
                                break;

                            case OpencodeError error:
                                yield return Fail(run, new AiError(error.Message, "cli_error"));
                                yield break;
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

            if (!ready && CheckReady(configDirectory, allowed) is { } missing)
            {
                // 사건 없이 끝난 실행: 플러그인 문제를 먼저 알린다.
                yield return missing;
                yield break;
            }

            // 도구를 부른 중간 단계(tool-calls)에서 스트림이 끊기면 마지막 step_finish 가 최종 단계가 아니다: 서두 글을 결과로 내지 않는다.
            if (!finished || exitCode != 0 || string.Equals(lastReason, ToolCallsReason, StringComparison.Ordinal))
            {
                yield return FailedWithoutResult(process.StandardErrorTail, exitCode);
                yield break;
            }

            if (finishSchema is not null && structured is null)
            {
                yield return new AiError("opencode 가 구조 결과를 내지 않고 끝났습니다(결과 도구를 부르지 않음).", "cli_error");
                yield break;
            }

            // 도구를 불렀는데 성공 없이 실패가 있으면 결과를 내보내지 않는다. 모두 거부됐거나 한 번도 안 불렀으면 결과 앞에 근거 없음을 알린다(새 대화 첫 차례만).
            if (usage.AllFailedError(CliDisplayName) is { } allFailed)
            {
                yield return allFailed;
                yield break;
            }

            if (usage.Notice(finishSchema is not null, resumeId is null) is { } noTools)
            {
                yield return new AiTextDelta(noTools);
            }

            yield return new AiUsage(inputTokens, outputTokens);
            yield return new AiFinal(structured is null ? lastText : null, structured, sessionId);
        }
        finally
        {
            await run.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>세션·브리지 사전 조건(orandactl 은 쓰지 않으므로 확인하지 않는다).</summary>
    private AiError? CheckPreconditions(AiRequest request)
    {
        if (request.Session is null)
        {
            return new AiError("AI 요청에 실행 범위(Session)가 없습니다.", "session_required");
        }

        if (request.Session.IsRevoked)
        {
            return new AiError("이 AI 요청의 실행 범위가 이미 폐기되었습니다. 새 요청으로 다시 시작하세요.", "session_revoked");
        }

        return _bridge.PipeName is null
            ? new AiError("AI 연결이 열려 있지 않습니다. AI 튜닝 기능이 켜져 있는지 확인하세요.", "bridge_not_ready")
            : null;
    }

    private static AiError? ToolNameError(IReadOnlyList<AgentToolInfo> tools)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tool in tools)
        {
            if (!OpencodeArguments.ToolNamePattern().IsMatch(tool.Name)
                || tool.Name == OpencodeArguments.FinishToolName
                || OpencodeArguments.ReservedToolNames.Contains(tool.Name)
                || !seen.Add(tool.Name))
            {
                return new AiError($"도구 이름 「{tool.Name}」 을 opencode 의 허용 규칙으로 안전하게 쓸 수 없어 실행하지 않았습니다.", "tool_name_invalid");
            }
        }

        return null;
    }

    private (string? Executable, AiError? Error) LocateExecutable()
    {
        // PATH 와 흔한 설치 위치 + opencode 설치 스크립트의 위치(~/.opencode/bin). 이 위치는 공용 목록에 없어 이 드라이버만 더한다.
        var executable = SystemCliExecutable.Find(
            "opencode",
            _pathVariable(),
            _fileExists,
            SystemCliExecutable.KnownDirectories(_home).Append(Path.Combine(_home, ".opencode", "bin")));
        if (executable is null)
        {
            return (null, new AiError("opencode 실행 파일을 찾지 못했습니다. opencode 를 설치한 뒤 다시 시도하세요.", "cli_not_found"));
        }

        return ClaudeExecutable.IsBatchFile(executable)
            ? (null, new AiError("opencode 의 배치 파일은 인자가 깨져 지원하지 않습니다.", "cli_unsupported"))
            : (executable, null);
    }

    private async Task<string?> ReadVersionAsync(string executable, CancellationToken ct)
    {
        try
        {
            return await _readVersion(executable, SystemCliVersion.ReadTimeLimit, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// 플러그인이 남긴 표식을 기대 도구 목록과 대조한다. 정상이면 null.
    /// 실패 표식(<see cref="OpencodePluginFile.FailedFileName"/>)이 있으면 그 글을, 표식이 없으면 로드되지 않은 것으로, 목록이 다르면 어긋난 것으로 닫는다.
    /// </summary>
    internal static AiError? CheckReady(string configDirectory, IReadOnlyCollection<string> expected)
    {
        var failed = Path.Combine(configDirectory, OpencodePluginFile.FailedFileName);
        try
        {
            if (File.Exists(failed))
            {
                var reason = File.ReadAllText(failed).Trim();
                return new AiError($"opencode 플러그인을 준비하지 못했습니다: {(reason.Length == 0 ? "원인 불명" : reason)}", "opencode_plugin_failed");
            }

            var ready = Path.Combine(configDirectory, OpencodePluginFile.ReadyFileName);
            if (!File.Exists(ready))
            {
                return new AiError("opencode 가 Oranda 플러그인을 불러오지 않아 실행하지 않았습니다. opencode 버전과 설정을 확인하세요.", "opencode_plugin_not_loaded");
            }

            using var document = JsonDocument.Parse(File.ReadAllText(ready));
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("tools", out var tools)
                || tools.ValueKind != JsonValueKind.Array
                || tools.EnumerateArray().Any(t => t.ValueKind != JsonValueKind.String))
            {
                return new AiError("opencode 플러그인의 표식 형식이 올바르지 않아 실행하지 않았습니다.", "opencode_plugin_mismatch");
            }

            var registered = tools.EnumerateArray().Select(t => t.GetString()!).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            var wanted = expected.OrderBy(n => n, StringComparer.Ordinal).ToArray();
            return registered.SequenceEqual(wanted, StringComparer.Ordinal)
                ? null
                : new AiError("opencode 플러그인이 등록한 도구가 기대와 달라 실행하지 않았습니다.", "opencode_plugin_mismatch");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new AiError("opencode 플러그인의 표식을 읽지 못해 실행하지 않았습니다.", "opencode_plugin_not_loaded");
        }
    }

    private static List<AiEvent> ToolEvents(OpencodeTool tool, SystemCliRun run, HashSet<string> callIds)
    {
        var events = new List<AiEvent>();
        var callId = callIds.Add(tool.CallId) ? tool.CallId : tool.CallId + "#" + callIds.Count;
        events.Add(new AiToolCall(callId, tool.Tool, Summarize(tool.InputJson)));

        // 앱 도구 호출은 모두 게이트를 지난다. 판단 기록이 없는 호출(opencode 쪽 오류·권한 거부·연결 실패)은 상태와 상관없이 실패로 센다.
        events.Add(run.Judgements.TryTake(tool.Tool, out var judged)
            ? new AiToolResult(callId, tool.Tool, judged.Size, judged.Judgement, judged.Note)
            : new AiToolResult(callId, tool.Tool, tool.OutputSize, AiToolJudgement.Failed));
        return events;
    }

    private static AiError Fail(SystemCliRun run, AiError error)
    {
        try
        {
            run.Process?.Kill();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException or AggregateException)
        {
            // 정리 단계가 다시 끝낸다.
        }

        return error;
    }

    private static AiError FailedWithoutResult(string stderrTail, int exitCode)
    {
        var detail = stderrTail.Trim();
        return new AiError(
            detail.Length == 0 ? $"opencode 가 결과 없이 끝났습니다(종료 코드 {exitCode})." : $"opencode 가 결과 없이 끝났습니다(종료 코드 {exitCode}): {detail}",
            "cli_failed");
    }

    private static JsonElement? TryParseObject(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Summarize(string inputJson)
        => inputJson.Length <= MaxArgumentSummaryChars ? inputJson : inputJson[..MaxArgumentSummaryChars] + "…";

    private static string? ReadTextIfExists(string path)
        => File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null;

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
}
