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

/// <summary>앱에 닿지 못했거나 앱이 JSON-RPC 오류로 거절했다. <see cref="Code"/> 는 AI 에게 보일 짧은 구분 값이다.</summary>
internal sealed class BridgeException : Exception
{
    public const string NotRunning = "oranda_not_running";

    public BridgeException()
        : this("bridge_error", "bridge_error")
    {
    }

    public BridgeException(string message)
        : this("bridge_error", message)
    {
    }

    public BridgeException(string message, Exception innerException)
        : base(message, innerException)
    {
        Code = "bridge_error";
    }

    public BridgeException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; } = "bridge_error";
}
