using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Profiles;
using Oranda.Core.Ai.Protocol;
using Oranda.Core.Composition;
using Oranda.Core.Storage;
using Oranda.Platform.Ai.Bridge;
using Oranda.Core.Ai.Gate;

namespace Oranda.Platform.Ai.Cli;

/// <summary>
/// Codex CLI(<c>codex exec --json</c>)로 AI 요청 한 번을 실행하는 시스템 CLI 드라이버. 라우터는 <see cref="ISystemCliAiDriver"/> 로만 부르며,
/// 모델·추론 노력은 받지 않는다(Codex 기본 모델). 실행마다 도구를 모두 끈 인자(<see cref="CodexArguments"/>)와 도구를 뺀 모델 카탈로그
/// (<see cref="CodexModelCatalog"/>)를 쓰고, 도구 호출은 codex → orandactl → 파이프 → AgentGate 로만 지난다.
/// 카탈로그를 만들지 못하거나 형식이 예상과 다르면 프로세스를 띄우지 않고 닫는다. 스트림에 허용 밖 항목이 보이면 곧바로 끝낸다(<see cref="CodexStreamParser"/>).
/// <para>
/// 프롬프트와 응답은 Codex 가 <c>~/.codex/sessions</c> 에 저장한다(<c>--ephemeral</c> 을 쓰면 이어 말하기가 막힌다).
/// <c>$CODEX_HOME/AGENTS.md</c>(없으면 <c>~/.codex/AGENTS.md</c>)는 막을 수 없으므로, 새 대화의 첫 차례에만 알린다.
/// </para>
/// </summary>
[OrandaService(typeof(ISystemCliAiDriver))]
internal sealed class CodexDriver : ISystemCliAiDriver
{
    /// <summary>
    /// 한 차례 실행의 시간 제한 = 6시간. 도구 호출 하나가 벤치마크 설정에 따라 수 분에서 수십 분(제한 0 이면 더 길게)까지 걸릴 수 있어
    /// 실행 제한이 짧으면 정상 실행을 끊는다. 도구 호출 제한(<see cref="CodexArguments.ToolTimeoutSeconds"/>)과 같은 값이다.
    /// 멈춘 CLI 는 사용자 중지로 끝내는 것이 일반 경로이고, 이 제한은 마지막 안전망이다(미로그인 재연결 같은 무한 대기도 여기서 닫힌다).
    /// 오래된 실행 폴더를 지우는 기준(<c>SystemCliRunner</c>)은 이 값보다 커야 진행 중인 실행의 폴더를 지우지 않는다.
    /// </summary>
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromHours(6);

    /// <summary>잠금을 검증한 Codex 버전(<c>codex --version</c> 의 숫자 부분). 다른 버전이면 실행은 막지 않고 새 대화 첫 차례에 안내한다.</summary>
    internal static readonly IReadOnlyCollection<string> VerifiedVersions = ["0.166.0"];

    private const string CliDisplayName = "Codex CLI";
    private const int MaxArgumentSummaryChars = 4000;

    private readonly SystemCliRunner _runner;
    private readonly IClaudeProcessLauncher _launcher;
    private readonly Func<string?> _pathVariable;
    private readonly Func<string, bool> _fileExists;
    private readonly Func<string, string, CancellationToken, Task<(string? Output, string? Error)>> _debugModels;
    private readonly Func<string, string?> _environment;
    private readonly string _home;
    private readonly TimeSpan _timeout;
    private readonly SystemCliVersion.Reader _readVersion;

    public CodexDriver(IAgentRunTokens tokens, IAgentGate gate, IAgentBridgeEndpoint bridge, IAppDataPaths paths)
        : this(
            tokens,
            gate,
            bridge,
            paths,
            new SystemClaudeProcessLauncher(),
            CliDriver.OrandactlPath(AppContext.BaseDirectory, File.Exists),
            () => Environment.GetEnvironmentVariable("PATH"),
            File.Exists,
            CodexModelCatalog.RunDebugModelsAsync,
            Environment.GetEnvironmentVariable,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            DefaultTimeout,
            SystemCliVersion.ReadAsync)
    {
    }

    /// <summary>시험용: 프로세스 실행·경로 찾기·카탈로그 생성·환경을 바꿔 넣는다.</summary>
    internal CodexDriver(
        IAgentRunTokens tokens,
        IAgentGate gate,
        IAgentBridgeEndpoint bridge,
        IAppDataPaths paths,
        IClaudeProcessLauncher launcher,
        string orandactlPath,
        Func<string?> pathVariable,
        Func<string, bool> fileExists,
        Func<string, string, CancellationToken, Task<(string? Output, string? Error)>> debugModels,
        Func<string, string?> environment,
        string home,
        TimeSpan timeout,
        SystemCliVersion.Reader? readVersion = null)
    {
        _runner = new SystemCliRunner(tokens, gate, bridge, paths, orandactlPath, fileExists);
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _pathVariable = pathVariable;
        _fileExists = fileExists;
        _debugModels = debugModels ?? throw new ArgumentNullException(nameof(debugModels));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _home = home;
        _timeout = timeout;
        _readVersion = readVersion ?? SystemCliVersion.ReadAsync;
    }

    public string CliId => AiProfilePresets.CodexCliId;

    public async IAsyncEnumerable<AiEvent> RunAsync(AiRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 모델·추론 노력은 읽지 않는다(라우터가 비워 넘기지만, 직접 불러도 -m 이 나가지 않게 한다).
        if (ClaudeStreamInput.Validate(request.Prompt, request.Images) is { } inputError)
        {
            yield return inputError;
            yield break;
        }

        // 이어 말하기 id 는 Codex 가 낸 thread id(UUID)만 받는다: 옵션처럼 보이는 글이 위치 인자로 들어가는 일을 막는다.
        if (request.ResumeId is { Length: > 0 } resumeCandidate && !Guid.TryParse(resumeCandidate, out _))
        {
            yield return new AiError("이어 말할 Codex 대화 id 의 형식이 올바르지 않습니다.", "resume_invalid");
            yield break;
        }

        var (preconditionError, pipeName) = _runner.CheckPreconditions(request);
        if (preconditionError is not null)
        {
            yield return preconditionError;
            yield break;
        }

        var (executable, locateError) = SystemCliExecutable.Locate("codex", "Codex CLI", _pathVariable(), _fileExists);
        if (locateError is not null)
        {
            yield return locateError;
            yield break;
        }

        var session = request.Session!;
        var resumeId = request.ResumeId is { Length: > 0 } ? request.ResumeId : null;

        // 새 대화의 첫 차례에만 검증하지 않은 버전을 알린다(이어 말하기 차례의 AI 글은 튜닝 레포트의 답변으로 모이므로 섞이면 안 된다).
        // 버전 읽기는 토큰 발급 전에 하고 토큰을 주지 않는다. 먼저 닫히는 경우(위 거부들)에는 읽지도 않는다.
        var versionNotice = resumeId is null
            ? await SystemCliVersion.UnverifiedNoticeAsync(_readVersion, executable!, VerifiedVersions, CliDisplayName, ct).ConfigureAwait(false)
            : null;
        var run = _runner.Begin(session, pipeName!, ct, _timeout);
        try
        {
            var (prepared, prepareError) = SystemCliRun.Prepare(() => PrepareRunFolder(run, request));
            if (prepareError is not null)
            {
                yield return prepareError;
                yield break;
            }

            // 카탈로그는 프로세스를 띄우기 전에 만든다. 실패하면 codex 를 띄우지 않는다(카탈로그 없이는 코드 모드가 돌아온다).
            var (catalogOutput, catalogRunError) = await _debugModels(executable!, run.RunDirectory, run.CancellationToken).ConfigureAwait(false);
            if (catalogRunError is not null || catalogOutput is null)
            {
                yield return new AiError(catalogRunError ?? "Codex 모델 목록을 얻지 못했습니다.", "cli_start_failed");
                yield break;
            }

            var (catalogJson, catalogError) = CodexModelCatalog.Lock(catalogOutput);
            if (catalogJson is null)
            {
                yield return new AiError($"Codex 모델 카탈로그를 만들지 못해 시작하지 않았습니다: {catalogError}", "cli_start_failed");
                yield break;
            }

            var (_, writeError) = SystemCliRun.Prepare(() =>
            {
                WritePrivate(prepared.CatalogPath, catalogJson);
                return 0;
            });
            if (writeError is not null)
            {
                yield return writeError;
                yield break;
            }

            var invocation = new CodexInvocation(
                resumeId,
                run.RunDirectory,
                prepared.CatalogPath,
                run.OrandactlPath,
                run.PipeName,
                AgentRunToken.EnvironmentVariable,
                prepared.SchemaPath,
                prepared.ImagePaths);
            var launch = new ClaudeLaunch(
                executable!,
                CodexArguments.Build(invocation),
                run.RunDirectory,
                request.Prompt + "\n",
                new Dictionary<string, string?> { [AgentRunToken.EnvironmentVariable] = run.Token },
                Secret: run.Token); // 표준 오류 끝부분에 토큰이 나와도 가린다

            var (started, startError) = run.Start(_launcher, launch, "codex");
            if (startError is not null)
            {
                yield return startError;
                yield break;
            }

            var parser = new CodexStreamParser();
            string? threadId = null;
            string? lastMessage = null;
            long inputTokens = 0, outputTokens = 0;
            var completed = false;
            var failed = false;
            var timedOut = false;
            var wantsStructured = request.OutputSchema is not null;
            var usage = new SystemCliToolUsage();

            await using var lines = started!.ReadLinesAsync(run.CancellationToken).GetAsyncEnumerator(run.CancellationToken);
            while (true)
            {
                if (session.IsRevoked)
                {
                    started.Kill();
                    yield return new AiError("이 AI 요청의 실행 범위가 폐기되어 중단했습니다.", "session_revoked");
                    yield break;
                }

                string line;
                try
                {
                    if (!await lines.MoveNextAsync().ConfigureAwait(false))
                    {
                        break;
                    }

                    line = lines.Current;
                }
                catch (OperationCanceledException) when (run.TimedOut)
                {
                    timedOut = true;
                    break;
                }

                foreach (var codexEvent in parser.Parse(line))
                {
                    switch (codexEvent)
                    {
                        case CodexUnexpectedItem unexpected:
                            yield return run.RejectUnexpectedTool(unexpected.Description, _ => false)!;
                            yield break;

                        case CodexThreadStarted thread:
                            threadId = thread.ThreadId;
                            yield return new AiStarted(thread.ThreadId);
                            if (versionNotice is not null)
                            {
                                yield return new AiTextDelta(versionNotice);
                            }

                            if (resumeId is null && InstructionsNotice() is { } notice)
                            {
                                yield return new AiTextDelta(notice);
                            }

                            break;

                        case CodexMessage message:
                            lastMessage = message.Text;

                            // 구조 결과를 요청했으면 마지막 글이 JSON 원문이라 화면에 내지 않는다.
                            if (!wantsStructured)
                            {
                                yield return new AiTextDelta(message.Text);
                            }

                            break;

                        case CodexToolStarted tool:
                        {
                            var toolCall = new AiToolCall(tool.CallId, tool.Tool, Summarize(tool.ArgumentsJson));
                            usage.Observe(toolCall);
                            yield return toolCall;
                            break;
                        }

                        case CodexToolDone done:
                        {
                            // orandactl 호출은 모두 게이트를 지난다. 판단 기록이 없는 완료는 게이트를 건너뛴 호출이므로 상태와 상관없이 실패로 센다.
                            var toolResult = run.Judgements.TryTake(done.Tool, out var judged)
                                ? new AiToolResult(done.CallId, done.Tool, judged.Size, judged.Judgement, judged.Note)
                                : new AiToolResult(done.CallId, done.Tool, done.Size, AiToolJudgement.Failed);
                            usage.Observe(toolResult);
                            yield return toolResult;
                            break;
                        }

                        case CodexTurnCompleted turnDone:
                            inputTokens = turnDone.InputTokens;
                            outputTokens = turnDone.OutputTokens;
                            completed = true;
                            break;

                        case CodexTurnFailed turnFailed:
                            failed = true;
                            yield return new AiError(turnFailed.Message, "cli_error");
                            break;
                    }
                }

                if (failed)
                {
                    break; // turn.failed 뒤에는 더 읽지 않는다
                }
            }

            if (timedOut)
            {
                started.Kill();
                yield return SystemCliRun.TimeoutError("codex", _timeout);
                yield break;
            }

            int exitCode;
            var waitTimedOut = false;
            try
            {
                exitCode = await started.WaitForExitAsync(run.CancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (run.TimedOut)
            {
                exitCode = -1;
                waitTimedOut = true;
            }

            if (waitTimedOut)
            {
                started.Kill();
                yield return SystemCliRun.TimeoutError("codex", _timeout);
                yield break;
            }

            if (failed)
            {
                yield break;
            }

            if (!completed)
            {
                var detail = started.StandardErrorTail.Trim();
                yield return new AiError(
                    detail.Length == 0 ? $"codex 가 결과 없이 끝났습니다(종료 코드 {exitCode})." : $"codex 가 결과 없이 끝났습니다(종료 코드 {exitCode}): {detail}",
                    "cli_failed");
                yield break;
            }

            yield return new AiUsage(inputTokens, outputTokens);
            var final = BuildFinal(request, lastMessage, threadId);
            if (final is AiFinal)
            {
                // 도구를 불렀는데 성공 없이 실패가 있으면 결과를 내보내지 않는다. 모두 거부됐거나 한 번도 안 불렀으면 결과 앞에 근거 없음을 알린다(새 대화 첫 차례만).
                if (usage.AllFailedError("Codex") is { } allFailed)
                {
                    yield return allFailed;
                    yield break;
                }

                if (usage.Notice(wantsStructured, string.IsNullOrEmpty(resumeId)) is { } noTools)
                {
                    yield return new AiTextDelta(noTools);
                }
            }

            yield return final;
        }
        finally
        {
            await run.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static AiEvent BuildFinal(AiRequest request, string? lastMessage, string? threadId)
    {
        if (request.OutputSchema is not { } schema)
        {
            return new AiFinal(lastMessage, null, threadId);
        }

        try
        {
            using var document = JsonDocument.Parse(lastMessage ?? string.Empty);
            return new AiFinal(null, CodexOutputSchema.StripOptionalNulls(document.RootElement, schema), threadId);
        }
        catch (JsonException)
        {
            return new AiError("Codex 가 구조 결과(JSON)가 아닌 글로 답했습니다.", "cli_error");
        }
    }

    private static (string CatalogPath, string? SchemaPath, IReadOnlyList<string> ImagePaths) PrepareRunFolder(SystemCliRun run, AiRequest request)
    {
        if (Path.GetDirectoryName(run.RunDirectory) is { Length: > 0 } parent)
        {
            PrivateDirectory.Ensure(parent);
        }

        PrivateDirectory.Ensure(run.RunDirectory);

        string? schemaPath = null;
        if (request.OutputSchema is { } schema)
        {
            schemaPath = Path.Combine(run.RunDirectory, "output-schema.json");
            WritePrivate(schemaPath, CodexOutputSchema.ToStrict(schema));
        }

        var images = new List<string>();
        foreach (var image in request.Images ?? [])
        {
            var path = Path.Combine(run.RunDirectory, $"image-{images.Count + 1}{ImageExtension(image.MediaType)}");
            WritePrivate(path, image.Data);
            images.Add(path);
        }

        return (Path.Combine(run.RunDirectory, "model-catalog.json"), schemaPath, images);
    }

    private static string ImageExtension(string mediaType) => mediaType switch
    {
        "image/jpeg" => ".jpg",
        "image/webp" => ".webp",
        "image/gif" => ".gif",
        _ => ".png",
    };

    private static void WritePrivate(string path, string text) => WritePrivate(path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text));

    private static void WritePrivate(string path, byte[] bytes)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using var stream = new FileStream(path, options);
        stream.Write(bytes);
    }

    /// <summary>사용자 지시문 파일이 있으면 알림 글(없으면 null). 막을 수는 없지만 도구가 늘어나는 경로는 아니므로 실행은 막지 않는다.</summary>
    private string? InstructionsNotice()
    {
        var codexHome = _environment("CODEX_HOME");
        var directory = string.IsNullOrWhiteSpace(codexHome) ? Path.Combine(_home, ".codex") : codexHome;
        var file = Path.Combine(directory, "AGENTS.md");
        return _fileExists(file)
            ? $"참고: {file} 의 지시문이 Codex 에 함께 전달됩니다(Oranda 가 막을 수 없습니다).\n"
            : null;
    }

    private static string Summarize(string json) => json.Length <= MaxArgumentSummaryChars ? json : json[..MaxArgumentSummaryChars] + "…";
}
