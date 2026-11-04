using Avalonia.Controls;
using Avalonia.Headless;
using Oranda.App.Features.Ai.Chat;
using Oranda.Core.Ai.Contracts;
using Oranda.App.Tests.Shell.Headless;

namespace Oranda.App.Tests.Features.Ai;

/// <summary>AI 대화 창 최소판(ai-ui u4): 도구 카드가 쌓이고, 실행 중에는 멈춤만, 끝나면 이어 말하기만 쓸 수 있다.</summary>
public class AiChatTests
{
    [Fact]
    public void Turn_adds_user_tool_and_assistant_lines_and_counts_tools()
    {
        var vm = new AiChatViewModel();
        vm.StartConversation("튜닝 — 부서별 주문 수");
        vm.BeginTurn("이 문장 튜닝");
        vm.AddToolCall("get_cursor_plan", "1.4 KB", "허용");
        vm.AddToolCall("get_index_info TB_ORDER", "0.9 KB", "허용");
        vm.AddAssistantText("TB_ORDER 를 FULL 로 읽습니다.");
        vm.EndTurn();

        Assert.Equal(["ChatUserEntry", "ChatToolGroupEntry", "ChatToolEntry", "ChatToolEntry", "ChatAssistantEntry"], vm.Entries.Select(e => e.GetType().Name));
        Assert.Contains("도구 2개", vm.ResultText, StringComparison.Ordinal);
    }

    private static async IAsyncEnumerable<AiEvent> Events(params AiEvent[] events)
    {
        foreach (var e in events)
        {
            yield return e;
            await Task.Yield();
        }
    }

    [Fact]
    public async Task Driver_events_turn_a_pending_tool_card_into_a_judged_one_and_end_the_turn()
    {
        var vm = new AiChatViewModel();
        vm.BeginTurn("이 문장 튜닝");
        await vm.RunAsync(
            Events(
                new AiStarted("sess-1"),
                new AiToolCall("c1", "get_cursor_plan", "sql_id=7h35"),
                new AiToolResult("c1", "get_cursor_plan", 1400, AiToolJudgement.Allowed),
                new AiToolCall("c2", "run_query", "UPDATE …"),
                new AiToolResult("c2", "run_query", 0, AiToolJudgement.Denied),
                new AiTextDelta("TB_ORDER 를 "),
                new AiTextDelta("FULL 로 읽습니다."),
                new AiFinal(null, null, "sess-2")),
            CancellationToken.None);

        var cards = vm.Entries.OfType<ChatToolEntry>().ToList();
        Assert.Equal(["허용", "거부"], cards.Select(c => c.Verdict));
        Assert.All(cards, c => Assert.True(c.Done));
        Assert.Equal("TB_ORDER 를 FULL 로 읽습니다.", vm.Entries.OfType<ChatAssistantEntry>().Single().Text);
        Assert.False(vm.IsRunning);
        Assert.Equal("sess-2", vm.ResumeId);
    }

    [Fact]
    public async Task Cancelling_the_stream_ends_the_turn_without_throwing()
    {
        var vm = new AiChatViewModel();
        vm.BeginTurn("이 문장 튜닝");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await vm.RunAsync(Events(new AiToolCall("c1", "get_cursor_plan", "")), cts.Token);
        Assert.False(vm.IsRunning);
    }

    [Fact]
    public void Stop_is_only_available_while_running_and_send_only_after_with_a_draft()
    {
        var vm = new AiChatViewModel();
        var stops = 0;
        string? sent = null;
        vm.StopRequested += (_, _) => stops++;
        vm.FollowUpRequested += (_, followUp) => sent = followUp.Text;

        vm.StartConversation("튜닝 — 부서별 주문 수");
        vm.Draft = "인덱스는 만들지 말고 힌트로만";
        vm.BeginTurn("이 문장 튜닝");
        Assert.True(vm.StopCommand.CanExecute(null));
        Assert.False(vm.SendCommand.CanExecute(null));
        vm.StopCommand.Execute(null);
        Assert.Equal(1, stops);

        vm.EndTurn();
        Assert.False(vm.StopCommand.CanExecute(null));
        Assert.True(vm.SendCommand.CanExecute(null));
        vm.SendCommand.Execute(null);
        Assert.Equal("인덱스는 만들지 말고 힌트로만", sent);
        Assert.Equal(string.Empty, vm.Draft);
    }

    [Fact]
    public Task View_renders_each_entry_kind_and_the_status_line() => HeadlessHost.Run(() =>
    {
        var vm = new AiChatViewModel();
        vm.StartConversation("튜닝 — 부서별 주문 수");
        vm.BeginTurn("이 문장 튜닝");
        vm.AddToolCall("get_cursor_plan", "1.4 KB", "허용");
        vm.AddAssistantText("지금까지 본 것");
        var window = new Window { Width = 440, Height = 500, Content = new AiChatView { DataContext = vm } };
        window.Show();

        var view = (AiChatView)window.Content!;
        Assert.Equal("튜닝 — 부서별 주문 수", view.FindControl<TextBlock>("TitleText")!.Text);
        Assert.Equal(4, view.FindControl<ItemsControl>("EntryList")!.ItemCount); // 사용자 글 · 도구 묶음 머리 줄 · 카드 · AI 글
        Assert.Equal("AI 응답을 기다리는 중", view.FindControl<TextBlock>("ProgressText")!.Text);
        Assert.True(view.FindControl<Button>("StopButton")!.Command!.CanExecute(null));
        window.Close();
    });

    [Fact]
    public void Send_is_available_from_the_start_and_locks_only_while_running()
    {
        var vm = new AiChatViewModel();
        vm.Draft = "인덱스는 만들지 말고 힌트로만";
        vm.FollowUpRequested += (_, _) => { };
        Assert.False(vm.IsTuning);
        Assert.True(vm.CanType); // 튜닝을 시작하지 않아도 일반 대화로 쓸 수 있다
        Assert.True(vm.SendCommand.CanExecute(null));

        vm.StartConversation("튜닝 — 부서별 주문 수");
        vm.Draft = "인덱스는 만들지 말고 힌트로만";
        Assert.True(vm.IsTuning);
        Assert.True(vm.CanType);
        Assert.True(vm.SendCommand.CanExecute(null));

        vm.BeginTurn("이 문장 튜닝");
        Assert.False(vm.CanType);
    }

    [Fact]
    public Task Enter_in_the_draft_box_sends_the_follow_up() => HeadlessHost.Run(() =>
    {
        var vm = new AiChatViewModel();
        string? sent = null;
        vm.FollowUpRequested += (_, followUp) => sent = followUp.Text;
        var window = new Window { Width = 440, Height = 500, Content = new AiChatView { DataContext = vm } };
        window.Show();
        vm.StartConversation("튜닝 — 부서별 주문 수");
        vm.Draft = "인덱스는 만들지 말고 힌트로만";
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var box = ((AiChatView)window.Content!).FindControl<TextBox>("DraftBox")!;
        box.Focus();
        box.CaretIndex = box.Text!.Length;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        window.KeyPress(Avalonia.Input.Key.Enter, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Enter, "\r");
        window.KeyRelease(Avalonia.Input.Key.Enter, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Enter, "\r");
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Equal("인덱스는 만들지 말고 힌트로만", sent);
        Assert.Equal(string.Empty, vm.Draft);
        window.Close();
    });

    [Fact]
    public Task View_shows_the_general_hint_with_the_input_enabled_before_a_conversation() => HeadlessHost.Run(() =>
    {
        var vm = new AiChatViewModel();
        var window = new Window { Width = 440, Height = 500, Content = new AiChatView { DataContext = vm } };
        window.Show();
        var view = (AiChatView)window.Content!;
        var hint = view.FindControl<TextBlock>("StartHint")!;
        var box = view.FindControl<TextBox>("DraftBox")!;
        Assert.True(hint.IsVisible);
        Assert.True(box.IsEnabled);

        vm.StartConversation("튜닝 — 부서별 주문 수");
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.False(hint.IsVisible);
        Assert.True(box.IsEnabled);
        window.Close();
    });
}
