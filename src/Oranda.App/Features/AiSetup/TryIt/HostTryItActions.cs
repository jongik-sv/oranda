using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Oranda.App.Features.Ai.Chat;
using Oranda.App.Features.Ai.External.Connect;
using Oranda.App.Features.Ai.Tuning;
using Oranda.App.Features.Ai.Tuning.Commands;
using Oranda.App.Features.ConnectionManager;
using Oranda.App.Features.SqlEditor;
using Oranda.Core.Connection;

namespace Oranda.App.Features.AiSetup.TryIt;

/// <summary>
/// 앱의 실제 길로 해 보기를 한다: 새 탭 = <see cref="SqlEditorHost.OpenTextTab"/>, SQL 설명 = 기존 명령을 <see cref="AiTuningLauncher.RunCommandAsync"/> 로,
/// 고급 = <see cref="ExternalAgentConnectCommands.ShowAsync"/>(부르기만). 튜닝·외부 에이전트 쪽 코드는 고치지 않는다.
/// <paramref name="services"/> 는 명령 처리자와 같은 앱 컨테이너여야 한다(런처가 컨테이너마다 하나다).
/// </summary>
public sealed class HostTryItActions(IServiceProvider services) : ITryItActions
{
    public Guid? OpenPracticeTab(ConnectionProfile profile, string title, string sql)
    {
        ArgumentNullException.ThrowIfNull(profile);
        try
        {
            return SqlEditorHost.Current.OpenTextTab(sql, title, profile.Id).TabId;
        }
        catch (InvalidOperationException)
        {
            return null; // 편집기가 아직 없다
        }
    }

    public string? CannotStartReason(ConnectionProfile profile, bool english)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (AiChatViewModel.Current is { } chat && (chat.IsRunning || chat.Owner is { IsRunning: true }))
        {
            return english ? "Another AI task is running. Press again when it finishes." : "다른 AI 작업이 도는 중이에요. 끝난 뒤 다시 눌러 주세요.";
        }

        if (services.GetService(typeof(IConnectionService)) is IConnectionService connections && connections.SessionOf(profile.Id) is null)
        {
            return english
                ? $"Connect to \"{profile.Name}\" first (connection manager), then press again."
                : $"먼저 「{profile.Name}」 에 접속한 뒤(접속 관리자) 다시 눌러 주세요.";
        }

        return null;
    }

    /// <summary>
    /// 명령은 대화 창에서 답이 끝날 때까지 돈 뒤 돌아온다. 새 대화가 열리지 않았으면(시작 거부) 그때 남긴 글을 이유로 돌려주고,
    /// 열렸으면 그 대화의 글과 완료 여부로 답을 읽는다(<see cref="TryItAnswerReader"/>).
    /// </summary>
    public async Task<TryItExplainResult> ExplainAsync(Guid tabId, CancellationToken ct)
    {
        if (!new HostEditorTabs().Activate(tabId))
        {
            return new TryItExplainResult(false, null, TabMissing: true); // 닫힌 연습 탭 대신 다른 탭의 SQL 이 나가지 않게 한다
        }

        var chatBefore = AiChatViewModel.Current;
        var generation = chatBefore?.ConversationGeneration;
        var countBefore = chatBefore?.Entries.Count ?? 0;
        await AiTuningLauncher.For(services).RunCommandAsync(ExplainSqlCommand.Instance, ct).ConfigureAwait(true);
        if (AiChatViewModel.Current is not { } chat)
        {
            return new TryItExplainResult(false, null);
        }

        if (ReferenceEquals(chat, chatBefore) && chat.ConversationGeneration == generation)
        {
            // 거부: 런처가 앞 대화 끝에 이유 한 줄을 남겼을 수 있다.
            var reason = chat.Entries.Count > countBefore ? chat.Entries.Skip(countBefore).OfType<ChatAssistantEntry>().LastOrDefault()?.Text : null;
            return new TryItExplainResult(false, null, reason);
        }

        return TryItAnswerReader.Read([.. chat.Entries], chat.ResultIsCompleted && !chat.IsRunning);
    }

    public Task OpenExternalAgentAsync(CancellationToken ct) => ExternalAgentConnectCommands.ShowAsync(services, ct);
}
