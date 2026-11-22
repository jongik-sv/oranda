using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Oranda.Core.Ai.Protocol;

namespace Oranda.Agent.Cli;

/// <summary>
/// stdio MCP 서버. 도구 목록과 호출을 모두 앱(<see cref="IBridgeClient"/>)에 맡긴다. 이 프로그램은 도구 스키마를 갖지 않는다.
/// stdout 은 MCP 전용이므로 진단은 stderr 로만 쓴다.
/// </summary>
internal static class McpServerHost
{
    /// <param name="bridge">앱 통로.</param>
    /// <param name="serverName">MCP 서버 이름. 밖에서 붙으면 <c>oranda</c>(E-20), 내장 실행은 <c>orandactl</c>.</param>
    /// <param name="cancellationToken">끝내기.</param>
    public static async Task<int> RunAsync(IBridgeClient bridge, string serverName, CancellationToken cancellationToken)
    {
        var options = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = serverName, Version = CliHost.Version },
            Handlers = new McpServerHandlers
            {
                ListToolsHandler = async (context, token) => new ListToolsResult
                {
                    Tools = await ListAsync(bridge, ClientOf(context.Server), token).ConfigureAwait(false),
                },
                CallToolHandler = async (context, token) =>
                {
                    var name = context.Params?.Name ?? string.Empty;
                    JsonNode? arguments = context.Params?.Arguments is { } dictionary
                        ? JsonSerializer.SerializeToNode(dictionary)
                        : null;
                    try
                    {
                        var result = await bridge.CallToolAsync(ClientOf(context.Server), name, arguments, token).ConfigureAwait(false);
                        return Text(result.IsError, result.Text);
                    }
                    catch (BridgeException ex)
                    {
                        // 하위 명령과 같은 오류 모양(계약 §4).
                        return Text(true, CliOutput.Json(new JsonObject { ["error"] = ex.Message, ["code"] = ex.Code }));
                    }
                },
            },
        };

        await using var transport = new StdioServerTransport(serverName);
        await using var server = McpServer.Create(transport, options);
        try
        {
            await server.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        return 0;
    }

    private static async Task<IList<Tool>> ListAsync(IBridgeClient bridge, AgentClientInfo client, CancellationToken token)
    {
        try
        {
            var tools = await bridge.ListToolsAsync(client, token).ConfigureAwait(false);
            return [.. tools.Select(t => new Tool
            {
                Name = t.Name,
                Description = t.Description,
                InputSchema = JsonSerializer.SerializeToElement(t.InputSchema),
            })];
        }
        catch (BridgeException ex)
        {
            // 빈 도구 목록으로 조용히 이어 가면 AI 가 도구 없이 지어낸 결과를 낸다. 오류로 알려 서버가 실패로 보이게 한다.
            await Console.Error.WriteLineAsync($"orandactl: {ex.Code}: {ex.Message}").ConfigureAwait(false);
            throw new McpException($"{ex.Code}: {ex.Message}", ex);
        }
    }

    private static CallToolResult Text(bool isError, string text) => new()
    {
        IsError = isError,
        Content = [new TextContentBlock { Text = text }],
    };

    private static AgentClientInfo ClientOf(McpServer server) =>
        new(server.ClientInfo?.Name ?? "unknown", server.ClientInfo?.Version ?? "0");
}
