using System.Text.Json;
using System.Text.Json.Nodes;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Ai.Protocol;
using Oranda.Core.Connection;
using Oranda.Core.Settings;
using Oranda.Core.Storage;
using Oranda.Platform.Ai.Bridge;
using Oranda.Platform.Ai.Cli;
using Oranda.Platform.Storage;

namespace Oranda.Platform.Tests.Ai.Cli;

/// <summary>
/// 실제 claude 를 한 번 부르는 끝까지 확인(구독 한도를 쓰므로 기본은 건너뜀). 환경 변수 <c>ORANDA_AI_E2E=1</c> 일 때만 돈다.
/// 가짜 게이트 + 진짜 AgentBridgeHost + 진짜 orandactl(<c>src/Oranda.Agent.Cli</c> 빌드 출력) + 진짜 claude.
/// </summary>
public sealed class ClaudeEndToEndTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oranda-e2e-" + Guid.NewGuid().ToString("N"));

    private static readonly JsonSerializerOptions RecordOptions = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public ClaudeEndToEndTests() => Directory.CreateDirectory(_dir);

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

    [EndToEndFact]
    public async Task Real_claude_calls_a_tool_through_orandactl_and_the_bridge_and_returns_structured_output()
    {
        var orandactl = FindOrandactl();
        var tokens = new FakeRunTokens();
        var gate = new RecordingGate();
        var unique = Guid.NewGuid().ToString("N");
        await using var host = new AgentBridgeHost(gate, tokens, (_, instance) => $"oranda-e2e-{unique}" + (instance is null ? string.Empty : "-" + instance));
        host.Start();

        var driver = new CliDriver(
            tokens,
            gate,
            host,
            new AppDataPaths(_dir),
            new FixedSettings(new AppSettings { Ai = new AiSettings { Model = "haiku" } }),
            new SystemClaudeProcessLauncher(),
            orandactl,
            () => Environment.GetEnvironmentVariable("PATH"),
            File.Exists);

        var profile = new ConnectionProfile("p", "u", "h/s");
        var context = new AiContext("p", null, AiAccessLevel.Plan);
        var schema = JsonDocument.Parse("""{"type":"object","properties":{"rows":{"type":"integer"}},"required":["rows"]}""").RootElement.Clone();
        var request = new AiRequest(
            "Call the describe_object tool exactly once with object set to EMP. Then answer with the num_rows value it returned in the rows field.",
            context,
            OutputSchema: schema,
            Session: new AgentSession(new AgentRunScope("e2e-run", profile, context)));

        var events = new List<AiEvent>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await foreach (var e in driver.RunAsync(request, timeout.Token))
        {
            events.Add(e);
        }

        if (Environment.GetEnvironmentVariable("ORANDA_AI_E2E_RECORD") is { Length: > 0 } recordPath)
        {
            // ai-ui 의 가짜 IAiDriver 입력용 녹화: 사건 목록을 형식 이름과 함께 JSON 으로 남긴다.
            File.WriteAllText(recordPath, JsonSerializer.Serialize(events.Select(e => new { type = e.GetType().Name, e = (object)e }), RecordOptions));
        }

        Assert.DoesNotContain(events, e => e is AiError);
        Assert.Contains(events, e => e is AiToolCall { ToolName: "describe_object" });
        Assert.Equal("describe_object", gate.LastTool);
        Assert.Equal("EMP", gate.LastArguments!["object"]!.GetValue<string>());
        Assert.False(string.IsNullOrWhiteSpace(gate.LastClient));
        var final = Assert.IsType<AiFinal>(events[^1]);
        Assert.Equal(1234, final.Result!.Value.GetProperty("rows").GetInt32());
        Assert.Equal(tokens.Issued, tokens.Revoked);
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

    private sealed class FixedSettings(AppSettings settings) : ISettingsStore
    {
        public Task<StoredDocument<AppSettings>> LoadAsync(CancellationToken ct) => Task.FromResult(new StoredDocument<AppSettings>(settings));

        public Task SaveAsync(AppSettings value, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class RecordingGate : IAgentGate
    {
        public string? LastTool { get; private set; }

        public JsonNode? LastArguments { get; private set; }

        public string? LastClient { get; private set; }

        public event EventHandler<AgentToolActivityEventArgs>? ToolCompleted;

        public IReadOnlyList<AgentToolInfo> ListTools(AgentSession session) =>
        [
            new AgentToolInfo(
                "describe_object",
                "Describes a table or view: returns its row count (num_rows) and column list.",
                JsonNode.Parse("""{"type":"object","properties":{"object":{"type":"string"}},"required":["object"]}""")!),
        ];

        public Task<AgentGateResponse> CallToolAsync(AgentSession session, string toolName, JsonNode? arguments, CancellationToken ct)
        {
            (LastTool, LastArguments, LastClient) = (toolName, arguments?.DeepClone(), session.Client);
            const string text = """{"object":"EMP","num_rows":1234,"columns":["EMPNO","ENAME"]}""";
            ToolCompleted?.Invoke(this, new AgentToolActivityEventArgs(session.RunId, session.NextSequence(), toolName, AiToolJudgement.Allowed, text.Length, null));
            return Task.FromResult(new AgentGateResponse(new AgentCallToolResult(false, text), AiToolJudgement.Allowed));
        }
    }
}

/// <summary>환경 변수 <c>ORANDA_AI_E2E=1</c> 이 없으면 건너뛴다(실제 claude 호출은 구독 한도를 쓴다).</summary>
public sealed class EndToEndFactAttribute : FactAttribute
{
    public EndToEndFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("ORANDA_AI_E2E") != "1")
        {
            Skip = "ORANDA_AI_E2E=1 일 때만 실제 claude 를 부른다.";
        }
    }
}
