using System.Text.Json;

namespace Oranda.Platform.Ai.Cli;

/// <summary>agy stream-json 한 줄을 옮긴 사건.</summary>
internal abstract record AgyStreamEvent;

/// <summary>첫 줄 <c>init</c>. 대화 id 만 쓴다: <c>init.tools</c> 는 잠금을 반영하지 않아(60개가 그대로 나온다) 자가 검증에 쓰지 않는다.</summary>
internal sealed record AgySessionStarted(string ConversationId) : AgyStreamEvent;

internal sealed record AgyText(string Text) : AgyStreamEvent;

/// <summary>
/// 도구 단계. <paramref name="Server"/>·<paramref name="Tool"/>·<paramref name="ArgumentsJson"/> 는 <c>call_mcp_tool</c> 의 매개변수다
/// (다른 도구면 Server 는 null, Tool 은 <c>tool_name</c>). <paramref name="State"/> 는 ACTIVE·DONE·ERROR 등 원문이다.
/// </summary>
internal sealed record AgyToolStep(
    int StepIndex,
    string State,
    string ToolName,
    string? Server,
    string Tool,
    string ArgumentsJson,
    string? Output,
    string? ErrorMessage) : AgyStreamEvent;

/// <summary>
/// 구조 결과 실행의 마지막 <c>finish</c> 단계(<c>step_type: "finish"</c>, 도구 정보 없음). 스키마 실행에서만 허용한다
/// (스키마가 없으면 <c>finish</c> 도구가 에이전트에 없으므로 나오면 닫는다).
/// </summary>
internal sealed record AgyFinishStep(string State) : AgyStreamEvent;

/// <summary>허용한 단계 종류(<see cref="AgyStreamParser.AllowedStepTypes"/>)가 아닌 단계. 감시가 곧바로 닫는다.</summary>
internal sealed record AgyUnknownStep(string StepType) : AgyStreamEvent;

/// <summary>끝. <paramref name="FinishedReason"/> 가 있으면 실패다(<c>print_timeout</c> 은 rc=0·status SUCCESS 로 온다).</summary>
internal sealed record AgyResult(
    string? ConversationId,
    string Status,
    string Response,
    string? StructuredOutputJson,
    string? Error,
    string? FinishedReason,
    long InputTokens,
    long OutputTokens) : AgyStreamEvent;

/// <summary>
/// agy <c>--output-format stream-json</c> 한 줄씩 해석한다. JSON 이 아닌 줄은 건너뛴다(도구 사건이 될 수 없다).
/// 모르는 최상위 사건·모르는 <c>step_type</c>·형식이 깨진 <c>step_update</c> 는 <see cref="AgyUnknownStep"/> 으로 알려 감시가 닫게 한다
/// (agy 가 업데이트되어 도구 실행·권한 요청을 새 사건 형식으로 보내도 감시를 그대로 지나가지 않게 한다).
/// </summary>
internal static class AgyStreamParser
{
    /// <summary>
    /// 허용하는 단계 종류. 표본(<c>agy-*.jsonl</c>)에서 관찰한 <c>user_input</c>·<c>agent_response</c>·<c>tool</c> 과, 실제 로그인 왕복에서 관찰한
    /// 구조 결과 실행의 <c>finish</c>(스키마 실행에서만 쓸 수 있음: 드라이버가 판단한다)만 허용한다.
    /// 그 밖의 종류(생각·명령·브라우저·서브에이전트 등)는 도구 실행으로 이어질 수 있으므로 즉시 닫는다.
    /// </summary>
    internal static readonly IReadOnlySet<string> AllowedStepTypes = new HashSet<string>(StringComparer.Ordinal) { "user_input", "agent_response", "tool", "finish" };

    public static IReadOnlyList<AgyStreamEvent> Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return [];
            }

            return String(root, "event") switch
            {
                "init" => String(root, "conversation_id") is { Length: > 0 } id ? [new AgySessionStarted(id)] : [new AgyUnknownStep("init:malformed")],
                "step_update" => ParseStep(root),
                "result" => ParseResult(root),
                var other => [new AgyUnknownStep("event:" + (other ?? string.Empty))],
            };
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static IReadOnlyList<AgyStreamEvent> ParseStep(JsonElement root)
    {
        if (!root.TryGetProperty("step_update", out var step) || step.ValueKind != JsonValueKind.Object)
        {
            return [new AgyUnknownStep("step_update:malformed")];
        }

        var type = String(step, "step_type") ?? string.Empty;
        if (!AllowedStepTypes.Contains(type))
        {
            return [new AgyUnknownStep(type)];
        }

        switch (type)
        {
            case "finish":
                return [new AgyFinishStep(String(step, "state") ?? string.Empty)];

            case "agent_response":
                return String(step, "text_delta") is { Length: > 0 } delta ? [new AgyText(delta)] : [];

            case "tool":
                var toolName = String(step, "tool_name") ?? string.Empty;
                var info = step.TryGetProperty("tool_info", out var i) && i.ValueKind == JsonValueKind.Object ? i : default;
                string? server = null;
                var tool = toolName;
                var arguments = "{}";
                if (info.ValueKind == JsonValueKind.Object && info.TryGetProperty("parameters", out var parameters) && parameters.ValueKind == JsonValueKind.Object)
                {
                    if (toolName == "call_mcp_tool")
                    {
                        server = String(parameters, "ServerName") ?? string.Empty;
                        tool = String(parameters, "ToolName") ?? string.Empty;
                        arguments = parameters.TryGetProperty("Arguments", out var a) ? a.GetRawText() : "{}";
                    }
                    else
                    {
                        arguments = parameters.GetRawText();
                    }
                }

                string? error = null;
                if (info.ValueKind == JsonValueKind.Object && info.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object)
                {
                    error = String(e, "message") ?? string.Empty;
                }

                var output = info.ValueKind == JsonValueKind.Object ? String(info, "output") : null;
                var index = step.TryGetProperty("step_index", out var n) && n.TryGetInt32(out var parsed) ? parsed : -1;
                return [new AgyToolStep(index, String(step, "state") ?? string.Empty, toolName, server, tool, arguments, output, error)];

            default:
                return [];
        }
    }

    private static IReadOnlyList<AgyStreamEvent> ParseResult(JsonElement root)
    {
        if (!root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        long input = 0;
        long output = 0;
        if (result.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            input = Number(usage, "input_tokens");
            output = Number(usage, "output_tokens");
        }

        var structured = result.TryGetProperty("structured_output", out var s) && s.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            ? s.GetRawText()
            : null;
        var errorText = result.TryGetProperty("error", out var e)
            ? e.ValueKind == JsonValueKind.String ? e.GetString() : e.ValueKind == JsonValueKind.Null ? null : e.GetRawText()
            : null;
        return
        [
            new AgyResult(
                String(result, "conversation_id"),
                String(result, "status") ?? string.Empty,
                String(result, "response") ?? string.Empty,
                structured,
                errorText,
                String(result, "finished_reason") is { Length: > 0 } reason ? reason : null,
                input,
                output),
        ];
    }

    private static string? String(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long Number(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) ? number : 0;
}
