using System;
using System.Threading;
using System.Threading.Tasks;
using Oranda.App.Features.Ai.External.Editor;

namespace Oranda.App.Features.Ai.External.Commands;

/// <summary>
/// 「띠 한 번」 등급의 확인: 탭 아래 에이전트 띠(편집 제안·저장 요청과 같은 줄)에 항목 하나를 세우고 사람의 답을 기다린다.
/// 단추는 띠의 「받아들이기」·「버리기」. 기다림이 끝나면 항목을 거둔다. 모달 창을 띄우지 않는다. UI 스레드에서 부른다.
/// </summary>
internal static class CommandConfirmBar
{
    /// <summary>사람을 기다리는 시간(저장 요청의 기본값과 같다).</summary>
    public static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(ExternalEditorService.DefaultWaitSeconds);

    public static async Task<CommandConfirmAnswer> AskAsync(AgentEditorBarViewModel bar, string? client, string title, string id, TimeSpan wait, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(bar);
        var done = new TaskCompletionSource<CommandConfirmAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
        AgentBarItem? item = null;
        item = new AgentBarItem(AgentBarItemKind.Proposal, CommandsText.ConfirmHeadline(string.IsNullOrWhiteSpace(client) ? CommandsText.DefaultClient : client.Trim()))
        {
            Detail = CommandsText.ConfirmDetail(title, id),
            Accept = () =>
            {
                done.TrySetResult(CommandConfirmAnswer.Accepted);
                bar.Remove(item!);
                return Task.FromResult<string?>(null);
            },
            Discard = () => done.TrySetResult(CommandConfirmAnswer.Rejected),
        };
        bar.Enqueue(item);
        try
        {
            await Task.WhenAny(done.Task, Task.Delay(wait <= TimeSpan.Zero ? TimeSpan.Zero : wait, ct)).ConfigureAwait(true);
        }
        finally
        {
            if (done.TrySetResult(CommandConfirmAnswer.Timeout))
            {
                bar.Remove(item);
            }
        }

        ct.ThrowIfCancellationRequested();
        return await done.Task.ConfigureAwait(true);
    }
}
