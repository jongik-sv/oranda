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
/// AI 파티션 추천 화면(Headless, 시안 ②·④): 확인 창의 체크 상태와 실제 값 잠금(PA-3)·보낼 내용 보기·모아서 넘기기,
/// 파티션 페이지의 단추 줄 → 채우기 → 띠 → 되돌리기. 대화 창 카드는 <see cref="PartitionAdvisorChatTests"/>.
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

            // 화면 글이 V$SQL_PLAN 그대로: 문자열 Content 는 단추 틀이 AccessText 로 그려 '_' 를 단축키 표시로 먹는다(V$SQLPLAN).
            var predicateTexts = Find<CheckBox>(w, "PredicatesCheck").GetVisualDescendants().OfType<TextBlock>()
                .Where(t => t.Text?.Contains("V$SQL", StringComparison.Ordinal) == true).ToList();
            Assert.NotEmpty(predicateTexts);
            Assert.All(predicateTexts, t => Assert.IsNotType<AccessText>(t));
            Assert.Contains(predicateTexts, t => t.Text!.Contains("V$SQL_PLAN", StringComparison.Ordinal));
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
        Assert.Single(collector.Calls); // 보기에서 모은 근거를 그대로 넘긴다
        Assert.False(collector.Calls[0].Predicates);
        Assert.Empty(driver.Requests); // 보내기는 AI 대화 창이 한다(PA-22)
        var request = Assert.IsType<AdvisorSendRequest>(vm.Request);
        Assert.Same(collector.Evidence, request.Evidence);
        Assert.True(request.Options.SendValues);
        Assert.All(vm.Steps, step => Assert.Equal(AdvisorStepState.Done, step.State));
    });

    [Fact]
    public Task Start_window_cancel_closes_without_a_request() => HeadlessHost.Run(() =>
    {
        var vm = new PartitionAdvisorStartViewModel(Run(new FakeDriver()));
        var closed = false;
        vm.CloseRequested += (_, _) => closed = true;

        vm.Cancel();

        Assert.True(closed);
        Assert.Null(vm.Request);
        Assert.Equal(4, vm.Steps.Count); // 열·키 → 통계 → 조회 조건 → 분포 (AI 응답은 대화 창에서 기다린다)
        return Task.CompletedTask;
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
