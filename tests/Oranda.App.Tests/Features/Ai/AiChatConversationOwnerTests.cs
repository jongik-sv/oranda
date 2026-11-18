using Oranda.App.Features.Ai.Chat;

namespace Oranda.App.Tests.Features.Ai;

/// <summary>
/// 대화 창의 대화를 가진 흐름(<see cref="IAiChatConversation"/>, 튜닝 밖 — 예 AI 파티션 추천): 보내기·멈춤이 그 흐름으로 가고 튜닝 이벤트로 가지 않으며,
/// 새 대화(또는 튜닝 시작)가 열리면 놓는다. 기능 카드는 <see cref="ChatCardEntry"/> 로 들어간다.
/// </summary>
public class AiChatConversationOwnerTests
{
    private sealed class Owner : IAiChatConversation
    {
        public List<ChatFollowUp> FollowUps { get; } = [];

        public int Cancels { get; private set; }

        public Func<ChatFollowUp, Task>? OnFollowUp { get; set; }

        public bool IsRunning => false;

        public Task FollowUpAsync(ChatFollowUp followUp, CancellationToken ct)
        {
            FollowUps.Add(followUp);
            return OnFollowUp?.Invoke(followUp) ?? Task.CompletedTask;
        }

        public void CancelRun() => Cancels++;
    }

    [Fact]
    public void Send_and_stop_go_to_the_owner_instead_of_the_tuning_events()
    {
        var chat = new AiChatViewModel();
        var owner = new Owner();
        var raised = 0;
        var stops = 0;
        chat.FollowUpRequested += (_, _) => raised++;
        chat.StopRequested += (_, _) => stops++;

        chat.StartConversation("AI 파티션 추천 — ORDERS", owner);
        Assert.Same(owner, chat.Owner);
        Assert.False(chat.IsTuning);
        Assert.Equal("AI 파티션 추천 — ORDERS", chat.Title);

        chat.Draft = "왜?";
        chat.SendCommand.Execute(null);
        Assert.Equal("왜?", Assert.Single(owner.FollowUps).Text);
        Assert.Equal(0, raised);
        Assert.Equal(string.Empty, chat.Draft);

        chat.BeginTurn("x");
        chat.StopCommand.Execute(null);
        Assert.Equal(1, owner.Cancels);
        Assert.Equal(1, stops); // 튜닝 흐름의 멈춤 연결은 그대로(진행 중인 것이 없으면 아무 일도 없다)
        chat.EndTurn();

        chat.AddCard("card");
        Assert.Equal("card", Assert.IsType<ChatCardEntry>(chat.Entries[^1]).Card);
    }

    [Fact]
    public void A_new_or_tuning_conversation_releases_the_owner_and_owner_errors_are_shown()
    {
        var chat = new AiChatViewModel();
        var owner = new Owner { OnFollowUp = _ => throw new InvalidOperationException("boom") };
        chat.StartConversation("추천", owner);

        chat.Draft = "글";
        chat.SendCommand.Execute(null);
        Assert.Contains(chat.Entries.OfType<ChatAssistantEntry>(), e => e.Text.Contains("오류가 났습니다", StringComparison.Ordinal)); // 예외를 밖으로 내지 않는다

        var generation = chat.ConversationGeneration;
        chat.StartNewConversation();
        Assert.Null(chat.Owner);
        Assert.True(chat.ConversationGeneration > generation);

        chat.StartConversation("추천", owner);
        chat.StartConversation("튜닝 — SELECT 1");
        Assert.Null(chat.Owner);
        Assert.True(chat.IsTuning);
        var raised = 0;
        chat.FollowUpRequested += (_, _) => raised++;
        chat.Draft = "튜닝 이어 말하기";
        chat.SendCommand.Execute(null);
        Assert.Equal(1, raised);
        Assert.Single(owner.FollowUps); // 앞의 한 번뿐
    }
}
