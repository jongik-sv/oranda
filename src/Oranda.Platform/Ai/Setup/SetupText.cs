using System.Text.RegularExpressions;

namespace Oranda.Platform.Ai.Setup;

/// <summary>
/// 설치·로그인·시험 출력에서 비밀을 가린다(대화 창 <c>AiErrorText.Redact</c> 와 같은 규칙 + 주소의 쿼리).
/// 로그인 주소의 쿼리에는 일회용 값(state·code_challenge)이 들어 있어 로그·진단 글에 남기지 않는다.
/// </summary>
internal static partial class SetupText
{
    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var clean = UrlQuery().Replace(text, "$1…");
        clean = Secrets().Replace(clean, m => m.Groups[1].Value + "=***");
        return UserInfo().Replace(clean, "://***@");
    }

    // 주소의 쿼리(?…)와 조각(#…)을 지운다. 머리 글자(? 또는 #)는 남긴다.
    [GeneratedRegex(@"(https?://[^\s?#\u0007\u001b]+[?#])[^\s\u0007\u001b]*", RegexOptions.CultureInvariant)]
    private static partial Regex UrlQuery();

    [GeneratedRegex(@"(?i)\b(bearer|authorization|api[-_ ]?key|apikey|token|password|passwd|secret)\b\W{0,3}\s*(?:bearer\s+|basic\s+)?[^\s,;]+", RegexOptions.CultureInvariant)]
    private static partial Regex Secrets();

    [GeneratedRegex(@"://[^/@\s]+@", RegexOptions.CultureInvariant)]
    private static partial Regex UserInfo();
}
