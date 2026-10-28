using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Ai.Protocol;
using Oranda.Core.Connection;
using Oranda.Platform.Ai.Bridge;
using static Oranda.Core.Ai.Protocol.AgentRpc;

namespace Oranda.Platform.Tests.Ai.Bridge;

public sealed class AgentBridgeHostTests : IAsyncDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly FakeRunTokens _tokens = new();
    private readonly FakeGate _gate = new();
    private readonly AgentBridgeHost _host;
    private readonly string _pipeName;

    public AgentBridgeHostTests()
    {
        var unique = Guid.NewGuid().ToString("N");
        _host = new AgentBridgeHost(_gate, _tokens, (_, instance) => $"oranda-bridge-test-{unique}" + (instance is null ? string.Empty : "-" + instance))
        {
            HelloTimeout = TimeSpan.FromMilliseconds(600),
            CallQueueTimeout = TimeSpan.FromMilliseconds(700),
        };
        _pipeName = _host.Start();
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task Method_before_hello_is_rejected()
    {
        await using var connection = await ConnectAsync();

        var reply = await connection.CallAsync(Methods.ListTools, null);

        Assert.Equal(ErrorCodes.HelloRequired, reply.Error!.Code);
    }

    [Theory]
    [InlineData("", ErrorCodes.RunRequired)]
    [InlineData("unknown-token", ErrorCodes.TokenInvalid)]
    public async Task Hello_with_missing_or_unknown_token_is_rejected(string token, int expectedCode)
    {
        await using var connection = await ConnectAsync();

        var reply = await connection.HelloAsync(token);

        Assert.Equal(expectedCode, reply.Error!.Code);
    }

    [Fact]
    public async Task Hello_with_unsupported_protocol_is_rejected()
    {
        var token = _tokens.Issue(NewSession("run-1"));
        await using var connection = await ConnectAsync();

        var reply = await connection.HelloAsync(token, protocol: 99);

        Assert.Equal(ErrorCodes.UnsupportedProtocol, reply.Error!.Code);
    }

    [Fact]
    public async Task Hello_binds_the_token_to_the_connection_and_records_the_client()
    {
        var session = NewSession("run-7");
        var token = _tokens.Issue(session);
        await using var connection = await ConnectAsync();

        var hello = await connection.HelloAsync(token);

        Assert.Equal("run-7", hello.Result!["runId"]!.GetValue<string>());
        Assert.Equal("claude-code", session.Client);

        var tools = await connection.CallAsync(Methods.ListTools, null);
        Assert.Equal("describe_object", tools.Result!["tools"]![0]!["name"]!.GetValue<string>());

        var call = await connection.CallAsync(Methods.CallTool, new JsonObject
        {
            ["name"] = "describe_object",
            ["arguments"] = new JsonObject { ["object"] = "EMP" },
        });
        Assert.False(call.Result!["isError"]?.GetValue<bool>() ?? false);
        Assert.Equal("{\"ok\":true}", call.Result["text"]!.GetValue<string>());
        Assert.Equal(("describe_object", "EMP"), (_gate.LastTool, _gate.LastArguments!["object"]!.GetValue<string>()));
        Assert.Same(session, _gate.LastSession);
    }

    [Fact]
    public async Task Second_hello_on_the_same_connection_is_rejected()
    {
        var token = _tokens.Issue(NewSession("run-1"));
        await using var connection = await ConnectAsync();
        await connection.HelloAsync(token);

        var reply = await connection.HelloAsync(token);

        Assert.Equal(ErrorCodes.InvalidRequest, reply.Error!.Code);
    }

    [Fact]
    public async Task Revoked_session_is_rejected_on_an_already_open_connection()
    {
        var token = _tokens.Issue(NewSession("run-1"));
        await using var connection = await ConnectAsync();
        await connection.HelloAsync(token);

        _tokens.Revoke(token);
        var reply = await connection.CallAsync(Methods.CallTool, new JsonObject { ["name"] = "describe_object" });

        Assert.Equal(ErrorCodes.TokenInvalid, reply.Error!.Code);
        Assert.Null(_gate.LastTool);
    }

    [Fact]
    public async Task Unknown_method_bad_params_and_garbage_get_error_codes_without_closing()
    {
        var token = _tokens.Issue(NewSession("run-1"));
        await using var connection = await ConnectAsync();
        await connection.HelloAsync(token);

        Assert.Equal(ErrorCodes.MethodNotFound, (await connection.CallAsync("oranda.nope", null)).Error!.Code);
        Assert.Equal(ErrorCodes.InvalidParams, (await connection.CallAsync(Methods.CallTool, new JsonObject())).Error!.Code);

        await connection.Channel.WriteRawAsync("this is not json");
        var parse = await connection.ReadAsync();
        Assert.Equal(ErrorCodes.ParseError, parse!.Error!.Code);

        Assert.Null((await connection.CallAsync(Methods.ListTools, null)).Error);
    }

    [Fact]
    public async Task Gate_exception_becomes_a_generic_internal_error()
    {
        var token = _tokens.Issue(NewSession("run-1"));
        _gate.Throw = true;
        await using var connection = await ConnectAsync();
        await connection.HelloAsync(token);

        var reply = await connection.CallAsync(Methods.CallTool, new JsonObject { ["name"] = "describe_object" });

        Assert.Equal(ErrorCodes.InternalError, reply.Error!.Code);
        Assert.DoesNotContain("secret detail", reply.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Gate_cancellation_that_is_not_ours_becomes_an_internal_error_and_the_server_keeps_running()
    {
        var token = _tokens.Issue(NewSession("run-1"));
        _gate.ThrowCancel = true;
        await using (var connection = await ConnectAsync())
        {
            await connection.HelloAsync(token);
            var reply = await connection.CallAsync(Methods.CallTool, new JsonObject { ["name"] = "describe_object" });
            Assert.Equal(ErrorCodes.InternalError, reply.Error!.Code);
        }

        _gate.ThrowCancel = false;
        await using var next = await ConnectAsync();
        Assert.Null((await next.HelloAsync(_tokens.Issue(NewSession("run-2")))).Error);
        Assert.NotNull(_host.PipeName);
    }

    [Fact]
    public async Task Result_over_the_line_limit_becomes_an_error_result_instead_of_closing_the_connection()
    {
        var token = _tokens.Issue(NewSession("run-1"));
        _gate.HugeResult = true;
        await using var connection = await ConnectAsync();
        await connection.HelloAsync(token);

        var reply = await connection.CallAsync(Methods.CallTool, new JsonObject { ["name"] = "describe_object" });

        Assert.True(reply.Result!["isError"]!.GetValue<bool>());
        Assert.Contains("result_too_large", reply.Result["text"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Null((await connection.CallAsync(Methods.ListTools, null)).Error);
    }

    [Fact]
    public async Task Escaped_size_not_raw_size_decides_whether_a_result_is_too_large()
    {
        var token = _tokens.Issue(NewSession("run-1"));
        _gate.QuoteResult = true;
        await using var connection = await ConnectAsync();
        await connection.HelloAsync(token);

        var reply = await connection.CallAsync(Methods.CallTool, new JsonObject { ["name"] = "describe_object" });

        Assert.Contains("result_too_large", reply.Result!["text"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tool_list_is_not_blocked_by_another_runs_slow_call_and_a_queued_call_gets_busy()
    {
        _gate.Block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var slow = await ConnectAsync();
        await using var other = await ConnectAsync();
        await slow.HelloAsync(_tokens.Issue(NewSession("run-slow")));
        await other.HelloAsync(_tokens.Issue(NewSession("run-other")));

        var slowCall = slow.CallAsync(Methods.CallTool, new JsonObject { ["name"] = "describe_object" });
        await Task.Delay(200);

        Assert.Null((await other.CallAsync(Methods.ListTools, null)).Error);
        var queued = await other.CallAsync(Methods.CallTool, new JsonObject { ["name"] = "describe_object" });
        Assert.Contains("busy", queued.Result!["text"]!.GetValue<string>(), StringComparison.Ordinal);

        _gate.Block.SetResult();
        Assert.False((await slowCall).Result!["isError"]?.GetValue<bool>() ?? false);
    }

    [Fact]
    public async Task Connection_without_hello_is_closed_after_the_timeout_and_does_not_block_others()
    {
        await using var idle = await ConnectAsync();
        await using var other = await ConnectAsync();

        // 두 번째 연결은 첫 연결이 붙들고 있어도 바로 처리된다.
        Assert.Null((await other.HelloAsync(_tokens.Issue(NewSession("run-1")))).Error);

        Assert.Null(await idle.ReadAsync());
    }

    [Fact]
    public async Task Two_runs_can_use_the_bridge_at_the_same_time()
    {
        await using var first = await ConnectAsync();
        await using var second = await ConnectAsync();
        await first.HelloAsync(_tokens.Issue(NewSession("run-a")));
        await second.HelloAsync(_tokens.Issue(NewSession("run-b")));

        Assert.Null((await first.CallAsync(Methods.ListTools, null)).Error);
        Assert.Null((await second.CallAsync(Methods.ListTools, null)).Error);
        Assert.Null((await first.CallAsync(Methods.ListTools, null)).Error);
    }

    private static AgentSession NewSession(string runId) =>
        new(new AgentRunScope(runId, new ConnectionProfile("p", "u", "h/s"), new AiContext("p", null, AiAccessLevel.Plan)));

    private async Task<Connection> ConnectAsync()
    {
        var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var cts = new CancellationTokenSource(Timeout);
        await client.ConnectAsync(cts.Token);
        return new Connection(client);
    }

    private sealed class Connection(NamedPipeClientStream client) : IAsyncDisposable
    {
        private long _id;

        public ConnectionChannel Channel { get; } = new(client);

        public async Task<AgentRpcMessage> CallAsync(string method, JsonNode? parameters)
        {
            await Channel.Inner.WriteAsync(Request(Interlocked.Increment(ref _id), method, parameters), CancellationToken.None);
            return (await ReadAsync())!;
        }

        public Task<AgentRpcMessage> HelloAsync(string token, int protocol = ProtocolVersion) =>
            CallAsync(Methods.Hello, new JsonObject
            {
                ["protocol"] = protocol,
                ["runToken"] = token,
                ["client"] = new JsonObject { ["name"] = "claude-code", ["version"] = "2.1" },
            });

        public async Task<AgentRpcMessage?> ReadAsync()
        {
            using var cts = new CancellationTokenSource(Timeout);
            var line = await Channel.Inner.ReadLineAsync(cts.Token);
            return line is null ? null : TryParse(line);
        }

        public async ValueTask DisposeAsync()
        {
            Channel.Inner.Dispose();
            await client.DisposeAsync();
        }
    }

    private sealed class ConnectionChannel(Stream stream)
    {
        public AgentRpcChannel Inner { get; } = new(stream);

        public async Task WriteRawAsync(string line)
        {
            await stream.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"));
            await stream.FlushAsync();
        }
    }
}
