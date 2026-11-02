namespace Oranda.Core.Ai.Profiles;

/// <summary>
/// 설정 파일이 손상되어 기본값으로 복구되었는지 한 번만 알려 주는 신호. 설정 저장소(<c>ISettingsStore</c> 구현)가 함께 구현한다.
/// 손상 복구는 활성 프로필 선택까지 기본값(= 시스템 CLI 의 Claude Code, 구독 계정)으로 되돌리므로, 사용자가 모르는 채 SQL 이 다른 곳으로 나가지 않게
/// 라우터는 복구 뒤 첫 AI 실행을 <c>settings_reset</c> 오류로 거부하고 다음 실행부터 통과시킨다.
/// 미래 버전·읽기 전용 같은 지속 상태는 이 신호가 아니다(그때는 계속 막지 않는다).
/// </summary>
public interface IAiSettingsResetSignal
{
    /// <summary>복구가 있었고 아직 알리지 않았으면 true 를 돌려주고 신호를 지운다. 이후 새 복구가 있기 전까지 false.</summary>
    bool TryTakeReset();
}
