using Oranda.App.Features.Localization;

namespace Oranda.App.Features.Ai.External.Connect;

/// <summary>
/// 연결 창·스킬 설치 창의 한/영 글(기존 <c>AiSettingsText</c> 방식). 지금 화면 언어가 영어면 영어, 아니면 한국어.
/// 상태 표시줄 띠는 문자열 표(<c>AgentLink.*</c>)를 쓴다.
/// </summary>
internal static class ExternalAgentText
{
    public static bool IsEnglish => UiText.Current?.Language == "en";

    public static string T(string ko, string en) => IsEnglish ? en : ko;
}
