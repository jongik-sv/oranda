using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Oranda.App.Features.Ai.Chat;
using Oranda.Core.Connection;

namespace Oranda.App.Features.AiSetup.TryIt;

/// <summary>SQL 설명 결과.</summary>
/// <param name="Answered">끝까지 답을 받았다.</param>
/// <param name="Answer">답 글.</param>
/// <param name="Reason">시작하지 않은(거부된) 이유. 상태줄에 그대로 보인다.</param>
/// <param name="TabMissing">연습 탭이 닫혀 있었다(다른 탭의 SQL 을 보내지 않으려고 시작하지 않았다).</param>
public sealed record TryItExplainResult(bool Answered, string? Answer, string? Reason = null, bool TabMissing = false);

/// <summary>해 보기 단계가 앱에 시키는 일(시험은 가짜를 넣는다). 실제 구현 = <see cref="HostTryItActions"/>.</summary>
public interface ITryItActions
{
    /// <summary>그 접속으로 새 편집기 탭을 열고(활성이 된다) 탭 id 를 돌려준다. 열지 못하면 null.</summary>
    Guid? OpenPracticeTab(ConnectionProfile profile, string title, string sql);

    /// <summary>
    /// 지금 시작할 수 없는 이유(그 접속에 연결 안 됨, 다른 AI 작업이 도는 중). 시작할 수 있으면 null.
    /// 런처는 이런 경우 대화 창에 글을 남기지 않고 조용히 돌아오므로 미리 본다.
    /// </summary>
    string? CannotStartReason(ConnectionProfile profile, bool english) => null;

    /// <summary>연습 탭 문장으로 기존 「SQL 설명」 명령을 돌리고(실행하지 않고 설명만) 답을 기다린다. 탭이 닫혔으면 시작하지 않는다.</summary>
    Task<TryItExplainResult> ExplainAsync(Guid tabId, CancellationToken ct);

    /// <summary>외부 에이전트 연결 창(스킬 설치 포함)을 연다.</summary>
    Task OpenExternalAgentAsync(CancellationToken ct);
}

/// <summary>
/// 「SQL 설명」 대화의 글로 답을 받았는지 읽는다. 대화는 「보낸 문장」 줄(·안내 줄) → 요청 줄(<see cref="ChatUserEntry"/>) → 답 순서다.
/// 답 = 요청 줄 뒤의 비지 않은 글 중 마지막이고, 턴이 끝까지 완료됐을 때만 답으로 본다(멈춤·오류는 아님).
/// </summary>
public static class TryItAnswerReader
{
    public const string ErrorPrefix = "오류: ";

    /// <param name="entries">그 대화의 글 전부.</param>
    /// <param name="completed">마지막 턴이 멈춤·오류 없이 끝났다(<see cref="AiChatViewModel.ResultIsCompleted"/>).</param>
    public static TryItExplainResult Read(IReadOnlyList<ChatEntry> entries, bool completed)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var request = -1;
        for (var i = entries.Count - 1; i >= 0; i--)
        {
            if (entries[i] is ChatUserEntry)
            {
                request = i;
                break;
            }
        }

        var answer = request < 0
            ? null
            : entries.Skip(request + 1).OfType<ChatAssistantEntry>().LastOrDefault(e => !string.IsNullOrWhiteSpace(e.Text));
        if (!completed || answer is null || answer.Text.StartsWith(ErrorPrefix, StringComparison.Ordinal))
        {
            return new TryItExplainResult(false, null, answer?.Text.StartsWith(ErrorPrefix, StringComparison.Ordinal) == true ? answer.Text : null);
        }

        return new TryItExplainResult(true, answer.Text);
    }
}
