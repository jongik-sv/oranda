using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.Ai.Kit;
using Oranda.App.Features.Rollback;
using Oranda.App.Features.Rollback.Ai;
using Oranda.App.Tests.Features.Ai.InPlace;
using Oranda.App.Tests.Features.Ai.Kit;
using Oranda.App.Tests.Features.Execution;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Connection;
using Oranda.Core.Metadata;
using Oranda.Core.Rollback;

namespace Oranda.App.Tests.Features.Rollback.Ai;

/// <summary>원복 스크립트 창 「AI 점검」(설계 §4.2): 입구 상태, 주 모델 요청, 점검 띠, 「고친 스크립트를 편집기에」 = 편집기 제안.</summary>
public class RollbackScriptAiTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 18, 9, 0, 0, TimeSpan.Zero);

    private static ConnectionProfile Profile(AiAccessLevel level) => new("dev-example", "app", "db:1521/ORCL", Id: Guid.NewGuid(), AiAccess: level);

    private static RollbackScriptDocument Doc() => new(RollbackScriptDocument.CurrentSchemaVersion, 318,
    [
        new RollbackScriptStatement(1, T0, "DELETE FROM SAMPLE.ORDERS WHERE ORDER_ID = 777", [], [new RollbackStep("INSERT INTO SAMPLE.ORDERS (ORDER_ID) VALUES (777)")], [], false, null, 1),
    ]);

    private static (RollbackScriptViewModel Vm, FakeDriver Driver, List<string> Confirmed) Create(AiAccessLevel level, bool connected = true, string? answer = null)
    {
        var profile = Profile(level);
        var group = new RollbackGroup
        {
            Id = 318, ProfileId = profile.Id, ProfileName = profile.Name, DbKey = "k", StartedAt = T0, HasScriptFile = true,
            Statements = [new RollbackStatementInfo(1, T0, DmlVerb.Delete, new DbObjectRef("SAMPLE", "ORDERS", "TABLE"), "DELETE FROM SAMPLE.ORDERS WHERE ORDER_ID = :v", 1, null, RollbackScriptState.Confirmed, null, 1, false)],
        };
        var driver = new FakeDriver { Answer = _ => KitFakes.Answer(answer ?? "## 점검 결과\n- 문제 없음\n## 결론\n그대로 써도 됩니다.\n## 고친 스크립트\n없음") };
        var confirmed = new List<string>();
        var session = new FakeSession(profile);
        var vm = new RollbackScriptViewModel(group, Doc()) { SessionFinder = _ => connected ? session : null };
        vm.FactsReader = (_, tables, _) => Task.FromResult<(IReadOnlyList<RollbackForeignKey>, IReadOnlyList<RollbackTableFacts>)>(
            ([new RollbackForeignKey("SAMPLE.ORDER_ITEMS", "SAMPLE.ORDERS", "FK_ITEMS_ORDER")], []));
        vm.UseAi(InPlaceFakes.Host(driver, confirmed: confirmed));
        return (vm, driver, confirmed);
    }

    [Fact]
    public void The_entry_needs_an_open_connection_and_ai_access()
    {
        Assert.True(Create(AiAccessLevel.Metadata).Vm.AiEntry!.IsEnabled);

        var closed = Create(AiAccessLevel.Plan, connected: false).Vm.AiEntry!;
        Assert.False(closed.IsEnabled);
        Assert.Equal(RollbackScriptViewModel.AiNoSessionText, closed.ToolTip);

        var off = Create(AiAccessLevel.Off).Vm.AiEntry!;
        Assert.False(off.IsEnabled);
        Assert.Equal(AiEntryRule.AccessOffText, off.ToolTip);
    }

    [Fact]
    public async Task Check_sends_one_main_request_after_the_warning_with_the_facts_and_masked_values()
    {
        var (vm, driver, confirmed) = Create(AiAccessLevel.Plan);

        await vm.AiEntry!.RunCommand.ExecuteAsync(null);

        var request = Assert.Single(driver.Requests);
        Assert.Equal(AiTier.Main, request.Tier);
        Assert.NotNull(request.Session); // 실제 드라이버처럼 실행 범위가 있어야 받는다
        Assert.Contains("FK_ITEMS_ORDER", request.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("777", request.Prompt, StringComparison.Ordinal);
        Assert.Contains("받는 곳:", Assert.Single(confirmed), StringComparison.Ordinal);
        Assert.Equal(AiStripState.Done, vm.AiStrip!.State);
        Assert.Equal("그대로 써도 됩니다.", vm.AiResult!.Conclusion);
        Assert.False(vm.HasAiFix);
    }

    [Fact]
    public async Task Cancelling_the_warning_sends_nothing()
    {
        var (vm, driver, _) = Create(AiAccessLevel.Plan);
        vm.UseAi(InPlaceFakes.Host(driver, confirm: false));

        await vm.AiEntry!.RunCommand.ExecuteAsync(null);

        Assert.Empty(driver.Requests);
        Assert.Equal(AiStripState.Refused, vm.AiStrip!.State);
    }

    [Fact]
    public async Task A_fixed_script_is_proposed_into_a_new_sql_tab_and_never_run()
    {
        var (vm, _, _) = Create(AiAccessLevel.QueryRows, answer: "## 점검 결과\n- [FK 순서] x\n## 결론\n고쳐야 합니다.\n## 고친 스크립트\n```sql\nINSERT INTO SAMPLE.ORDERS (ORDER_ID) VALUES (777);\n```");
        var opened = new List<string>();
        var editor = new ProposalEditor(() => opened.LastOrDefault() ?? string.Empty);
        vm.EditorOpener = (text, _) =>
        {
            opened.Add(text);
            return true;
        };
        vm.EditorContext = () => editor;

        await vm.AiEntry!.RunCommand.ExecuteAsync(null);
        Assert.True(vm.HasAiFix);
        Assert.True(await vm.ProposeAiFixAsync());

        Assert.Equal(vm.Text, Assert.Single(opened));
        var edit = Assert.Single(editor.Proposals);
        Assert.Equal(0, edit.Range.Start);
        Assert.Equal(vm.Text.Length, edit.Range.Length);
        Assert.Equal("INSERT INTO SAMPLE.ORDERS (ORDER_ID) VALUES (777);", edit.NewText);
    }

    [Fact]
    public Task The_window_shows_the_entry_strip_and_fix_button() => HeadlessHost.Run(async () =>
    {
        var (vm, driver, _) = Create(AiAccessLevel.QueryRows, answer: "## 점검 결과\n- x\n## 결론\ny\n## 고친 스크립트\n```sql\nSELECT 1 FROM DUAL;\n```");
        var view = new RollbackScriptView { DataContext = vm };
        var window = new Window { Width = 800, Height = 600, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var entry = Find<Button>(view, "AiCheckButton");
        Assert.True(entry.IsVisible);
        Assert.False(Find<Button>(view, "AiFixButton").IsVisible);
        Assert.False(Find<AiInlineStrip>(view, "AiCheckStrip").IsVisible); // 묻기 전에는 띠를 숨긴다

        entry.Command!.Execute(null);
        await HeadlessHost.WaitUntil(() => vm.AiStrip!.State == AiStripState.Done);
        Dispatcher.UIThread.RunJobs();

        Assert.Single(driver.Requests);
        Assert.True(Find<Button>(view, "AiFixButton").IsVisible);
        Assert.True(Find<AiInlineStrip>(view, "AiCheckStrip").IsEffectivelyVisible);
        window.Close();
    });

    private static T Find<T>(Control root, string name)
        where T : Control =>
        root.GetVisualDescendants().OfType<T>().First(c => c.Name == name);
}
