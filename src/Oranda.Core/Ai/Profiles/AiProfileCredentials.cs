namespace Oranda.Core.Ai.Profiles;

/// <summary>
/// 프로필 API 키의 <c>ICredentialStore</c> 키 이름. 키 값은 설정 파일·로그·감사 기록·명령줄 인자에 두지 않고 이 이름으로만 저장·조회한다.
/// 프로필을 지우면 같은 이름의 키도 지운다.
/// </summary>
public static class AiProfileCredentials
{
    public const string KeyPrefix = "oranda:ai:";

    public static string KeyFor(Guid profileId) => KeyPrefix + profileId.ToString("D");
}
