using System.Text.Json.Serialization;
using Oranda.Core.Serialization;

namespace Oranda.Core.Rollback;

/// <summary>
/// 원복 파일 평문(<see cref="RollbackScriptDocument"/>, 설계 12.2)의 소스 생성 직렬화 문맥. camelCase, 열거형 문자열, null 생략.
/// 저장소(Platform, RB-21)가 암호화 전후에 쓴다.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true,
    Converters = [typeof(UtcDateTimeOffsetConverter)])]
[JsonSerializable(typeof(RollbackScriptDocument))]
public sealed partial class RollbackJsonContext : JsonSerializerContext
{
}
