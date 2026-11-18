using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using Oranda.App.Features.Ai.Chat;
using Oranda.App.Features.TableEdit.Advisor;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Schema.TableEdit.Partitions.Advisor;
using static Oranda.App.Tests.Features.TableEdit.PartitionAdvisorTests;

namespace Oranda.App.Tests.Features.TableEdit;

/// <summary>
/// AI 파티션 추천을 AI 대화 창에서(PA-22): 응답 흐름 → 후보 카드·마무리 카드, 이어 묻기 = 같은 대화(ResumeId)와 차례마다 새 실행 범위,
/// 새 구조 결과마다 카드·앱 확인 다시, 「후보 3개 받기」, 못 읽은 응답·오류·멈춤, 새 대화로 바뀐 뒤. 가짜 드라이버는 실행 범위가 없거나 폐기됐으면 거절한다.
/// </summary>
public class PartitionAdvisorChatTests
{
    private const string Resume = "resume-1";

    private static IReadOnlyList<AiEvent> Answer(string json, string? resume = Resume) =>
        [new AiStarted(resume), new AiTextDelta(json), new AiFinal(null, JsonDocument.Parse(json).RootElement.Clone(), resume)];

    private static (PartitionAdviceConversation Conversation, AiChatViewModel Chat, List<AdvisorCandidate> Filled) Create(
        FakeDriver driver, AdvisorEngine? engine = null, List<AgentSession>? tracked = null)
    {
        var chat = new AiChatViewModel();
        var run = Run(driver, engine: engine);
        if (tracked is not null)
        {
            run = new PartitionAdvisorRun(run.Setup with { TrackSession = s => { tracked.Add(s); return s; } });
        }

        var filled = new List<AdvisorCandidate>();
        var conversation = new PartitionAdviceConversation(chat, run, Evidence(), new AdvicePromptOptions(AdviceGoal.Query, false, "ko"), new AdviceConversationHost
        {
            Fill = c =>
            {
                filled.Add(c);
                return true;
            },
        });
        return (conversation, chat, filled);
    }

    private static List<T> CardsOf<T>(AiChatViewModel chat) => [.. chat.Entries.OfType<ChatCardEntry>().Select(e => e.Card).OfType<T>()];

    [Fact]
    public async Task First_answer_streams_into_the_chat_then_becomes_a_summary_with_cards()
    {
        var driver = new FakeDriver { Answer = _ => Answer(AdviceJson(AdviceVerdict.Partition, Candidate(2, title: "B"), Candidate(1))) };
        var (conversation, chat, _) = Create(driver);

        await conversation.StartAsync();

        Assert.Same(conversation, chat.Owner);
        Assert.False(chat.IsTuning);
        var user = Assert.IsType<ChatUserEntry>(chat.Entries[0]);
        Assert.Contains("SAMPLE.ORDERS", user.Text, StringComparison.Ordinal);
        Assert.Contains("값 가림", user.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(chat.Entries.OfType<ChatAssistantEntry>(), e => e.Text.Contains("\"verdict\"", StringComparison.Ordinal)); // 흘러온 JSON 은 걷어 낸다
        Assert.Contains(chat.Entries.OfType<ChatAssistantEntry>(), e => e.Text == "월 범위가 좋습니다");
        var cards = CardsOf<AdviceCandidateCard>(chat);
        Assert.Equal([1, 2], cards.Select(c => c.Candidate.Candidate.Rank));
        var result = Assert.Single(CardsOf<AdviceResultCard>(chat));
        Assert.Equal(["STATUS — 값이 6개뿐"], result.NotChosenLines);
        Assert.True(result.HasRawText);
        Assert.False(result.ShowsRequestCandidates);
        Assert.Equal(Resume, chat.ResumeId);
        Assert.False(chat.IsRunning);
        Assert.True(chat.ResultIsCompleted);
        Assert.Null(Assert.Single(driver.Requests).ResumeId);
    }

    [Fact]
    public async Task Follow_up_continues_the_same_conversation_with_a_fresh_session_and_rechecks_new_cards()
    {
        var checks = 0;
        var engine = Engine(c =>
        {
            checks++;
            return [new AdviceCheck(AdviceCheckLevel.Ok, AdviceCheckCodes.LocalOk, "PK_ORDERS")];
        });
        var tracked = new List<AgentSession>();
        var driver = new FakeDriver
        {
            Answer = r => r.ResumeId is null
                ? Answer(AdviceJson(AdviceVerdict.Partition, Candidate(1)))
                : Answer(AdviceJson(AdviceVerdict.Partition, Candidate(1, AdviceMethod.Hash, "HASH(ORDER_ID) 8"), Candidate(2))),
        };
        var (conversation, chat, _) = Create(driver, engine, tracked);
        await conversation.StartAsync();
        Assert.Equal(1, checks);

        chat.Draft = "왜 HASH 는 아닌가?";
        chat.SendCommand.Execute(null);
        await WaitIdleAsync(conversation);

        Assert.Equal(2, driver.Requests.Count);
        var follow = driver.Requests[1];
        Assert.Equal(Resume, follow.ResumeId); // 같은 대화
        Assert.StartsWith("# 이어 묻기", follow.Prompt, StringComparison.Ordinal);
        Assert.Contains("왜 HASH 는 아닌가?", follow.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("PROMPT ORDERS", follow.Prompt, StringComparison.Ordinal); // 이어받기 id 가 있으면 근거를 다시 싣지 않는다
        Assert.Equal(PartitionAdvice.Schema().GetRawText(), follow.OutputSchema!.Value.GetRawText());
        Assert.Equal(2, tracked.Count); // 차례마다 새 실행 범위(드라이버가 늘 요구한다)
        Assert.NotSame(tracked[0], tracked[1]);
        Assert.Same(tracked[1], follow.Session);
        Assert.Equal([false, false], driver.RevokedAtStart);
        Assert.All(tracked, s => Assert.True(s.IsRevoked));
        Assert.Equal(3, checks); // 새 결과의 후보 둘을 다시 확인
        Assert.Equal(3, CardsOf<AdviceCandidateCard>(chat).Count);
        Assert.Equal(2, CardsOf<AdviceResultCard>(chat).Count);
        Assert.Equal(2, chat.Entries.OfType<ChatUserEntry>().Count());
        Assert.Equal("왜 HASH 는 아닌가?", chat.Entries.OfType<ChatUserEntry>().Last().Text);
        Assert.Equal(2, conversation.Results.Count);
        Assert.Equal(AdviceMethod.Hash, conversation.LastOutcome!.Candidates[0].Candidate.Method);
    }

    [Fact]
    public async Task Follow_up_without_a_resume_id_repeats_the_evidence_prompt()
    {
        var driver = new FakeDriver { Answer = _ => Answer(AdviceJson(AdviceVerdict.Partition, Candidate(1)), resume: null) };
        var (conversation, chat, _) = Create(driver);
        await conversation.StartAsync();

        await conversation.FollowUpAsync(new ChatFollowUp("다른 안은?", []), CancellationToken.None);

        var follow = driver.Requests[1];
        Assert.Null(follow.ResumeId);
        Assert.StartsWith("PROMPT ORDERS", follow.Prompt, StringComparison.Ordinal);
        Assert.Contains("# 이어 묻기\n다른 안은?", follow.Prompt, StringComparison.Ordinal);
        Assert.NotNull(follow.Session);
        Assert.Equal(2, CardsOf<AdviceResultCard>(chat).Count);
    }

    [Fact]
    public async Task No_partition_offers_three_options_in_the_same_conversation()
    {
        var driver = new FakeDriver
        {
            Answer = r => r.Prompt.Contains("반드시 1~3개", StringComparison.Ordinal)
                ? Answer(AdviceJson(AdviceVerdict.Partition, Candidate(1), Candidate(2), Candidate(3)))
                : Answer(AdviceJson(AdviceVerdict.NoPartition)),
        };
        var (conversation, chat, _) = Create(driver);
        await conversation.StartAsync();

        var result = Assert.Single(CardsOf<AdviceResultCard>(chat));
        Assert.True(result.IsNoPartition);
        Assert.True(result.ShowsRequestCandidates);
        Assert.Contains(chat.Entries.OfType<ChatAssistantEntry>(), e => e.Text.Contains("파티션 안 함 권장", StringComparison.Ordinal) && e.Text.Contains("행이 적어", StringComparison.Ordinal));
        Assert.Empty(CardsOf<AdviceCandidateCard>(chat));
        Assert.True(result.RequestCandidatesCommand.CanExecute(null));

        await result.RequestCandidatesCommand.ExecuteAsync(null);

        var again = driver.Requests[^1];
        Assert.Equal(Resume, again.ResumeId);
        Assert.Contains("반드시 1~3개", again.Prompt, StringComparison.Ordinal);
        Assert.Equal("후보 3개 받기", chat.Entries.OfType<ChatUserEntry>().Last().Text);
        Assert.Equal(3, CardsOf<AdviceCandidateCard>(chat).Count);
        Assert.False(CardsOf<AdviceResultCard>(chat)[^1].ShowsRequestCandidates);
    }

    [Fact]
    public async Task Unreadable_answer_keeps_the_raw_text_and_an_error_adds_no_cards()
    {
        var driver = new FakeDriver { Answer = _ => [new AiTextDelta("모양 없는 글"), new AiFinal(null, null, null)] };
        var (conversation, chat, _) = Create(driver);
        await conversation.StartAsync();

        Assert.Contains(chat.Entries.OfType<ChatAssistantEntry>(), e => e.Text == "모양 없는 글"); // 원문은 대화에 남는다
        var result = Assert.Single(CardsOf<AdviceResultCard>(chat));
        Assert.True(result.IsUnreadable);
        Assert.Equal("모양 없는 글", result.RawText);
        Assert.Empty(CardsOf<AdviceCandidateCard>(chat));

        driver.Answer = _ => [new AiError("profile_changed")];
        await conversation.FollowUpAsync(new ChatFollowUp("다시", []), CancellationToken.None);
        Assert.Contains(chat.Entries.OfType<ChatAssistantEntry>(), e => e.Text.StartsWith("오류:", StringComparison.Ordinal));
        Assert.Single(CardsOf<AdviceResultCard>(chat)); // 오류 차례는 카드를 더하지 않는다
        Assert.True(chat.ResultIsFailed);
    }

    [Fact]
    public async Task Stop_cancels_the_turn_and_revokes_its_session()
    {
        var driver = new FakeDriver { Gate = new TaskCompletionSource() };
        var (conversation, chat, _) = Create(driver);
        var pending = conversation.StartAsync();
        await Task.Yield();
        Assert.True(chat.IsRunning);
        Assert.True(conversation.IsRunning);
        var session = Assert.Single(driver.Requests).Session!;

        chat.StopCommand.Execute(null);
        await pending;

        Assert.True(session.IsRevoked);
        Assert.False(chat.IsRunning);
        Assert.False(conversation.IsRunning);
        Assert.Contains("멈춤", chat.ResultText, StringComparison.Ordinal);
        Assert.Empty(CardsOf<AdviceResultCard>(chat));
    }

    [Fact]
    public async Task Follow_ups_are_refused_while_running_with_images_or_after_a_new_conversation()
    {
        var driver = new FakeDriver { Answer = _ => Answer(AdviceJson(AdviceVerdict.Partition, Candidate(1))) };
        var (conversation, chat, filled) = Create(driver);
        await conversation.StartAsync();
        var card = Assert.Single(CardsOf<AdviceCandidateCard>(chat));

        await conversation.FollowUpAsync(new ChatFollowUp("그림", [new ChatImage(new AiNormalizedImage(new AiImageAttachment("image/png", [1, 2]), [1], 1, 1, false))]), CancellationToken.None);
        Assert.Single(driver.Requests);
        Assert.Equal("그림", chat.Draft); // 되돌림
        Assert.Contains(chat.Entries.OfType<ChatAssistantEntry>(), e => e.Text.Contains("이미지를 보낼 수 없습니다", StringComparison.Ordinal));

        chat.StartNewConversation(); // 사용자가 「새 대화」
        Assert.Null(chat.Owner);
        Assert.False(conversation.IsCurrent);
        chat.Draft = "이어서";
        chat.SendCommand.Execute(null); // 새 대화의 보내기는 이 추천 대화로 오지 않는다
        await conversation.FollowUpAsync(new ChatFollowUp("늦게", []), CancellationToken.None);
        Assert.Single(driver.Requests);

        Assert.True(card.Fill()); // 채우기는 카드가 들고 있는 편집 세션 동작으로(객체 상세가 세션을 확인한다)
        Assert.Single(filled);
    }

    [Fact]
    public Task Chat_view_draws_candidate_cards_with_checks_and_disables_fill_on_a_stop_card() => HeadlessHost.Run(async () =>
    {
        var engine = Engine(c => c.Rank == 2 ? [new AdviceCheck(AdviceCheckLevel.Stop, AdviceCheckCodes.IntervalNull, "ORDER_DATE")] : []);
        var driver = new FakeDriver { Answer = _ => Answer(AdviceJson(AdviceVerdict.Partition, Candidate(1), Candidate(2, title: "B"))) };
        var (conversation, chat, filled) = Create(driver, engine);
        await conversation.StartAsync();
        var window = new Window { Width = 520, Height = 900, Content = new AiChatView { DataContext = chat } };
        window.Show();
        try
        {
            HeadlessHost.Settle(window);
            var fills = window.GetVisualDescendants().OfType<Button>().Where(b => b.Name == "AdviceFillButton").ToList();
            Assert.Equal(2, fills.Count);
            Assert.True(fills[0].IsEffectivelyEnabled);
            Assert.False(fills[1].IsEffectivelyEnabled); // 명령의 CanExecute 가 끈다
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "✖" && t.Classes.Contains("stop"));
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Name == "AdviceCardHeader" && t.Text == "1안 RANGE(ORDER_DATE) 월");

            fills[0].Command!.Execute(null);
            HeadlessHost.Settle(window);
            Assert.Equal([1], filled.Select(c => c.Candidate.Rank));
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Name == "AdviceFilledMark" && t.IsEffectivelyVisible);

            var result = CardsOf<AdviceResultCard>(chat).Single();
            Assert.Contains(window.GetVisualDescendants().OfType<ToggleButton>(), t => t.Name == "AdviceRawToggle" && t.IsEffectivelyVisible);
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>(), b => b.Name == "AdviceRequestCandidatesButton" && b.IsEffectivelyVisible);
            result.IsNotChosenOpen = true;
            result.IsRawOpen = true;
            HeadlessHost.Settle(window);
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "STATUS — 값이 6개뿐" && t.IsEffectivelyVisible);
            Assert.Contains(window.GetVisualDescendants().OfType<TextBox>(), t => t.Name == "AdviceRawBox" && t.IsEffectivelyVisible && t.Text!.Contains("\"verdict\"", StringComparison.Ordinal));
        }
        finally
        {
            window.Close();
        }
    });

    /// <summary>보내기 단추가 시작한 이어 묻기가 끝날 때까지.</summary>
    private static async Task WaitIdleAsync(PartitionAdviceConversation conversation)
    {
        for (var i = 0; i < 50 && conversation.IsRunning; i++)
        {
            await Task.Delay(10);
        }

        await Task.Yield();
    }
}
