using System.Text.Json;
using System.Text.Json.Serialization;

namespace Oranda.Core.Settings;

/// <summary>
/// 모르는 값을 만나도 설정 파일 전체를 손상으로 만들지 않는 열거형 판독기. 이 빌드가 모르는 이름·숫자·형식(다음 버전이 더한 프리셋 등)은
/// <c>Unknown</c> 으로 읽는다. <c>JsonDocumentFile</c> 은 역직렬화 예외를 손상으로 보고 설정 전체를 백업으로 옮긴 뒤 기본값으로 시작하므로,
/// 프로필 칸의 열거형은 이 판독기를 거쳐야 한다. 저장할 때 모르는 값의 원문은 <c>Unknown</c> 으로 바뀐다(잃어도 된다).
/// <typeparamref name="T"/> 는 <c>Unknown</c> 이름의 멤버를 가져야 한다.
/// </summary>
internal sealed class TolerantEnumConverter<T> : JsonConverter<T>
    where T : struct, Enum
{
    private static readonly T Fallback = Enum.Parse<T>("Unknown");

    public override bool HandleNull => true;

    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return Enum.TryParse<T>(reader.GetString(), ignoreCase: true, out var named) && Enum.IsDefined(named) ? named : Fallback;
            case JsonTokenType.Number:
                return reader.TryGetInt32(out var number) && Enum.IsDefined(typeof(T), number) ? (T)Enum.ToObject(typeof(T), number) : Fallback;
            case JsonTokenType.StartObject or JsonTokenType.StartArray:
                reader.Skip();
                return Fallback;
            default:
                return Fallback;
        }
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
}

/// <summary>
/// <c>ai.systemCliId</c> 판독기. 값이 없거나 null 이면 null(= Claude Code)이지만, 문자열이 아닌 값(숫자·객체·배열·불리언)은 예외 대신
/// <see cref="AiSettings.UnreadableSystemCliId"/> 로 읽는다. 목록에 없는 id 라 라우터가 <c>system_cli_unavailable</c> 로 닫으므로, 깨진 값이 조용히 Claude Code 로 바뀌지 않는다
/// (<see cref="TolerantActiveProfileIdConverter"/> 와 같은 원칙).
/// </summary>
internal sealed class TolerantSystemCliIdConverter : JsonConverter<string?>
{
    public override bool HandleNull => true;

    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;
            case JsonTokenType.String:
                return reader.GetString();
            case JsonTokenType.StartObject or JsonTokenType.StartArray:
                reader.Skip();
                return AiSettings.UnreadableSystemCliId;
            default:
                return AiSettings.UnreadableSystemCliId;
        }
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteStringValue(value);
        }
    }
}

/// <summary>
/// <c>ai.activeProfileId</c> 판독기. 값이 없거나 null 이면 null(= 시스템 CLI 의 Claude Code)이지만, 있는데 읽을 수 없는 값(빈 문자열·깨진 id)은
/// <see cref="Guid.Empty"/> 로 읽는다. 목록에 없는 id 라 라우터가 <c>profile_missing</c> 으로 닫으므로, 깨진 값이 조용히 Claude Code(구독 계정)로 바뀌지 않는다.
/// </summary>
internal sealed class TolerantActiveProfileIdConverter : JsonConverter<Guid?>
{
    public override bool HandleNull => true;

    public override Guid? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;
            case JsonTokenType.String:
                return Guid.TryParse(reader.GetString(), out var id) ? id : Guid.Empty;
            case JsonTokenType.StartObject or JsonTokenType.StartArray:
                reader.Skip();
                return Guid.Empty;
            default:
                return Guid.Empty;
        }
    }

    public override void Write(Utf8JsonWriter writer, Guid? value, JsonSerializerOptions options)
    {
        if (value is { } id)
        {
            writer.WriteStringValue(id);
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}

/// <summary>
/// <c>ai.benchmarkTimeoutSeconds</c> 판독기. 정수가 아닌 값(문자열·소수·객체·null)과 범위 밖 정수는 예외 대신 기본값 30 으로 읽는다.
/// 설정 파일 전체가 손상으로 취급되어 초기화되지 않게 하고, 깨진 값이 조용히 제한 없음(0)이 되지도 않게 한다.
/// </summary>
internal sealed class TolerantBenchmarkTimeoutConverter : JsonConverter<int?>
{
    public override bool HandleNull => true;

    public override int? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                return reader.TryGetInt32(out var seconds) ? AiSettings.NormalizeBenchmarkTimeout(seconds) : AiSettings.DefaultBenchmarkTimeoutSeconds;
            case JsonTokenType.StartObject or JsonTokenType.StartArray:
                reader.Skip();
                return AiSettings.DefaultBenchmarkTimeoutSeconds;
            default:
                return AiSettings.DefaultBenchmarkTimeoutSeconds;
        }
    }

    public override void Write(Utf8JsonWriter writer, int? value, JsonSerializerOptions options) =>
        writer.WriteNumberValue(value ?? AiSettings.DefaultBenchmarkTimeoutSeconds);
}
