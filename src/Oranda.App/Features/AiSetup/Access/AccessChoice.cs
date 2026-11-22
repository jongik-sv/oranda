using Oranda.Core.Ai.Access;
using Oranda.Core.Connection;

namespace Oranda.App.Features.AiSetup.Access;

/// <summary>
/// 켬/끔 스위치를 접속의 AI 허용 수준으로 바꾼다. 켬 = 환경 기본값(null: 개발·시험 「계획」, 운영 「조회 실행」), 끔 = <see cref="AiAccessLevel.Off"/>.
/// 사용자가 높은 수준을 직접 고르는 길은 없다(그건 접속 편집 몫): 저장 값은 늘 null 또는 Off 라 운영 이름 확인에 걸리지 않는다.
/// </summary>
public static class AccessChoice
{
    /// <summary>지금 AI 가 켜져 있는가(실효 수준이 끔이 아님).</summary>
    public static bool IsOn(ConnectionProfile profile) => AiAccessPolicy.Effective(profile) != AiAccessLevel.Off;

    /// <summary>스위치 값을 적용한 접속. 스위치가 지금 상태와 같으면 그대로 돌려준다(직접 고른 수준을 건드리지 않는다).</summary>
    public static ConnectionProfile Apply(ConnectionProfile profile, bool on)
    {
        System.ArgumentNullException.ThrowIfNull(profile);
        if (IsOn(profile) == on)
        {
            return profile;
        }

        return profile with { AiAccess = on ? null : AiAccessLevel.Off };
    }
}
