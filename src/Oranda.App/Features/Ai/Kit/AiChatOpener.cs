using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Oranda.App.Features.Ai.Chat;
using Oranda.App.Features.Ai.Settings;
using Oranda.App.Shell;

namespace Oranda.App.Features.Ai.Kit;

/// <summary>
/// 기능이 채팅창을 쓰는 데 필요한 바깥 동작. 앱은 <see cref="For"/>, 시험은 가짜를 넣는다.
/// </summary>
/// <param name="ShowChat">AI 채팅창을 앞으로 띄우고 그 모델을 돌려준다(못 띄우면 null).</param>
/// <param name="AskStop">진행 중인 작업을 멈추고 새 대화를 열지 묻는다(묻는 글 → 멈춤이면 true). null 이면 묻지 않고 열지 않는다.</param>
/// <param name="ConfirmText">가리는 대화에서 입력한 글이 가려지지 않고 나간다는 확인 창(대화당 한 번). null 이면 가리는 대화의 이어 묻기를 보내지 않는다.</param>
public sealed record AiChatHost(
    Func<Task<AiChatViewModel?>> ShowChat,
    Func<string, Task<bool>>? AskStop = null,
    Func<AiImageSendNotice, CancellationToken, Task<bool>>? ConfirmText = null)
{
    /// <summary>앱 기본: 셸 도구 창을 띄우고, 확인은 주 창 위 작은 확인 창·글 보내기 확인 창으로.</summary>
    public static AiChatHost For(IServiceProvider? services) => new(
        async () =>
        {
            (services?.GetService(typeof(IShellUi)) as IShellUi)?.Docking.ShowTool("oranda.panel.aiChat");
            for (var i = 0; i < 5 && AiChatViewModel.Current is null; i++)
            {
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            }

            return AiChatViewModel.Current;
        },
        async question => MainWindow(services) is { } owner && await AiConfirmDialog.ShowAsync(owner, question, "멈추고 새 대화", "취소").ConfigureAwait(true),
        async (notice, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return MainWindow(services) is { } owner && await AiTextWarningDialog.ShowAsync(owner, notice).ConfigureAwait(true);
        });

    private static Window? MainWindow(IServiceProvider? services) =>
        (services?.GetService(typeof(ShellController)) as ShellController)?.MainWindow
        ?? (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
}

/// <summary>
/// 채팅창에 기능 대화를 새로 연다(설계 §2.2): 채팅창을 앞으로 띄우고, 다른 대화 중이면 「새 대화」 를 누른 것과 같이 놓는다.
/// 앞 대화가 진행 중이면 먼저 멈출지 묻고, 멈추지 않으면 열지 않는다(앞 대화는 그대로).
/// </summary>
public static class AiChatOpener
{
    /// <summary>진행 중일 때 묻는 글.</summary>
    public const string StopQuestion = "AI 채팅창에서 다른 작업이 진행 중입니다. 멈추고 새 대화를 열까요?";

    /// <summary>멈춘 뒤에도 앞 작업이 끝나지 않았을 때 채팅창에 남기는 글.</summary>
    public const string StillRunningText = "앞 작업이 아직 멈추지 않아 새 대화를 열지 않았습니다. 잠시 뒤 다시 시도하세요.";

    /// <summary>멈춤을 누른 뒤 앞 작업이 끝나기를 기다리는 최대 시간.</summary>
    internal static TimeSpan StopWait { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>채팅창이 일하는 중이다(튜닝·명령·다른 기능 대화).</summary>
    public static bool IsBusy(AiChatViewModel chat)
    {
        ArgumentNullException.ThrowIfNull(chat);
        return chat.IsRunning || chat.IsBusy || chat.IsPreparing || chat.Owner is { IsRunning: true };
    }

    /// <summary>
    /// 채팅창을 띄우고 새 대화를 연다. 열었으면 대화를 돌려주고(이미 <see cref="AiFeatureConversation.Open"/> 됨), 못 열었으면 null.
    /// </summary>
    /// <param name="host">바깥 동작.</param>
    /// <param name="create">채팅창 모델로 대화를 만든다(열기 직전에 부른다).</param>
    public static async Task<T?> OpenAsync<T>(AiChatHost host, Func<AiChatViewModel, T> create)
        where T : AiFeatureConversation
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(create);
        if (await host.ShowChat().ConfigureAwait(true) is not { } chat)
        {
            return null;
        }

        if (IsBusy(chat))
        {
            if (host.AskStop is not { } ask || !await ask(StopQuestion).ConfigureAwait(true))
            {
                return null;
            }

            if (chat.StopCommand.CanExecute(null))
            {
                chat.StopCommand.Execute(null); // 튜닝 흐름·대화 주인 모두 멈춘다(채팅창의 「멈춤」 과 같다)
            }

            var until = DateTime.UtcNow + StopWait;
            while (IsBusy(chat) && DateTime.UtcNow < until)
            {
                await Task.Delay(50).ConfigureAwait(true);
            }

            if (IsBusy(chat))
            {
                chat.AddAssistantText(StillRunningText);
                return null;
            }
        }

        var conversation = create(chat);
        conversation.Open();
        return conversation;
    }
}
