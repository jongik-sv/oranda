using Oranda.Core.Settings;

namespace Oranda.Platform.Ai.Profiles;

/// <summary>
/// Anthropic 형식 프로필이 키를 어떤 헤더로 보내는지 정하는 규칙 하나. CLI 환경 변수 조합(<c>ClaudeProfileEnvironment</c>)과
/// 서버 조회(<see cref="AiProfileProbe"/>)가 같은 함수를 써서, 연결 테스트가 실제 실행과 다른 인증으로 통과하는 일이 없게 한다.
/// </summary>
internal static class AiProfileAuth
{
    /// <summary>
    /// true 면 <c>x-api-key</c> 헤더(<c>ANTHROPIC_API_KEY</c>), false 면 <c>Authorization: Bearer</c> 헤더(<c>ANTHROPIC_AUTH_TOKEN</c>).
    /// Anthropic 프리셋만 <c>x-api-key</c> 이고, Z.AI·직접 입력은 Bearer 다.
    /// </summary>
    public static bool UsesApiKeyHeader(AiLlmProfile profile) => profile.Preset == AiProfilePreset.Anthropic;

    /// <summary>키가 없을 때 Bearer 자리에 넣는 임의의 값(CLI 가 인증 값 없이는 시작하지 않는다).</summary>
    public const string PlaceholderToken = "local";
}
