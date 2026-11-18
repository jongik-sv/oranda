namespace Oranda.App.Features.Ai.Chat;

/// <summary>
/// 대화 창의 대화 하나를 가진 흐름(튜닝·일반 대화 밖, 예 AI 파티션 추천). <see cref="AiChatViewModel.StartConversation(string, IAiChatConversation)"/> 으로
/// 대화를 열면 그 대화의 이어 말하기·멈춤이 이 흐름으로 간다. 새 대화(또는 다른 흐름의 시작)가 열리면 대화 창이 놓는다(<see cref="AiChatViewModel.Owner"/> = null).
/// </summary>
public interface IAiChatConversation
{
    /// <summary>요청이 진행 중이다.</summary>
    bool IsRunning { get; }

    /// <summary>
    /// 이어 말하기(같은 대화). 보내지 못하면 사유를 대화 창에 남기고 <see cref="AiChatViewModel.RestoreFollowUp"/> 으로 글을 되돌린다.
    /// </summary>
    Task FollowUpAsync(ChatFollowUp followUp, CancellationToken ct);

    /// <summary>진행 중인 요청을 멈춘다(대화 창의 「멈춤」).</summary>
    void CancelRun();
}
