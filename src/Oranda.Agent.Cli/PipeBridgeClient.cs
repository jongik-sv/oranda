using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Nodes;
using Oranda.Core.Ai.Protocol;
using static Oranda.Core.Ai.Protocol.AgentRpc;

namespace Oranda.Agent.Cli;

/// <summary>
/// 현재 사용자 전용 명명 파이프로 앱(AgentBridgeHost)에 JSON-RPC 요청을 보낸다. 연결 직후 <c>oranda.hello</c> 로
/// 환경 변수 <c>ORANDA_RUN_TOKEN</c> 의 실행 토큰을 내고, 이후 <c>oranda.listTools</c>·<c>oranda.callTool</c> 을 차례로 보낸다.
/// 연결이 끊기면 다음 요청에서 다시 맺고 hello 를 다시 한다(토큰은 앱이 폐기하기 전까지 유효하다).
/// </summary>
internal sealed class PipeBridgeClient(string pipeName, string runToken) : IBridgeClient
{
    private const string NotRunningText = "Oranda 앱이 실행 중이 아니거나 AI 튜닝 연결이 열려 있지 않습니다.";

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private NamedPipeClientStream? _pipe;
    private AgentRpcChannel? _channel;
    private long _nextId;

    public async Task<IReadOnlyList<AgentToolInfo>> ListToolsAsync(AgentClientInfo client, CancellationToken cancellationToken)
    {
        var result = await RequestAsync(client, Methods.ListTools, null, cancellationToken).ConfigureAwait(false);
        var tools = result.Deserialize(AgentRpcJsonContext.Default.AgentListToolsResult)
            ?? throw new BridgeException("listTools 응답을 읽지 못했습니다.");
        return tools.Tools;
    }

    public async Task<AgentCallToolResult> CallToolAsync(AgentClientInfo client, string name, JsonNode? arguments, CancellationToken cancellationToken)
    {
        var parameters = ToNode(new AgentCallToolParams(name, arguments), AgentRpcJsonContext.Default.AgentCallToolParams);
        var result = await RequestAsync(client, Methods.CallTool, parameters, cancellationToken).ConfigureAwait(false);
        return result.Deserialize(AgentRpcJsonContext.Default.AgentCallToolResult)
            ?? throw new BridgeException("callTool 응답을 읽지 못했습니다.");
    }

    public ValueTask DisposeAsync()
    {
        Reset();
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }

    private static string CodeOf(int code) => code switch
    {
        ErrorCodes.TokenInvalid => "oranda_run_invalid",
        ErrorCodes.RunRequired => "run_required",
        ErrorCodes.HelloRequired => "hello_required",
        ErrorCodes.ShuttingDown => "oranda_shutting_down",
        ErrorCodes.UnsupportedProtocol => "unsupported_protocol",
        _ => "bridge_error",
    };

    private async Task<JsonNode> RequestAsync(AgentClientInfo client, string method, JsonNode? parameters, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                var channel = await EnsureConnectedAsync(client, cancellationToken).ConfigureAwait(false);
                return await SendAsync(channel, method, parameters, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ObjectDisposedException)
            {
                // 끊긴 연결은 버린다. 다음 요청이 다시 맺는다.
                Reset();
                throw new BridgeException(BridgeException.NotRunning, NotRunningText);
            }
            catch (OperationCanceledException)
            {
                // 쓰기·읽기 도중 취소되면 줄 틀이 깨졌을 수 있으므로 이 연결을 다시 쓰지 않는다.
                Reset();
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<AgentRpcChannel> EnsureConnectedAsync(AgentClientInfo client, CancellationToken cancellationToken)
    {
        if (_channel is { } existing)
        {
            return existing;
        }

        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync((int)ConnectTimeout.TotalMilliseconds, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw new BridgeException(BridgeException.NotRunning, NotRunningText);
        }

        var channel = new AgentRpcChannel(pipe);
        try
        {
            var hello = new AgentHelloParams(ProtocolVersion, runToken, client);
            await SendAsync(channel, Methods.Hello, ToNode(hello, AgentRpcJsonContext.Default.AgentHelloParams), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            channel.Dispose();
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        (_pipe, _channel) = (pipe, channel);
        return channel;
    }

    private async Task<JsonNode> SendAsync(AgentRpcChannel channel, string method, JsonNode? parameters, CancellationToken cancellationToken)
    {
        var id = ++_nextId;
        await channel.WriteAsync(Request(id, method, parameters), cancellationToken).ConfigureAwait(false);
        while (await channel.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            var message = TryParse(line);
            if (message is null || message.Id != id)
            {
                continue;
            }

            if (message.Error is { } error)
            {
                throw new BridgeException(CodeOf(error.Code), error.Message);
            }

            return message.Result ?? new JsonObject();
        }

        throw new IOException("연결이 닫혔습니다.");
    }

    private void Reset()
    {
        _channel?.Dispose();
        _pipe?.Dispose();
        (_channel, _pipe) = (null, null);
    }
}
