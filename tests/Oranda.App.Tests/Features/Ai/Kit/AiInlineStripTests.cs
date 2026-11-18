using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.Ai.Chat;
using Oranda.App.Features.Ai.Kit;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Connection;
using Oranda.App.Tests.Shell.Headless;

namespace Oranda.App.Tests.Features.Ai.Kit;

/// <summary>
/// AI 띠(설계 §2.1)와 채팅창 쪽(§2.2): 요청·멈춤·다시·복사·거부, 답 틀, 「채팅에서 이어 보기」(다시 보내지 않음 · 이어 묻기는 같은 경로·같은 모델),
/// 채팅창이 다른 대화 중일 때 새 대화로(진행 중이면 멈출지 물음), 대화 주인 공통 거절. 화면은 Avalonia Headless 로.
/// </summary>
public class AiInlineStripTests
{
    private static readonly AiAnswerTemplate Template = new([
        new AiAnswerSection("상황", "one line"),
        new AiAnswerSection("권하는 행동", "one line"),
    ]);

    private const string TemplatedAnswer = "## 상황\n812 가 막음\n\n## 권하는 행동\n담당에게 연락";

    private static (AiInlineStripViewModel Strip, FakeDriver Driver, List<int> Prepared) Create(
        FakeDriver? driver = null, AiChatViewModel? chat = null, Func<string, Task<bool>>? askStop = null, ConnectionProfile? profile = null, Func<AiImageSendNotice, CancellationToken, Task<bool>>? confirmText = null)
    {
        driver ??= new FakeDriver { Answer = _ => KitFakes.Answer(TemplatedAnswer) };
        var prepared = new List<int>();
        var run = KitFakes.Run(driver, profile);
        var strip = new AiInlineStripViewModel("상황 풀이", _ =>
        {
            prepared.Add(prepared.Count + 1);
            var bundle = run.Route.Bundle("상황 풀이", new KitMasker())
                .Add("chain", "사슬", "SID 812 → 1043 (" + prepared.Count + ")", AiAccessLevel.Metadata)
                .Build();
            return Task.FromResult(AiStripPreparation.Ready(new AiStripRequest(run, bundle, "Explain the lock chain.", Template)));
        }, chat is null ? null : KitFakes.Host(chat, askStop, confirmText));
        return (strip, driver, prepared);
    }

    [Fact]
    public async Task Request_shows_the_templated_answer_and_the_model_that_answered()
    {
        var (strip, driver, _) = Create();
        Assert.False(strip.IsVisible);

        await strip.RequestCommand.ExecuteAsync(null);

        Assert.Equal(AiStripState.Done, strip.State);
        Assert.True(strip.IsVisible);
        Assert.Equal("haiku", strip.ModelLabel);
        Assert.True(strip.HasParts);
        Assert.Equal("812 가 막음", strip.Answer!["상황"]!.Body);
        var prompt = Assert.Single(driver.Requests).Prompt;
        Assert.StartsWith("Explain the lock chain.", prompt, StringComparison.Ordinal);
        Assert.Contains("## 권하는 행동", prompt, StringComparison.Ordinal); // 답 틀
        Assert.Contains("<context name=\"chain\">", prompt, StringComparison.Ordinal); // 꾸러미
        Assert.Equal(prompt, strip.LastRequest!.Prompt);
    }

    [Fact]
    public async Task A_broken_template_shows_the_text_as_it_came()
    {
        var (strip, _, _) = Create(new FakeDriver { Answer = _ => KitFakes.Answer("그냥 글로 답합니다") });

        await strip.RunAsync(CancellationToken.None);

        Assert.Equal(AiStripState.Done, strip.State);
        Assert.False(strip.HasParts);
        Assert.True(strip.ShowsRawText);
        Assert.Equal("그냥 글로 답합니다", strip.AnswerText);
    }

    [Fact]
    public async Task Stop_ends_the_request_and_retry_collects_the_screen_again()
    {
        var driver = new FakeDriver { Gate = new TaskCompletionSource(), Answer = _ => KitFakes.Answer(TemplatedAnswer) };
        var (strip, _, prepared) = Create(driver);

        var running = strip.RunAsync(CancellationToken.None);
        await KitFakes.WaitAsync(() => driver.Requests.Count == 1 && strip.AnswerText.Length > 0);
        Assert.True(strip.IsRunning);
        Assert.Equal("부분 ", strip.AnswerText); // 흘러오는 글이 띠에 보인다
        Assert.True(strip.StopCommand.CanExecute(null));
        Assert.False(strip.RequestCommand.CanExecute(null));
        strip.StopCommand.Execute(null);
        await running;

        Assert.Equal(AiStripState.Stopped, strip.State);
        Assert.Equal("멈춤", strip.StatusText);
        Assert.True(driver.Requests[0].Session!.IsRevoked);
        Assert.False(strip.ContinueInChatCommand.CanExecute(null));

        driver.Gate = null;
        await strip.RetryCommand.ExecuteAsync(null);
        Assert.Equal(AiStripState.Done, strip.State);
        Assert.Equal([1, 2], prepared);
        Assert.Contains("(2)", driver.Requests[1].Prompt, StringComparison.Ordinal); // 다시 = 지금 화면으로 다시 모음
    }

    [Fact]
    public async Task Failure_refusal_and_cancelled_confirm_do_not_pretend_success()
    {
        var (failed, _, _) = Create(new FakeDriver { Answer = _ => [new AiError("연결 실패", "network")] });
        await failed.RunAsync(CancellationToken.None);
        Assert.Equal(AiStripState.Failed, failed.State);
        Assert.StartsWith("오류: ", failed.StatusText, StringComparison.Ordinal);
        Assert.True(failed.RetryCommand.CanExecute(null));

        var driver = new FakeDriver();
        var refused = new AiInlineStripViewModel("풀이", _ => Task.FromResult(AiStripPreparation.Refuse("고른 사슬이 없습니다")));
        await refused.RunAsync(CancellationToken.None);
        Assert.Equal(AiStripState.Refused, refused.State);
        Assert.Equal("고른 사슬이 없습니다", refused.StatusText);

        var confirmDriver = new FakeDriver();
        var run = KitFakes.Run(confirmDriver);
        var asked = new List<string>();
        var confirmed = new AiInlineStripViewModel("상황 풀이", _ => Task.FromResult(AiStripPreparation.Ready(
            new AiStripRequest(run, run.Route.Bundle("상황 풀이", null).Add("chain", "사슬", "SID 1", AiAccessLevel.Metadata).Build(), "x"))))
        {
            Confirm = (request, _) =>
            {
                asked.Add(request.Bundle.PreviewText);
                return Task.FromResult(false);
            },
        };
        await confirmed.RunAsync(CancellationToken.None);
        Assert.Equal(AiStripState.Refused, confirmed.State);
        Assert.Equal(AiInlineStripViewModel.CancelledText, confirmed.StatusText);
        Assert.Contains("■ 사슬", Assert.Single(asked), StringComparison.Ordinal); // 경고 창은 꾸러미를 그대로 본다
        Assert.Empty(confirmDriver.Requests);
        Assert.Empty(driver.Requests);
    }

    [Fact]
    public async Task Copy_writes_the_answer_text()
    {
        var copied = new List<string>();
        var driver = new FakeDriver { Answer = _ => KitFakes.Answer(TemplatedAnswer) };
        var run = KitFakes.Run(driver);
        var strip = new AiInlineStripViewModel("풀이", _ => Task.FromResult(AiStripPreparation.Ready(new AiStripRequest(run, run.Route.Bundle("풀이", null).Build(), "x"))))
        {
            CopyText = t =>
            {
                copied.Add(t);
                return Task.CompletedTask;
            },
        };
        Assert.False(strip.CopyCommand.CanExecute(null));

        await strip.RunAsync(CancellationToken.None);
        await strip.CopyCommand.ExecuteAsync(null);

        Assert.Equal([TemplatedAnswer], copied);
    }

    // ---- 등급(설계 §1.2·AE-1) -----------------------------------------------------

    private static (AiInlineStripViewModel Strip, FakeDriver Driver, List<AiTier?> Tiers) CreateTiered(FakeDriver driver, AiChatViewModel? chat = null)
    {
        var tiers = new List<AiTier?>();
        var profile = KitFakes.Profile(access: AiAccessLevel.Plan);
        var settings = new Oranda.Core.Settings.AiSettings { Model = "opus", Light = new Oranda.Core.Settings.AiLightSettings { Model = "haiku" } };
        var strip = new AiInlineStripViewModel("상황 풀이", (tier, _) =>
        {
            tiers.Add(tier);
            var run = new AiOneShotRun(new AiOneShotSetup(driver, AiKitRoute.For(profile, settings, tier ?? AiTier.Light), "lock_explain"));
            return Task.FromResult(AiStripPreparation.Ready(new AiStripRequest(run, run.Route.Bundle("상황 풀이", null).Build(), "x", Template)));
        }, chat is null ? null : KitFakes.Host(chat));
        return (strip, driver, tiers);
    }

    [Fact]
    public async Task Light_request_goes_with_its_tier_and_shows_the_model_the_router_chose()
    {
        var driver = new FakeDriver { Answer = _ => [new AiModelChosen("opus", AiTier.Main, AiTier.Light), .. KitFakes.Answer(TemplatedAnswer)] };
        var (strip, _, tiers) = CreateTiered(driver);

        await strip.RunAsync(CancellationToken.None);

        Assert.Equal([null], tiers);
        Assert.Equal(AiTier.Light, Assert.Single(driver.Requests).Tier);
        Assert.Equal("opus", strip.ModelLabel); // 라우터가 크기 때문에 주 모델로 올렸다
        Assert.Equal(AiInlineStripViewModel.EscalatedText, strip.StatusText);
        Assert.Equal("opus", strip.LastResult!.ModelLabel);
        Assert.False(strip.ShowsRetryOnMain);
    }

    [Fact]
    public async Task Light_failure_offers_retry_on_main_which_collects_again_for_the_main_model()
    {
        var driver = new FakeDriver { Answer = r => r.Tier == AiTier.Light ? [new AiError("로컬 서버 응답 없음", "network") { RetryOnMain = true }] : KitFakes.Answer(TemplatedAnswer) };
        var (strip, _, tiers) = CreateTiered(driver);

        await strip.RunAsync(CancellationToken.None);
        Assert.Equal(AiStripState.Failed, strip.State);
        Assert.True(strip.ShowsRetryOnMain);
        Assert.Equal("haiku", strip.ModelLabel);

        await strip.RetryOnMainCommand.ExecuteAsync(null);

        Assert.Equal([null, AiTier.Main], tiers);
        Assert.Equal(AiTier.Main, driver.Requests[1].Tier);
        Assert.Equal(AiStripState.Done, strip.State);
        Assert.False(strip.ShowsRetryOnMain);
        Assert.Equal("opus", strip.ModelLabel);
    }

    [Fact]
    public async Task Fixed_tier_strip_never_offers_retry_on_main()
    {
        var driver = new FakeDriver { Answer = _ => [new AiError("실패", "x") { RetryOnMain = true }] };
        var run = new AiOneShotRun(new AiOneShotSetup(driver, AiKitRoute.For(KitFakes.Profile(access: AiAccessLevel.Plan), new Oranda.Core.Settings.AiSettings(), AiTier.Light), "f"));
        var strip = new AiInlineStripViewModel("검토", _ => Task.FromResult(AiStripPreparation.Ready(new AiStripRequest(run, run.Route.Bundle("검토", null).Build(), "x"))));

        await strip.RunAsync(CancellationToken.None);

        Assert.Equal(AiStripState.Failed, strip.State);
        Assert.False(strip.ShowsRetryOnMain);
    }

    [Fact]
    public async Task Carried_follow_up_keeps_the_first_request_tier()
    {
        var chat = new AiChatViewModel();
        var driver = new FakeDriver { Answer = _ => KitFakes.Answer(TemplatedAnswer) };
        var (strip, _, _) = CreateTiered(driver, chat);
        await strip.RunAsync(CancellationToken.None);
        await strip.ContinueInChatCommand.ExecuteAsync(null);

        await strip.Carried!.FollowUpAsync(new ChatFollowUp("더?", []), CancellationToken.None);

        Assert.Equal([AiTier.Light, AiTier.Light], driver.Requests.Select(r => r.Tier));
        Assert.Equal(driver.Requests[0].Model, driver.Requests[1].Model);
    }

    // ---- 채팅에서 이어 보기 -------------------------------------------------------

    [Fact]
    public async Task Continue_in_chat_moves_the_sent_context_and_answer_without_sending_again()
    {
        var chat = new AiChatViewModel();
        var (strip, driver, _) = Create(chat: chat);
        await strip.RunAsync(CancellationToken.None);

        await strip.ContinueInChatCommand.ExecuteAsync(null);

        Assert.Single(driver.Requests); // 다시 보내지 않는다
        var carried = Assert.IsType<AiCarriedConversation>(chat.Owner);
        Assert.Same(carried, strip.Carried);
        Assert.Equal("상황 풀이", chat.Title);
        Assert.Equal(carried.FirstLine, Assert.IsType<ChatUserEntry>(chat.Entries[0]).Text);
        Assert.Equal(TemplatedAnswer, Assert.IsType<ChatAssistantEntry>(chat.Entries[1]).Text);
        Assert.Contains("haiku", Assert.IsType<ChatAssistantEntry>(chat.Entries[2]).Text, StringComparison.Ordinal);
        Assert.False(chat.IsRunning);
    }

    [Fact]
    public async Task Follow_up_in_chat_goes_the_same_way_and_model_with_the_resume_id()
    {
        var chat = new AiChatViewModel();
        var (strip, driver, _) = Create(chat: chat);
        await strip.RunAsync(CancellationToken.None);
        await strip.ContinueInChatCommand.ExecuteAsync(null);
        driver.Answer = _ => KitFakes.Answer("10분 기다리세요", resume: "resume-2");

        chat.Draft = "기다려도 되나?";
        chat.SendCommand.Execute(null);
        await KitFakes.WaitAsync(() => driver.Requests.Count == 2 && !chat.IsRunning);

        var first = driver.Requests[0];
        var follow = driver.Requests[1];
        Assert.Equal("resume-1", follow.ResumeId);
        Assert.Equal(first.Expected, follow.Expected); // 같은 받는 곳
        Assert.Equal(first.Model, follow.Model); // 같은 모델
        Assert.Equal(first.Context, follow.Context);
        Assert.StartsWith("# Follow-up\n기다려도 되나?", follow.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("<context name=\"chain\">", follow.Prompt, StringComparison.Ordinal); // 이어받기 id 가 있으면 문맥을 다시 싣지 않는다
        Assert.NotSame(first.Session, follow.Session);
        Assert.True(follow.Session!.IsRevoked);
        Assert.Equal("기다려도 되나?", chat.Entries.OfType<ChatUserEntry>().Last().Text);
        Assert.Equal("10분 기다리세요", chat.Entries.OfType<ChatAssistantEntry>().Last().Text);
        Assert.True(chat.ResultIsCompleted);
    }

    [Fact]
    public async Task Follow_up_without_a_resume_id_carries_the_first_prompt_and_answer()
    {
        var chat = new AiChatViewModel();
        var (strip, driver, _) = Create(new FakeDriver { Answer = _ => KitFakes.Answer(TemplatedAnswer, resume: null) }, chat);
        await strip.RunAsync(CancellationToken.None);
        await strip.ContinueInChatCommand.ExecuteAsync(null);

        await strip.Carried!.FollowUpAsync(new ChatFollowUp("왜?", []), CancellationToken.None);
        await strip.Carried.FollowUpAsync(new ChatFollowUp("그다음?", []), CancellationToken.None);

        var second = driver.Requests[1];
        Assert.Null(second.ResumeId);
        Assert.StartsWith(strip.LastRequest!.Prompt, second.Prompt, StringComparison.Ordinal);
        Assert.Contains("<previous_answer>\n" + TemplatedAnswer, second.Prompt, StringComparison.Ordinal);
        var third = driver.Requests[2].Prompt;
        Assert.Contains("<previous_question>\n왜?", third, StringComparison.Ordinal);
        Assert.EndsWith("Never write tool or function names.", third, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Continue_replaces_another_conversation_and_asks_before_stopping_a_running_one()
    {
        var chat = new AiChatViewModel();
        chat.StartConversation("튜닝 — SELECT 1");
        chat.AddAssistantText("앞 대화");
        var questions = new List<string>();
        var answerStop = false;
        var (strip, _, _) = Create(chat: chat, askStop: q =>
        {
            questions.Add(q);
            return Task.FromResult(answerStop);
        });
        await strip.RunAsync(CancellationToken.None);

        // 쉬는 다른 대화: 묻지 않고 「새 대화」 처럼 놓는다
        await strip.ContinueInChatCommand.ExecuteAsync(null);
        Assert.Empty(questions);
        Assert.IsType<AiCarriedConversation>(chat.Owner);
        Assert.DoesNotContain(chat.Entries.OfType<ChatAssistantEntry>(), e => e.Text == "앞 대화");

        // 진행 중인 다른 대화: 묻고, 아니라면 그대로 둔다
        var busy = new BusyOwner();
        chat.StartConversation("다른 기능", busy);
        chat.BeginTurn("진행 중");
        await strip.ContinueInChatCommand.ExecuteAsync(null);
        Assert.Equal([AiChatOpener.StopQuestion], questions);
        Assert.Same(busy, chat.Owner);
        Assert.False(busy.Cancelled);

        // 멈추겠다면 멈춘 뒤 새 대화로
        answerStop = true;
        busy.OnCancel = chat.EndTurn;
        await strip.ContinueInChatCommand.ExecuteAsync(null);
        Assert.True(busy.Cancelled);
        Assert.IsType<AiCarriedConversation>(chat.Owner);
        Assert.False(chat.IsRunning);
    }

    private sealed class BusyOwner : IAiChatConversation
    {
        public bool Cancelled { get; private set; }

        public Action? OnCancel { get; set; }

        public bool IsRunning => !Cancelled;

        public Task FollowUpAsync(ChatFollowUp followUp, CancellationToken ct) => Task.CompletedTask;

        public void CancelRun()
        {
            Cancelled = true;
            OnCancel?.Invoke();
        }
    }

    [Fact]
    public async Task Carried_conversation_refuses_follow_ups_it_cannot_send()
    {
        var chat = new AiChatViewModel();
        var (strip, driver, _) = Create(chat: chat);
        await strip.RunAsync(CancellationToken.None);
        await strip.ContinueInChatCommand.ExecuteAsync(null);
        var carried = strip.Carried!;

        await carried.FollowUpAsync(new ChatFollowUp("이미지", [new ChatImage(new AiNormalizedImage(new AiImageAttachment("image/png", [1]), [1], 1, 1, false))]), CancellationToken.None);
        Assert.Contains(chat.Entries.OfType<ChatAssistantEntry>(), e => e.Text == AiFeatureConversation.ImagesRefusedText);

        chat.StartNewConversation(); // 대화가 바뀐 뒤에는 이 대화로 보내지 않는다
        await carried.FollowUpAsync(new ChatFollowUp("늦은 질문", []), CancellationToken.None);
        Assert.Single(driver.Requests);
        Assert.False(carried.IsCurrent);
    }

    [Fact]
    public async Task Masked_follow_up_asks_once_whether_typed_text_may_go_unmasked()
    {
        var chat = new AiChatViewModel();
        var notices = new List<AiImageSendNotice>();
        var allow = false;
        var (strip, driver, _) = Create(chat: chat, profile: KitFakes.Profile(ConnectionEnvironment.Production, AiAccessLevel.Plan), confirmText: (n, _) =>
        {
            notices.Add(n);
            return Task.FromResult(allow);
        });
        await strip.RunAsync(CancellationToken.None);
        await strip.ContinueInChatCommand.ExecuteAsync(null);

        await strip.Carried!.FollowUpAsync(new ChatFollowUp("값 A100 은?", []), CancellationToken.None);
        Assert.Single(driver.Requests); // 취소 = 보내지 않음
        Assert.True(Assert.Single(notices).IsProduction);

        allow = true;
        await strip.Carried.FollowUpAsync(new ChatFollowUp("값 A100 은?", []), CancellationToken.None);
        await strip.Carried.FollowUpAsync(new ChatFollowUp("또?", []), CancellationToken.None);
        Assert.Equal(3, driver.Requests.Count);
        Assert.Equal(2, notices.Count); // 확인한 뒤로는 다시 묻지 않는다
    }

    // ---- 화면 -------------------------------------------------------------------

    [Fact]
    public Task Strip_view_shows_buttons_for_each_state() => HeadlessHost.Run(async () =>
    {
        var chat = new AiChatViewModel();
        var driver = new FakeDriver { Gate = new TaskCompletionSource(), Answer = _ => KitFakes.Answer(TemplatedAnswer) };
        var (strip, _, _) = Create(driver, chat);
        var view = new AiInlineStrip { DataContext = strip };
        var window = new Window { Width = 520, Height = 360, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.False(view.IsVisible); // 묻기 전에는 숨김

        var running = strip.RunAsync(CancellationToken.None);
        await HeadlessHost.WaitUntil(() => driver.Requests.Count == 1);
        Dispatcher.UIThread.RunJobs();
        Assert.True(view.IsVisible);
        Assert.True(Find<Button>(view, "StripStop").IsVisible);
        Assert.False(Find<Button>(view, "StripRetry").IsVisible);
        Assert.Contains("답을 받는 중", Find<TextBlock>(view, "StripStatus").Text, StringComparison.Ordinal);

        driver.Gate.SetResult();
        await running;
        Dispatcher.UIThread.RunJobs();
        Assert.False(Find<Button>(view, "StripStop").IsVisible);
        Assert.True(Find<Button>(view, "StripRetry").IsVisible);
        Assert.True(Find<Button>(view, "StripCopy").IsVisible);
        Assert.True(Find<Button>(view, "StripContinue").IsVisible);
        Assert.Equal("✨ AI · 상황 풀이", Find<TextBlock>(view, "StripTitle").Text);
        Assert.Equal("haiku", Find<TextBlock>(view, "StripModel").Text);
        Assert.False(Find<ItemsControl>(view, "StripParts").IsVisible); // 흘러온 「부분 」 머리말 때문에 틀이 깨졌다 → 받은 글 그대로
        Assert.True(Find<MarkdownView>(view, "StripRaw").IsVisible);
        window.Close();
    });

    [Fact]
    public Task Feature_card_draws_in_the_chat_with_its_buttons() => HeadlessHost.Run(() =>
    {
        var chat = new AiChatViewModel();
        chat.StartConversation("계획이 바뀐 원인", new BusyOwner());
        var inserted = new List<string>();
        var card = new AiFeatureCard("계획이 바뀐 원인", "opus", TemplatedAnswer, Template.Parse(TemplatedAnswer))
        {
            InsertText = "EXEC DBMS_SPM.LOAD_PLANS_FROM_CURSOR_CACHE(...)",
            Insert = t =>
            {
                inserted.Add(t);
                return true;
            },
            Deeper = () => Task.CompletedTask,
        };
        chat.AddCard(card);
        var window = new Window { Width = 440, Height = 700, Content = new AiChatView { DataContext = chat } };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var view = (AiChatView)window.Content!;
        Assert.Equal("opus", Find<TextBlock>(view, "FeatureCardModel").Text);
        Assert.True(Find<Button>(view, "FeatureCardInsert").IsVisible);
        Assert.True(Find<Button>(view, "FeatureCardDeeper").IsVisible);
        Assert.False(Find<MarkdownView>(view, "FeatureCardText").IsVisible); // 틀대로 읽었으니 칸으로 보인다
        card.InsertEditorCommand.Execute(null);
        Assert.Equal(["EXEC DBMS_SPM.LOAD_PLANS_FROM_CURSOR_CACHE(...)"], inserted);
        Assert.False(card.InsertEditorCommand.CanExecute(null)); // 한 번 넣으면 끝
        window.Close();
    });

    private static T Find<T>(Control root, string name)
        where T : Control =>
        root.GetVisualDescendants().OfType<T>().First(c => c.Name == name);
}
