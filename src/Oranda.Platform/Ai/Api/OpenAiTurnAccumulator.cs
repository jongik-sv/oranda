using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Oranda.Platform.Ai.Api;

/// <summary>응답이 알려 준 토큰 수. 서버가 usage 를 주지 않으면 <see cref="OpenAiTurn.Usage"/> 가 null 이다.</summary>
internal readonly record struct OpenAiUsage(long PromptTokens, long CompletionTokens);

/// <summary>모델이 부른 도구 한 개. <paramref name="ArgumentsText"/> 는 모델이 보낸 인자 글 그대로(깨진 JSON 일 수 있다).</summary>
internal sealed record OpenAiToolCall(string Id, string Name, string ArgumentsText);

/// <summary>응답 한 번(모델 호출 한 번)을 모은 결과.</summary>
/// <param name="Text">응답 글(<c>content</c> 조각을 이은 것).</param>
/// <param name="AssistantMessageJson">
/// 다음 요청에 되돌려 보낼 assistant 메시지(JSON 글). 받은 칸(<c>reasoning_content</c>·Gemini <c>extra_content</c> 서명 등)을 그대로 두고,
/// 스트림에서만 쓰는 <c>index</c> 와 빠진 <c>id</c>·<c>type</c> 만 채운다.
/// </param>
internal sealed record OpenAiTurn(
    string Text,
    IReadOnlyList<OpenAiToolCall> ToolCalls,
    string AssistantMessageJson,
    string? FinishReason,
    OpenAiUsage? Usage);

/// <summary>조각 하나를 반영한 결과. <paramref name="TextDelta"/> 는 화면에 낼 글 조각, <paramref name="Error"/> 는 서버가 조각으로 알린 오류 글이다.</summary>
internal readonly record struct OpenAiChunkResult(string? TextDelta, string? Error);

/// <summary>
/// 스트리밍 응답(<c>chat/completions</c> 의 <c>data:</c> 조각)을 한 번의 assistant 메시지로 모은다. HTTP 와 무관하다.
/// 조각 조립 규칙: <c>content</c>·<c>reasoning_content</c>·<c>function.arguments</c> 는 이어 붙이고, <c>role</c>·<c>id</c>·<c>type</c>·<c>function.name</c> 같은 나머지 글 칸은
/// 첫 비어 있지 않은 값을 쓴다(서버가 조각마다 <c>"role":"assistant"</c> 를 되풀이해도 값이 깨지지 않게). 모르는 객체 칸은 재귀로 합치고 null 은 건너뛴다.
/// <c>tool_calls</c> 는 <c>index</c> 로 짝짓고, 없으면 <c>id</c>, 그것도 없으면 직전 호출의 이어짐으로 본다.
/// </summary>
internal sealed class OpenAiTurnAccumulator
{
    // 조각 글을 이어 붙이는 칸. 그 밖의 글 칸은 서명처럼 한 번에 오거나 되풀이되는 값이라 첫 값을 유지한다.
    private static readonly HashSet<string> ConcatKeys = new(StringComparer.Ordinal)
    {
        "content", "reasoning_content", "reasoning", "thinking", "refusal", "arguments",
    };

    // 한글을 유니코드 이스케이프로 풀어 쓰면 크기가 6배가 되어 대화 크기 상한에 빨리 닿는다(요청 쪽 OpenAiRequestBuilder 와 같은 방침).
    private static readonly JsonSerializerOptions MessageJson = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly JsonObject _message = new();
    private readonly StringBuilder _text = new();
    private readonly List<ToolSlot> _slots = [];
    private string? _finishReason;
    private OpenAiUsage? _usage;

    /// <summary>지금까지 받은 조각 글자 수(서버가 끝없이 보내는 응답을 끊는 데 쓴다).</summary>
    public long Size { get; private set; }

    /// <summary>끝 사유(<c>finish_reason</c>)를 받았다. 끝 표시 <c>[DONE]</c> 없이 연결이 닫혀도 이것이 있으면 정상 종료로 본다.</summary>
    public bool HasFinishReason => _finishReason is not null;

    /// <summary>조각 하나(<c>data:</c> 뒤의 JSON)를 반영한다.</summary>
    public OpenAiChunkResult Apply(JsonElement chunk)
    {
        Size += chunk.GetRawText().Length;
        if (chunk.ValueKind != JsonValueKind.Object)
        {
            return default;
        }

        if (chunk.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
        {
            return new OpenAiChunkResult(null, ErrorText(error));
        }

        if (chunk.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            _usage = new OpenAiUsage(ReadLong(usage, "prompt_tokens"), ReadLong(usage, "completion_tokens"));
        }

        string? textDelta = null;
        if (chunk.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
        {
            foreach (var choice in choices.EnumerateArray())
            {
                if (choice.ValueKind != JsonValueKind.Object || (choice.TryGetProperty("index", out var index) && index.ValueKind == JsonValueKind.Number && index.TryGetInt32(out var choiceIndex) && choiceIndex != 0))
                {
                    continue;
                }

                if (choice.TryGetProperty("finish_reason", out var finish) && finish.ValueKind == JsonValueKind.String)
                {
                    _finishReason = finish.GetString();
                }

                // 스트림은 delta, 서버가 stream 을 무시하고 한 번에 보낸 응답은 message 다.
                var body = choice.TryGetProperty("delta", out var delta) ? delta : choice.TryGetProperty("message", out var message) ? message : default;
                if (body.ValueKind == JsonValueKind.Object)
                {
                    textDelta = ApplyDelta(body) ?? textDelta;
                }
            }
        }

        return new OpenAiChunkResult(textDelta, null);
    }

    /// <summary>모은 결과를 확정한다. 빠진 도구 호출 id 는 만들어 채운다.</summary>
    public OpenAiTurn Finish()
    {
        var calls = new List<OpenAiToolCall>(_slots.Count);
        var toolCallsJson = new JsonArray();
        foreach (var slot in _slots)
        {
            var node = slot.Node;
            var function = node["function"] as JsonObject ?? new JsonObject();
            var id = StringOf(node["id"]);
            if (id.Length == 0)
            {
                id = "call_" + Guid.NewGuid().ToString("N")[..16];
            }

            var name = StringOf(function["name"]);
            // 인자를 문자열 대신 JSON 객체로 보내는 서버가 있다: 버리지 않고 글로 굳힌다.
            var arguments = function["arguments"] is JsonObject argumentsObject ? argumentsObject.ToJsonString(MessageJson) : StringOf(function["arguments"]);
            node["id"] = id;
            node["type"] = "function";
            function["name"] = name;
            function["arguments"] = arguments;
            node["function"] = function;
            calls.Add(new OpenAiToolCall(id, name, arguments));
            toolCallsJson.Add(node);
        }

        // content 는 널 대신 빈 글로 둔다: 널을 받지 못하는 서버 템플릿이 있고, 빈 글은 OpenAI 규격도 받는다.
        _message["role"] = "assistant";
        _message["content"] = _text.ToString();
        if (toolCallsJson.Count > 0)
        {
            _message["tool_calls"] = toolCallsJson;
        }

        return new OpenAiTurn(_text.ToString(), calls, _message.ToJsonString(MessageJson), _finishReason, _usage);
    }

    private string? ApplyDelta(JsonElement delta)
    {
        string? textDelta = null;
        foreach (var property in delta.EnumerateObject())
        {
            switch (property.Name)
            {
                case "tool_calls":
                    if (property.Value.ValueKind == JsonValueKind.Array)
                    {
                        MergeToolCalls(property.Value);
                    }

                    break;
                case "content":
                    // 글 조각만 받는다(content 가 부분 배열인 서버는 글을 얻을 수 없으므로 건너뛴다).
                    if (property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() is { Length: > 0 } piece)
                    {
                        _text.Append(piece);
                        textDelta = (textDelta ?? string.Empty) + piece;
                    }

                    break;
                case "role":
                    break;
                default:
                    MergeInto(_message, property.Name, property.Value);
                    break;
            }
        }

        return textDelta;
    }

    private void MergeToolCalls(JsonElement calls)
    {
        foreach (var call in calls.EnumerateArray())
        {
            if (call.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            int? index = call.TryGetProperty("index", out var indexValue) && indexValue.ValueKind == JsonValueKind.Number && indexValue.TryGetInt32(out var parsed) ? parsed : null;
            var id = call.TryGetProperty("id", out var idValue) && idValue.ValueKind == JsonValueKind.String ? idValue.GetString() : null;
            var hasName = call.TryGetProperty("function", out var function) && function.ValueKind == JsonValueKind.Object
                && function.TryGetProperty("name", out var nameValue) && nameValue.ValueKind == JsonValueKind.String && nameValue.GetString() is { Length: > 0 };
            var slot = FindSlot(index, id, hasName);
            foreach (var property in call.EnumerateObject())
            {
                if (property.Name == "index")
                {
                    continue;
                }

                MergeInto(slot.Node, property.Name, property.Value);
            }
        }
    }

    private ToolSlot FindSlot(int? index, string? id, bool hasName)
    {
        ToolSlot? slot = null;
        if (index is { } i)
        {
            slot = _slots.FirstOrDefault(s => s.Index == i);
        }
        else if (!string.IsNullOrEmpty(id))
        {
            slot = _slots.FirstOrDefault(s => StringOf(s.Node["id"]) == id);
        }
        else if (_slots.Count > 0 && !hasName)
        {
            // index 도 id 도 없고 이름도 없는 조각은 직전 호출의 이어짐이다(이름이 있으면 새 호출이다).
            slot = _slots[^1];
        }

        if (slot is null)
        {
            slot = new ToolSlot(index, new JsonObject());
            _slots.Add(slot);
        }

        return slot;
    }

    private static void MergeInto(JsonObject target, string key, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return;
            case JsonValueKind.String:
                var text = value.GetString() ?? string.Empty;
                var existing = StringOf(target[key]);
                if (ConcatKeys.Contains(key))
                {
                    target[key] = existing + text;
                }
                else if (existing.Length == 0)
                {
                    target[key] = text;
                }

                return;
            case JsonValueKind.Object:
                if (target[key] is not JsonObject child)
                {
                    child = new JsonObject();
                    target[key] = child;
                }

                foreach (var property in value.EnumerateObject())
                {
                    MergeInto(child, property.Name, property.Value);
                }

                return;
            case JsonValueKind.Array:
                if (target[key] is not JsonArray array)
                {
                    array = [];
                    target[key] = array;
                }

                foreach (var item in value.EnumerateArray())
                {
                    array.Add(JsonNode.Parse(item.GetRawText()));
                }

                return;
            default:
                target[key] = JsonNode.Parse(value.GetRawText());
                return;
        }
    }

    private static string StringOf(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : string.Empty;

    private static long ReadLong(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : 0;

    private static string ErrorText(JsonElement error)
    {
        if (error.ValueKind == JsonValueKind.String)
        {
            return error.GetString() ?? string.Empty;
        }

        if (error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
        {
            return message.GetString() ?? string.Empty;
        }

        return error.GetRawText();
    }

    private sealed record ToolSlot(int? Index, JsonObject Node);
}
