using System.Text.Json.Serialization;
using Oranda.Core.Serialization;

namespace Oranda.Core.Transfer.Copy;

/// <summary>
/// 복사 작업 파일의 소스 생성 직렬화 문맥. 옵션은 <see cref="OrandaJsonContext"/> 와 같다
/// (camelCase, 열거형 문자열, null 생략, 들여쓰기 2칸).
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true,
    WriteIndented = true,
    Converters = [typeof(UtcDateTimeOffsetConverter)])]
[JsonSerializable(typeof(CopyJob))]
public sealed partial class CopyJobJsonContext : JsonSerializerContext
{
}
