namespace Oranda.App.Features.AiSetup.Wizard;

/// <summary>
/// 떠나기 전에 물어야 하는 단계(설치 중 등). 창 틀이 [이전]·창 닫기 전에 <see cref="LeaveQuestion"/> 을 창 안 한 줄로 묻고,
/// 「그만두기」면 평소처럼 <c>LeaveAsync</c> 를 부른다(단계는 거기서 하던 일을 취소한다).
/// </summary>
public interface IAiSetupLeaveGuard
{
    /// <summary>물을 글. null·빈 글이면 묻지 않는다.</summary>
    string? LeaveQuestion { get; }
}
