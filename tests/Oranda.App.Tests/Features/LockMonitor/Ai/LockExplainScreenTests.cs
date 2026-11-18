using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Oranda.App.Features.Ai.Chat;
using Oranda.App.Features.Ai.InPlace;
using Oranda.App.Features.Ai.Kit;
using Oranda.App.Features.LockMonitor;
using Oranda.App.Features.LockMonitor.Ai;
using Oranda.App.Features.SessionMonitor;
using Oranda.App.Tests.Features.Ai.InPlace;
using Oranda.App.Tests.Features.Ai.Kit;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Connection;
using Oranda.Core.Monitoring;
using Oranda.Language.Ai.Literals;

namespace Oranda.App.Tests.Features.LockMonitor.Ai;

/// <summary>
/// 락 모니터 「AI 풀이」·세션 모니터 「대기 풀이」(설계 §3.2): 고른 줄의 사슬, 가벼운 일 한 번, 모니터 아래 띠, 「더 깊게」 = 주 모델로 채팅창 새 대화,
/// KILL 확인 창의 결론 한 줄(받은 풀이가 없으면 숨김, 새 요청 없음).
/// </summary>
public class LockExplainScreenTests
{
    private const string Answer = "## 상황\nUSER1 이 잡고 있습니다.\n## 원인 추정\n커밋 빠짐\n## 권하는 행동\n- USER1 에게 연락\n## 결론\nUSER1 에게 커밋을 요청하세요.";

    private static readonly ConnectionProfile Dev = new("dev-example", "mesadm", "h:1521/s", Id: Guid.NewGuid(), AiAccess: AiAccessLevel.Plan);

    private static async Task<(LockHarness H, FakeDriver Driver, AiChatViewModel Chat)> StartedAsync(string answer = Answer)
    {
        var h = new LockHarness(profile: Dev);
        var driver = new FakeDriver { Answer = _ => KitFakes.Answer(answer) };
        var chat = new AiChatViewModel();
        h.Vm.UseAi(InPlaceFakes.Host(driver));
        await h.Vm.StartAsync();
        return (h, driver, chat);
    }

    [Fact]
    public async Task The_input_is_the_whole_chain_of_the_picked_row_from_the_holder_down()
    {
        var (h, _, _) = await StartedAsync();
        h.Vm.SelectedKey = FakeLockMonitor.W1;

        var input = h.Vm.AiInput()!;

        Assert.Equal(LockExplainKind.LockChain, input.Kind);
        Assert.Equal(FakeLockMonitor.H1, input.Sessions[0].Key);
        Assert.Null(input.Sessions[0].BlockerKey);
        Assert.Equal(FakeLockMonitor.H1, input.Sessions.Single(s => s.Key == FakeLockMonitor.W1).BlockerKey);
        Assert.DoesNotContain(input.Sessions, s => s.Key == FakeLockMonitor.H2); // 다른 사슬은 넣지 않는다
    }

    [Fact]
    public async Task The_entry_needs_a_picked_row_and_plan_level()
    {
        var (h, _, _) = await StartedAsync();
        Assert.False(h.Vm.AiExplain!.Entry.IsEnabled);
        Assert.Equal(LockExplainPanelViewModel.NoSelectionText, h.Vm.AiExplain.Entry.ToolTip);

        h.Vm.SelectedKey = FakeLockMonitor.H1;
        Assert.True(h.Vm.AiExplain.Entry.IsEnabled);

        var meta = new LockHarness(profile: Dev with { AiAccess = AiAccessLevel.Metadata });
        meta.Vm.UseAi(InPlaceFakes.Host(new FakeDriver()));
        await meta.Vm.StartAsync();
        meta.Vm.SelectedKey = FakeLockMonitor.H1;
        Assert.False(meta.Vm.AiExplain!.Entry.IsEnabled);
        Assert.Equal(AiEntryRule.LevelTooLowText(AiAccessLevel.Plan), meta.Vm.AiExplain.Entry.ToolTip);
    }

    [Fact]
    public async Task Explain_sends_one_light_request_and_the_kill_dialog_repeats_the_conclusion_only_after_it()
    {
        var (h, driver, _) = await StartedAsync();
        h.Vm.SelectedKey = FakeLockMonitor.H1;
        Assert.Null(h.Vm.BuildKillRequest(disconnect: false)!.AiLine); // 받은 풀이가 없으면 숨김

        await h.Vm.AiExplain!.Entry.RunCommand.ExecuteAsync(null);

        var request = Assert.Single(driver.Requests);
        Assert.Equal(AiTier.Light, request.Tier);
        Assert.NotNull(request.Session);
        Assert.Contains("USER1", request.Prompt, StringComparison.Ordinal); // 계획 수준 = 자리표시(AE-3)
        Assert.DoesNotContain("MES_APP", request.Prompt, StringComparison.Ordinal);
        var result = h.Vm.AiExplain.Result!;
        Assert.Equal(LockExplainPrompt.ChainTitle, result.Title);
        Assert.Equal(LockExplainPrompt.AliasNote, h.Vm.AiExplain.Note);

        var kill = h.Vm.BuildKillRequest(disconnect: false)!;
        Assert.StartsWith(LockExplainPrompt.KillDialogPrefix, kill.AiLine, StringComparison.Ordinal);
        Assert.DoesNotContain("USER1", kill.AiLine, StringComparison.Ordinal); // 이 PC 화면에는 원래 이름
        Assert.Single(driver.Requests); // 확인 창은 새로 묻지 않는다

        h.Vm.SelectedKey = FakeLockMonitor.H2; // 풀이에 없던 세션
        Assert.Null(h.Vm.BuildKillRequest(disconnect: false)!.AiLine);
    }

    [Fact]
    public Task Kill_dialog_shows_the_ai_line_only_when_there_is_one() => HeadlessHost.Run(() =>
    {
        var request = new LockKillRequest(FakeLockMonitor.H1, false) { Question = "세션을 종료할까요?", Statement = "ALTER SYSTEM KILL SESSION '1,2'", AiLine = "AI 풀이: 기다리세요." };

        var with = LockKillDialog.Build(request, out _, out _);
        var without = LockKillDialog.Build(request with { AiLine = null }, out _, out _);

        Assert.Contains(with.GetLogicalDescendants().OfType<TextBlock>(), t => t.Name == "AiLine" && t.Text == "AI 풀이: 기다리세요.");
        Assert.DoesNotContain(without.GetLogicalDescendants().OfType<TextBlock>(), t => t.Name == "AiLine");
        return Task.CompletedTask;
    });

    [Fact]
    public async Task Deeper_opens_a_new_chat_conversation_on_the_main_model()
    {
        var h = new LockHarness(profile: Dev);
        var driver = new FakeDriver { Answer = _ => KitFakes.Answer(Answer) };
        var chat = new AiChatViewModel();
        var host = new AiInPlaceHost(null)
        {
            Settings = () => new Oranda.Core.Settings.AiSettings { Model = "opus" },
            Driver = () => driver,
            Masker = new SqlLiteralMasker(),
            Confirm = (_, _) => Task.FromResult(true),
            ChatHost = KitFakes.Host(chat),
        };
        h.Vm.UseAi(host);
        await h.Vm.StartAsync();
        h.Vm.SelectedKey = FakeLockMonitor.H1;
        await h.Vm.AiExplain!.Entry.RunCommand.ExecuteAsync(null);
        Assert.True(h.Vm.AiExplain.CanDeeperVisible);

        await h.Vm.AiExplain.DeeperCommand.ExecuteAsync(null);

        Assert.Equal(2, driver.Requests.Count);
        var deeper = driver.Requests[1];
        Assert.Equal(AiTier.Main, deeper.Tier);
        Assert.Null(deeper.ResumeId); // 띠의 대화는 넘기지 않는다
        Assert.Contains(AiDeeperConversation.DeeperRule, deeper.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("## 결론", deeper.Prompt, StringComparison.Ordinal); // 답 틀 대신 자유 글
        Assert.IsType<AiDeeperConversation>(chat.Owner);
    }

    [Fact]
    public Task The_monitor_shows_the_entry_and_the_strip_below() => HeadlessHost.Run(async () =>
    {
        var (h, _, _) = await StartedAsync();
        h.Vm.SelectedKey = FakeLockMonitor.H1;
        var view = new LockMonitorView { DataContext = h.Vm };
        var window = new Window { Width = 1300, Height = 800, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var button = Find<Button>(view, "AiExplainButton");
        Assert.True(button.IsEffectivelyVisible);
        var strip = Find<AiInlineStrip>(view, "ExplainStrip");
        Assert.False(strip.IsVisible);

        button.Command!.Execute(null);
        await HeadlessHost.WaitUntil(() => h.Vm.AiExplain!.Strip.State == AiStripState.Done);
        Dispatcher.UIThread.RunJobs();

        Assert.True(strip.IsEffectivelyVisible);
        window.Close();
    });

    [Fact]
    public void Session_monitor_sends_the_picked_session_and_its_read_sql_only()
    {
        var vm = new SessionMonitorViewModel();
        vm.UseAi(InPlaceFakes.Host(new FakeDriver()));
        Assert.Null(vm.AiInput());

        var key = new SessionKey(1, 812, 77);
        var row = new SessionRow(key, "ACTIVE") { Username = "HR", Event = "enq: TX - row lock contention", SecondsInWait = 40, BlockingSession = new SessionKey(1, 1043, 5) };
        vm.SelectedSession = new SessionItem(row);
        vm.Detail = new SessionDetail(row) { CurrentSqlText = "UPDATE SAMPLE.ORDERS SET A = 1" };

        var input = vm.AiInput()!;

        Assert.Equal(LockExplainKind.SessionWait, input.Kind);
        var s = Assert.Single(input.Sessions);
        Assert.Equal(new SessionKey(1, 1043, 5), s.BlockerKey);
        Assert.Equal("UPDATE SAMPLE.ORDERS SET A = 1", s.SqlText);
        Assert.False(vm.AiExplain!.Entry.IsEnabled); // 모니터가 열려 있지 않다
        Assert.Equal(LockExplainPanelViewModel.NotOpenText, vm.AiExplain.Entry.ToolTip);

        vm.Detail = new SessionDetail(row with { Key = new SessionKey(1, 9, 9) }) { CurrentSqlText = "SELECT OTHER" };
        Assert.Null(vm.AiInput()!.Sessions[0].SqlText); // 다른 줄의 상세는 쓰지 않는다
    }

    private static T Find<T>(Control root, string name)
        where T : Control =>
        root.GetVisualDescendants().OfType<T>().First(c => c.Name == name);
}
