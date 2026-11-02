using System.Buffers;
using System.Text;
using System.Text.Json;
using Oranda.Core.Ai.Contracts;

namespace Oranda.Platform.Ai.Cli;

/// <summary>
/// <c>--input-format stream-json</c> 의 표준 입력 한 줄: <c>{"type":"user","message":{"role":"user","content":[…]}}</c>.
/// 글 블록 뒤에 이미지 블록(base64)을 붙인다. 글만 있는 요청도 같은 형식을 쓴다(경로를 하나로 만들기 위해서다).
/// 줄 끝 문자는 마지막 하나뿐이어야 한다: 기본 인코더는 비 ASCII 문자(U+2028·U+2029 포함)를 <c>\uXXXX</c> 로 쓰므로 본문에 줄바꿈이 남지 않는다.
/// </summary>
internal static class ClaudeStreamInput
{
    private static readonly HashSet<string> MediaTypes = new(StringComparer.Ordinal) { "image/png", "image/jpeg", "image/webp", "image/gif" };

    /// <summary>보낼 수 있는 형태인지 확인한다(빈 요청, 지원하지 않는 이미지). 문제가 있으면 오류, 없으면 null.</summary>
    public static AiError? Validate(string prompt, IReadOnlyList<AiImageAttachment>? images)
    {
        if (string.IsNullOrWhiteSpace(prompt) && images is not { Count: > 0 })
        {
            return new AiError("보낼 내용이 없습니다.", "prompt_empty");
        }

        if (images is null)
        {
            return null;
        }

        foreach (var image in images)
        {
            if (image is null || image.Data is not { Length: > 0 })
            {
                return new AiError("비어 있는 이미지는 보낼 수 없습니다.", "image_invalid");
            }

            if (!MediaTypes.Contains(image.MediaType ?? string.Empty))
            {
                return new AiError($"지원하지 않는 이미지 형식입니다: {image.MediaType}. PNG·JPEG·WebP·GIF 만 보낼 수 있습니다.", "image_invalid");
            }
        }

        return null;
    }

    /// <summary>끝에 <c>\n</c> 이 붙은 한 줄을 만든다.</summary>
    public static string Build(string prompt, IReadOnlyList<AiImageAttachment>? images)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "user");
            writer.WriteStartObject("message");
            writer.WriteString("role", "user");
            writer.WriteStartArray("content");

            // 이미지만 보내는 요청이면 빈 글 블록을 만들지 않는다(API 가 빈 text 블록을 거절한다).
            var hasImages = images is { Count: > 0 };
            if (!hasImages || !string.IsNullOrWhiteSpace(prompt))
            {
                writer.WriteStartObject();
                writer.WriteString("type", "text");
                writer.WriteString("text", prompt);
                writer.WriteEndObject();
            }

            foreach (var image in images ?? [])
            {
                writer.WriteStartObject();
                writer.WriteString("type", "image");
                writer.WriteStartObject("source");
                writer.WriteString("type", "base64");
                writer.WriteString("media_type", image.MediaType);
                writer.WriteBase64String("data", image.Data);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan) + "\n";
    }
}
