using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Oranda.Core.Ai.Protocol;

namespace Oranda.Agent.Cli;

/// <summary>
/// <c>orandactl</c> 본체: 명령줄 해석 → 붙을 창 고르기 → 도구 호출 → 출력·종료 코드(설계 §4, 계약 §4).
/// stdout = 결과 JSON 한 개(<c>--text</c> 면 글), stderr = 진단. 오류도 stdout 에 <c>{"error":…,"code":…}</c> 로 낸다(<c>--text</c> 면 stderr).
/// </summary>
internal static class CliApp
{
    /// <summary>밖에서 붙을 때 MCP 서버 이름(E-20). 내장 실행은 지금처럼 <c>orandactl</c>.</summary>
    public const string ExternalMcpServerName = "oranda";

    public const string BuiltInMcpServerName = "orandactl";

    public static async Task<int> RunAsync(string[] args, CliHost host, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(host);
        var text = args.Contains("--text", StringComparer.Ordinal);
        try
        {
            var command = CommandParser.Parse(args, host.ReadFile, host.ReadStdin);
            text = command.Globals.Text;
            switch (command.Kind)
            {
                case CommandKind.Help:
                    await host.Out.WriteAsync(CommandParser.HelpText()).ConfigureAwait(false);
                    return AgentCliExit.Success;
                case CommandKind.StatusAll:
                    await WriteAsync(host, text, StatusAll(host)).ConfigureAwait(false);
                    return AgentCliExit.Success;
                case CommandKind.Mcp:
                    return await RunMcpAsync(command.Globals, host, cancellationToken).ConfigureAwait(false);
            }

            await using var bridge = Bridge(command.Globals, host);
            if (command.Kind == CommandKind.Tools)
            {
                var tools = await bridge.ListToolsAsync(host.Client(), cancellationToken).ConfigureAwait(false);
                await WriteAsync(host, text, ToolList(tools, text)).ConfigureAwait(false);
                return AgentCliExit.Success;
            }

            var result = await bridge.CallToolAsync(host.Client(), command.Tool!, command.Arguments, cancellationToken).ConfigureAwait(false);
            return await WriteResultAsync(host, text, result).ConfigureAwait(false);
        }
        catch (BridgeException ex)
        {
            return await WriteErrorAsync(host, text, ex.Code, ex.Message, ex.Details).ConfigureAwait(false);
        }
    }

    /// <summary>MCP: 붙을 곳은 요청 때 정한다 — 앱이 꺼져 있어도 서버는 뜨고, <c>tools/list</c> 가 오류로 알린다(설계 §4.3).</summary>
    private static async Task<int> RunMcpAsync(GlobalOptions globals, CliHost host, CancellationToken cancellationToken)
    {
        var external = string.IsNullOrEmpty(host.GetEnvironmentVariable(AgentRunToken.EnvironmentVariable));
        await using var bridge = Bridge(globals, host);
        return await host.RunMcp(bridge, external ? ExternalMcpServerName : BuiltInMcpServerName, cancellationToken).ConfigureAwait(false);
    }

    private static DiscoveringBridgeClient Bridge(GlobalOptions globals, CliHost host) => new(
        () => BridgeTargetResolver.Resolve(globals.Pipe, globals.Instance, host.GetEnvironmentVariable, host.Os, host.Locator()),
        host.CreateClient);

    private static async Task<int> WriteResultAsync(CliHost host, bool text, AgentCallToolResult result)
    {
        var node = result.Structured?.DeepClone() ?? TryParse(result.Text) ?? new JsonObject { ["text"] = result.Text };
        if (!result.IsError)
        {
            await WriteAsync(host, text, node).ConfigureAwait(false);
            return AgentCliExit.Success;
        }

        var code = (node as JsonObject)?["code"]?.GetValueKind() == JsonValueKind.String ? (string?)node["code"] : null;
        var message = (node as JsonObject)?["error"]?.GetValueKind() == JsonValueKind.String ? (string?)node["error"] : result.Text;
        if (text)
        {
            await host.Error.WriteLineAsync(ErrorLine(code, message)).ConfigureAwait(false);
        }
        else
        {
            await host.Out.WriteLineAsync(CliOutput.Json(node)).ConfigureAwait(false);
        }

        return AgentCliExit.For(code);
    }

    private static async Task<int> WriteErrorAsync(CliHost host, bool text, string code, string message, JsonObject? details)
    {
        if (text)
        {
            await host.Error.WriteLineAsync(ErrorLine(code, message)).ConfigureAwait(false);
            if (details is not null)
            {
                await host.Error.WriteAsync(CliOutput.Text(details)).ConfigureAwait(false);
            }
        }
        else
        {
            var error = new JsonObject { ["error"] = message, ["code"] = code };
            foreach (var (key, value) in details ?? [])
            {
                error[key] = value?.DeepClone();
            }

            await host.Out.WriteLineAsync(CliOutput.Json(error)).ConfigureAwait(false);
        }

        if (code == AgentErrorCodes.Usage)
        {
            await host.Error.WriteLineAsync("Run 'orandactl help' for the command list.").ConfigureAwait(false);
        }

        return AgentCliExit.For(code);
    }

    private static string ErrorLine(string? code, string? message) => $"orandactl: {code ?? "error"}: {message}";

    private static Task WriteAsync(CliHost host, bool text, JsonNode node) =>
        text ? host.Out.WriteAsync(CliOutput.Text(node)) : host.Out.WriteLineAsync(CliOutput.Json(node));

    private static JsonObject ToolList(IReadOnlyList<AgentToolInfo> tools, bool text) => new()
    {
        ["tools"] = new JsonArray([.. tools.Select(t =>
        {
            var item = new JsonObject { ["name"] = t.Name, ["description"] = t.Description };
            if (!text)
            {
                item["input_schema"] = t.InputSchema.DeepClone();
            }

            return (JsonNode)item;
        })]),
    };

    /// <summary><c>status --all</c>: 모든 연결 파일(토큰 없음). 앱에 붙지 않는다.</summary>
    private static JsonObject StatusAll(CliHost host)
    {
        var locator = host.Locator();
        return new JsonObject
        {
            ["agents_dir"] = locator.AgentsDirectory,
            ["instances"] = new JsonArray([.. locator.Scan().Select(c =>
            {
                var item = c.Summary();
                item["app_version"] = c.File.AppVersion;
                item["expires_at"] = c.File.ExpiresAt?.ToString("O", CultureInfo.InvariantCulture);
                item["alive"] = c.Alive;
                item["expired"] = c.Expired;
                item["endpoint"] = new JsonObject { ["kind"] = c.File.Endpoint.Kind, ["name"] = c.File.Endpoint.Name };
                item["connections"] = new JsonArray([.. c.File.Connections.Select(x => (JsonNode)new JsonObject
                {
                    ["alias"] = x.Alias,
                    ["environment"] = x.Environment.ToString(),
                    ["level"] = x.Level.ToString(),
                })]);
                return (JsonNode)item;
            })]),
        };
    }

    private static JsonNode? TryParse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
