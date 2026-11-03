using System.Net;
using System.Net.Sockets;
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
/// agy 드라이버의 AI 튜닝 왕복: 실제 <see cref="AgyDriver"/> · 실제 orandactl · 실제 <c>AgentBridgeHost</c> · 실제 <see cref="TuningResult"/> 스키마 ·
/// 실제 튜닝 요청 글(<c>AiTuningFlow.BuildPrompt</c>)로 돌고, 결과를 <c>AiTuningFlow.Capture</c>(비공개, 리플렉션)로 붙잡아 <see cref="TuningResult"/> 로 읽는다.
/// 위치가 App.Tests 인 이유: BuildPrompt 와 Capture 가 App 쪽이다. 요청 글에 도구 사용을 강제하는 문장을 더하지 않는다: 모델이 실제 튜닝 요청에서
/// 스스로 도구를 쓰는지가 이 시험이 보려는 것이다.
/// 대체한 것: 게이트(<c>AgentGate</c>)는 <see cref="RecordingGate"/> 로 바꿨다(실제 <see cref="AgentTools"/> 도구 목록을 내고 예시 결과를 돌려주며 판단 사건을 낸다).
/// 실제 orandactl → AgentGate → agent_audit 확인은 앱에서 한다.
/// <list type="bullet">
/// <item>가짜 모델 서버 확인: <c>ORANDA_AI_E2E_AGY=1</c>. 미끼 HOME 과 가짜 서버를 쓰고 실제 로그인은 쓰지 않는다.</item>
/// <item>실제 로그인 왕복: <c>ORANDA_AI_E2E_AGY_REAL=1</c>. 실제 HOME 과 실제 모델 호출(쿼터를 쓴다). 실행:
/// <c>ORANDA_AI_E2E_AGY_REAL=1 dotnet test tests/Oranda.App.Tests -c Debug --filter FullyQualifiedName~AiSystemCliAgyRoundTripTests</c></item>
/// </list>
/// 켜기 조건: MCP 도구 호출이 1건 이상 성공하고(게이트 쪽 기록), 최종 결과가 Capture 를 거쳐 튜닝 결과로 파싱되며, 스트림 감시가 정상 실행을 닫지 않는다.
/// </summary>
public sealed class AiSystemCliAgyRoundTripTests : IDisposable
{
    private const string SampleStatement = "SELECT e.empno, e.ename FROM emp e WHERE e.deptno = 10 ORDER BY e.ename";

    private static readonly JsonSerializerOptions RecordOptions = new() { WriteIndented = true };

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oranda-agy-rt-" + Guid.NewGuid().ToString("N"));

    public AiSystemCliAgyRoundTripTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // 임시 폴더 정리 실패는 무시한다.
        }
    }

    [AgyRoundTripFact("ORANDA_AI_E2E_AGY")]
    public async Task The_flow_works_against_a_fake_model_server_and_a_gate_judged_call_counts_as_a_success()
    {
        var decoyHome = Path.Combine(_dir, "home");
        Directory.CreateDirectory(decoyHome);
        using var server = new FakeModelServer(
        [
            ("call_mcp_tool", new JsonObject { ["ServerName"] = "orandactl", ["ToolName"] = "describe_object", ["Arguments"] = new JsonObject { ["object"] = "EMP" } }),
            ("finish", new JsonObject
            {
                ["summary"] = "Example summary: the statement reads EMP by DEPTNO.",
                ["rewrites"] = new JsonArray(),
                ["index_suggestions"] = new JsonArray(),
                ["evidence"] = new JsonArray(new JsonObject { ["tool"] = "describe_object", ["finding"] = "EMP has 1234 rows." }),
            }),
        ]);

        var flow = await RunFlowAsync(new FakeModelLauncher(server.BaseUrl, decoyHome), decoyHome);

        Assert.Equal("Example summary: the statement reads EMP by DEPTNO.", flow.Result.Summary);
    }

    [AgyRoundTripFact("ORANDA_AI_E2E_AGY_REAL")]
    public async Task Real_agy_uses_the_agent_tools_on_the_real_tuning_prompt_and_returns_a_result_that_the_tuning_flow_captures()
    {
        var flow = await RunFlowAsync(new SystemClaudeProcessLauncher(), home: null);

        Assert.False(string.IsNullOrWhiteSpace(flow.Result.Summary));
    }

    private sealed record Flow(TuningResult Result, List<AiEvent> Events);

    private async Task<Flow> RunFlowAsync(IClaudeProcessLauncher launcher, string? home)
    {
        var tokens = new AgentRunTokenStore();
        var gate = new RecordingGate();
        var unique = Guid.NewGuid().ToString("N");
        await using var host = new AgentBridgeHost(gate, tokens, (_, instance) => $"oa-{unique[..8]}" + (instance is null ? string.Empty : "-" + instance));
        host.Start();

        var driver = new AgyDriver(
            tokens,
            gate,
            host,
            new AppDataPaths(_dir),
            launcher,
            FindOrandactl(),
            () => Environment.GetEnvironmentVariable("PATH"),
            File.Exists,
            home,
            AgyDriver.RunTimeLimit,
            (executable, limit, ct) => SystemCliVersion.ReadAsync(executable, limit, AgyDriver.LaunchEnvironment(null), ct));

        var profile = new ConnectionProfile("p", "u", "h/s");
        var context = new AiContext("dev-example", null, AiAccessLevel.Plan);
        var request = new AiRequest(
            AiTuningFlow.BuildPrompt(new TuningStartChoice(Benchmark: false, IndexDdl: true, ModelId: "default"), SampleStatement),
            context,
            OutputSchema: TuningResult.Schema(),
            Session: new AgentSession(new AgentRunScope("e2e-agy-structured", profile, context)));

        // 실제 앱처럼 AiTuningFlow.Capture 를 거친다(비공개 정적 메서드).
        var capture = typeof(AiTuningFlow).GetMethod("Capture", BindingFlags.NonPublic | BindingFlags.Static)!;
        JsonElement? captured = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        var stream = (IAsyncEnumerable<AiEvent>)capture.Invoke(null, [driver.RunAsync(request, timeout.Token), (Action<JsonElement?>)(r => captured = r), CancellationToken.None])!;
        var events = new List<AiEvent>();
        await foreach (var e in stream)
        {
            events.Add(e);
        }

        if (Environment.GetEnvironmentVariable("ORANDA_AI_E2E_RECORD") is { Length: > 0 } recordPath)
        {
            File.WriteAllText(recordPath, JsonSerializer.Serialize(events.Select(e => new { type = e.GetType().Name, e = (object)e }), RecordOptions));
        }

        // 스트림 감시가 정상 실행을 닫지 않았고 all_tools_failed 로도 닫히지 않았다.
        Assert.DoesNotContain(events, e => e is AiError);

        // MCP 도구 호출이 1건 이상 성공했다: 화면 사건과 게이트 쪽 기록이 함께 증명한다.
        var oranda = AgentTools.All.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Contains(events, e => e is AiToolCall call && oranda.Contains(call.ToolName));
        Assert.Contains(events, e => e is AiToolResult { Judgement: AiToolJudgement.Allowed or AiToolJudgement.Masked });
        Assert.True(gate.Calls.Count >= 1, "게이트에 도구 호출이 한 건도 도착하지 않았다.");
        Assert.All(gate.Calls, call => Assert.Contains(call, oranda));

        // 최종 결과가 Capture 에 잡혔고 튜닝 결과로 파싱된다.
        Assert.NotNull(captured);
        Assert.Equal(JsonValueKind.Object, captured!.Value.ValueKind);
        return new Flow(TuningResult.Parse(captured.Value), events);
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

    /// <summary>잠금 인자는 그대로 두고 모델 제공자만 가짜 서버로 바꾼다(<c>modelProvider</c> 를 설정에 더하고 가짜 키·주소·미끼 HOME 환경을 준다).</summary>
    private sealed class FakeModelLauncher(string baseUrl, string home) : IClaudeProcessLauncher
    {
        private readonly SystemClaudeProcessLauncher _inner = new();

        public IClaudeProcess Start(ClaudeLaunch launch)
        {
            var arguments = launch.Arguments.ToList();
            var at = arguments.IndexOf("--settings") + 1;
            var settings = JsonNode.Parse(arguments[at])!.AsObject();
            settings["modelProvider"] = "gemini";
            arguments[at] = settings.ToJsonString();
            var environment = new Dictionary<string, string?>(launch.Environment ?? new Dictionary<string, string?>())
            {
                ["GEMINI_API_KEY"] = "probe-fake-key",
                ["GOOGLE_GEMINI_BASE_URL"] = baseUrl,
                ["HOME"] = home,
            };
            return _inner.Start(launch with { Arguments = arguments, Environment = environment });
        }
    }

    /// <summary>실제 <see cref="AgentTools"/> 도구 목록을 제공하고 호출을 기록하며, 게이트처럼 판단 사건(<c>ToolCompleted</c>)을 내는 대체 게이트(감사 기록·접속 없음).</summary>
    private sealed class RecordingGate : IAgentGate
    {
        private readonly List<string> _calls = [];

        public IReadOnlyList<string> Calls
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

    /// <summary>대본형 가짜 Gemini 서버. 도구 선언이 든 요청에는 대본의 functionCall 을 하나씩 돌려주고 끝나면 글로 끝낸다. 선언이 없는 요청(제목 생성)에는 짧은 글을 준다.</summary>
    private sealed class FakeModelServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly IReadOnlyList<(string Name, JsonObject Args)> _calls;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;
        private readonly object _gate = new();
        private int _step;

        public FakeModelServer(IReadOnlyList<(string Name, JsonObject Args)> calls)
        {
            _calls = calls;
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            BaseUrl = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Add(BaseUrl + "/");
            _listener.Start();
            _loop = Task.Run(LoopAsync);
        }

        public string BaseUrl { get; }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Close();
            try
            {
                _loop.Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException)
            {
                // 닫는 중의 종료 예외는 무시한다.
            }

            _stop.Dispose();
        }

        private async Task LoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
                {
                    return;
                }

                _ = Task.Run(() => HandleAsync(context));
            }
        }

        private async Task HandleAsync(HttpListenerContext context)
        {
            using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();
            JsonNode? request = null;
            try
            {
                request = JsonNode.Parse(body);
            }
            catch (JsonException)
            {
                // 본문이 JSON 이 아니면 선언 수집만 건너뛴다.
            }

            var declared = request?["tools"]?.AsArray().SelectMany(t => t?["functionDeclarations"]?.AsArray() ?? []).Count() ?? 0;
            JsonNode answer;
            lock (_gate)
            {
                if (declared == 0)
                {
                    answer = TextAnswer("Probe Title");
                }
                else if (_step < _calls.Count)
                {
                    var call = _calls[_step++];
                    answer = new JsonObject
                    {
                        ["candidates"] = new JsonArray(new JsonObject
                        {
                            ["content"] = new JsonObject
                            {
                                ["role"] = "model",
                                ["parts"] = new JsonArray(new JsonObject { ["functionCall"] = new JsonObject { ["name"] = call.Name, ["args"] = call.Args.DeepClone() } }),
                            },
                            ["finishReason"] = "STOP",
                        }),
                        ["usageMetadata"] = Usage(),
                    };
                }
                else
                {
                    answer = TextAnswer("probe-done");
                }
            }

            var json = answer.ToJsonString();
            var sse = context.Request.Url!.Query.Contains("alt=sse", StringComparison.Ordinal) || context.Request.Url.AbsolutePath.Contains("stream", StringComparison.OrdinalIgnoreCase);
            var payload = Encoding.UTF8.GetBytes(sse ? "data: " + json + "\n\n" : json);
            context.Response.StatusCode = 200;
            context.Response.ContentType = sse ? "text/event-stream" : "application/json";
            await context.Response.OutputStream.WriteAsync(payload);
            context.Response.Close();
        }

        private static JsonObject Usage() => new() { ["promptTokenCount"] = 1, ["candidatesTokenCount"] = 1, ["totalTokenCount"] = 2 };

        private static JsonObject TextAnswer(string text) => new()
        {
            ["candidates"] = new JsonArray(new JsonObject
            {
                ["content"] = new JsonObject { ["role"] = "model", ["parts"] = new JsonArray(new JsonObject { ["text"] = text }) },
                ["finishReason"] = "STOP",
            }),
            ["usageMetadata"] = Usage(),
        };
    }
}

/// <summary>지정한 환경 변수가 <c>1</c> 이 아니면 건너뛴다(실제 agy 실행·로그인 호출은 쿼터를 쓴다).</summary>
public sealed class AgyRoundTripFactAttribute : FactAttribute
{
    public AgyRoundTripFactAttribute(string environmentVariable)
    {
        if (Environment.GetEnvironmentVariable(environmentVariable) != "1")
        {
            Skip = environmentVariable + "=1 일 때만 실제 agy 를 부른다.";
        }
        else if (OperatingSystem.IsWindows())
        {
            Skip = "macOS 전용 시험이다(Windows 는 미검증).";
        }
    }
}
