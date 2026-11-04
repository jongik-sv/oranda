using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.Ai.Chat;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Ai.Contracts;

namespace Oranda.App.Tests.Features.Ai;

/// <summary>AI 대화 창의 진행 표식(시안 A): 지금 하는 일, 흐르는 경과 시간, 오래 걸림 경고, 끝난 차례의 결과 줄, 맨 아래 따라가기.</summary>
public sealed class AiChatProgressTests
{
    private static readonly DateTime Start = new(2026, 11, 4, 9, 0, 0, DateTimeKind.Utc);

    private sealed class Rig
    {
        public DateTime Now { get; set; } = Start;

        public AiChatViewModel Vm { get; }

        public Rig()
        {
            Vm = new AiChatViewModel { UtcNow = () => Now };
            Vm.StartConversation("대화");
        }

        public void Pass(int seconds)
        {
            Now = Now.AddSeconds(seconds);
            Vm.RefreshProgress();
        }
    }

    private static AiToolCall ExplainCall() => new("c1", "explain_plan", string.Empty);

    [Fact]
    public void Progress_names_what_the_ai_is_doing_at_each_step()
    {
        var rig = new Rig();
        Assert.Empty(rig.Vm.ProgressText);

        rig.Vm.BeginTurn("왜 느려?");
        Assert.Equal("AI 응답을 기다리는 중", rig.Vm.ProgressText);

        rig.Vm.Apply(ExplainCall());
        Assert.Equal("AI 작업 중 · 실행 계획(EXPLAIN)", rig.Vm.ProgressText);

        rig.Vm.Apply(new AiToolResult("c1", "explain_plan", 100, AiToolJudgement.Allowed));
        Assert.Equal("AI 응답을 기다리는 중", rig.Vm.ProgressText);

        rig.Vm.Apply(new AiTextDelta("전체 스캔"));
        Assert.Equal("답변 작성 중", rig.Vm.ProgressText);

        rig.Vm.EndTurn();
        Assert.Empty(rig.Vm.ProgressText);
        Assert.Empty(rig.Vm.ProgressElapsed);
    }

    [Fact]
    public void Elapsed_time_moves_without_events_and_reads_in_minutes()
    {
        var rig = new Rig();
        rig.Vm.BeginTurn("왜 느려?");
        Assert.Equal("0초", rig.Vm.ProgressElapsed);

        rig.Pass(14);
        Assert.Equal("14초", rig.Vm.ProgressElapsed);

        rig.Pass(58);
        Assert.Equal("1분 12초", rig.Vm.ProgressElapsed);

        rig.Pass(48);
        Assert.Equal("2분", rig.Vm.ProgressElapsed);
    }

    [Fact]
    public void Warns_after_a_minute_without_events_and_recovers_when_one_arrives()
    {
        var rig = new Rig();
        rig.Vm.BeginTurn("왜 느려?");

        rig.Pass(59);
        Assert.False(rig.Vm.IsProgressSlow);
        Assert.True(rig.Vm.ShowProgressNormal);

        rig.Pass(1);
        Assert.True(rig.Vm.IsProgressSlow);
        Assert.False(rig.Vm.ShowProgressNormal);
        Assert.Equal("AI 작업 중 · 1분 넘게 새 소식 없음", rig.Vm.ProgressText);

        rig.Vm.Apply(new AiTextDelta("글"));
        Assert.False(rig.Vm.IsProgressSlow);
        Assert.Equal("답변 작성 중", rig.Vm.ProgressText);
        Assert.Equal("1분", rig.Vm.ProgressElapsed); // 경과 시간은 차례 시작부터 잰다
    }

    [Fact]
    public void A_running_tool_gets_five_minutes_before_the_warning()
    {
        var rig = new Rig();
        rig.Vm.BeginTurn("왜 느려?");
        rig.Vm.Apply(ExplainCall());

        rig.Pass(299);
        Assert.False(rig.Vm.IsProgressSlow);

        rig.Pass(1);
        Assert.True(rig.Vm.IsProgressSlow);
        Assert.Equal("AI 작업 중 · 5분 넘게 새 소식 없음", rig.Vm.ProgressText);
    }

    [Fact]
    public void Result_line_says_how_the_turn_ended_and_counts_only_this_turn()
    {
        var rig = new Rig();
        rig.Vm.BeginTurn("첫 요청");
        rig.Vm.AddToolCall("get_cursor_plan", "1 KB", "허용");
        rig.Pass(72);
        rig.Vm.Apply(new AiFinal("끝", null, null));
        Assert.Equal(("✓", "완료 · 1분 12초 · 도구 1개"), (rig.Vm.ResultMark, rig.Vm.ResultText));
        Assert.True(rig.Vm.ResultIsCompleted);

        rig.Vm.BeginTurn("이어서");
        Assert.False(rig.Vm.HasResult); // 새 차례가 시작되면 앞 결과는 사라진다
        rig.Pass(8);
        rig.Vm.Apply(new AiError("실패"));
        Assert.Equal(("!", "오류로 끝남 · 8초 · 도구 0개"), (rig.Vm.ResultMark, rig.Vm.ResultText));
        Assert.True(rig.Vm.ResultIsFailed);
        Assert.False(rig.Vm.ResultIsCompleted);
    }

    [Fact]
    public async Task Stopping_reports_a_stop_and_ends_the_running_tool_card()
    {
        var rig = new Rig();
        rig.Vm.BeginTurn("왜 느려?");
        rig.Vm.Apply(ExplainCall());
        var card = Assert.Single(rig.Vm.Entries.OfType<ChatToolEntry>());
        Assert.True(card.IsSpinning);
        Assert.Equal("실행 중", card.Verdict);

        rig.Pass(34);
        rig.Vm.StopCommand.Execute(null);
        await rig.Vm.RunAsync(Nothing(), CancellationToken.None);

        Assert.Equal(("■", "멈춤 · 34초 · 도구 1개"), (rig.Vm.ResultMark, rig.Vm.ResultText));
        Assert.False(rig.Vm.ResultIsCompleted);
        card = Assert.Single(rig.Vm.Entries.OfType<ChatToolEntry>());
        Assert.False(card.IsSpinning); // 영원히 돌지 않는다
        Assert.Equal("중단", card.Verdict);
        var group = Assert.Single(rig.Vm.Entries.OfType<ChatToolGroupEntry>()).Group;
        Assert.False(group.IsToolRunning);
        Assert.True(group.ShowProblemMark);
    }

    [Fact]
    public async Task Cancelling_the_event_stream_also_reads_as_stopped()
    {
        var rig = new Rig();
        rig.Vm.BeginTurn("왜 느려?");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await rig.Vm.RunAsync(Forever(), cts.Token);

        Assert.StartsWith("멈춤", rig.Vm.ResultText, StringComparison.Ordinal);
        Assert.False(rig.Vm.IsRunning);
    }

    [Fact]
    public void A_second_end_turn_keeps_the_result()
    {
        var rig = new Rig();
        rig.Vm.BeginTurn("왜 느려?");
        rig.Pass(5);
        rig.Vm.EndTurn();
        rig.Pass(50);
        rig.Vm.EndTurn(); // 끝 사건과 흐름의 마무리가 둘 다 부른다

        Assert.Equal("완료 · 5초 · 도구 0개", rig.Vm.ResultText);
    }

    [Fact]
    public void Starting_a_new_conversation_clears_the_result()
    {
        var rig = new Rig();
        rig.Vm.BeginTurn("왜 느려?");
        rig.Vm.EndTurn();
        Assert.True(rig.Vm.HasResult);

        rig.Vm.StartNewConversation();

        Assert.False(rig.Vm.HasResult);
    }

    [Fact]
    public Task View_shows_the_progress_row_with_a_spinner_only_while_running() => HeadlessHost.Run(() =>
    {
        var rig = new Rig();
        var view = new AiChatView { DataContext = rig.Vm };
        var window = new Window { Width = 440, Height = 500, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var row = view.FindControl<Border>("ProgressRow")!;
        var spinner = view.FindControl<Arc>("ProgressSpinner")!;
        var result = view.FindControl<Grid>("ResultRow")!;
        Assert.False(row.IsEffectivelyVisible);
        Assert.DoesNotContain("aiSpin", spinner.Classes); // 쉬는 동안에는 애니메이션이 돌지 않는다
        Assert.False(result.IsEffectivelyVisible);

        rig.Vm.BeginTurn("왜 느려?");
        Dispatcher.UIThread.RunJobs();
        Assert.True(row.IsEffectivelyVisible);
        Assert.Contains("aiSpin", spinner.Classes);

        rig.Pass(60);
        Dispatcher.UIThread.RunJobs();
        Assert.False(row.IsEffectivelyVisible);
        Assert.True(view.FindControl<Border>("ProgressSlowRow")!.IsEffectivelyVisible);
        Assert.True(view.FindControl<TextBlock>("ProgressSlowHint")!.IsEffectivelyVisible);

        rig.Vm.EndTurn();
        Dispatcher.UIThread.RunJobs();
        Assert.False(view.FindControl<StackPanel>("ProgressPanel")!.IsEffectivelyVisible);
        Assert.DoesNotContain("aiSpin", spinner.Classes);
        Assert.True(result.IsEffectivelyVisible);
        window.Close();
    });

    [Fact]
    public Task View_shows_a_spinner_on_the_running_tool_card_and_its_group() => HeadlessHost.Run(() =>
    {
        var rig = new Rig();
        rig.Vm.BeginTurn("왜 느려?");
        rig.Vm.Apply(ExplainCall());
        var view = new AiChatView { DataContext = rig.Vm };
        var window = new Window { Width = 440, Height = 500, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Arc Named(string name) => view.GetVisualDescendants().OfType<Arc>().Single(a => a.Name == name);
        Assert.True(Named("ToolCardSpinner").IsEffectivelyVisible);
        Assert.True(Named("ToolGroupSpinner").IsEffectivelyVisible);

        rig.Vm.Apply(new AiToolResult("c1", "explain_plan", 100, AiToolJudgement.Allowed));
        Dispatcher.UIThread.RunJobs();
        Assert.False(Named("ToolGroupSpinner").IsEffectivelyVisible);
        window.Close();
    });

    [Fact]
    public Task View_keeps_the_progress_row_in_sight_unless_the_user_scrolled_up() => HeadlessHost.Run(() =>
    {
        var rig = new Rig();
        var view = new AiChatView { DataContext = rig.Vm };
        var window = new Window { Width = 440, Height = 400, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var scroll = view.FindControl<ScrollViewer>("ConversationScroll")!;
        void Settle()
        {
            for (var i = 0; i < 4; i++)
            {
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
            }
        }

        bool AtEnd() => scroll.Offset.Y + scroll.Viewport.Height >= scroll.Extent.Height - 1;

        rig.Vm.BeginTurn("왜 느려?");
        for (var i = 0; i < 30; i++)
        {
            rig.Vm.AddAssistantText($"긴 답변 {i}");
        }

        Settle();
        Assert.True(scroll.Extent.Height > scroll.Viewport.Height);
        Assert.True(AtEnd());

        scroll.Offset = new Avalonia.Vector(0, 0); // 사용자가 위로 올려 읽는다
        Settle();
        rig.Vm.AddAssistantText("더 온 글");
        Settle();
        Assert.Equal(0, scroll.Offset.Y);

        rig.Vm.EndTurn();
        rig.Vm.BeginTurn("이어서"); // 새로 보내면 맨 아래로 돌아간다
        Settle();
        Assert.True(AtEnd());
        window.Close();
    });

#pragma warning disable CS1998 // 사건 없는 흐름
    private static async IAsyncEnumerable<AiEvent> Nothing()
    {
        yield break;
    }
#pragma warning restore CS1998

    private static async IAsyncEnumerable<AiEvent> Forever([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Delay(Timeout.Infinite, ct);
        yield break;
    }
}
