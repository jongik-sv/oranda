using System.Text.Json;

namespace Oranda.Platform.Ai.Cli;

internal abstract record CodexEvent;

internal sealed record CodexThreadStarted(string ThreadId) : CodexEvent;

internal sealed record CodexMessage(string Text) : CodexEvent;

internal sealed record CodexToolStarted(string CallId, string Tool, string ArgumentsJson) : CodexEvent;

internal sealed record CodexToolDone(string CallId, string Tool, bool IsError, int Size) : CodexEvent;

internal sealed record CodexTurnCompleted(long InputTokens, long OutputTokens) : CodexEvent;

internal sealed record CodexTurnFailed(string Message) : CodexEvent;

/// <summary>허용 목록 밖 항목(낯선 item.type, orandactl 이 아닌 MCP 서버). 보이는 즉시 실행을 끝낸다.</summary>
internal sealed record CodexUnexpectedItem(string Description) : CodexEvent;

/// <summary>
/// <c>codex exec --json</c> 한 줄 해석. 스트림 감시(마지막 방어선)를 겸한다: item 은 <c>started</c>·<c>updated</c>·<c>completed</c> 어느 쪽이든
/// 처음 보이는 순간 검사한다(completed 까지 기다리면 명령이 이미 돈 뒤다). 허용: <c>agent_message</c>·<c>reasoning</c>·<c>error</c>·
/// <c>mcp_tool_call</c>(server=orandactl). 최상위 <c>{"type":"error"}</c> 는 재연결 보고이므로 실패가 아니다 — 실패는 <c>turn.failed</c> 뿐이다.
/// </summary>
internal sealed class CodexStreamParser
{
    private readonly HashSet<string> _announced = new(StringComparer.Ordinal);

    public IReadOnlyList<CodexEvent> Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return [];
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return []; // JSON 이 아닌 줄(경고 글)은 무시한다.
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String)
            {
                return [];
            }

            switch (typeElement.GetString())
            {
                case "thread.started" when String(root, "thread_id") is { Length: > 0 } threadId:
                    return [new CodexThreadStarted(threadId)];
                case "turn.completed":
                    return [Usage(root)];
                case "turn.failed":
                    return [new CodexTurnFailed(FailureMessage(root))];
                case "item.started" or "item.updated" or "item.completed":
                    return ParseItem(typeElement.GetString()!, root);
                case "turn.started" or "error":
                    return []; // 중간 error 는 재연결 보고이므로 실패가 아니다
                default:
                    // 모르는 최상위 이벤트는 감시를 통과시키지 않는다(앞으로 생길 도구 이벤트가 열려 있는 채 지나가지 않게).
                    return [new CodexUnexpectedItem($"이벤트 {typeElement.GetString()}")];
            }
        }
    }

    private List<CodexEvent> ParseItem(string kind, JsonElement root)
    {
        if (!root.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object || String(item, "type") is not { Length: > 0 } itemType)
        {
            return [new CodexUnexpectedItem("item.type 이 없는 항목")];
        }

        switch (itemType)
        {
            case "agent_message":
                return kind == "item.completed" && String(item, "text") is { } text ? [new CodexMessage(text)] : [];
            case "reasoning":
                return [];
            case "error":
                // Codex 가 잠금 설정을 인식하지 못해 무시했다는 경고는 도구가 되살아났을 수 있다는 뜻이다. 일반 오류 항목(재연결 등)은 통과한다.
                return IgnoredSettingNotice(item) is { } notice ? [new CodexUnexpectedItem($"설정 무시 경고: {notice}")] : [];
            case "mcp_tool_call":
                return ParseToolCall(kind, item);
            default:
                return [new CodexUnexpectedItem(itemType)];
        }
    }

    private static string? IgnoredSettingNotice(JsonElement item)
    {
        var text = String(item, "message") ?? String(item, "text");
        return text is not null && text.Contains("unrecognized configuration", StringComparison.OrdinalIgnoreCase) ? text : null;
    }

    private List<CodexEvent> ParseToolCall(string kind, JsonElement item)
    {
        var server = String(item, "server");
        var tool = String(item, "tool") ?? string.Empty;
        if (!string.Equals(server, CodexArguments.McpServerName, StringComparison.Ordinal))
        {
            return [new CodexUnexpectedItem($"MCP 서버 {server ?? "(없음)"}")];
        }

        var id = String(item, "id") ?? tool;
        var events = new List<CodexEvent>();
        if (_announced.Add(id))
        {
            events.Add(new CodexToolStarted(id, tool, item.TryGetProperty("arguments", out var args) ? args.GetRawText() : "{}"));
        }

        if (kind == "item.completed")
        {
            var failed = String(item, "status") == "failed" || (item.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object);
            events.Add(new CodexToolDone(id, tool, failed, ResultSize(item)));
        }

        return events;
    }

    private static int ResultSize(JsonElement item)
    {
        var size = 0;
        if (item.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object
            && result.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var part in content.EnumerateArray())
            {
                size += String(part, "text")?.Length ?? 0;
            }
        }
        else if (item.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            size = String(error, "message")?.Length ?? 0;
        }

        return size;
    }

    private static CodexTurnCompleted Usage(JsonElement root)
    {
        long input = 0, output = 0;
        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            input = Number(usage, "input_tokens");
            output = Number(usage, "output_tokens");
        }

        return new CodexTurnCompleted(input, output);
    }

    private static string FailureMessage(JsonElement root) =>
        root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object && String(error, "message") is { Length: > 0 } message
            ? message
            : "Codex 실행 오류";

    private static long Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : 0;

    private static string? String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
