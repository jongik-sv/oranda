using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Oranda.App.Features.Ai.Tuning;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Ai.Protocol;
using Oranda.Core.Ai.Schemas;
using Oranda.Core.Connection;
using Oranda.Platform.Ai.Bridge;
using Oranda.Platform.Ai.Cli;
using Oranda.Platform.Storage;

namespace Oranda.App.Tests.Features.Ai;

/// <summary>
/// Codex 드라이버의 구조 결과 왕복: 실제 <c>CodexDriver</c> · 실제 <c>TuningResult</c> 스키마(<c>CodexOutputSchema</c> 변환 포함) · 실제 튜닝 요청 글(<c>AiTuningFlow.BuildPrompt</c>)로 돌고,
/// 결과를 <c>AiTuningFlow.Capture</c>(비공개, 리플렉션)로 붙잡아 <c>TuningResult</c> 로 읽는다. 위치가 App.Tests 인 이유: Capture 와 BuildPrompt 가 App 쪽이다.
/// 실제 orandactl · 실제 <c>AgentBridgeHost</c> 로 MCP 호출이 지난다. 대체한 것: 게이트(<c>AgentGate</c>)는 <c>RecordingGate</c> 로 바꿨다
/// (실제 <c>AgentTools</c> 도구 목록을 내고 예시 결과를 돌려주며 판단 사건을 낸다). 판단 사건이 없으면 드라이버는 호출을 성공으로 세지 않아 <c>all_tools_failed</c> 로 닫는다.
/// 실제 orandactl → AgentGate → agent_audit 확인은 앱에서 한다.
/// <list type="bullet">
/// <item>가짜 모델 서버 확인: <c>ORANDA_AI_E2E_CODEX=1</c>. 빈 임시 CODEX_HOME, 가짜 키, 실제 로그인 없음.</item>
/// <item>실제 로그인 왕복: <c>ORANDA_AI_E2E_CODEX_REAL=1</c>. 실제 CODEX_HOME(없으면 ~/.codex), 실제 모델 호출. 실행:
/// <c>ORANDA_AI_E2E_CODEX_REAL=1 dotnet test tests/Oranda.App.Tests -c Debug --filter FullyQualifiedName~AiSystemCliCodexRoundTripTests</c></item>
/// </list>
/// </summary>
[Collection("codex-env")]
public sealed class AiSystemCliCodexRoundTripTests : IDisposable
{
    private const string SampleStatement = "SELECT e.empno, e.ename FROM emp e WHERE e.deptno = 10 ORDER BY e.ename";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oranda-codex-rt-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string?> _savedEnv = [];

    public AiSystemCliCodexRoundTripTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (var (name, value) in _savedEnv)
        {
            Environment.SetEnvironmentVariable(name, value);
        }

        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // 임시 폴더 정리 실패는 무시한다.
        }
    }

    [CodexRoundTripFact("ORANDA_AI_E2E_CODEX")]
    public async Task The_flow_works_against_a_fake_model_server_and_the_server_gets_the_converted_strict_schema()
    {
        var codexHome = Path.Combine(_dir, "codex-home");
        Directory.CreateDirectory(codexHome);
        foreach (var (name, value) in new[] { ("CODEX_HOME", codexHome), ("OPENAI_API_KEY", "fake-key-not-real") })
        {
            _savedEnv[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        var (output, error) = await CodexModelCatalog.RunDebugModelsAsync(FindOnPath("codex"), Path.GetTempPath(), CancellationToken.None);
        Assert.Null(error);
        var model = JsonNode.Parse(output!)!["models"]!.AsArray().OfType<JsonObject>()
            .First(m => m["tool_mode"] is not null || m["apply_patch_tool_type"] is not null)["slug"]!.GetValue<string>();
        const string answer = """{"summary":"**EMP 는 1234 행** 입니다.","rewrites":[],"index_suggestions":[],"evidence":[{"tool":"describe_object","finding":"num_rows 1234"}]}""";
        await using var server = new FakeModelServer(
            [new JsonObject { ["type"] = "function_call", ["namespace"] = "mcp__orandactl", ["name"] = "describe_object", ["arguments"] = """{"object":"EMP"}""" }],
            answer);

        var flow = await RunFlowAsync(new ProviderArgsLauncher(server.Port, model), codexHome);

        Assert.Contains("describe_object", flow.GateCalls);
        var format = JsonNode.Parse(server.Requests[0])!["text"]!["format"]!;
        Assert.True(format["strict"]!.GetValue<bool>());
        var rewrite = format["schema"]!["properties"]!["rewrites"]!["items"]!;
        Assert.Contains("benchmark", rewrite["required"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.False(rewrite["additionalProperties"]!.GetValue<bool>());
    }

    [CodexRoundTripFact("ORANDA_AI_E2E_CODEX_REAL")]
    public async Task Real_codex_calls_an_agent_tool_and_returns_a_structured_result_that_the_tuning_flow_captures()
    {
        var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");

        var flow = await RunFlowAsync(new SystemClaudeProcessLauncher(), codexHome);

        Assert.NotEmpty(flow.Result.Summary);
    }

    private sealed record Flow(TuningResult Result, List<string> GateCalls, List<AiEvent> Events);

    /// <summary>두 시험이 같이 쓰는 순서와 단언. 실제 기존 파일(~/.codex 의 shell_snapshots 포함)은 열지 않고, 실행 전 목록에 없던 새 파일만 읽어 토큰이 없는지 본다.</summary>
    private async Task<Flow> RunFlowAsync(IClaudeProcessLauncher launcher, string codexHome)
    {
        var wrapper = Path.Combine(_dir, "orandactl-with-dotnet");
        var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT") is { Length: > 0 } root ? root : Path.GetDirectoryName(FindOnPath("dotnet"))!;
        File.WriteAllText(wrapper, $"#!/bin/sh\nDOTNET_ROOT=\"{dotnetRoot}\" exec \"{FindOrandactl()}\" \"$@\"\n"); // 개발 빌드 orandactl 은 Codex 가 MCP 자식의 환경을 비워 .NET 을 못 찾는다
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var tokens = new RecordingTokens();
        var gate = new RecordingGate();
        var unique = Guid.NewGuid().ToString("N");
        await using var host = new AgentBridgeHost(gate, tokens, (_, instance) => $"oa-{unique[..8]}" + (instance is null ? string.Empty : "-" + instance));
        host.Start();
        var driver = new CodexDriver(
            tokens,
            gate,
            host,
            new AppDataPaths(_dir),
            launcher,
            wrapper,
            () => Environment.GetEnvironmentVariable("PATH"),
            File.Exists,
            CodexModelCatalog.RunDebugModelsAsync,
            Environment.GetEnvironmentVariable,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            TimeSpan.FromMinutes(5));

        var before = Directory.Exists(codexHome)
            ? Directory.EnumerateFiles(codexHome, "*", SearchOption.AllDirectories).ToHashSet(StringComparer.Ordinal)
            : [];

        var profile = new ConnectionProfile("p", "u", "h/s");
        var context = new AiContext("dev-example", null, AiAccessLevel.Plan);
        var request = new AiRequest(
            AiTuningFlow.BuildPrompt(new TuningStartChoice(Benchmark: false, IndexDdl: true, ModelId: "default"), SampleStatement),
            context,
            OutputSchema: TuningResult.Schema(),
            Session: new AgentSession(new AgentRunScope("e2e-codex-structured", profile, context)));

        // 실제 앱처럼 AiTuningFlow.Capture 를 거친다(비공개 정적 메서드).
        var capture = typeof(AiTuningFlow).GetMethod("Capture", BindingFlags.NonPublic | BindingFlags.Static)!;
        JsonElement? captured = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var stream = (IAsyncEnumerable<AiEvent>)capture.Invoke(null, [driver.RunAsync(request, timeout.Token), (Action<JsonElement?>)(r => captured = r), CancellationToken.None])!;
        var events = new List<AiEvent>();
        await foreach (var e in stream.WithCancellation(timeout.Token))
        {
            events.Add(e);
        }

        // 스트림 감시가 정상 실행을 닫지 않았다.
        Assert.DoesNotContain(events, e => e is AiError);
        Assert.IsType<AiFinal>(events[^1]);
        Assert.NotNull(captured);
        Assert.NotEqual(JsonValueKind.Null, captured!.Value.ValueKind);
        var result = TuningResult.Parse(captured.Value);

        // MCP 도구 호출이 1건 이상 성공했다: 화면 사건과 게이트 쪽 기록이 함께 증명한다. 게이트 판단이 없는 호출은 성공이 아니다.
        var agentToolNames = AgentTools.All.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Contains(events, e => e is AiToolCall call && agentToolNames.Contains(call.ToolName));
        Assert.Contains(events, e => e is AiToolResult { Judgement: AiToolJudgement.Allowed or AiToolJudgement.Masked });
        Assert.True(gate.Calls.Count >= 1, "게이트에 도구 호출이 한 건도 도착하지 않았다.");
        Assert.All(gate.Calls, call => Assert.Contains(call, agentToolNames));

        // 잠금 쪽: 토큰 폐기, 새 파일에 토큰 없음.
        var token = Assert.Single(tokens.Issued);
        Assert.Equal(tokens.Issued, tokens.Revoked);
        foreach (var file in Directory.EnumerateFiles(codexHome, "*", SearchOption.AllDirectories).Where(f => !before.Contains(f)))
        {
            if (new FileInfo(file).Length <= 50_000_000)
            {
                Assert.DoesNotContain(token, Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file)), StringComparison.Ordinal);
            }
        }

        return new Flow(result, gate.Calls, events);
    }

    private static string FindOnPath(string name)
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"{name} 이(가) PATH 에 없다.");
    }

    private static string FindOrandactl()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Oranda.sln")))
        {
            directory = directory.Parent;
        }

        var exe = OperatingSystem.IsWindows() ? "orandactl.exe" : "orandactl";
        var path = Path.Combine(directory!.FullName, "src", "Oranda.Agent.Cli", "bin", "Debug", "net10.0", exe);
        return File.Exists(path) ? path : throw new FileNotFoundException("orandactl 을 먼저 빌드하세요.", path);
    }

    private sealed class RecordingTokens : IAgentRunTokens
    {
        private readonly AgentRunTokenStore _inner = new();

        public List<string> Issued { get; } = [];

        public List<string> Revoked { get; } = [];

        public string Issue(AgentSession session)
        {
            var token = _inner.Issue(session);
            Issued.Add(token);
            return token;
        }

        public AgentSession? Resolve(string token) => _inner.Resolve(token);

        public void Revoke(string token)
        {
            Revoked.Add(token);
            _inner.Revoke(token);
        }
    }

    /// <summary>실제 <see cref="AgentTools"/> 도구 목록을 제공하고 호출을 기록하며, 게이트처럼 판단 사건(<c>ToolCompleted</c>)을 내는 대체 게이트(감사 기록·접속 없음).</summary>
    private sealed class RecordingGate : IAgentGate
    {
        private readonly List<string> _calls = [];

        public List<string> Calls
        {
            get
            {
                lock (_calls)
                {
                    return [.. _calls];
                }
            }
        }

        public event EventHandler<AgentToolActivityEventArgs>? ToolCompleted;

        public IReadOnlyList<AgentToolInfo> ListTools(AgentSession session) => [.. AgentTools.All.Select(AgentToolInfo.From)];

        public Task<AgentGateResponse> CallToolAsync(AgentSession session, string toolName, JsonNode? arguments, CancellationToken ct)
        {
            lock (_calls)
            {
                _calls.Add(toolName);
            }

            var text = toolName == AgentToolNames.DescribeObject
                ? """{"object":"EMP","num_rows":1234,"columns":["EMPNO","ENAME","DEPTNO"]}"""
                : """{"note":"example result"}""";
            ToolCompleted?.Invoke(this, new AgentToolActivityEventArgs(session.RunId, session.NextSequence(), toolName, AiToolJudgement.Allowed, text.Length, null));
            return Task.FromResult(new AgentGateResponse(new AgentCallToolResult(false, text), AiToolJudgement.Allowed));
        }
    }

    /// <summary>가짜 서버로 보낼 인자를 끼워 넣는 실행기(잠금 인자는 건드리지 않는다).</summary>
    private sealed class ProviderArgsLauncher(int port, string model) : IClaudeProcessLauncher
    {
        private readonly SystemClaudeProcessLauncher _inner = new();

        public IClaudeProcess Start(ClaudeLaunch launch)
        {
            var arguments = launch.Arguments.ToList();
            arguments.InsertRange(arguments.IndexOf("--ignore-rules") + 1,
            [
                "-c", "model_provider=fake",
                "-c", $"model_providers.fake={{name=\"f\",base_url=\"http://127.0.0.1:{port}/v1\",wire_api=\"responses\",env_key=\"OPENAI_API_KEY\"}}",
                "-c", $"model=\"{model}\"",
            ]);
            return _inner.Start(launch with { Arguments = arguments });
        }
    }

    /// <summary>Responses API 흉내: 요청마다 대본 항목을 하나씩 내고, 끝나면 <c>finalText</c> 글로 마친다.</summary>
    private sealed class FakeModelServer : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Task _loop;
        private readonly List<JsonObject> _script;
        private readonly string _finalText;
        private int _step;
        private int _counter;

        public FakeModelServer(IEnumerable<JsonObject> script, string finalText)
        {
            _script = [.. script];
            _finalText = finalText;
            using var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            Port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            _listener.Start();
            _loop = Task.Run(LoopAsync);
        }

        public int Port { get; }

        public List<string> Requests { get; } = [];

        private async Task LoopAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
                {
                    return;
                }

                using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                var body = await reader.ReadToEndAsync();
                JsonObject item;
                lock (Requests)
                {
                    if (context.Request.HttpMethod == "POST")
                    {
                        Requests.Add(body);
                    }

                    _counter++;
                    item = _step < _script.Count
                        ? (JsonObject)_script[_step++].DeepClone()
                        : new JsonObject
                        {
                            ["type"] = "message",
                            ["role"] = "assistant",
                            ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = _finalText, ["annotations"] = new JsonArray() }),
                        };
                    item["id"] = "it_" + _counter;
                    item["status"] = "completed";
                    if (item["type"]?.GetValue<string>() is "function_call" or "custom_tool_call")
                    {
                        item["call_id"] = "call_" + _counter;
                    }
                }

                context.Response.ContentType = "text/event-stream";
                var id = "resp_" + _counter;
                var sse = new StringBuilder();
                void Event(string type, JsonObject payload)
                {
                    payload["type"] = type;
                    sse.Append("event: ").Append(type).Append("\ndata: ").Append(payload.ToJsonString()).Append("\n\n");
                }

                Event("response.created", new JsonObject { ["response"] = new JsonObject { ["id"] = id } });
                Event("response.output_item.added", new JsonObject { ["output_index"] = 0, ["item"] = item.DeepClone() });
                Event("response.output_item.done", new JsonObject { ["output_index"] = 0, ["item"] = item.DeepClone() });
                Event("response.completed", new JsonObject
                {
                    ["response"] = new JsonObject
                    {
                        ["id"] = id,
                        ["usage"] = new JsonObject
                        {
                            ["input_tokens"] = 1,
                            ["output_tokens"] = 1,
                            ["total_tokens"] = 2,
                            ["input_tokens_details"] = new JsonObject { ["cached_tokens"] = 0 },
                            ["output_tokens_details"] = new JsonObject { ["reasoning_tokens"] = 0 },
                        },
                    },
                });
                await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(sse.ToString()));
                context.Response.Close();
            }
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            _listener.Close();
            await Task.WhenAny(_loop, Task.Delay(1000));
        }
    }
}

/// <summary>지정한 환경 변수가 <c>1</c> 이 아니면 건너뛴다(가짜 서버 확인·실제 로그인 왕복을 가른다).</summary>
public sealed class CodexRoundTripFactAttribute : FactAttribute
{
    public CodexRoundTripFactAttribute(string environmentVariable)
    {
        if (Environment.GetEnvironmentVariable(environmentVariable) != "1")
        {
            Skip = $"{environmentVariable}=1 일 때만 돈다.";
        }
    }
}
