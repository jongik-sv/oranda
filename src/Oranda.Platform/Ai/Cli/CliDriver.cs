using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Composition;
using Oranda.Core.Settings;
using Oranda.Core.Storage;
using Oranda.Platform.Ai.Bridge;

namespace Oranda.Platform.Ai.Cli;

/// <summary>
/// Claude Code CLI(<c>claude -p</c>)로 AI 요청 한 번을 실행하는 드라이버(설계 §2.3). 실행마다 새 실행 토큰을 발급해
/// <c>orandactl</c> 의 mcp-config 에 환경 변수로 넣고, 프로세스가 끝나거나 취소되면 토큰을 폐기하고 프로세스 트리를 끝낸다.
/// 도구 호출은 claude → orandactl → 파이프 → AgentGate 를 지나므로 이 드라이버는 화면에 보일 사건만 stream-json 에서 옮긴다.
/// <c>--resume</c> 때도 같은 옵션을 모두 다시 준다(<see cref="ClaudeCliArguments"/>). 시작 직후 <c>system/init</c> 으로
/// 실제 붙은 MCP 서버와 도구를 확인하고, orandactl 밖의 서버·도구가 있거나 orandactl 도구가 없으면 곧바로 끝낸다.
/// </summary>
[OrandaService(typeof(IAiDriver))]
internal sealed class CliDriver : IAiDriver
{
    private const int MaxArgumentSummaryChars = 4000;

    private static readonly TimeSpan StaleRunAge = TimeSpan.FromHours(6);

    private static readonly AiDriverCapabilities DriverCapabilities =
        new(Skills: false, Tools: true, Resume: true, LocalOnly: false, Streaming: false);

    private readonly IAgentRunTokens _tokens;
    private readonly IAgentGate _gate;
    private readonly IAgentBridgeEndpoint _bridge;
    private readonly IAppDataPaths _paths;
    private readonly ISettingsStore _settings;
    private readonly IClaudeProcessLauncher _launcher;
    private readonly string _orandactlPath;
    private readonly Func<string?> _pathVariable;
    private readonly Func<string, bool> _fileExists;

    public CliDriver(IAgentRunTokens tokens, IAgentGate gate, IAgentBridgeEndpoint bridge, IAppDataPaths paths, ISettingsStore settings)
        : this(
            tokens,
            gate,
            bridge,
            paths,
            settings,
            new SystemClaudeProcessLauncher(),
            Path.Combine(AppContext.BaseDirectory, "orandactl", OperatingSystem.IsWindows() ? "orandactl.exe" : "orandactl"),
            () => Environment.GetEnvironmentVariable("PATH"),
            File.Exists)
    {
    }

    /// <summary>시험용: 프로세스 실행·경로 찾기를 바꿔 넣는다.</summary>
    internal CliDriver(
        IAgentRunTokens tokens,
        IAgentGate gate,
        IAgentBridgeEndpoint bridge,
        IAppDataPaths paths,
        ISettingsStore settings,
        IClaudeProcessLauncher launcher,
        string orandactlPath,
        Func<string?> pathVariable,
        Func<string, bool> fileExists)
    {
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _orandactlPath = orandactlPath;
        _pathVariable = pathVariable;
        _fileExists = fileExists;
    }

    public AiDriverCapabilities Capabilities => DriverCapabilities;

    public async IAsyncEnumerable<AiEvent> RunAsync(AiRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var checkedRun = await CheckPreconditionsAsync(request, ct).ConfigureAwait(false);
        if (checkedRun.Error is not null)
        {
            yield return checkedRun.Error;
            yield break;
        }

        var session = request.Session!;
        var aiDirectory = Path.Combine(_paths.DataDirectory, "ai");
        var runsDirectory = Path.Combine(aiDirectory, "runs");
        var runDirectory = Path.Combine(runsDirectory, $"{session.RunId}-{Guid.NewGuid():N}");
        DeleteStaleRuns(runsDirectory);

        var judgements = new ConcurrentDictionary<string, ConcurrentQueue<AgentToolActivityEventArgs>>(StringComparer.Ordinal);
        void OnToolCompleted(object? sender, AgentToolActivityEventArgs e)
        {
            if (e.RunId == session.RunId)
            {
                judgements.GetOrAdd(e.ToolName, _ => new ConcurrentQueue<AgentToolActivityEventArgs>()).Enqueue(e);
            }
        }

        var token = _tokens.Issue(session);
        IClaudeProcess? process = null;
        CancellationTokenRegistration killOnCancel = default;
        _gate.ToolCompleted += OnToolCompleted;
        try
        {
            var (configPath, workDirectory, prepareError) = Prepare(runDirectory, aiDirectory, checkedRun.PipeName!, token);
            if (prepareError is not null)
            {
                yield return prepareError;
                yield break;
            }

            // 스키마는 줄바꿈 없이 한 줄로 넘긴다(공백·줄바꿈이 든 원문은 일부 실행 경로에서 인자가 잘린다).
            var schema = request.OutputSchema is { } element ? JsonSerializer.Serialize(element) : null;
            var invocation = new ClaudeCliInvocation(configPath!, schema, request.Model ?? checkedRun.Settings!.Model, request.ResumeId, request.Effort ?? checkedRun.Settings!.Effort);
            var launch = new ClaudeLaunch(checkedRun.Executable!, ClaudeCliArguments.Build(invocation), workDirectory!, request.Prompt);

            (process, var startError) = TryStart(launch);
            if (startError is not null)
            {
                yield return startError;
                yield break;
            }

            // 읽기 취소가 늦게 반영되는 경우에도 취소하면 곧바로 프로세스를 끝낸다.
            var started = process!;
            killOnCancel = ct.Register(started.Kill);

            var parser = new ClaudeStreamParser();
            var toolNames = new Dictionary<string, string>(StringComparer.Ordinal);
            var finished = false;
            var validated = false;
            await foreach (var line in started.ReadLinesAsync(ct).ConfigureAwait(false))
            {
                foreach (var streamEvent in parser.Parse(line))
                {
                    if (streamEvent is ClaudeSessionStarted init)
                    {
                        if (ValidateInit(init) is { } problem)
                        {
                            yield return problem;
                            yield break;
                        }

                        validated = true;
                    }
                    else if (!validated && streamEvent is ClaudeToolUse or ClaudeResult { IsError: false })
                    {
                        // 시작 정보(init) 없이 도구 호출이나 결과가 먼저 오면 무엇이 붙었는지 확인하지 못한 것이다. 닫는 쪽으로 실패한다.
                        yield return new AiError("claude 가 시작 정보를 보내기 전에 도구를 부르거나 결과를 냈습니다.", "init_missing");
                        yield break;
                    }

                    foreach (var aiEvent in Translate(streamEvent, toolNames, judgements))
                    {
                        finished |= aiEvent is AiFinal or AiError;
                        yield return aiEvent;
                    }
                }
            }

            var exitCode = await started.WaitForExitAsync(ct).ConfigureAwait(false);
            if (!finished)
            {
                var detail = started.StandardErrorTail.Trim();
                yield return new AiError(
                    detail.Length == 0 ? $"claude 가 결과 없이 끝났습니다(종료 코드 {exitCode})." : $"claude 가 결과 없이 끝났습니다(종료 코드 {exitCode}): {detail}",
                    "cli_failed");
            }
        }
        finally
        {
            _gate.ToolCompleted -= OnToolCompleted;
            await CleanUpAsync(token, process, killOnCancel, runDirectory).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// claude 가 실제로 붙인 것을 확인한다. MCP 서버는 orandactl 하나뿐이고 연결돼 있어야 하며, 도구는 orandactl 도구 1개 이상과
    /// 합성 도구(StructuredOutput·ToolSearch)뿐이어야 한다. 어긋나면 AI 가 도구 없이 지어낸 결과를 내거나 게이트를 건너뛰는 통로가 생긴다.
    /// </summary>
    internal static AiError? ValidateInit(ClaudeSessionStarted init)
    {
        var foreign = init.McpServers.FirstOrDefault(s => s.Name != ClaudeCliArguments.McpServerName);
        if (foreign is not null)
        {
            return new AiError($"예상하지 못한 MCP 서버가 붙었습니다: {foreign.Name}", "unexpected_mcp_server");
        }

        if (!init.McpServers.Any(s => s.Name == ClaudeCliArguments.McpServerName && s.Status == "connected"))
        {
            return new AiError("Oranda 도구 연결(orandactl)이 시작되지 않았습니다.", "bridge_unavailable");
        }

        var unexpectedTool = init.Tools.FirstOrDefault(t =>
            !t.StartsWith(ClaudeCliArguments.McpToolPrefix, StringComparison.Ordinal) && t is not ("StructuredOutput" or "ToolSearch"));
        if (unexpectedTool is not null)
        {
            return new AiError($"예상하지 못한 도구가 열렸습니다: {unexpectedTool}", "unexpected_tool");
        }

        return init.Tools.Any(t => t.StartsWith(ClaudeCliArguments.McpToolPrefix, StringComparison.Ordinal))
            ? null
            : new AiError("Oranda 도구 목록이 비어 있습니다. 앱의 AI 연결 상태와 접속의 AI 허용 수준을 확인하세요.", "bridge_unavailable");
    }

    private static List<AiEvent> Translate(
        ClaudeStreamEvent streamEvent,
        Dictionary<string, string> toolNames,
        ConcurrentDictionary<string, ConcurrentQueue<AgentToolActivityEventArgs>> judgements)
    {
        var events = new List<AiEvent>();
        switch (streamEvent)
        {
            case ClaudeSessionStarted started:
                events.Add(new AiStarted(started.SessionId));
                break;

            case ClaudeText text:
                events.Add(new AiTextDelta(text.Text));
                break;

            case ClaudeToolUse use:
                toolNames[use.CallId] = use.ToolName;
                events.Add(new AiToolCall(use.CallId, use.ToolName, Summarize(use.InputJson)));
                break;

            case ClaudeToolResult result:
                var name = toolNames.GetValueOrDefault(result.CallId, string.Empty);
                if (judgements.TryGetValue(name, out var queue) && queue.TryDequeue(out var completed))
                {
                    events.Add(new AiToolResult(result.CallId, name, completed.Size, completed.Judgement, completed.Note));
                }
                else
                {
                    // 게이트를 거치지 않았다(권한 거부·연결 실패). 판단 기록이 없으면 오류 여부로만 알린다.
                    events.Add(new AiToolResult(result.CallId, name, result.Size, result.IsError ? AiToolJudgement.Failed : AiToolJudgement.Allowed));
                }

                break;

            case ClaudeResult result:
                if (result.IsError)
                {
                    events.Add(new AiError(string.IsNullOrWhiteSpace(result.Text) ? "claude 실행 오류" : result.Text, "cli_error"));
                    break;
                }

                events.Add(new AiUsage(result.InputTokens, result.OutputTokens, result.CostUsd));
                events.Add(new AiFinal(result.StructuredOutput is null ? result.Text : null, result.StructuredOutput, result.SessionId));
                break;
        }

        return events;
    }

    private static string Summarize(string inputJson)
        => inputJson.Length <= MaxArgumentSummaryChars ? inputJson : inputJson[..MaxArgumentSummaryChars] + "…";

    private static void DeleteQuietly(string directory)
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

    /// <summary>앱이 죽었거나 삭제가 실패해 남은 오래된 실행 폴더(토큰이 든 mcp.json)를 지운다. 진행 중인 다른 실행은 건드리지 않는다.</summary>
    private static void DeleteStaleRuns(string runsDirectory)
    {
        try
        {
            if (!Directory.Exists(runsDirectory))
            {
                return;
            }

            foreach (var directory in Directory.EnumerateDirectories(runsDirectory))
            {
                if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(directory) > StaleRunAge)
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

    /// <summary>한 단계가 실패해도 나머지(프로세스 종료·토큰 폐기·폴더 삭제)는 반드시 한다.</summary>
    private async Task CleanUpAsync(string token, IClaudeProcess? process, CancellationTokenRegistration killOnCancel, string runDirectory)
    {
        try
        {
            await killOnCancel.DisposeAsync().ConfigureAwait(false);
            if (process is not null)
            {
                process.Kill();
                await process.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            try
            {
                _tokens.Revoke(token);
            }
            finally
            {
                DeleteQuietly(runDirectory);
            }
        }
    }

    /// <summary>실행 폴더·mcp-config·작업 폴더를 만든다. 디스크 오류는 예외 대신 AiError 로 알린다.</summary>
    private (string? ConfigPath, string? WorkDirectory, AiError? Error) Prepare(string runDirectory, string aiDirectory, string pipeName, string token)
    {
        try
        {
            var configPath = McpConfigWriter.Write(runDirectory, _orandactlPath, pipeName, token);
            var workDirectory = Path.Combine(aiDirectory, "work");
            Directory.CreateDirectory(workDirectory);
            return (configPath, workDirectory, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, null, new AiError($"AI 실행 준비에 실패했습니다: {ex.Message}", "cli_start_failed"));
        }
    }

    private (IClaudeProcess? Process, AiError? Error) TryStart(ClaudeLaunch launch)
    {
        try
        {
            return (_launcher.Start(launch), null);
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            return (null, new AiError($"claude 를 시작하지 못했습니다: {ex.Message}", "cli_start_failed"));
        }
    }

    private async Task<(AiError? Error, string? Executable, AiSettings? Settings, string? PipeName)> CheckPreconditionsAsync(AiRequest request, CancellationToken ct)
    {
        if (request.Session is null)
        {
            return (new AiError("AI 요청에 실행 범위(Session)가 없습니다.", "session_required"), null, null, null);
        }

        if (request.Session.IsRevoked)
        {
            return (new AiError("이 AI 요청의 실행 범위가 이미 폐기되었습니다. 새 요청으로 다시 시작하세요.", "session_revoked"), null, null, null);
        }

        var pipeName = _bridge.PipeName;
        if (pipeName is null)
        {
            return (new AiError("AI 연결이 열려 있지 않습니다. AI 튜닝 기능이 켜져 있는지 확인하세요.", "bridge_not_ready"), null, null, null);
        }

        if (!_fileExists(_orandactlPath))
        {
            return (new AiError("orandactl 실행 파일을 찾지 못했습니다.", "orandactl_missing"), null, null, null);
        }

        AiSettings settings;
        try
        {
            settings = (await _settings.LoadAsync(ct).ConfigureAwait(false)).Value.Ai;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (new AiError($"설정을 읽지 못했습니다: {ex.Message}", "settings_unreadable"), null, null, null);
        }

        var executable = ClaudeExecutable.Find(settings.ExecutablePath, _pathVariable(), _fileExists);
        if (executable is null)
        {
            return (new AiError("claude 실행 파일을 찾지 못했습니다. 설정에서 경로를 지정하거나 Claude Code 를 설치하세요.", "cli_not_found"), null, null, null);
        }

        return ClaudeExecutable.IsBatchFile(executable)
            ? (new AiError("claude.cmd 같은 배치 파일은 인자가 깨져 지원하지 않습니다. 설정에서 claude.exe 경로를 지정하세요.", "cli_unsupported"), null, null, null)
            : (null, executable, settings, pipeName);
    }
}
