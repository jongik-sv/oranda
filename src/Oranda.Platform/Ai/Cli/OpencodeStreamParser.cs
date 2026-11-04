using System.Text.Json;

namespace Oranda.Platform.Ai.Cli;

/// <summary><c>opencode run --format json</c> 한 줄에서 읽은 사건.</summary>
internal abstract record OpencodeEvent(string? SessionId);

/// <summary>한 단계의 시작(<c>step_start</c>).</summary>
internal sealed record OpencodeStepStart(string? SessionId) : OpencodeEvent(SessionId);

/// <summary>모델의 글 조각(<c>text</c>). 한 번에 한 덩어리로 온다.</summary>
internal sealed record OpencodeText(string? SessionId, string Text) : OpencodeEvent(SessionId);

/// <summary>
/// 도구 호출(<c>tool_use</c>). opencode 는 도구가 끝난 뒤에만 이 사건을 한 번 낸다(진행 중 사건 없음: 40초 호출로 확인).
/// <paramref name="Status"/> 는 <c>completed</c> 또는 <c>error</c> 다. 도구 이름이 없는 사건은 <see cref="OpencodeUnexpected"/> 로 돌려준다.
/// </summary>
internal sealed record OpencodeTool(string? SessionId, string CallId, string Tool, string Status, string InputJson, int OutputSize, string? Error)
    : OpencodeEvent(SessionId);

/// <summary>한 단계의 끝(<c>step_finish</c>). 토큰 수는 입력·출력만 센다.</summary>
internal sealed record OpencodeStepFinish(string? SessionId, string? Reason, long InputTokens, long OutputTokens) : OpencodeEvent(SessionId);

/// <summary>실행 오류(<c>error</c>).</summary>
internal sealed record OpencodeError(string? SessionId, string Message) : OpencodeEvent(SessionId);

/// <summary>읽을 수 없는 도구 사건(이름이 없거나 모양이 다름). 호출 측은 허용 밖으로 보고 닫는다(fail-closed).</summary>
internal sealed record OpencodeUnexpected(string? SessionId, string Description) : OpencodeEvent(SessionId);

/// <summary>
/// <c>--format json</c> 출력(한 줄에 사건 하나)을 읽는다. 알려진 사건 종류만 돌려주고 JSON 이 아닌 줄과 모르는 종류는 버린다:
/// 도구를 여는 경로는 <c>tool_use</c> 뿐이므로 그것만 엄격하게 읽는다(이름이 없거나 모양이 다르면 <see cref="OpencodeUnexpected"/>).
/// </summary>
internal static class OpencodeStreamParser
{
    public static IEnumerable<OpencodeEvent> Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            yield break;
        }

        JsonDocument? document = null;
        if (line[0] == '{')
        {
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                // 아래에서 도구 사건 여부를 가린다.
            }
        }

        if (document is null)
        {
            // 읽을 수 없는 줄이 도구 사건이면 무엇을 불렀는지 알 수 없다: 버리지 않고 닫는다(fail-closed).
            if (line.Contains("\"tool_use\"", StringComparison.Ordinal))
            {
                yield return new OpencodeUnexpected(null, "tool_use(읽을 수 없는 줄)");
            }

            yield break;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String)
            {
                yield break;
            }

            var sessionId = StringOf(root, "sessionID");
            switch (typeElement.GetString())
            {
                case "step_start":
                    yield return new OpencodeStepStart(sessionId);
                    break;

                case "text":
                    if (root.TryGetProperty("part", out var textPart) && StringOf(textPart, "text") is { } text)
                    {
                        yield return new OpencodeText(sessionId, text);
                    }

                    break;

                case "tool_use":
                    yield return ParseTool(root, sessionId);
                    break;

                case "step_finish":
                    yield return ParseStepFinish(root, sessionId);
                    break;

                case "error":
                    yield return new OpencodeError(sessionId, ErrorMessage(root));
                    break;
            }
        }
    }

    private static OpencodeEvent ParseTool(JsonElement root, string? sessionId)
    {
        if (!root.TryGetProperty("part", out var part) || part.ValueKind != JsonValueKind.Object || StringOf(part, "tool") is not { Length: > 0 } tool)
        {
            return new OpencodeUnexpected(sessionId, "tool_use(이름 없음)");
        }

        var state = part.TryGetProperty("state", out var s) && s.ValueKind == JsonValueKind.Object ? s : default;
        var status = state.ValueKind == JsonValueKind.Object ? StringOf(state, "status") ?? string.Empty : string.Empty;
        var input = state.ValueKind == JsonValueKind.Object && state.TryGetProperty("input", out var i) ? i.GetRawText() : "{}";
        var output = state.ValueKind == JsonValueKind.Object ? StringOf(state, "output") : null;
        var error = state.ValueKind == JsonValueKind.Object ? StringOf(state, "error") : null;
        var callId = StringOf(part, "id") ?? StringOf(part, "partID") ?? tool;
        return new OpencodeTool(sessionId, callId, tool, status, input, output?.Length ?? 0, error);
    }

    private static OpencodeStepFinish ParseStepFinish(JsonElement root, string? sessionId)
    {
        long input = 0, output = 0;
        string? reason = null;
        if (root.TryGetProperty("part", out var part) && part.ValueKind == JsonValueKind.Object)
        {
            reason = StringOf(part, "reason");
            if (part.TryGetProperty("tokens", out var tokens) && tokens.ValueKind == JsonValueKind.Object)
            {
                input = LongOf(tokens, "input");
                output = LongOf(tokens, "output");
            }
        }

        return new OpencodeStepFinish(sessionId, reason, input, output);
    }

    private static string ErrorMessage(JsonElement root)
    {
        if (root.TryGetProperty("error", out var error))
        {
            if (error.ValueKind == JsonValueKind.Object && StringOf(error, "message") is { Length: > 0 } message)
            {
                return message;
            }

            if (error.ValueKind == JsonValueKind.String)
            {
                return error.GetString() ?? "opencode 실행 오류";
            }
        }

        return "opencode 실행 오류";
    }

    private static string? StringOf(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long LongOf(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : 0;
}
