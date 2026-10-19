using System.Text.Json.Serialization;
using Oranda.App.Shell.Layout;
using Oranda.Core.Shell;

namespace Oranda.App.Shell;

/// <summary>
/// 셸 파일(키맵·레이아웃·명령 제목) 직렬화. 소스 생성기라 첫 프레임 경로에서 리플렉션 직렬화를 하지 않는다(D-006).
/// 옵션은 data-model.md 1.3 공통 규칙과 같다.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true,
    WriteIndented = true)]
[JsonSerializable(typeof(Keymap))]
[JsonSerializable(typeof(LayoutDocument))]
[JsonSerializable(typeof(System.Collections.Generic.Dictionary<string, string>))]
internal sealed partial class ShellJsonContext : JsonSerializerContext
{
}
