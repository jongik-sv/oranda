using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Oranda.Core.Ai.Bridge;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Ai.Protocol;
using Oranda.Core.Composition;
using Oranda.Core.Storage;
using static Oranda.Core.Ai.Protocol.AgentRpc;

namespace Oranda.Platform.Ai.Bridge;

/// <summary>앱이 연 AI 다리의 파이프 이름. CliDriver 가 <c>orandactl mcp --pipe &lt;이름&gt;</c> 으로 넘긴다. 다리가 안 열렸으면 null.</summary>
internal interface IAgentBridgeEndpoint
{
    string? PipeName { get; }
}

/// <summary>
/// AI 다리(연구서 §4): 현재 사용자 전용 파이프로 <c>orandactl</c> 의 JSON-RPC 요청을 받아 <see cref="IAgentGate"/> 로 넘긴다.
/// 연결의 첫 메시지는 <c>oranda.hello</c> 이며 실행 토큰을 <see cref="AgentSession"/> 으로 바꿔 연결에 묶는다.
/// 그 전의 다른 메서드는 <see cref="ErrorCodes.HelloRequired"/>, 토큰이 없으면 <see cref="ErrorCodes.RunRequired"/>,
/// 모르거나 폐기된 토큰은 <see cref="ErrorCodes.TokenInvalid"/> 로 거절한다. 연결은 한 번에 하나, 요청은 차례로 처리한다.
/// </summary>
[OrandaService(typeof(IAgentBridgeHost))]
internal sealed class AgentBridgeHost : IAgentBridgeEndpoint, IAgentBridgeHost, IAsyncDisposable
{
    private static readonly string ServerVersion =
        typeof(AgentBridgeHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0";

    private readonly IAgentGate _gate;
    private readonly IAgentRunTokens _tokens;
    private readonly Func<string, int?, string> _nameOf;
    private readonly SemaphoreSlim _callGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private AgentPipeServer? _server;

    public AgentBridgeHost(IAgentGate gate, IAgentRunTokens tokens, IAppDataPaths paths)
        : this(gate, tokens, DefaultNameOf(paths))
    {
    }

    /// <summary>시험용: 파이프 이름 규칙을 지정한다.</summary>
    internal AgentBridgeHost(IAgentGate gate, IAgentRunTokens tokens, Func<string, int?, string> nameOf)
    {
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _nameOf = nameOf ?? throw new ArgumentNullException(nameof(nameOf));
    }

    /// <summary>다른 실행의 조회가 끝나기를 기다리는 한도. 넘으면 busy 결과를 돌려준다.</summary>
    internal TimeSpan CallQueueTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>hello 를 보내지 않은 연결을 기다리는 시간. 시험이 줄인다.</summary>
    internal TimeSpan HelloTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>수락 루프가 살아 있을 때만 이름을 돌려준다. 죽었으면 null 이라 CliDriver 가 bridge_not_ready 로 알린다.</summary>
    public string? PipeName => _server is { IsRunning: true } server ? server.PipeName : null;

    /// <summary>파이프를 연다. 이미 열었으면 아무 일도 하지 않는다. 열린 이름을 돌려준다.</summary>
    public string Start()
    {
        if (_server is { } existing)
        {
            return existing.PipeName;
        }

        var server = new AgentPipeServer(_nameOf, HandleConnectionAsync);
        server.Start();
        _server = server;
        return server.PipeName;
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        if (_server is not null)
        {
            await _server.DisposeAsync().ConfigureAwait(false);
        }

        _shutdown.Dispose();
        _callGate.Dispose();
    }

    /// <summary>Windows 는 사용자 SID 해시 이름, 그 밖은 데이터 폴더 아래 <c>run/agent.sock</c>.</summary>
    private static Func<string, int?, string> DefaultNameOf(IAppDataPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return OperatingSystem.IsWindows()
            ? (userKey, instance) => AgentPipeName.Windows(userKey, instance)
            : (_, instance) => AgentPipeName.Unix(paths.DataDirectory, instance);
    }

    /// <summary>연결 하나의 읽기 루프. 시험이 파이프 없이 부를 수 있도록 internal.</summary>
    internal async Task HandleConnectionAsync(AgentRpcChannel channel, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token);
        var token = linked.Token;
        AgentSession? session = null;

        // hello 를 보내지 않고 붙어만 있는 연결이 자리를 차지하지 못하게, hello 전의 읽기에는 시한을 둔다.
        using var beforeHello = CancellationTokenSource.CreateLinkedTokenSource(token);
        beforeHello.CancelAfter(HelloTimeout);

        while (true)
        {
            string? line;
            try
            {
                line = await channel.ReadLineAsync(session is null ? beforeHello.Token : token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                return; // hello 시한 초과. 연결을 닫는다.
            }

            if (line is null)
            {
                return;
            }

            var request = TryParse(line);
            if (request is null)
            {
                await channel.WriteAsync(Failure(null, ErrorCodes.ParseError, "parse error"), token).ConfigureAwait(false);
                continue;
            }

            if (!request.IsRequest)
            {
                continue; // 응답 모양 메시지는 이쪽이 받을 것이 없다.
            }

            AgentRpcMessage response;
            if (request.Method == Methods.Hello)
            {
                (response, session) = Hello(request, session);
            }
            else
            {
                response = await DispatchAsync(request, session, token).ConfigureAwait(false);
            }

            if (request.Id is not null)
            {
                await channel.WriteAsync(response, token).ConfigureAwait(false);
            }
        }
    }

    private (AgentRpcMessage Response, AgentSession? Session) Hello(AgentRpcMessage request, AgentSession? current)
    {
        if (_shutdown.IsCancellationRequested)
        {
            return (Failure(request.Id, ErrorCodes.ShuttingDown, "shutting down"), current);
        }

        if (current is not null)
        {
            return (Failure(request.Id, ErrorCodes.InvalidRequest, "hello already done"), current);
        }

        var hello = Deserialize(request.Params, AgentRpcJsonContext.Default.AgentHelloParams);
        if (hello is null)
        {
            return (Failure(request.Id, ErrorCodes.InvalidParams, "invalid hello params"), null);
        }

        if (hello.Protocol != ProtocolVersion)
        {
            return (Failure(request.Id, ErrorCodes.UnsupportedProtocol, $"unsupported protocol {hello.Protocol}"), null);
        }

        if (string.IsNullOrEmpty(hello.RunToken))
        {
            return (Failure(request.Id, ErrorCodes.RunRequired, "run token required"), null);
        }

        var session = _tokens.Resolve(hello.RunToken);
        if (session is null || session.IsRevoked)
        {
            return (Failure(request.Id, ErrorCodes.TokenInvalid, "run token invalid"), null);
        }

        session.Client = hello.Client?.Name;
        var result = new AgentHelloResult(session.RunId, ServerVersion);
        return (Success(request.Id, ToNode(result, AgentRpcJsonContext.Default.AgentHelloResult)), session);
    }

    private async Task<AgentRpcMessage> DispatchAsync(AgentRpcMessage request, AgentSession? session, CancellationToken ct)
    {
        if (_shutdown.IsCancellationRequested)
        {
            return Failure(request.Id, ErrorCodes.ShuttingDown, "shutting down");
        }

        if (session is null)
        {
            return Failure(request.Id, ErrorCodes.HelloRequired, "hello required");
        }

        if (session.IsRevoked)
        {
            return Failure(request.Id, ErrorCodes.TokenInvalid, "run token revoked");
        }

        if (request.Method is not (Methods.ListTools or Methods.CallTool))
        {
            return Failure(request.Id, ErrorCodes.MethodNotFound, "method not found");
        }

        AgentCallToolParams? call = null;
        if (request.Method == Methods.CallTool)
        {
            call = Deserialize(request.Params, AgentRpcJsonContext.Default.AgentCallToolParams);
            if (call is null || string.IsNullOrWhiteSpace(call.Name))
            {
                return Failure(request.Id, ErrorCodes.InvalidParams, "invalid callTool params");
            }
        }

        try
        {
            if (call is null)
            {
                // 목록은 DB 를 쓰지 않으므로 다른 실행의 긴 조회를 기다리지 않는다.
                var tools = new AgentListToolsResult(_gate.ListTools(session));
                return Success(request.Id, ToNode(tools, AgentRpcJsonContext.Default.AgentListToolsResult));
            }

            return await CallToolSerializedAsync(request.Id, session, call, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // 게이트·도구 내부 오류의 세부는 파이프 밖(AI)에 내보내지 않는다. 게이트 안의 자체 시간 초과(취소 예외)도 여기서 오류 응답이 된다.
            return Failure(request.Id, ErrorCodes.InternalError, "internal error");
        }
    }

    /// <summary>도구 실행은 연결이 여럿이어도 한 번에 하나만 한다(같은 DB 세션을 쓰는 조회가 겹치지 않게). 오래 기다리면 busy 결과를 돌려준다.</summary>
    private async Task<AgentRpcMessage> CallToolSerializedAsync(long? id, AgentSession session, AgentCallToolParams call, CancellationToken ct)
    {
        if (!await _callGate.WaitAsync(CallQueueTimeout, ct).ConfigureAwait(false))
        {
            return Success(id, ToNode(ErrorResult("busy", "다른 AI 요청의 조회가 끝나기를 기다리다 시간이 지났습니다. 잠시 뒤 다시 요청하세요."), AgentRpcJsonContext.Default.AgentCallToolResult));
        }

        try
        {
            var response = await _gate.CallToolAsync(session, call.Name, call.Arguments, ct).ConfigureAwait(false);
            var message = Success(id, ToNode(response.Result, AgentRpcJsonContext.Default.AgentCallToolResult));

            // 한 줄 한도는 직렬화한 실제 바이트로 잰다(따옴표·한글 이스케이프로 원문보다 커진다). 넘으면 연결을 끊는 대신 이유를 알려 준다.
            if (System.Text.Encoding.UTF8.GetByteCount(Serialize(message)) >= MaxMessageBytes - 1024)
            {
                message = Success(id, ToNode(ErrorResult("result_too_large", "결과가 너무 커서 돌려줄 수 없습니다. 범위를 좁혀 다시 요청하세요."), AgentRpcJsonContext.Default.AgentCallToolResult));
            }

            return message;
        }
        finally
        {
            _callGate.Release();
        }
    }

    private static AgentCallToolResult ErrorResult(string code, string message) =>
        new(true, JsonSerializer.Serialize(new Dictionary<string, string> { ["error"] = message, ["code"] = code }));

    private static T? Deserialize<T>(JsonNode? node, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info)
        where T : class
    {
        if (node is null)
        {
            return null;
        }

        try
        {
            return node.Deserialize(info);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
