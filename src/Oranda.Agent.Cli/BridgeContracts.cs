using System.Text.Json.Nodes;
using Oranda.Core.Ai.Protocol;

namespace Oranda.Agent.Cli;

/// <summary>앱과 주고받는 통로. 연결과 hello 는 첫 요청 때 한 번 맺고 이후 요청에 재사용한다.</summary>
internal interface IBridgeClient : IAsyncDisposable
{
    /// <summary>앱이 이 실행의 허용 수준으로 거른 도구 목록.</summary>
    Task<IReadOnlyList<AgentToolInfo>> ListToolsAsync(AgentClientInfo client, CancellationToken cancellationToken);

    /// <summary>도구 호출. 거부와 도구 실패는 <see cref="AgentCallToolResult.IsError"/> 로 온다.</summary>
    Task<AgentCallToolResult> CallToolAsync(AgentClientInfo client, string name, JsonNode? arguments, CancellationToken cancellationToken);
}

/// <summary>
/// 앱에 닿지 못했거나, 앱이 JSON-RPC 오류로 거절했거나, 붙을 창을 고르지 못했다. <see cref="Code"/> 는 계약의 오류 코드
/// (<see cref="AgentErrorCodes"/>)이고 <see cref="AgentCliExit.For"/> 로 종료 코드가 된다.
/// </summary>
internal sealed class BridgeException : Exception
{
    public const string NotRunning = AgentErrorCodes.NotRunning;

    public BridgeException()
        : this(AgentErrorCodes.BridgeError, AgentErrorCodes.BridgeError)
    {
    }

    public BridgeException(string message)
        : this(AgentErrorCodes.BridgeError, message)
    {
    }

    public BridgeException(string message, Exception innerException)
        : base(message, innerException)
    {
        Code = AgentErrorCodes.BridgeError;
    }

    public BridgeException(string code, string message, JsonObject? details = null)
        : base(message)
    {
        Code = code;
        Details = details;
    }

    public string Code { get; } = AgentErrorCodes.BridgeError;

    /// <summary>오류 JSON 에 더할 칸(<c>multiple_instances</c> 의 후보 목록 등). 없으면 null.</summary>
    public JsonObject? Details { get; }
}
