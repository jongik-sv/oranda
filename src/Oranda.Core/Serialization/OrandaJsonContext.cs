using System.Text.Json.Serialization;
using Oranda.Core.Connection;
using Oranda.Core.Formatting;
using Oranda.Core.Settings;
using Oranda.Core.Workspaces;

namespace Oranda.Core.Serialization;

/// <summary>
/// 로컬 JSON 파일의 소스 생성 직렬화 문맥(data-model 1.3). camelCase, 열거형 문자열, null 생략, 들여쓰기 2칸.
/// 키맵(<c>Oranda.Core.Shell</c>)·레이아웃(App DTO)은 각 소유 영역의 문맥에 등록한다.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true,
    WriteIndented = true,
    Converters = [typeof(UtcDateTimeOffsetConverter)])]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(ConnectionsDocument))]
[JsonSerializable(typeof(ConnectionExportDocument))]
[JsonSerializable(typeof(FormatterProfile))]
[JsonSerializable(typeof(Workspace))]
public sealed partial class OrandaJsonContext : JsonSerializerContext
{
}
