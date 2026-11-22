using System.IO.Pipes;
using System.Text.Json.Nodes;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Ai.Protocol;
using Oranda.Core.Ai.Schemas;
using Oranda.Core.Connection;
using Oranda.Core.Storage;
using Oranda.Platform.Ai.Bridge;
using Oranda.Testing;
using static Oranda.Core.Ai.Protocol.AgentRpc;

namespace Oranda.Platform.Tests.Ai.Bridge;

/// <summary>다리의 외부 연결(A4): hello 허가 해석·앱 도구 갈래(대기열 밖)·연결 파일 쓰기/고치기/지우기·끝난 허가 거부.</summary>
public sealed class AgentBridgeExternalTests : IAsyncDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly TempDirectory _data = new("oranda-ext-");
    private readonly ManualTimeProvider _time = new();
    private readonly AgentGrantStore _grants;
    private readonly FakeExternalGate _external = new();
    private readonly AgentBridgeHost _host;
    private readonly string _pipeName;

    public AgentBridgeExternalTests()
    {
        Directory.CreateDirectory(_data);
        _grants = new AgentGrantStore(_time);
        var unique = Guid.NewGuid().ToString("N")[..12];
        _host = new AgentBridgeHost(new NullGate(), new AgentRunTokenStore(), (_, i) => $"oranda-ext-test-{unique}" + (i is null ? string.Empty : "-" + i), _grants, _external, _data)
        {
            HelloTimeout = TimeSpan.FromSeconds(30),
            CallQueueTimeout = TimeSpan.FromSeconds(30),
        };
        _pipeName = _host.Start();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        _data.Dispose();
    }

    private string FilePath => AppDataLocation.EndpointFile(_data, Environment.ProcessId);

    private AgentGrantIssue Create(TimeSpan? lifetime = null) =>
        _grants.Create(new AgentGrantRequest(
            [new AgentGrantTarget(new ConnectionProfile("dev-erp", "u", "h/s", Id: Guid.NewGuid(), AiAccess: AiAccessLevel.QueryRows), AiAccessLevel.QueryRows)],
            lifetime,
            Label: "기본 작업 공간"));

    [Fact]
    public void Turning_on_writes_the_endpoint_file_with_the_pipe_and_token()
    {
        var issue = Create(TimeSpan.FromHours(8));

        var file = AgentEndpointFile.TryParse(File.ReadAllText(FilePath))!;
        Assert.Equal(Environment.ProcessId, file.Pid);
        Assert.Equal(issue.Token, file.Token);
        Assert.Equal(_pipeName, file.Endpoint.Name);
        Assert.Equal(AgentEndpoint.PipeKind, file.Endpoint.Kind); // 시험 이름은 절대 경로가 아니다
        Assert.Equal("기본 작업 공간", file.Label);
        Assert.Equal(new AgentEndpointConnection("dev-erp", ConnectionEnvironment.Development, AiAccessLevel.QueryRows), Assert.Single(file.Connections));
        Assert.Equal(issue.Grant.ExpiresAt, file.ExpiresAt);
        Assert.False(File.Exists(FilePath + ".tmp"));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(FilePath));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Path.GetDirectoryName(FilePath)!));
        }
    }

    [Fact]
    public void Extending_rewrites_the_expiry_and_disconnecting_deletes_the_file()
    {
        var issue = Create(TimeSpan.FromHours(8));
        _time.Advance(TimeSpan.FromHours(1));
        _grants.Extend(issue.Grant.GrantId, TimeSpan.FromHours(8));

        var file = AgentEndpointFile.TryParse(File.ReadAllText(FilePath))!;
        Assert.Equal(_time.GetUtcNow() + TimeSpan.FromHours(8), file.ExpiresAt);
        Assert.Equal(issue.Token, file.Token);

        _grants.Revoke(issue.Grant.GrantId);
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public void Expiry_deletes_the_file()
    {
        Create(TimeSpan.FromHours(8));
        _time.Advance(TimeSpan.FromHours(8));
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public async Task Closing_the_bridge_revokes_the_grant_and_deletes_the_file()
    {
        var issue = Create();
        await _host.DisposeAsync();
        Assert.False(File.Exists(FilePath));
        Assert.Null(_grants.Resolve(issue.Token));
    }

    [Fact]
    public void Starting_cleans_files_of_dead_processes_and_of_this_pid_but_keeps_others()
    {
        var directory = AppDataLocation.AgentsDirectory(_data);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "111.json"), "{}");
        File.WriteAllText(Path.Combine(directory, "222.json"), "{}");
        File.WriteAllText(Path.Combine(directory, "333.json"), "{}");
        File.WriteAllText(Path.Combine(directory, "333.json.tmp"), "{}");
        File.WriteAllText(Path.Combine(directory, "notes.txt"), "x");

        var removed = AgentEndpointFiles.CleanStale(_data, 333, pid => pid == 222);

        Assert.Equal(3, removed);
        Assert.Equal(["222.json", "notes.txt"], Directory.GetFiles(directory).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.True(AgentEndpointFiles.IsProcessAlive(Environment.ProcessId));
        Assert.Equal(0, AgentEndpointFiles.CleanStale(Path.Combine(_data, "none"), 1, _ => true));
    }

    [Fact]
    public async Task Grant_token_hello_lists_external_tools_and_records_the_client()
    {
        var issue = Create();
        await using var connection = await ConnectAsync();

        var hello = await connection.HelloAsync(issue.Token, "codex");

        Assert.Equal(issue.Grant.GrantId, hello.Result!["runId"]!.GetValue<string>());
        Assert.Equal(["codex"], _grants.Find(issue.Grant.GrantId)!.Clients);
        var tools = await connection.CallAsync(Methods.ListTools, null);
        Assert.Equal(AppToolNames.ListTabs, tools.Result!["tools"]![0]!["name"]!.GetValue<string>());

        var call = await connection.CallAsync(Methods.CallTool, new JsonObject { ["name"] = AppToolNames.ListTabs });
        Assert.Equal("{\"ok\":true}", call.Result!["text"]!.GetValue<string>());
        Assert.Equal(("codex", AppToolNames.ListTabs), (_external.LastClient, _external.LastTool));
    }

    [Fact]
    public async Task App_tools_do_not_wait_behind_a_slow_database_tool()
    {
        var issue = Create();
        _external.Block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var slow = await ConnectAsync();
        await using var other = await ConnectAsync();
        await slow.HelloAsync(issue.Token, "claude-code");
        await other.HelloAsync(issue.Token, "codex");

        var slowCall = slow.CallAsync(Methods.CallTool, new JsonObject { ["name"] = AppToolNames.Query });
        await _external.Entered.Task.WaitAsync(Timeout);

        // 편집기 도구는 DB 대기열을 잡지 않는다.
        var quick = await other.CallAsync(Methods.CallTool, new JsonObject { ["name"] = AppToolNames.ProposeEdit }).WaitAsync(Timeout);
        Assert.Equal("{\"ok\":true}", quick.Result!["text"]!.GetValue<string>());

        _external.Block.SetResult();
        Assert.NotNull((await slowCall.WaitAsync(Timeout)).Result);
    }

    [Fact]
    public async Task Ended_grant_is_refused_on_an_open_connection_and_expired_token_gets_its_own_code()
    {
        var issue = Create(TimeSpan.FromHours(1));
        await using var connection = await ConnectAsync();
        await connection.HelloAsync(issue.Token, "codex");

        _time.Advance(TimeSpan.FromHours(1));

        Assert.Equal(ErrorCodes.TokenInvalid, (await connection.CallAsync(Methods.ListTools, null)).Error!.Code);
        await using var late = await ConnectAsync();
        var hello = await late.HelloAsync(issue.Token, "codex");
        Assert.Equal(ErrorCodes.GrantExpired, hello.Error!.Code);
        Assert.Equal(AgentErrorCodes.GrantExpired, ErrorCodes.ToErrorCode(hello.Error.Code));

        var revoked = Create();
        _grants.Revoke(revoked.Grant.GrantId);
        await using var third = await ConnectAsync();
        Assert.Equal(ErrorCodes.TokenInvalid, (await third.HelloAsync(revoked.Token, "codex")).Error!.Code);
    }

    private async Task<Connection> ConnectAsync()
    {
        var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var cts = new CancellationTokenSource(Timeout);
        await client.ConnectAsync(cts.Token);
        return new Connection(client);
    }

    private sealed class Connection(NamedPipeClientStream client) : IAsyncDisposable
    {
        private readonly AgentRpcChannel _channel = new(client);
        private long _id;

        public async Task<AgentRpcMessage> CallAsync(string method, JsonNode? parameters)
        {
            await _channel.WriteAsync(Request(Interlocked.Increment(ref _id), method, parameters), CancellationToken.None);
            using var cts = new CancellationTokenSource(Timeout);
            return TryParse((await _channel.ReadLineAsync(cts.Token))!)!;
        }

        public Task<AgentRpcMessage> HelloAsync(string token, string client) =>
            CallAsync(Methods.Hello, new JsonObject
            {
                ["protocol"] = ProtocolVersion,
                ["runToken"] = token,
                ["client"] = new JsonObject { ["name"] = client, ["version"] = "1" },
            });

        public async ValueTask DisposeAsync()
        {
            _channel.Dispose();
            await client.DisposeAsync();
        }
    }

    private sealed class NullGate : IAgentGate
    {
        public event EventHandler<AgentToolActivityEventArgs>? ToolCompleted
        {
            add { }
            remove { }
        }

        public IReadOnlyList<AgentToolInfo> ListTools(AgentSession session) => [];

        public Task<AgentGateResponse> CallToolAsync(AgentSession session, string toolName, JsonNode? arguments, CancellationToken ct) =>
            throw new InvalidOperationException("not used");
    }

    private sealed class FakeExternalGate : IAgentExternalGate
    {
        public event EventHandler<AgentExternalToolActivity>? ExternalToolCompleted
        {
            add { }
            remove { }
        }

        public TaskCompletionSource? Block { get; set; }

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string? LastClient { get; private set; }

        public string? LastTool { get; private set; }

        public IReadOnlyList<AgentToolInfo> ListTools(AgentGrantView grant) => [AgentToolInfo.From(AppTools.Find(AppToolNames.ListTabs)!)];

        public bool RunsWithoutDatabase(string toolName) => AppTools.Find(toolName) is { Host: not AgentToolHost.Db };

        public async Task<AgentGateResponse> CallToolAsync(AgentGrantView grant, string? client, string toolName, JsonNode? arguments, CancellationToken ct)
        {
            LastClient = client;
            LastTool = toolName;
            if (!RunsWithoutDatabase(toolName) && Block is { } block)
            {
                Entered.TrySetResult();
                await block.Task.WaitAsync(ct);
            }

            return new AgentGateResponse(new AgentCallToolResult(false, "{\"ok\":true}"), AiToolJudgement.Allowed);
        }
    }
}
