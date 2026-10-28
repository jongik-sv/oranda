using System.Text.Json;

namespace Oranda.Platform.Ai.Cli;

/// <summary>claude --output-format stream-json 한 줄에서 읽은 사건. 계약(AiEvent)과 무관한 내부 형태다.</summary>
internal abstract record ClaudeStreamEvent;

/// <summary>MCP 서버 한 개의 상태(<c>system/init</c> 의 <c>mcp_servers</c>).</summary>
internal sealed record ClaudeMcpServer(string Name, string Status);

/// <summary><paramref name="McpServers"/>·<paramref name="Tools"/> 는 claude 가 실제로 붙인 MCP 서버와 도구 이름이다(실행 검증용).</summary>
internal sealed record ClaudeSessionStarted(string SessionId, string? Model, IReadOnlyList<ClaudeMcpServer> McpServers, IReadOnlyList<string> Tools) : ClaudeStreamEvent;

internal sealed record ClaudeText(string Text) : ClaudeStreamEvent;

/// <summary>모델이 도구를 불렀다. <paramref name="ToolName"/> 은 <c>mcp__orandactl__</c> 접두사를 뗀 이름이다.</summary>
internal sealed record ClaudeToolUse(string CallId, string ToolName, string InputJson) : ClaudeStreamEvent;

/// <summary><paramref name="Size"/> 는 모델에게 돌려준 글자 수.</summary>
internal sealed record ClaudeToolResult(string CallId, int Size, bool IsError) : ClaudeStreamEvent;

internal sealed record ClaudePermissionDenied(string ToolName, string Message) : ClaudeStreamEvent;

internal sealed record ClaudeResult(
    bool IsError,
    string? SessionId,
    string? Text,
    JsonElement? StructuredOutput,
    decimal? CostUsd,
    long InputTokens,
    long OutputTokens,
    IReadOnlyList<string> PermissionDenials) : ClaudeStreamEvent;

/// <summary>
/// stream-json 줄을 한 줄씩 읽는다. 사용자에게 보일 도구 호출이 아닌 ToolSearch·StructuredOutput(합성 도구)과
/// 그 결과는 내보내지 않고, 모르는 줄·깨진 줄은 건너뛴다. 줄 사이에 상태(숨긴 호출 id)가 있어 실행마다 새로 만든다.
/// </summary>
internal sealed class ClaudeStreamParser
{
    private static readonly HashSet<string> HiddenTools = new(StringComparer.Ordinal) { "ToolSearch", "StructuredOutput" };

    private readonly HashSet<string> _hiddenCallIds = new(StringComparer.Ordinal);

    public IReadOnlyList<ClaudeStreamEvent> Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            return ParseRoot(document.RootElement);
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private List<ClaudeStreamEvent> ParseRoot(JsonElement root)
    {
        var events = new List<ClaudeStreamEvent>();
        switch (GetString(root, "type"))
        {
            case "system":
                ParseSystem(root, events);
                break;
            case "assistant":
                ParseAssistant(root, events);
                break;
            case "user":
                ParseUser(root, events);
                break;
            case "result":
                events.Add(ParseResult(root));
                break;
        }

        return events;
    }

    private static void ParseSystem(JsonElement root, List<ClaudeStreamEvent> events)
    {
        switch (GetString(root, "subtype"))
        {
            case "init" when GetString(root, "session_id") is { } sessionId:
                events.Add(new ClaudeSessionStarted(sessionId, GetString(root, "model"), ReadMcpServers(root), ReadTools(root)));
                break;
            case "permission_denied":
                events.Add(new ClaudePermissionDenied(
                    StripPrefix(GetString(root, "tool_name") ?? string.Empty),
                    GetString(root, "message") ?? string.Empty));
                break;
        }
    }

    private void ParseAssistant(JsonElement root, List<ClaudeStreamEvent> events)
    {
        foreach (var block in ContentBlocks(root))
        {
            switch (GetString(block, "type"))
            {
                case "text" when GetString(block, "text") is { Length: > 0 } text:
                    events.Add(new ClaudeText(text));
                    break;
                case "tool_use":
                    var callId = GetString(block, "id") ?? string.Empty;
                    var name = GetString(block, "name") ?? string.Empty;
                    if (HiddenTools.Contains(name))
                    {
                        _hiddenCallIds.Add(callId);
                        break;
                    }

                    var input = block.TryGetProperty("input", out var inputElement) ? inputElement.GetRawText() : "{}";
                    events.Add(new ClaudeToolUse(callId, StripPrefix(name), input));
                    break;
            }
        }
    }

    private void ParseUser(JsonElement root, List<ClaudeStreamEvent> events)
    {
        foreach (var block in ContentBlocks(root))
        {
            if (GetString(block, "type") != "tool_result")
            {
                continue;
            }

            var callId = GetString(block, "tool_use_id") ?? string.Empty;
            if (_hiddenCallIds.Contains(callId))
            {
                continue;
            }

            var isError = block.TryGetProperty("is_error", out var flag) && flag.ValueKind == JsonValueKind.True;
            events.Add(new ClaudeToolResult(callId, ContentLength(block), isError));
        }
    }

    private static ClaudeResult ParseResult(JsonElement root)
    {
        JsonElement? structured = root.TryGetProperty("structured_output", out var so) && so.ValueKind == JsonValueKind.Object
            ? so.Clone()
            : null;

        var denials = new List<string>();
        if (root.TryGetProperty("permission_denials", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            denials.AddRange(list.EnumerateArray().Select(d => StripPrefix(GetString(d, "tool_name") ?? string.Empty)));
        }

        decimal? cost = root.TryGetProperty("total_cost_usd", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetDecimal() : null;

        long input = 0;
        long output = 0;
        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            input = GetLong(usage, "input_tokens") + GetLong(usage, "cache_creation_input_tokens") + GetLong(usage, "cache_read_input_tokens");
            output = GetLong(usage, "output_tokens");
        }

        var isError = (root.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True)
            || GetString(root, "subtype") is { } subtype && subtype != "success";

        return new ClaudeResult(isError, GetString(root, "session_id"), GetString(root, "result"), structured, cost, input, output, denials);
    }

    private static List<ClaudeMcpServer> ReadMcpServers(JsonElement root)
    {
        var servers = new List<ClaudeMcpServer>();
        if (root.TryGetProperty("mcp_servers", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            servers.AddRange(list.EnumerateArray().Select(s => new ClaudeMcpServer(GetString(s, "name") ?? string.Empty, GetString(s, "status") ?? string.Empty)));
        }

        return servers;
    }

    private static List<string> ReadTools(JsonElement root)
    {
        var tools = new List<string>();
        if (root.TryGetProperty("tools", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            tools.AddRange(list.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.String).Select(t => t.GetString()!));
        }

        return tools;
    }

    private static List<JsonElement> ContentBlocks(JsonElement root) =>
        root.TryGetProperty("message", out var message)
        && message.TryGetProperty("content", out var content)
        && content.ValueKind == JsonValueKind.Array
            ? [.. content.EnumerateArray()]
            : [];

    private static int ContentLength(JsonElement toolResult)
    {
        if (!toolResult.TryGetProperty("content", out var content))
        {
            return 0;
        }

        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString()!.Length;
        }

        return content.ValueKind == JsonValueKind.Array
            ? content.EnumerateArray().Sum(part => GetString(part, "text")?.Length ?? 0)
            : 0;
    }

    private static string StripPrefix(string toolName) =>
        toolName.StartsWith(ClaudeCliArguments.McpToolPrefix, StringComparison.Ordinal)
            ? toolName[ClaudeCliArguments.McpToolPrefix.Length..]
            : toolName;

    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long GetLong(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt64() : 0;
}
