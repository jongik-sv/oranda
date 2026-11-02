using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Protocol;
using Oranda.Core.Settings;

namespace Oranda.Platform.Ai.Api;

/// <summary>
/// <c>chat/completions</c> 요청 본문과 메시지 JSON 을 만든다. 대화 메시지는 만든 시점에 JSON 글로 굳혀 보관하므로(JsonNode 는 부모를 하나만 가질 수 있어
/// 모델 호출마다 다시 쓰면 오류가 난다) 요청마다 글을 그대로 끼워 넣는다.
/// </summary>
internal static class OpenAiRequestBuilder
{
    /// <summary>구조 결과를 받는 합성 도구 이름. CLI(claude)의 합성 도구와 같은 이름이며 도구 카드에 보이지 않는다.</summary>
    public const string StructuredOutputTool = "StructuredOutput";

    // 한글과 base64 의 +, = 를 \uXXXX 로 풀어 쓰면 크기가 몇 배로 늘고 메모리 상한을 빨리 채운다.
    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>OpenAI 공식 서버는 o 시리즈·gpt-5 계열에서 <c>max_tokens</c> 를 거부하고 <c>max_completion_tokens</c> 만 받는다. 호환 서버는 반대다.</summary>
    private static string MaxTokensField(AiLlmProfile profile) =>
        profile.Preset == AiProfilePreset.OpenAi ? "max_completion_tokens" : "max_tokens";

    public static byte[] BuildBody(AiLlmProfile profile, IReadOnlyList<string> messagesJson, string? toolsJson)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();

            // 모델이 비어 있으면 칸을 빼서 서버 기본 모델을 쓰게 한다(단일 모델 서버). 요청의 Model 은 프로필 모델을 덮지 않는다.
            if (profile.Model.Length > 0)
            {
                writer.WriteString("model", profile.Model);
            }

            writer.WritePropertyName("messages");
            writer.WriteStartArray();
            foreach (var message in messagesJson)
            {
                writer.WriteRawValue(message, skipInputValidation: true);
            }

            writer.WriteEndArray();
            writer.WriteBoolean("stream", true);
            writer.WriteStartObject("stream_options");
            writer.WriteBoolean("include_usage", true);
            writer.WriteEndObject();
            writer.WriteNumber(MaxTokensField(profile), profile.MaxOutputTokens);

            // OpenAI 는 빈 tools 배열을 거부하므로 도구가 없으면 칸 자체를 뺀다.
            if (toolsJson is not null)
            {
                writer.WritePropertyName("tools");
                writer.WriteRawValue(toolsJson, skipInputValidation: true);
            }

            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>게이트가 연 도구와(스키마가 있으면) 합성 도구 <c>StructuredOutput</c> 을 OpenAI <c>tools</c> 배열로. 도구가 하나도 없으면 null.</summary>
    public static string? BuildTools(IReadOnlyList<AgentToolInfo> gateTools, JsonElement? outputSchema)
    {
        if (gateTools.Count == 0 && outputSchema is null)
        {
            return null;
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartArray();
            foreach (var tool in gateTools)
            {
                WriteFunction(writer, tool.Name, tool.Description, schema => tool.InputSchema.WriteTo(schema));
            }

            if (outputSchema is { } schemaElement)
            {
                WriteFunction(
                    writer,
                    StructuredOutputTool,
                    "최종 결과를 제출하는 도구입니다. 조사를 마치면 글로 답하지 말고 반드시 이 도구의 인자로 결과를 제출하세요.",
                    schema => schemaElement.WriteTo(schema));
            }

            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public static string SystemMessage(string text) => Message("system", text);

    public static string AssistantMessage(string text) => Message("assistant", text);

    /// <summary>사용자 메시지. 이미지가 없으면 content 는 글 하나, 있으면 글과 <c>image_url</c>(data URL) 부분의 배열이다.</summary>
    public static string UserMessage(string prompt, IReadOnlyList<AiImageAttachment>? images)
    {
        if (images is not { Count: > 0 })
        {
            return Message("user", prompt);
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("role", "user");
            writer.WritePropertyName("content");
            writer.WriteStartArray();
            writer.WriteStartObject();
            writer.WriteString("type", "text");
            writer.WriteString("text", prompt);
            writer.WriteEndObject();
            foreach (var image in images)
            {
                writer.WriteStartObject();
                writer.WriteString("type", "image_url");
                writer.WriteStartObject("image_url");
                writer.WriteString("url", $"data:{image.MediaType};base64,{Convert.ToBase64String(image.Data)}");
                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public static string ToolMessage(string callId, string content)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("role", "tool");
            writer.WriteString("tool_call_id", callId);
            writer.WriteString("content", content);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string Message(string role, string content)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("role", role);
            writer.WriteString("content", content);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteFunction(Utf8JsonWriter writer, string name, string description, Action<Utf8JsonWriter> writeParameters)
    {
        writer.WriteStartObject();
        writer.WriteString("type", "function");
        writer.WriteStartObject("function");
        writer.WriteString("name", name);
        writer.WriteString("description", description);
        writer.WritePropertyName("parameters");
        writeParameters(writer);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }
}
