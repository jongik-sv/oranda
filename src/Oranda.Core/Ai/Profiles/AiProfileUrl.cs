using System.Net;
using Oranda.Core.Settings;

namespace Oranda.Core.Ai.Profiles;

/// <summary>
/// 프로필 기준 URL 정규화. 사용자는 끝의 <c>/</c>·<c>/v1</c> 유무와 무관하게 입력하고, 쓰는 곳(CLI 환경 변수·API 요청·모델 목록)마다
/// <see cref="BaseFor"/> 하나에서 형식에 맞는 기준 URL 을 받는다. 잘못된 주소(비어 있음·http/https 가 아님·사용자 정보(user:pw@)나 쿼리·조각을 포함)는 null 이다.
/// </summary>
public static class AiProfileUrl
{
    /// <summary>
    /// 형식별 기준 URL. Anthropic 형식은 끝의 <c>/</c>·<c>/v1</c> 을 떼어 <c>{base}</c> 를 만들고(경로는 호출 쪽이 <c>/v1/messages</c> 로 잇는다),
    /// OpenAI 형식은 경로가 없으면 <c>{base}/v1</c> 로 한다. 이미 경로가 있으면(예: Gemini 의 <c>/v1beta/openai</c>) 그대로 둔다.
    /// </summary>
    public static string? BaseFor(AiApiFormat format, string? baseUrl)
    {
        if (!TryParse(baseUrl, out var uri))
        {
            return null;
        }

        var origin = uri.GetLeftPart(UriPartial.Authority);
        var path = uri.AbsolutePath.TrimEnd('/');
        if (format == AiApiFormat.Anthropic)
        {
            if (path.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            {
                path = path[..^3];
            }

            return origin + path.TrimEnd('/');
        }

        return path.Length == 0 ? origin + "/v1" : origin + path;
    }

    /// <summary>CLI 환경 변수 <c>ANTHROPIC_BASE_URL</c> 값. 형식은 항상 Anthropic 이다(OpenAI 형식 프로필은 CLI 로 가지 않는다).</summary>
    public static string? CliBaseUrl(string? baseUrl) => BaseFor(AiApiFormat.Anthropic, baseUrl);

    /// <summary>API 요청 URL. OpenAI 형식은 <c>{base}/{endpoint}</c>(예: <c>chat/completions</c>), Anthropic 형식은 <c>{base}/v1/{endpoint}</c>(예: <c>messages</c>).</summary>
    public static string? RequestUrl(AiApiFormat format, string? baseUrl, string endpoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        var root = BaseFor(format, baseUrl);
        if (root is null)
        {
            return null;
        }

        var tail = endpoint.TrimStart('/');
        return format == AiApiFormat.Anthropic ? $"{root}/v1/{tail}" : $"{root}/{tail}";
    }

    /// <summary>모델 목록 조회 URL. Anthropic 형식 <c>{base}/v1/models</c>, OpenAI 형식 <c>{base}/models</c>.</summary>
    public static string? ModelsUrl(AiApiFormat format, string? baseUrl) => RequestUrl(format, baseUrl, "models");

    /// <summary>
    /// 암호화 없이(<c>http://</c>) 이 PC 밖으로 나가는 주소인가. 설정 화면이 「SQL 과 이미지가 암호화 없이 전달됩니다」 안내를 보일 조건이다.
    /// 주소가 잘못됐으면 false.
    /// </summary>
    public static bool IsPlainTextRemote(string? baseUrl) =>
        TryParse(baseUrl, out var uri) && uri.Scheme == Uri.UriSchemeHttp && !IsLocalHost(uri.Host);

    // localhost 와 루프백 주소(127.0.0.0/8, ::1)만 이 PC 로 본다. Uri.IsLoopback 은 「loopback」 같은 임의 이름도 true 로 돌려줘서 쓰지 않는다.
    private static bool IsLocalHost(string host)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address);
    }

    private static bool TryParse(string? baseUrl, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(baseUrl)
            || !Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var parsed)
            || parsed.Scheme is not ("http" or "https")
            || string.IsNullOrEmpty(parsed.Host)
            || parsed.UserInfo.Length > 0
            || parsed.Query.Length > 0
            || parsed.Fragment.Length > 0)
        {
            // 주소에 심은 비밀(user:pw@)과 조용히 버려질 쿼리·조각은 받지 않는다.
            return false;
        }

        uri = parsed;
        return true;
    }
}
