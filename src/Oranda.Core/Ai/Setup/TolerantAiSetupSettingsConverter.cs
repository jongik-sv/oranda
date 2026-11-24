using System.Text.Json;
using System.Text.Json.Serialization;
using Oranda.Core.Settings;

namespace Oranda.Core.Ai.Setup;

/// <summary>
/// <c>ai.setup</c> 판독기. 객체가 아닌 값(null·숫자·배열)은 기본값으로, <c>firstRunPromptClosed</c> 는 JSON <c>true</c> 일 때만 참으로,
/// <c>lastPath</c>·<c>lastStep</c> 은 이 빌드가 아는 이름일 때만, <c>lastProfileId</c> 는 id 글일 때만, <c>lastTest</c> 는 시각·결과·대상이 다 있을 때만 읽고 그 밖은 null(처음부터)로 읽는다.
/// 설정 파일 전체가 손상으로 취급되어 초기화되지 않게 한다(<c>TolerantLightSettingsConverter</c> 와 같은 원칙).
/// </summary>
internal sealed class TolerantAiSetupSettingsConverter : JsonConverter<AiSetupSettings>
{
    public override bool HandleNull => true;

    public override AiSetupSettings Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            reader.Skip();
            return new AiSetupSettings();
        }

        var closed = false;
        AiSetupPath? path = null;
        AiSetupStepId? step = null;
        Guid? profileId = null;
        AiConnectionTestRecord? lastTest = null;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            var property = reader.GetString();
            reader.Read();
            if (string.Equals(property, "firstRunPromptClosed", StringComparison.OrdinalIgnoreCase))
            {
                closed = reader.TokenType == JsonTokenType.True;
            }
            else if (string.Equals(property, "lastPath", StringComparison.OrdinalIgnoreCase))
            {
                path = Named<AiSetupPath>(ref reader);
            }
            else if (string.Equals(property, "lastStep", StringComparison.OrdinalIgnoreCase))
            {
                step = Named<AiSetupStepId>(ref reader);
            }
            else if (string.Equals(property, "lastProfileId", StringComparison.OrdinalIgnoreCase))
            {
                profileId = reader.TokenType == JsonTokenType.String && Guid.TryParse(reader.GetString(), out var id) && id != Guid.Empty ? id : null;
            }
            else if (string.Equals(property, "lastTest", StringComparison.OrdinalIgnoreCase))
            {
                lastTest = ReadTest(ref reader); // 객체를 다 읽고 끝 토큰에 선다(아래 Skip 을 타지 않는다)
                continue;
            }

            if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
            {
                reader.Skip();
            }
        }

        return new AiSetupSettings { FirstRunPromptClosed = closed, LastPath = path, LastStep = step, LastProfileId = profileId, LastTest = lastTest };
    }

    /// <summary>
    /// <c>lastTest</c>: 객체가 아니거나 시각·결과·대상 중 하나라도 읽을 수 없으면 null(기록 없음). 실패 종류의 모르는 이름은 <see cref="AiSetupErrorKind.Unknown"/>.
    /// 끝나면 읽개는 그 값의 마지막 토큰에 있다.
    /// </summary>
    private static AiConnectionTestRecord? ReadTest(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            reader.Skip();
            return null;
        }

        DateTimeOffset? at = null;
        bool? succeeded = null;
        string? target = null;
        long elapsed = 0;
        AiSetupErrorKind? error = null;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            var property = reader.GetString();
            reader.Read();
            if (string.Equals(property, "at", StringComparison.OrdinalIgnoreCase))
            {
                at = reader.TokenType == JsonTokenType.String && reader.TryGetDateTimeOffset(out var when) ? when : null;
            }
            else if (string.Equals(property, "succeeded", StringComparison.OrdinalIgnoreCase))
            {
                succeeded = reader.TokenType switch { JsonTokenType.True => true, JsonTokenType.False => false, _ => null };
            }
            else if (string.Equals(property, "target", StringComparison.OrdinalIgnoreCase))
            {
                target = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
            }
            else if (string.Equals(property, "elapsedMs", StringComparison.OrdinalIgnoreCase))
            {
                elapsed = reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var ms) && ms >= 0 ? ms : 0;
            }
            else if (string.Equals(property, "error", StringComparison.OrdinalIgnoreCase))
            {
                error = reader.TokenType == JsonTokenType.String ? Named<AiSetupErrorKind>(ref reader) ?? AiSetupErrorKind.Unknown : null;
            }

            if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
            {
                reader.Skip();
            }
        }

        return at is { } a && succeeded is { } ok && !string.IsNullOrWhiteSpace(target)
            ? new AiConnectionTestRecord(a, ok, target, elapsed, ok ? null : error ?? AiSetupErrorKind.Unknown)
            : null;
    }

    public override void Write(Utf8JsonWriter writer, AiSetupSettings value, JsonSerializerOptions options)
    {
        value ??= new AiSetupSettings();
        writer.WriteStartObject();
        writer.WriteBoolean("firstRunPromptClosed", value.FirstRunPromptClosed);
        WriteName(writer, "lastPath", value.LastPath);
        WriteName(writer, "lastStep", value.LastStep);
        if (value.LastProfileId is { } id)
        {
            writer.WriteString("lastProfileId", id);
        }
        else
        {
            writer.WriteNull("lastProfileId");
        }

        if (value.LastTest is { } test)
        {
            writer.WriteStartObject("lastTest");
            writer.WriteString("at", test.At);
            writer.WriteBoolean("succeeded", test.Succeeded);
            writer.WriteString("target", test.Target);
            writer.WriteNumber("elapsedMs", test.ElapsedMs);
            WriteName(writer, "error", test.Error);
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
    }

    // 이름 문자열만 받는다(숫자는 판이 바뀌면 뜻이 달라질 수 있어 받지 않는다).
    private static T? Named<T>(ref Utf8JsonReader reader)
        where T : struct, Enum =>
        reader.TokenType == JsonTokenType.String
        && reader.GetString() is { } text
        && !text.Any(char.IsAsciiDigit)
        && Enum.TryParse<T>(text, ignoreCase: true, out var value)
        && Enum.IsDefined(value)
            ? value
            : null;

    private static void WriteName<T>(Utf8JsonWriter writer, string name, T? value)
        where T : struct, Enum
    {
        if (value is { } known)
        {
            writer.WriteString(name, known.ToString());
        }
        else
        {
            writer.WriteNull(name);
        }
    }
}
