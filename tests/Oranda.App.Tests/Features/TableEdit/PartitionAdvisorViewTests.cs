using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using Oranda.App.Features.TableEdit;
using Oranda.App.Features.TableEdit.Advisor;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Connection;
using Oranda.Core.Schema.TableEdit.Partitions.Advisor;
using Oranda.Core.Settings;
using static Oranda.App.Tests.Features.TableEdit.PartitionAdvisorTests;
using static Oranda.App.Tests.Features.TableEdit.TableEditPartitionSessionTests;
using static Oranda.App.Tests.Features.TableEdit.TableEditTestSupport;

namespace Oranda.App.Tests.Features.TableEdit;

/// <summary>
/// AI 파티션 추천 화면(Headless, 시안 ②~⑤): 확인 창의 체크 상태와 실제 값 잠금(PA-3)·보낼 내용 보기, 결과 카드 ✖ 면 채우기 끔,
/// 「파티션 안 함」 화면과 「후보 3개 받기」, 파티션 페이지의 단추 줄 → 채우기 → 띠 → 되돌리기.
/// </summary>
public class PartitionAdvisorViewTests
{
    private static T Find<T>(Control root, string name)
        where T : Control => root.FindControl<T>(name) ?? root.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

    [Fact]
    public Task Start_window_checks_the_defaults_and_locks_real_values_on_production() => HeadlessHost.Run(async () =>
    {
        var run = Run(new FakeDriver(), AdvisorRoute.For(Profile(ConnectionEnvironment.Production), new AiSettings()));
        var vm = new PartitionAdvisorStartViewModel(run);
        var w = new PartitionAdvisorStartWindow { DataContext = vm };
        w.Show();
        try
        {
            HeadlessHost.Settle(w);
            Assert.Equal("Claude Code", Find<TextBlock>(w, "RecipientText").Text);
            Assert.True(Find<CheckBox>(w, "PredicatesCheck").IsChecked);
            Assert.True(Find<CheckBox>(w, "DistributionsCheck").IsChecked);
            Assert.True(Find<CheckBox>(w, "ColumnsCheck").IsChecked);
            Assert.False(Find<CheckBox>(w, "ColumnsCheck").IsEnabled); // 열·키·통계는 늘 보낸다
            var values = Find<CheckBox>(w, "SendValuesCheck");
            Assert.False(values.IsChecked);
            Assert.False(values.IsEnabled);
            Assert.Contains("운영 접속", Find<TextBlock>(w, "SendValuesNoteText").Text, StringComparison.Ordinal);

            vm.SendValues = true; // 잠긴 줄은 켜지지 않는다
            Assert.False(vm.SendValues);

            await vm.TogglePreviewAsync();
            HeadlessHost.Settle(w);
            var preview = Find<TextBox>(w, "PreviewBox");
            Assert.True(preview.IsEffectivelyVisible);
            Assert.StartsWith("PROMPT ORDERS", preview.Text, StringComparison.Ordinal);
            Assert.Contains("values=False", preview.Text, StringComparison.Ordinal);
            Assert.Contains(vm.SkippedLines, l => l.Contains("NoPrivilege", StringComparison.Ordinal) || l.Contains("권한", StringComparison.Ordinal));
        }
        finally
        {
            w.Close();
        }
    });

    [Fact]
    public Task Start_window_allows_real_values_when_the_route_does_and_sends_once_with_the_collected_evidence() => HeadlessHost.Run(async () =>
    {
        var driver = new FakeDriver();
        var collector = new FakeCollector();
        var run = Run(driver, AdvisorRoute.For(Profile(access: AiAccessLevel.QueryRows), new AiSettings()), collector: collector);
        var vm = new PartitionAdvisorStartViewModel(run);
        var w = new PartitionAdvisorStartWindow { DataContext = vm };
        var closed = false;
        w.Closed += (_, _) => closed = true;
        w.Show();
        HeadlessHost.Settle(w);
        Assert.True(Find<CheckBox>(w, "SendValuesCheck").IsEnabled);
        vm.SendValues = true;
        vm.IncludePredicates = false;
        await vm.TogglePreviewAsync();
        Assert.Contains("values=True", vm.PreviewText, StringComparison.Ordinal);

        Assert.True(await vm.SendAsync());
        HeadlessHost.Settle(w);

        Assert.True(closed);
        Assert.Single(collector.Calls); // 보기에서 모은 근거를 그대로 보낸다
        Assert.False(collector.Calls[0].Predicates);
        Assert.Contains("values=True", Assert.Single(driver.Requests).Prompt, StringComparison.Ordinal);
        Assert.NotNull(vm.Outcome);
    });

    [Fact]
    public Task Start_window_stays_open_with_the_error_when_the_ai_fails() => HeadlessHost.Run(async () =>
    {
        var driver = new FakeDriver { Answer = _ => [new AiError("profile_changed")] };
        var vm = new PartitionAdvisorStartViewModel(Run(driver));

        Assert.False(await vm.SendAsync());

        Assert.Null(vm.Outcome);
        Assert.Contains("profile_changed", vm.ErrorText, StringComparison.Ordinal);
        Assert.False(vm.IsBusy);
    });

    [Fact]
    public Task Result_window_disables_fill_on_a_stop_card_and_fills_an_ok_card() => HeadlessHost.Run(async () =>
    {
        var engine = Engine(c => c.Rank == 2 ? [new AdviceCheck(AdviceCheckLevel.Stop, AdviceCheckCodes.IntervalNull, "ORDER_DATE")] : []);
        var outcome = await Run(new FakeDriver(), engine: engine).AskAsync(Evidence(), new AdvicePromptOptions(AdviceGoal.Query, false, "ko"), CancellationToken.None);
        var filled = new List<int>();
        var vm = new PartitionAdvisorResultViewModel(outcome, new AdvisorResultHost
        {
            Fill = c =>
            {
                filled.Add(c.Candidate.Rank);
                return true;
            },
        }, "Claude Code");
        var w = new PartitionAdvisorResultWindow { DataContext = vm };
        w.Show();
        try
        {
            HeadlessHost.Settle(w);
            Assert.Contains("받은 곳 Claude Code", vm.Title, StringComparison.Ordinal);
            var fills = w.GetVisualDescendants().OfType<Button>().Where(b => b.Content as string == "편집기에 채우기").ToList();
            Assert.Equal(2, fills.Count);
            Assert.True(fills[0].IsEnabled);
            Assert.False(fills[1].IsEnabled);
            Assert.Contains(w.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "✖" && t.Classes.Contains("stop"));
            Assert.False(Find<Border>(w, "NoPartitionPanel").IsVisible);

            Assert.False(vm.Fill(vm.Cards[1])); // ✖ 카드는 모델에서도 막는다
            fills[0].RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            HeadlessHost.Settle(w);
            Assert.Equal([1], filled);
            Assert.True(vm.Cards[0].IsFilled);

            vm.IsNotChosenOpen = true;
            HeadlessHost.Settle(w);
            Assert.Contains(Find<ItemsControl>(w, "NotChosenList").GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "STATUS — 값이 6개뿐");
        }
        finally
        {
            w.Close();
        }
    });

    [Fact]
    public Task No_partition_shows_the_reason_and_asks_again_requiring_candidates() => HeadlessHost.Run(async () =>
    {
        var driver = new FakeDriver
        {
            Answer = r => [new AiFinal(null, JsonDocument.Parse(r.Prompt.Contains("require=True", StringComparison.Ordinal)
                ? AdviceJson(AdviceVerdict.Partition, Candidate(1), Candidate(2), Candidate(3))
                : AdviceJson(AdviceVerdict.NoPartition)).RootElement.Clone(), null)],
        };
        var run = Run(driver);
        var outcome = await run.AskAsync(Evidence(), new AdvicePromptOptions(AdviceGoal.Query, false, "ko"), CancellationToken.None);
        var vm = new PartitionAdvisorResultViewModel(outcome, new AdvisorResultHost { Ask = (o, ct) => run.AskAsync(outcome.Evidence, o, ct) }, "Claude Code");
        var w = new PartitionAdvisorResultWindow { DataContext = vm };
        w.Show();
        try
        {
            HeadlessHost.Settle(w);
            Assert.True(Find<Border>(w, "NoPartitionPanel").IsEffectivelyVisible);
            Assert.True(Find<Button>(w, "RequestCandidatesButton").IsEffectivelyVisible);
            Assert.Equal("행이 적어 얻을 것이 없습니다", Find<TextBlock>(w, "SummaryText").Text);
            Assert.Empty(vm.Cards);

            await vm.RequestCandidatesAsync();
            HeadlessHost.Settle(w);

            Assert.Contains("require=True", driver.Requests[^1].Prompt, StringComparison.Ordinal);
            Assert.Equal(3, vm.Cards.Count);
            Assert.False(vm.IsNoPartition);
            Assert.False(Find<Border>(w, "NoPartitionPanel").IsVisible);
        }
        finally
        {
            w.Close();
        }
    });

    [Fact]
    public Task Unreadable_answer_shows_only_the_raw_text() => HeadlessHost.Run(async () =>
    {
        var outcome = await Run(new FakeDriver { Answer = _ => [new AiFinal("모양 없는 글", null, null)] })
            .AskAsync(Evidence(), new AdvicePromptOptions(AdviceGoal.Query, false, "ko"), CancellationToken.None);
        var vm = new PartitionAdvisorResultViewModel(outcome, new AdvisorResultHost(), "Claude Code");
        var w = new PartitionAdvisorResultWindow { DataContext = vm };
        w.Show();
        try
        {
            HeadlessHost.Settle(w);
            Assert.True(Find<TextBlock>(w, "UnreadableText").IsEffectivelyVisible);
            Assert.True(Find<ToggleButton>(w, "RawToggle").IsEffectivelyVisible);
            vm.IsRawOpen = true;
            HeadlessHost.Settle(w);
            Assert.Equal("모양 없는 글", Find<TextBox>(w, "RawBox").Text);
            Assert.Empty(vm.Cards);
        }
        finally
        {
            w.Close();
        }
    });

    [Fact]
    public Task Partition_page_shows_the_advisor_row_then_the_banner_after_fill_and_undo_removes_it() => HeadlessHost.Run(() =>
    {
        var s = Create(WithOrderDate(OrdersTable()));
        var asked = 0;
        s.OpenAiAdvisor = () =>
        {
            asked++;
            return Task.CompletedTask;
        };
        s.ReopenAiAdvice = () => Task.CompletedTask;
        var view = new TableEditPartitionsView { DataContext = s };
        var w = new Window { Content = view, Width = 1100, Height = 600 };
        w.Show();
        try
        {
            HeadlessHost.Settle(w);
            Assert.True(Find<Border>(view, "AiAdvisorBar").IsEffectivelyVisible);
            Assert.False(Find<Border>(view, "AdviceBanner").IsVisible);
            Find<Button>(view, "AiAdvisorButton").Command!.Execute(null);
            Assert.Equal(1, asked);

            var c = new AdvisorCandidate(Candidate(1), [new AdviceCheck(AdviceCheckLevel.Warn, AdviceCheckCodes.PkGlobal, "PK_ORDERS")],
                Engine().Apply(Candidate(1), Evidence(), new EvidenceValueVault(), DateOnly.FromDateTime(PartitionAdvisorTests.Now)));
            Assert.True(s.ApplyAdvice(c.Candidate, c.Draft, c.Checks));
            HeadlessHost.Settle(w);
            Assert.True(Find<Border>(view, "AdviceBanner").IsEffectivelyVisible);
            Assert.Contains("1안을 채웠습니다", Find<TextBlock>(view, "AdviceBannerText").Text, StringComparison.Ordinal);
            Assert.True(Find<ItemsControl>(view, "AdviceBannerWarningList").IsEffectivelyVisible);
            Assert.True(Find<Button>(view, "ReopenAdviceButton").IsEffectivelyVisible);
            Assert.Equal(2, Find<ListBox>(view, "PartitionRows").ItemCount);

            Find<Button>(view, "UndoAdviceButton").Command!.Execute(null);
            HeadlessHost.Settle(w);
            Assert.False(Find<Border>(view, "AdviceBanner").IsVisible);
            Assert.Equal(0, Find<ListBox>(view, "PartitionRows").ItemCount);
        }
        finally
        {
            w.Close();
        }
    });
}
