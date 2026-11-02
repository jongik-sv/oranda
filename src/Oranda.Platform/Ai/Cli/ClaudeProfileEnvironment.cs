using System.Globalization;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Profiles;
using Oranda.Core.Settings;
using Oranda.Platform.Ai.Profiles;

namespace Oranda.Platform.Ai.Cli;

/// <summary>
/// 프로필 경로의 claude 환경 변수(설계 §6.1). Claude Code(<see cref="AiRoute.Profile"/> 이 null)는 구독 인증을 지키려고 아무것도 더하지 않는다.
/// 프로필이면 키·주소·모델이 모두 확실할 때만 값을 만들고, 하나라도 어긋나면 claude 를 띄우지 않고 오류로 닫는다
/// (빠뜨린 값이 있으면 claude 는 기본 주소나 구독 계정으로 조용히 나간다).
/// </summary>
internal static class ClaudeProfileEnvironment
{
    public const string BaseUrl = "ANTHROPIC_BASE_URL";

    public const string ApiKey = "ANTHROPIC_API_KEY";

    public const string AuthToken = "ANTHROPIC_AUTH_TOKEN";

    public const string DisableNonessentialTraffic = "CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC";

    public const string MaxOutputTokens = "CLAUDE_CODE_MAX_OUTPUT_TOKENS";

    /// <summary>
    /// 프로필 실행에서 부모 환경에서 물려받지 않는 변수의 이름 접두사. 제공자 전환(<c>CLAUDE_CODE_USE_*</c>), 소켓·게이트웨이·사용자 지정 헤더(<c>ANTHROPIC_*</c>),
    /// 원격 측정(<c>OTEL_*</c>, 사용자 글 전송 포함)이 한 가지씩 막으면 다음 것이 나오는 식이라, 알려진 이름을 지우지 않고 이 접두사 전체를 허용 목록 밖이면 넘기지 않는다.
    /// <c>CLAUDECODE</c> 같은 이름도 <c>CLAUDE</c> 접두사에 걸린다.
    /// </summary>
    public static readonly IReadOnlyList<string> ScrubbedPrefixes = ["ANTHROPIC_", "CLAUDE", "OTEL_"];

    /// <summary>
    /// 접두사에 걸려도 넘기는 변수: claude 설정 폴더 위치, Windows 의 Git Bash 경로(없으면 claude 가 시작하지 못한다),
    /// 클라이언트 인증서(mTLS, 인증서를 요구하는 사내 게이트웨이 프로필에 필요하다).
    /// </summary>
    public static readonly IReadOnlyList<string> PreservedNames =
        ["CLAUDE_CONFIG_DIR", "CLAUDE_CODE_GIT_BASH_PATH", "CLAUDE_CODE_CLIENT_CERT", "CLAUDE_CODE_CLIENT_KEY", "CLAUDE_CODE_CLIENT_KEY_PASSPHRASE"];

    /// <summary>
    /// 프록시 변수(대소문자 둘 다). 프로필 실행은 주소와 무관하게 프록시를 거치지 않고 프로필 서버에 직접 연결한다(사내 네트워크 사용이 목적이고,
    /// 주소로 사내·외부를 가르는 판정은 잘못 가르면 연결이 막히므로 만들지 않는다). 프록시로만 외부에 나갈 수 있는 환경에서는 클라우드 프로필이 연결되지 않는다.
    /// </summary>
    public static readonly IReadOnlyList<string> ProxyVariables =
        ["HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY", "http_proxy", "https_proxy", "all_proxy", "no_proxy"];

    /// <summary>접두사 규칙으로 지워야 하는 이름인가.</summary>
    public static bool IsScrubbed(string name) =>
        ScrubbedPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        && !PreservedNames.Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 등급별 기본 모델 변수. 지우기만 하면 claude 가 등급별 내장 Claude 모델명(작은 모델을 쓰는 보조 호출 등)을 쓰므로,
    /// 프로필 모델로 채워 어떤 호출이든 프로필 모델만 서버로 나가게 한다.
    /// </summary>
    public static readonly IReadOnlyList<string> TierModelVariables =
    [
        "ANTHROPIC_SMALL_FAST_MODEL",
        "ANTHROPIC_DEFAULT_OPUS_MODEL",
        "ANTHROPIC_DEFAULT_SONNET_MODEL",
        "ANTHROPIC_DEFAULT_HAIKU_MODEL",
    ];

    /// <summary>값이 null 이면 자식 프로세스 환경에서 지운다. 프로필 실행은 이와 별개로 <see cref="IsScrubbed"/> 이름을 모두 물려받지 않는다(<see cref="ClaudeLaunch.IsolateEnvironment"/>).</summary>
    public static (IReadOnlyDictionary<string, string?> Variables, AiError? Error) Resolve(AiRoute route)
    {
        ArgumentNullException.ThrowIfNull(route);
        var variables = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (route.Profile is not { } profile)
        {
            return (variables, null);
        }

        if (profile.Format != AiApiFormat.Anthropic || profile.Preset is not (AiProfilePreset.Anthropic or AiProfilePreset.ZAi or AiProfilePreset.Custom))
        {
            return (variables, new AiError(
                $"프로필 「{profile.Name}」 은 claude 로 연결할 수 없는 종류입니다. Anthropic 형식 프로필만 claude 를 거칩니다.",
                AiActiveProfile.ProfileInvalidCode));
        }

        if (AiProfileUrl.CliBaseUrl(profile.BaseUrl) is not { } baseUrl)
        {
            return (variables, new AiError(
                $"프로필 「{profile.Name}」 의 서버 주소가 올바르지 않습니다. http:// 또는 https:// 로 시작하는 주소를 입력하세요.",
                "profile_invalid_url"));
        }

        if (string.IsNullOrWhiteSpace(profile.Model))
        {
            return (variables, new AiError($"프로필 「{profile.Name}」 에 모델 이름이 없습니다. 프로필 관리에서 모델을 입력하세요.", "profile_model_missing"));
        }

        var key = string.IsNullOrEmpty(route.ApiKey) ? null : route.ApiKey;
        if (AiProfileAuth.UsesApiKeyHeader(profile))
        {
            if (key is null)
            {
                return (variables, new AiError($"프로필 「{profile.Name}」 에는 API 키가 필요합니다. 프로필 관리에서 키를 입력하세요.", "profile_key_missing"));
            }

            variables[ApiKey] = key;
            variables[AuthToken] = null;
        }
        else
        {
            variables[AuthToken] = key ?? AiProfileAuth.PlaceholderToken;
            variables[ApiKey] = string.Empty;
        }

        foreach (var name in ProxyVariables)
        {
            variables[name] = null;
        }

        foreach (var name in TierModelVariables)
        {
            variables[name] = profile.Model.Trim();
        }

        variables[BaseUrl] = baseUrl;
        variables[DisableNonessentialTraffic] = "1";
        variables[MaxOutputTokens] = profile.MaxOutputTokens.ToString(CultureInfo.InvariantCulture);
        return (variables, null);
    }
}
