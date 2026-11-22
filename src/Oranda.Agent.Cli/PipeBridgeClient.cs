using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Nodes;
using Oranda.Core.Ai.Protocol;
using static Oranda.Core.Ai.Protocol.AgentRpc;

namespace Oranda.Agent.Cli;

/// <summary>
/// 현재 사용자 전용 명명 파이프(Windows) 또는 Unix 도메인 소켓(macOS·Linux, 이름이 절대 경로)으로 앱(AgentBridgeHost)에 JSON-RPC 요청을 보낸다.
/// 연결 직후 <c>oranda.hello</c> 로 토큰(내장 실행 = <c>ORANDA_RUN_TOKEN</c>, 외부 연결 = 연결 파일의 토큰)을 내고,
/// 이후 <c>oranda.listTools</c>·<c>oranda.callTool</c> 을 차례로 보낸다.
/// 연결이 끊기면 다음 요청에서 다시 맺고 hello 를 다시 한다(토큰은 앱이 폐기하기 전까지 유효하다).
/// </summary>
internal sealed class PipeBridgeClient(string pipeName, string runToken) : IBridgeClient
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private NamedPipeClientStream? _pipe;
    private AgentRpcChannel? _channel;
    private long _nextId;

    public async Task<IReadOnlyList<AgentToolInfo>> ListToolsAsync(AgentClientInfo client, CancellationToken cancellationToken)
    {
        var result = await RequestAsync(client, Methods.ListTools, null, cancellationToken).ConfigureAwait(false);
        var tools = result.Deserialize(AgentRpcJsonContext.Default.AgentListToolsResult)
            ?? throw new BridgeException(CliText.BadResponse);
        return tools.Tools;
    }

    public async Task<AgentCallToolResult> CallToolAsync(AgentClientInfo client, string name, JsonNode? arguments, CancellationToken cancellationToken)
    {
        var parameters = ToNode(new AgentCallToolParams(name, arguments), AgentRpcJsonContext.Default.AgentCallToolParams);
        var result = await RequestAsync(client, Methods.CallTool, parameters, cancellationToken).ConfigureAwait(false);
        return result.Deserialize(AgentRpcJsonContext.Default.AgentCallToolResult)
            ?? throw new BridgeException(CliText.BadResponse);
    }

    public ValueTask DisposeAsync()
    {
        Reset();
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }

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
                throw new BridgeException(BridgeException.NotRunning, CliText.NotRunning);
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

        // Unix 는 CurrentUserOnly 가 소켓 상대(서버)의 uid 가 나와 같은지 본다. 다른 사용자의 서버면 UnauthorizedAccessException.
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync((int)ConnectTimeout.TotalMilliseconds, cancellationToken).ConfigureAwait(false);
        }
        catch (System.Net.Sockets.SocketException ex) when (ex.SocketErrorCode == System.Net.Sockets.SocketError.AccessDenied)
        {
            // connect 의 EPERM/EACCES: 소켓은 있는데 샌드박스(Codex read-only·workspace-write 실측)나 폴더 권한이 막았다. 앱이 꺼진 것이 아니다.
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw new BridgeException(AgentErrorCodes.SandboxBlocked, CliText.SandboxBlocked);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException or System.Net.Sockets.SocketException)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw new BridgeException(BridgeException.NotRunning, CliText.NotRunning);
        }
        catch (ArgumentException)
        {
            // 소켓 경로가 sun_path 한도(Linux 108·macOS 104바이트)를 넘었다. 앱은 이 경로로 열 수 없으므로 붙을 곳이 없다.
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw new BridgeException(AgentErrorCodes.BridgeError, CliText.SocketPathTooLong);
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
                throw new BridgeException(ErrorCodes.ToErrorCode(error.Code), error.Message);
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
