using Oranda.Core.Ai.Contracts;

namespace Oranda.App.Features.Ai.Kit;

/// <summary>
/// 편집기 제안 막대(설계 §2.1 바로 처리 — SQL·PL/SQL 오류 고치기 등): 「받아들이기」·「버리기」 가 붙은 AI 띠. 답을 틀대로 읽었을 때만 받아들일 수 있고,
/// 받아들이기는 기능이 기존 차이 미리보기(<c>IEditorContext.ProposeEditAsync</c>)로 넣는다. 까닭 한 줄은 답 틀의 칸으로 막대에 보인다. 편집기 화면은 막대를
/// <see cref="AiInlineStrip"/> 로 그린다(SQL 편집기 = <c>SqlEditorViewModel.AiBar</c>).
/// </summary>
public static class AiSuggestionBar
{
    /// <summary>막대를 만든다.</summary>
    /// <param name="title">막대 이름(「오류 고치기」).</param>
    /// <param name="prepare">요청을 모은다(등급 인자: null = 기능 등급, Main = 「주 모델로 다시 보내기」).</param>
    /// <param name="accept">편집기에 제안으로 넣는다(넣었으면 null, 아니면 까닭).</param>
    /// <param name="chatHost">채팅창 동작(null 이면 「채팅에서 이어 보기」 를 숨긴다).</param>
    public static AiInlineStripViewModel Create(
        string title,
        Func<AiTier?, CancellationToken, Task<AiStripPreparation>> prepare,
        Func<AiStripAccept, CancellationToken, Task<string?>> accept,
        AiChatHost? chatHost = null)
    {
        ArgumentNullException.ThrowIfNull(accept);
        return new AiInlineStripViewModel(title, prepare, chatHost) { Accept = accept };
    }
}
