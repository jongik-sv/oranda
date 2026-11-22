using System.Text.Json.Nodes;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Protocol;
using Oranda.Core.Connection;
using Oranda.Core.Storage;

namespace Oranda.Agent.Cli.Tests;

/// <summary>앱 대신 답하는 가짜 통로. 받은 호출을 남기고 정해 둔 답을 준다.</summary>
internal sealed class FakeBridgeClient : IBridgeClient
{
    public List<(string Name, JsonNode? Arguments, AgentClientInfo Client)> Calls { get; } = [];

    public Func<string, JsonNode?, AgentCallToolResult> Respond { get; set; } = (_, _) => new AgentCallToolResult(false, "{\"ok\":true}");

    public Exception? Throw { get; set; }

    public IReadOnlyList<AgentToolInfo> Tools { get; set; } = [];

    public bool Disposed { get; private set; }

    public Task<IReadOnlyList<AgentToolInfo>> ListToolsAsync(AgentClientInfo client, CancellationToken cancellationToken) =>
        Throw is { } ex ? Task.FromException<IReadOnlyList<AgentToolInfo>>(ex) : Task.FromResult(Tools);

    public Task<AgentCallToolResult> CallToolAsync(AgentClientInfo client, string name, JsonNode? arguments, CancellationToken cancellationToken)
    {
        Calls.Add((name, arguments?.DeepClone(), client));
        return Throw is { } ex ? Task.FromException<AgentCallToolResult>(ex) : Task.FromResult(Respond(name, arguments));
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

/// <summary>임시 데이터 폴더(<c>ORANDA_DATA_DIR</c>)와 연결 파일 만들기.</summary>
internal sealed class TempDataDir : IDisposable
{
    public TempDataDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "orandactl-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(AppDataLocation.AgentsDirectory(Path));
    }

    public string Path { get; }

    public static DateTimeOffset Now { get; } = new(2026, 11, 22, 10, 0, 0, TimeSpan.FromHours(9));

    public AgentEndpointFile Write(int pid, string label = "기본 작업 공간", DateTimeOffset? expiresAt = null, string? token = null)
    {
        var file = new AgentEndpointFile(
            AgentEndpointFile.CurrentVersion,
            pid,
            Now.AddHours(-1),
            "0.1.32",
            label,
            new AgentEndpoint(AgentEndpoint.UnixKind, $"/tmp/agent-{pid}.sock"),
            token ?? $"token-{pid}",
            expiresAt ?? Now.AddHours(7),
            [new AgentEndpointConnection("dev-erp", ConnectionEnvironment.Development, AiAccessLevel.QueryRows)]);
        File.WriteAllText(AppDataLocation.EndpointFile(Path, pid), file.Serialize());
        return file;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>가짜 바깥으로 <see cref="CliApp"/> 을 돌린다.</summary>
internal sealed class CliHarness
{
    private readonly Dictionary<string, string?> _env = new(StringComparer.Ordinal);

    public CliHarness(string dataDir)
    {
        _env[AppDataLocation.DataDirEnvironmentVariable] = dataDir;
    }

    public StringWriter Out { get; } = new();

    public StringWriter Error { get; } = new();

    public string Stdin { get; set; } = string.Empty;

    public Dictionary<string, string> Files { get; } = [];

    public HashSet<int> AlivePids { get; } = [];

    public FakeBridgeClient Bridge { get; } = new();

    public List<BridgeTarget> Targets { get; } = [];

    public string? McpServerName { get; private set; }

    public string? this[string name]
    {
        get => _env.GetValueOrDefault(name);
        set => _env[name] = value;
    }

    public CliHost Host() => new()
    {
        Out = Out,
        Error = Error,
        ReadStdin = () => Stdin,
        ReadFile = path => Files.TryGetValue(path, out var text) ? text : throw new FileNotFoundException("missing", path),
        GetEnvironmentVariable = name => _env.GetValueOrDefault(name),
        Os = AppOs.Linux,
        IsProcessAlive = AlivePids.Contains,
        Now = () => TempDataDir.Now,
        CreateClient = target =>
        {
            Targets.Add(target);
            return Bridge;
        },
        RunMcp = async (bridge, name, token) =>
        {
            McpServerName = name;
            await bridge.ListToolsAsync(new AgentClientInfo("test", "1"), token);
            return 0;
        },
    };

    public Task<int> RunAsync(params string[] args) => CliApp.RunAsync(args, Host(), CancellationToken.None);

    /// <summary>stdout 의 JSON 한 줄.</summary>
    public JsonObject OutJson()
    {
        var text = Out.ToString();
        Assert.EndsWith("\n", text, StringComparison.Ordinal);
        Assert.Single(text.TrimEnd('\n').Split('\n'));
        return Assert.IsType<JsonObject>(JsonNode.Parse(text));
    }
}
