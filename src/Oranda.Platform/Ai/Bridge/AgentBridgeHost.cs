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
/// 연결의 첫 메시지는 <c>oranda.hello</c> 이며 토큰을 연결에 묶는다: 앱이 띄운 AI 실행의 토큰이면 <see cref="AgentSession"/>,
/// 외부 연결(밖의 LLM CLI, agent-cli §3.2)의 토큰이면 허가(<see cref="AgentGrantView"/>)다.
/// 그 전의 다른 메서드는 <see cref="ErrorCodes.HelloRequired"/>, 토큰이 없으면 <see cref="ErrorCodes.RunRequired"/>,
/// 모르거나 폐기된 토큰은 <see cref="ErrorCodes.TokenInvalid"/>, 기한이 지난 외부 토큰은 <see cref="ErrorCodes.GrantExpired"/> 로 거절한다.
/// DB 도구는 앱 전체에서 한 번에 하나(<c>_callGate</c>), 외부 연결의 편집기·언어 도구는 그 대기열 밖에서 처리한다.
/// </summary>
[OrandaService(typeof(IAgentBridgeHost))]
internal sealed class AgentBridgeHost : IAgentBridgeEndpoint, IAgentBridgeHost, IAsyncDisposable
{
    private static readonly string ServerVersion =
        typeof(AgentBridgeHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0";

    private readonly IAgentGate _gate;
    private readonly IAgentRunTokens _tokens;
    private readonly Func<string, int?, string> _nameOf;
    private readonly IAgentGrantSessions? _grants;
    private readonly IAgentExternalGate? _external;
    private readonly string? _dataDirectory;
    private readonly SemaphoreSlim _callGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private AgentPipeServer? _server;

    public AgentBridgeHost(IAgentGate gate, IAgentRunTokens tokens, IAppDataPaths paths, IAgentGrantSessions? grants = null, IAgentExternalGate? external = null)
        : this(gate, tokens, DefaultNameOf(paths), grants, external, paths?.DataDirectory)
    {
    }

    /// <summary>
    /// 시험용: 파이프 이름 규칙을 지정한다. <paramref name="dataDirectory"/> 가 있으면 다리를 열 때 그 아래 <c>run/agents/</c> 에
    /// 외부 연결 파일을 쓰는 쪽을 허가 저장소에 붙인다(없으면 붙이지 않는다 — 시험이 직접 붙인다).
    /// </summary>
    internal AgentBridgeHost(
        IAgentGate gate,
        IAgentRunTokens tokens,
        Func<string, int?, string> nameOf,
        IAgentGrantSessions? grants = null,
        IAgentExternalGate? external = null,
        string? dataDirectory = null)
    {
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _nameOf = nameOf ?? throw new ArgumentNullException(nameof(nameOf));
        _grants = grants;
        _external = external ?? gate as IAgentExternalGate;
        _dataDirectory = dataDirectory;
    }

    /// <summary>다른 실행의 조회가 끝나기를 기다리는 한도. 넘으면 busy 결과를 돌려준다.</summary>
    internal TimeSpan CallQueueTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>hello 를 보내지 않은 연결을 기다리는 시간. 시험이 줄인다.</summary>
    internal TimeSpan HelloTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>hello·대기열 시한을 재는 시계. 시험은 손으로 돌리는 시계를 넣어 실제 시간·부하에 판정이 흔들리지 않게 한다.</summary>
    internal TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>수락 루프가 살아 있을 때만 이름을 돌려준다. 죽었으면 null 이라 CliDriver 가 bridge_not_ready 로 알린다.</summary>
    public string? PipeName => _server is { IsRunning: true } server ? server.PipeName : null;

    /// <summary>앱 버전(빌드 꼬리 <c>+커밋</c> 을 뺀 값).</summary>
    private static string AppVersion => ServerVersion.Split('+')[0];

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
        AttachEndpointFiles(server.PipeName);
        return server.PipeName;
    }

    public async ValueTask DisposeAsync()
    {
        // 앱이 끝나면 외부 허가도 끝난다: 토큰 폐기·연결 파일 삭제.
        if (_grants is not null)
        {
            _grants.RevokeAll();
            _grants.AttachWriter(null);
        }

        await _shutdown.CancelAsync().ConfigureAwait(false);
        if (_server is not null)
        {
            await _server.DisposeAsync().ConfigureAwait(false);
        }

        _shutdown.Dispose();
        _callGate.Dispose();
    }

    /// <summary>
    /// 외부 연결 파일(설계 §3.3, A4): 이 앱(pid)의 지난 파일과 죽은 앱의 파일을 지우고, 허가 저장소에 파일 쓰는 쪽을 붙인다.
    /// 청소·붙이기 실패는 다리를 막지 않는다(외부 연결만 못 켠다).
    /// </summary>
    private void AttachEndpointFiles(string pipeName)
    {
        if (_grants is null || _dataDirectory is null)
        {
            return;
        }

        try
        {
            AgentEndpointFiles.CleanStale(_dataDirectory, Environment.ProcessId, AgentEndpointFiles.IsProcessAlive);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 남의 파일을 못 지워도 orandactl 은 죽은 pid·붙지 못하는 파일을 건너뛴다.
        }

        var kind = Path.IsPathRooted(pipeName) ? AgentEndpoint.UnixKind : AgentEndpoint.PipeKind;
        _grants.AttachWriter(new AgentEndpointFiles(_dataDirectory, new AgentEndpoint(kind, pipeName), AppVersion, Environment.ProcessId));
    }

    /// <summary>
    /// Windows 는 사용자 SID 해시 이름, 그 밖은 데이터 폴더 아래 <c>run/agent.sock</c>. 소켓 경로가 sun_path 한도를 넘으면
    /// 임시 폴더 아래 짧은 경로(<see cref="AgentSocketPath"/>)를 쓴다. 밖의 <c>orandactl</c> 은 연결 파일에서 실제 경로를 읽는다.
    /// </summary>
    private static Func<string, int?, string> DefaultNameOf(IAppDataPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return OperatingSystem.IsWindows()
            ? (userKey, instance) => AgentPipeName.Windows(userKey, instance)
            : (userKey, instance) => AgentSocketPath.Choose(
                AgentPipeName.Unix(paths.DataDirectory, instance),
                Path.GetTempPath(),
                userKey,
                instance,
                AgentSocketPath.LimitFor(OperatingSystem.IsMacOS()));
    }

    /// <summary>연결 하나의 읽기 루프. 시험이 파이프 없이 부를 수 있도록 internal.</summary>
    internal async Task HandleConnectionAsync(AgentRpcChannel channel, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token);
        var token = linked.Token;
        Peer? peer = null;

        // hello 를 보내지 않고 붙어만 있는 연결이 자리를 차지하지 못하게, hello 전의 읽기에는 시한을 둔다. hello 가 끝나면 시한 타이머는 버린다.
        using var helloTimer = new CancellationTokenSource(HelloTimeout, Time);
        using var beforeHello = CancellationTokenSource.CreateLinkedTokenSource(token, helloTimer.Token);

        while (true)
        {
            string? line;
            try
            {
                line = await channel.ReadLineAsync(peer is null ? beforeHello.Token : token).ConfigureAwait(false);
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
                (response, peer) = Hello(request, peer);
                if (peer is not null)
                {
                    helloTimer.Dispose();
                }
            }
            else
            {
                response = await DispatchAsync(request, peer, token).ConfigureAwait(false);
            }

            if (request.Id is not null)
            {
                await channel.WriteAsync(response, token).ConfigureAwait(false);
            }
        }
    }

    private (AgentRpcMessage Response, Peer? Peer) Hello(AgentRpcMessage request, Peer? current)
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
        if (session is not null && !session.IsRevoked)
        {
            session.Client = hello.Client?.Name;
            var result = new AgentHelloResult(session.RunId, ServerVersion);
            return (Success(request.Id, ToNode(result, AgentRpcJsonContext.Default.AgentHelloResult)), new Peer(session, null, null));
        }

        // 앱이 띄운 실행의 토큰이 아니면 외부 허가 토큰인지 본다(설계 §3.2). 만료는 따로 알린다(연결 창에서 다시 켜거나 연장).
        if (_grants is not null && _external is not null)
        {
            var lookup = _grants.Lookup(hello.RunToken);
            if (lookup.Grant is { } grant)
            {
                var client = string.IsNullOrWhiteSpace(hello.Client?.Name) ? "unknown" : hello.Client.Name.Trim();
                _grants.AddClient(grant.GrantId, client);
                var result = new AgentHelloResult(grant.GrantId, ServerVersion);
                return (Success(request.Id, ToNode(result, AgentRpcJsonContext.Default.AgentHelloResult)), new Peer(null, grant.GrantId, client));
            }

            if (lookup.Expired)
            {
                return (Failure(request.Id, ErrorCodes.GrantExpired, "external connection expired"), null);
            }
        }

        return (Failure(request.Id, ErrorCodes.TokenInvalid, "run token invalid"), null);
    }

    private async Task<AgentRpcMessage> DispatchAsync(AgentRpcMessage request, Peer? peer, CancellationToken ct)
    {
        if (_shutdown.IsCancellationRequested)
        {
            return Failure(request.Id, ErrorCodes.ShuttingDown, "shutting down");
        }

        if (peer is null)
        {
            return Failure(request.Id, ErrorCodes.HelloRequired, "hello required");
        }

        var session = peer.Session;
        AgentGrantView? grant = null;
        if (session is not null)
        {
            if (session.IsRevoked)
            {
                return Failure(request.Id, ErrorCodes.TokenInvalid, "run token revoked");
            }
        }
        else
        {
            // 끊기·만료 뒤에는 이미 붙은 연결도 받지 않는다.
            grant = _grants?.Find(peer.GrantId!);
            if (grant is null)
            {
                return Failure(request.Id, ErrorCodes.TokenInvalid, "external connection ended");
            }
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
                var tools = new AgentListToolsResult(session is not null ? _gate.ListTools(session) : _external!.ListTools(grant!));
                return Success(request.Id, ToNode(tools, AgentRpcJsonContext.Default.AgentListToolsResult));
            }

            if (session is not null)
            {
                return await CallToolSerializedAsync(request.Id, () => _gate.CallToolAsync(session, call.Name, call.Arguments, ct), ct).ConfigureAwait(false);
            }

            var external = _external!;
            Task<AgentGateResponse> CallExternal() => external.CallToolAsync(grant!, peer.Client, call.Name, call.Arguments, ct);

            // 편집기·언어 도구는 DB 를 쓰지 않으므로 다른 연결의 긴 조회를 기다리지 않는다(propose_edit 의 기다림도 대기열을 잡지 않는다, §8.4).
            return external.RunsWithoutDatabase(call.Name)
                ? Limit(request.Id, await CallExternal().ConfigureAwait(false))
                : await CallToolSerializedAsync(request.Id, CallExternal, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // 게이트·도구 내부 오류의 세부는 파이프 밖(AI)에 내보내지 않는다. 게이트 안의 자체 시간 초과(취소 예외)도 여기서 오류 응답이 된다.
            return Failure(request.Id, ErrorCodes.InternalError, "internal error");
        }
    }

    /// <summary>DB 도구 실행은 연결이 여럿이어도 한 번에 하나만 한다(같은 DB 세션을 쓰는 조회가 겹치지 않게). 오래 기다리면 busy 결과를 돌려준다.</summary>
    private async Task<AgentRpcMessage> CallToolSerializedAsync(long? id, Func<Task<AgentGateResponse>> call, CancellationToken ct)
    {
        using (var queueTimer = new CancellationTokenSource(CallQueueTimeout, Time))
        using (var waiting = CancellationTokenSource.CreateLinkedTokenSource(ct, queueTimer.Token))
        {
            try
            {
                await _callGate.WaitAsync(waiting.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return Success(id, ToNode(ErrorResult("busy", "다른 AI 요청의 조회가 끝나기를 기다리다 시간이 지났습니다. 잠시 뒤 다시 요청하세요."), AgentRpcJsonContext.Default.AgentCallToolResult));
            }
        }

        try
        {
            return Limit(id, await call().ConfigureAwait(false));
        }
        finally
        {
            _callGate.Release();
        }
    }

    /// <summary>한 줄 한도는 직렬화한 실제 바이트로 잰다(따옴표·한글 이스케이프로 원문보다 커진다). 넘으면 연결을 끊는 대신 이유를 알려 준다.</summary>
    private static AgentRpcMessage Limit(long? id, AgentGateResponse response)
    {
        var message = Success(id, ToNode(response.Result, AgentRpcJsonContext.Default.AgentCallToolResult));
        if (System.Text.Encoding.UTF8.GetByteCount(Serialize(message)) >= MaxMessageBytes - 1024)
        {
            message = Success(id, ToNode(ErrorResult("result_too_large", "결과가 너무 커서 돌려줄 수 없습니다. 범위를 좁혀 다시 요청하세요."), AgentRpcJsonContext.Default.AgentCallToolResult));
        }

        return message;
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

    /// <summary>hello 를 마친 연결 하나: 앱이 띄운 실행(<see cref="Session"/>) 또는 외부 허가(<see cref="GrantId"/>·<see cref="Client"/>).</summary>
    private sealed record Peer(AgentSession? Session, string? GrantId, string? Client);
}
