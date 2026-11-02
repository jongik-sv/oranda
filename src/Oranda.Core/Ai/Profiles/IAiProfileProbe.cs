using System.Text;
using Oranda.Core.Settings;

namespace Oranda.Core.Ai.Profiles;

/// <summary>
/// 프로필 서버 조회: 모델 목록과 연결 테스트(설계 §6.5). 저장 전 프로필로도 실행하므로 프로필과 키를 값으로 받는다.
/// Core 는 HTTP 를 직접 하지 않으므로 인터페이스와 결과 형식만 두고, 구현은 Platform 이 한다.
/// 구현은 실패를 예외가 아니라 결과로 돌려주고(취소만 <see cref="OperationCanceledException"/>), 메시지에 API 키를 넣지 않는다.
/// </summary>
public interface IAiProfileProbe
{
    /// <summary>정규화한 URL 의 <c>models</c> 를 조회한다. 응답에 이미지 지원 정보가 있으면 <see cref="AiModelInfo.SupportsImages"/> 를 채운다.</summary>
    Task<AiModelListResult> ListModelsAsync(AiProfileProbeRequest request, CancellationToken ct);

    /// <summary>고른 모델로 최대 출력이 작은 요청을 한 번 보내 성공·실패·걸린 시간을 돌려준다.</summary>
    Task<AiConnectionTestResult> TestConnectionAsync(AiProfileProbeRequest request, CancellationToken ct);
}

/// <summary>조회 대상. 저장 전 프로필도 받으며, 키는 입력 칸에서 막 읽은 값이거나 <c>ICredentialStore</c> 에서 읽은 값이다.</summary>
/// <param name="Profile">조회할 프로필(URL·형식·모델).</param>
/// <param name="ApiKey">API 키. 필요 없는 서버면 null.</param>
public sealed record AiProfileProbeRequest(AiLlmProfile Profile, string? ApiKey = null)
{
    // 기본 출력(ToString·로그·디버거)에 키가 나오지 않게 한다.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Profile = ").Append(Profile?.Name).Append(", ApiKey = ").Append(string.IsNullOrEmpty(ApiKey) ? "(none)" : "***");
        return true;
    }
}

/// <summary>서버가 알려 준 모델 한 개.</summary>
/// <param name="SupportsImages">이미지 입력 지원. 서버가 알려 주지 않으면 null.</param>
public sealed record AiModelInfo(string Id, bool? SupportsImages = null);

/// <summary>모델 목록 조회 결과.</summary>
/// <param name="Message">실패 이유(키 없음). 성공이면 null.</param>
public sealed record AiModelListResult(bool Success, IReadOnlyList<AiModelInfo> Models, string? Message = null)
{
    public static AiModelListResult Ok(IReadOnlyList<AiModelInfo> models) => new(true, models);

    public static AiModelListResult Fail(string message) => new(false, [], message);
}

/// <summary>연결 테스트 결과.</summary>
/// <param name="Elapsed">요청을 보내고 응답이 끝날 때까지 걸린 시간(실패여도 채운다).</param>
/// <param name="Message">성공이면 선택 안내, 실패면 이유(키 없음).</param>
public sealed record AiConnectionTestResult(bool Success, TimeSpan Elapsed, string? Message = null);
