using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Oranda.App.Features.DataCopy;
using Oranda.App.Features.Localization;
using Oranda.App.Features.SessionMonitor;
using Oranda.App.Shell;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Transfer.Copy;

namespace Oranda.App.Tests.Features.DataCopy;

/// <summary>복사 규칙 목록에서 여러 규칙을 한 번에 지운다: 확인 창 한 번·취소·한 개는 확인 없음·지운 뒤 선택·저장 안 한 변경 표시.</summary>
[Collection("UiLanguage")]
public sealed class RuleRemoveManyTests : IDisposable
{
    private readonly IShellText? _previousText = UiText.Current;

    public void Dispose() => UiText.Current = _previousText;

    private static CopyJob FiveRuleJob() => TestJobs.Job("규칙 여럿 지우기", "SRC", "TGT",
        TestJobs.Rule("T1"), TestJobs.Rule("T2"), TestJobs.Rule("T3"), TestJobs.Rule("T4"), TestJobs.Rule("T5"));

    private static List<string> Names(DataCopyViewModel vm) => [.. vm.Editor.Rules.Select(r => r.Table)];

    private static List<string> Asked(DataCopyViewModel vm)
    {
        var asked = new List<string>();
        vm.ConfirmMessage = message =>
        {
            asked.Add(message);
            return Task.FromResult(true);
        };
        return asked;
    }

    private static TableRuleViewModel[] Pick(DataCopyViewModel vm, params int[] indexes) => [.. indexes.Select(i => vm.Editor.Rules[i])];

    [Fact]
    public async Task Several_rules_are_removed_after_one_confirmation()
    {
        var vm = await new DataCopyHarness().OpenAsync(FiveRuleJob());
        var asked = Asked(vm);

        Assert.True(await vm.RemoveRulesAsync(Pick(vm, 1, 3)));

        Assert.Equal(["T1", "T3", "T5"], Names(vm));
        var message = Assert.Single(asked);
        Assert.Contains("2개 테이블 규칙을 지웁니다", message);
        Assert.DoesNotContain("표", message);
    }

    [Fact]
    public async Task Cancelling_the_confirmation_removes_nothing()
    {
        var vm = await new DataCopyHarness().OpenAsync(FiveRuleJob());
        await vm.SaveCurrentJobAsync(quiet: true); // 연 직후의 정리된 모양으로 한 번 맞춘다(DataCopySaveTests 와 같음)
        var asked = 0;
        vm.ConfirmMessage = _ =>
        {
            asked++;
            return Task.FromResult(false);
        };
        var selected = vm.Editor.Rules[2];
        vm.Editor.SelectedRule = selected;

        Assert.False(await vm.RemoveRulesAsync(Pick(vm, 1, 2)));

        Assert.Equal(1, asked);
        Assert.Equal(["T1", "T2", "T3", "T4", "T5"], Names(vm));
        Assert.Same(selected, vm.Editor.SelectedRule);
        Assert.False(vm.HasUnsavedChanges);
    }

    [Fact]
    public async Task One_rule_is_removed_without_asking()
    {
        var vm = await new DataCopyHarness().OpenAsync(FiveRuleJob());
        var asked = Asked(vm);

        Assert.True(await vm.RemoveRulesAsync(Pick(vm, 2)));

        Assert.Empty(asked);
        Assert.Equal(["T1", "T2", "T4", "T5"], Names(vm));
    }

    [Fact]
    public async Task Several_rules_are_not_removed_when_the_confirmation_window_is_missing()
    {
        var vm = await new DataCopyHarness().OpenAsync(FiveRuleJob());
        vm.ConfirmMessage = null;

        Assert.False(await vm.RemoveRulesAsync(Pick(vm, 0, 1)));

        Assert.Equal(5, vm.Editor.Rules.Count);
        Assert.True(vm.StatusIsError);
    }

    [Fact]
    public async Task Rules_that_are_no_longer_in_the_list_are_ignored()
    {
        var vm = await new DataCopyHarness().OpenAsync(FiveRuleJob());
        var asked = Asked(vm);
        var gone = vm.Editor.Rules[0];
        vm.Editor.RemoveRule(gone);

        // 이미 지운 규칙과 중복은 세지 않는다: 남은 하나만 지우니 묻지 않는다.
        Assert.True(await vm.RemoveRulesAsync([gone, vm.Editor.Rules[0], vm.Editor.Rules[0]]));

        Assert.Empty(asked);
        Assert.Equal(["T3", "T4", "T5"], Names(vm));
    }

    [Theory]
    [InlineData(new[] { 1, 2 }, 1, "T4")] // 지운 것 중 가장 위 행 자리의 다음 행
    [InlineData(new[] { 1, 3 }, 1, "T3")] // 떨어진 행도 가장 위 행 자리
    [InlineData(new[] { 3, 4 }, 3, "T3")] // 끝까지 지우면 마지막 행
    [InlineData(new[] { 0, 1, 2, 3 }, 0, "T5")]
    public async Task Selection_moves_to_the_row_after_the_topmost_removed_one(int[] removed, int selectedIndex, string expected)
    {
        var vm = await new DataCopyHarness().OpenAsync(FiveRuleJob());
        Asked(vm);
        vm.Editor.SelectedRule = vm.Editor.Rules[selectedIndex];

        await vm.RemoveRulesAsync(Pick(vm, removed));

        Assert.Equal(expected, vm.Editor.SelectedRule?.Table);
    }

    [Fact]
    public async Task Selection_stays_when_the_selected_rule_is_not_removed()
    {
        var vm = await new DataCopyHarness().OpenAsync(FiveRuleJob());
        Asked(vm);
        vm.Editor.SelectedRule = vm.Editor.Rules[4];

        await vm.RemoveRulesAsync(Pick(vm, 0, 1));

        Assert.Equal("T5", vm.Editor.SelectedRule?.Table);
    }

    [Fact]
    public async Task Removing_every_rule_clears_the_selection()
    {
        var vm = await new DataCopyHarness().OpenAsync(FiveRuleJob());
        Asked(vm);

        await vm.RemoveRulesAsync([.. vm.Editor.Rules]);

        Assert.Empty(vm.Editor.Rules);
        Assert.Null(vm.Editor.SelectedRule);
    }

    [Fact]
    public async Task Removing_rules_marks_the_job_changed_and_the_saved_job_has_only_the_rest()
    {
        var h = new DataCopyHarness();
        var vm = await h.OpenAsync(FiveRuleJob() with { Sequences = [new SequenceRule("S2", "T2", "ID"), new SequenceRule("S4", "T4", "ID"), new SequenceRule("S5", "T5", "ID")] });
        Asked(vm);
        await vm.SaveCurrentJobAsync(quiet: true); // 연 직후의 정리된 모양으로 한 번 맞춘다(DataCopySaveTests 와 같음)
        Assert.False(vm.HasUnsavedChanges);
        var defaults = vm.Editor.CurrentTableDefaults;

        await vm.RemoveRulesAsync(Pick(vm, 1, 3));

        Assert.True(vm.HasUnsavedChanges);
        Assert.Equal(defaults, vm.Editor.CurrentTableDefaults); // 전체 기본값은 그대로
        var built = vm.Editor.BuildJob();
        Assert.Equal(["T1", "T3", "T5"], built.Tables.Select(t => t.Table));
        Assert.Equal(["T5"], built.Sequences.Select(s => s.Table)); // 지운 규칙에 이은 시퀀스 연결도 함께 사라진다

        Assert.True(await vm.SaveCurrentJobAsync());
        Assert.Equal(["T1", "T3", "T5"], h.Store.Jobs[vm.CurrentJob!.Id].Tables.Select(t => t.Table));
        Assert.False(vm.HasUnsavedChanges);
    }

    [Fact]
    public async Task Editor_removes_a_list_and_reports_how_many()
    {
        var vm = await new DataCopyHarness().OpenAsync(FiveRuleJob());
        var rules = vm.Editor.Rules;

        var removed = vm.Editor.RemoveRules([rules[0], rules[2], rules[2]]);

        Assert.Equal(2, removed);
        Assert.Equal(["T2", "T4", "T5"], Names(vm));
        Assert.Equal(0, vm.Editor.RemoveRules([]));
    }

    // ---- 화면(Headless): 규칙 그리드에서 여러 행을 고르고 지운다 ----

    private static void Settle(Window window)
    {
        for (var i = 0; i < 3; i++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(20);
            for (var j = 0; j < 6; j++)
            {
                Dispatcher.UIThread.RunJobs();
            }

            window.UpdateLayout();
        }
    }

    private static async Task<(DataCopyWindow Window, DataCopyViewModel Vm)> OpenWindowAsync()
    {
        var text = new ShellText(language: "ko");
        UiText.Current = text;
        LiteralTranslator.Install(text);
        var vm = await new DataCopyHarness().OpenAsync(FiveRuleJob());
        vm.Editor.SelectedRule = vm.Editor.Rules[0];
        var window = new DataCopyWindow { DataContext = vm };
        window.Show();
        LiteralTranslator.Translate(window);
        Settle(window);
        return (window, vm);
    }

    [Fact]
    public Task Grid_selection_of_several_rows_is_removed_at_once_and_selection_follows() => HeadlessHost.Run(async () =>
    {
        var (window, vm) = await OpenWindowAsync();
        try
        {
            var asked = Asked(vm);
            var grid = window.FindControl<MonitorGrid>("RuleGrid")!;

            grid.Model!.SetSelectedRows([(1, 2)]); // T2 를 누르고 Shift 로 T3 까지 넓힌 것과 같다(현재 행은 마지막 행 T3)
            Settle(window);
            Assert.Equal(["T2", "T3"], window.PickedRules().Select(r => r.Table));
            Assert.Equal("T3", vm.Editor.SelectedRule!.Table); // 선택한 규칙 편집 칸은 현재 행 하나 기준 그대로

            await window.RemovePickedRulesAsync();
            Settle(window);

            Assert.Single(asked);
            Assert.Equal(["T1", "T4", "T5"], Names(vm));
            Assert.Equal("T4", vm.Editor.SelectedRule!.Table);
            Assert.Equal(3, grid.Model!.RowCount);
            Assert.Equal("T4", grid.Model.GetCellText(grid.Model.Active.Row, 1));
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Grid_with_a_ctrl_picked_row_set_removes_exactly_those_rows() => HeadlessHost.Run(async () =>
    {
        var (window, vm) = await OpenWindowAsync();
        try
        {
            var asked = Asked(vm);
            var grid = window.FindControl<MonitorGrid>("RuleGrid")!;

            grid.Model!.SetSelectedRows([(0, 0), (2, 2), (4, 4)], 2); // Ctrl 클릭으로 떨어진 세 행
            Settle(window);
            Assert.Equal(["T1", "T3", "T5"], window.PickedRules().Select(r => r.Table));

            await window.RemovePickedRulesAsync();
            Settle(window);

            Assert.Single(asked);
            Assert.Contains("3개 테이블 규칙을 지웁니다", asked[0]);
            Assert.Equal(["T2", "T4"], Names(vm));
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Grid_with_one_row_selected_removes_it_without_asking() => HeadlessHost.Run(async () =>
    {
        var (window, vm) = await OpenWindowAsync();
        try
        {
            var asked = Asked(vm);

            Assert.Equal(["T1"], window.PickedRules().Select(r => r.Table));
            await window.RemovePickedRulesAsync();
            Settle(window);

            Assert.Empty(asked);
            Assert.Equal(["T2", "T3", "T4", "T5"], Names(vm));
            Assert.Equal("T2", vm.Editor.SelectedRule!.Table);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Toolbar_remove_button_removes_every_picked_rule_after_confirming() => HeadlessHost.Run(async () =>
    {
        var (window, vm) = await OpenWindowAsync();
        try
        {
            var asked = Asked(vm);
            var grid = window.FindControl<MonitorGrid>("RuleGrid")!;
            grid.Model!.SetSelectedRows([(0, 1)], 0);
            Settle(window);

            window.FindControl<Button>("RemoveRuleButton")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            for (var i = 0; i < 10 && vm.Editor.Rules.Count == 5; i++)
            {
                await Task.Yield();
                Settle(window);
            }

            Assert.Single(asked);
            Assert.Equal(["T3", "T4", "T5"], Names(vm));
        }
        finally
        {
            window.Close();
        }
    });
}
