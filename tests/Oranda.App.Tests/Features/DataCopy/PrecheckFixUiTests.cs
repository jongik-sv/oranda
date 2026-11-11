using Oranda.App.Features.DataCopy;
using Oranda.Core.Connection;
using Oranda.Core.Transfer.Copy;

namespace Oranda.App.Tests.Features.DataCopy;

/// <summary>
/// 사전 점검에서 그 자리에서 고치기(PC1, 결정 D-066): 항목 체크 → 「선택한 것 처리」 → 규칙이 바뀌고 점검을 다시 한다(가짜 점검).
/// 그리고 규칙 편집에 다녀와도 점검 결과를 버리지 않기(규칙이 그대로면 다시 점검하지 않음, 고쳤으면 「다시 점검 필요」·실행 막음).
/// </summary>
public sealed class PrecheckFixUiTests
{
    private static readonly CopyFixContext Dev = new(Dbms.Oracle, Dbms.Oracle, TargetIsDevelopment: true);

    // 원본에 NOTE 가 있고 대상에 없다: 규칙이 그 열을 건너뛰거나 더하기로 하면 항목이 사라진다(실제 점검처럼).
    private static PrecheckReport ColumnReport(CopyJob job)
    {
        var rule = job.Tables[0];
        var plan = new CopyTablePlan(rule,
            TestJobs.Table("ORDERS", TestJobs.Column("ID", "NUMBER"), TestJobs.Column("NOTE", "VARCHAR2", 2, 40)),
            TestJobs.Table("ORDERS", TestJobs.Column("ID", "NUMBER")), ["ID"], []);
        var handled = rule.SkipColumns.Contains("NOTE") || rule.AddColumns?.Contains("NOTE") == true;
        CheckItem[] items = handled
            ? [new CheckItem("ORDERS", "NoPrimaryKey", CheckLevel.Confirm, "PK 없음.")]
            : [new CheckItem("ORDERS", "ColumnNotInTarget", CheckLevel.Confirm, "ORDERS.NOTE: 대상에 없는 열이라 건너뜁니다.") { Column = "NOTE" }];
        return CopyPrecheckFixes.Attach(job, TestJobs.Report(items, [plan]), Dev);
    }

    [Fact]
    public async Task Checking_a_fix_and_applying_changes_the_rule_saves_rechecks_and_reports_a_summary()
    {
        var h = new DataCopyHarness();
        h.Precheck = new FakePrecheck(TestJobs.Report()) { ReportFor = ColumnReport };
        var vm = await h.OpenAsync(TestJobs.Job("열 추가", rules: TestJobs.Rule("ORDERS")));

        await vm.GoToPrecheckAsync();
        var fix = Assert.Single(vm.Precheck.Fixes);
        Assert.True(fix.HasAlternatives);
        Assert.Equal(["그 열 빼고 복사", "대상에 열 추가"], fix.Options.Select(o => o.Title));
        Assert.Equal("APP.ORDERS.NOTE · 대상에 없는 열", fix.HeaderText);
        Assert.Same(fix, vm.Precheck.Items.Single().Fix); // 상세 칸·그리드와 같은 줄
        Assert.Equal("그 열 빼고 복사 / 대상에 열 추가", vm.Precheck.Items.Single().FixText);
        Assert.False(vm.Precheck.CanApplyFixes); // 아무것도 고르지 않았다

        fix.Chosen = fix.Options[1];
        Assert.Equal("ALTER TABLE \"APP\".\"ORDERS\" ADD (\"NOTE\" VARCHAR2(40 CHAR))", fix.PreviewText);
        fix.IsSelected = true;
        Assert.True(vm.Precheck.CanApplyFixes);
        await vm.ApplySelectedFixesAsync();

        Assert.Equal(2, h.Precheck.Calls); // 다시 점검했다
        Assert.Equal(["NOTE"], vm.CurrentJob!.Tables[0].AddColumns);
        Assert.Equal(["NOTE"], h.Store.Jobs[vm.CurrentJob.Id].Tables[0].AddColumns); // 저장했다
        Assert.Empty(vm.Precheck.Fixes); // 고친 항목은 다시 점검한 결과에 없다
        Assert.Equal("1건 처리 — 사전 점검을 다시 했습니다", vm.Precheck.FixSummary);
        Assert.True(vm.Precheck.HasFixSummary);
        Assert.Equal(DataCopyStep.Precheck, vm.Step);

        // 규칙 편집에서 되돌릴 수 있다: 「대상에 추가할 열」 체크를 풀면 더하지 않는다.
        var rule = Assert.Single(vm.Editor.Rules);
        var added = Assert.Single(rule.AddColumns);
        Assert.True(rule.HasAddColumns);
        added.IsAdded = false;
        Assert.Null(vm.Editor.BuildJob().Tables[0].AddColumns);
    }

    [Fact]
    public async Task Skip_column_fix_puts_the_column_in_the_rule_skips_and_select_all_and_clear_work()
    {
        var h = new DataCopyHarness();
        h.Precheck = new FakePrecheck(TestJobs.Report()) { ReportFor = ColumnReport };
        var vm = await h.OpenAsync(TestJobs.Job("열 빼기", rules: TestJobs.Rule("ORDERS")));
        await vm.GoToPrecheckAsync();

        vm.Precheck.SelectAllFixes();
        Assert.True(vm.Precheck.Fixes.All(f => f.IsSelected));
        Assert.Contains("고른 것 1건", vm.Precheck.FixHeaderText, StringComparison.Ordinal);
        vm.Precheck.ClearFixSelection();
        Assert.False(vm.Precheck.CanApplyFixes);

        vm.Precheck.SelectAllFixes(); // 기본 대안 = 그 열 빼고 복사
        await vm.ApplySelectedFixesAsync();

        Assert.Equal(["NOTE"], vm.CurrentJob!.Tables[0].SkipColumns);
        Assert.Null(vm.CurrentJob.Tables[0].AddColumns);
        Assert.Equal(2, h.Precheck.Calls);
    }

    [Fact]
    public async Task Run_decision_fix_only_picks_the_choice_without_rechecking_and_survives_a_later_recheck()
    {
        var h = new DataCopyHarness();
        var plan = TestJobs.Plan("ORDERS", canDisableTriggers: true);
        PrecheckReport Report(CopyJob job) => CopyPrecheckFixes.Attach(job, TestJobs.Report(
        [
            new CheckItem("ORDERS", "TargetTriggers", CheckLevel.Confirm, "대상 트리거가 실행됩니다.") { Choices = [CheckChoice.DisableTriggers] },
            new CheckItem("ORDERS", "ChildRowsMayBlock", CheckLevel.Confirm, "자식 행.") { Choices = [CheckChoice.SwitchToMerge] },
        ], [plan with { Rule = job.Tables[0] }]), Dev);
        h.Precheck = new FakePrecheck(TestJobs.Report()) { ReportFor = Report };
        var vm = await h.OpenAsync(TestJobs.Job("트리거 끄기", rules: TestJobs.Rule("ORDERS")));
        await vm.GoToPrecheckAsync();

        var trigger = vm.Precheck.Fixes.Single(f => f.Owner.Code == "TargetTriggers");
        trigger.IsSelected = true;
        await vm.ApplySelectedFixesAsync();

        Assert.Equal(1, h.Precheck.Calls); // 실행 결정만: 다시 점검하지 않는다
        Assert.Equal("1건 처리 — 실행할 때 적용합니다", vm.Precheck.FixSummary);
        Assert.Contains("ORDERS", vm.Precheck.BuildDecisions().DisableTriggerTables);
        Assert.False(trigger.IsSelected);

        // 규칙을 바꾸는 처리(MERGE)로 다시 점검해도 트리거 끄기는 남는다.
        vm.Precheck.Fixes.Single(f => f.Owner.Code == "ChildRowsMayBlock").IsSelected = true;
        await vm.ApplySelectedFixesAsync();

        Assert.Equal(2, h.Precheck.Calls);
        Assert.Equal(TargetAction.MergeByPk, vm.CurrentJob!.Tables[0].Action);
        Assert.True(vm.CurrentJob.Tables[0].Overrides.HasFlag(RuleOverrides.Action));
        Assert.Contains("ORDERS", vm.Precheck.BuildDecisions().DisableTriggerTables);
    }

    // ---- 규칙 편집에 다녀와도 점검 결과 유지 ----

    [Fact]
    public async Task Going_back_to_rule_edit_and_returning_unchanged_keeps_the_result_without_rechecking()
    {
        var h = new DataCopyHarness();
        h.Precheck = new FakePrecheck(TestJobs.Report([new CheckItem("ORDERS", "OK", CheckLevel.Pass, "통과")]));
        var vm = await h.OpenAsync(TestJobs.Job("다녀오기", rules: TestJobs.Rule("ORDERS")));

        await vm.GoToPrecheckAsync();
        var report = vm.Precheck.Report;
        Assert.Equal("방금 점검", vm.Precheck.CheckedAgoText);
        vm.BackToRuleEdit();
        Assert.True(vm.HasPrecheckResult); // 규칙 편집으로 돌아와도 결과를 버리지 않는다

        await vm.GoToPrecheckAsync();

        Assert.Equal(1, h.Precheck.Calls); // 다시 점검하지 않았다
        Assert.Same(report, vm.Precheck.Report);
        Assert.Equal(DataCopyStep.Precheck, vm.Step);
        Assert.False(vm.Precheck.IsStale);
        Assert.True(vm.CanStartRun);
        Assert.Contains("이전 사전 점검 결과입니다(방금 점검)", vm.StatusText, StringComparison.Ordinal);

        vm.BackToRuleEdit();
        vm.ShowPrecheckResult(); // 「점검 결과 보기」도 같다
        Assert.False(vm.Precheck.IsStale);
        Assert.True(vm.CanStartRun);

        await vm.RecheckAsync(); // 「다시 점검」은 늘 다시 한다
        Assert.Equal(2, h.Precheck.Calls);
    }

    [Fact]
    public async Task Changing_a_rule_marks_the_kept_result_stale_and_blocks_the_run_until_rechecked()
    {
        var h = new DataCopyHarness();
        h.Precheck = new FakePrecheck(TestJobs.Report([new CheckItem("ORDERS", "OK", CheckLevel.Pass, "통과")]));
        var vm = await h.OpenAsync(TestJobs.Job("규칙 고침", rules: TestJobs.Rule("ORDERS")));
        await vm.GoToPrecheckAsync();
        vm.BackToRuleEdit();

        vm.Editor.Rules[0].Action = TargetAction.AppendOnly;
        vm.ShowPrecheckResult();

        Assert.Equal(DataCopyStep.Precheck, vm.Step);
        Assert.True(vm.Precheck.IsStale);
        Assert.NotNull(vm.Precheck.Report); // 결과는 남긴다
        Assert.Equal("규칙이 바뀌어 다시 점검이 필요합니다. 다시 점검하기 전에는 「복사 실행」을 할 수 없습니다.", vm.Precheck.StaleText);
        Assert.False(vm.Precheck.CanRun);
        Assert.False(vm.CanStartRun);
        await vm.StartRunAsync();
        Assert.Equal(0, h.Runner.Calls);
        Assert.Equal(1, h.Precheck.Calls);

        // 고친 규칙으로 「사전 점검 ›」을 누르면 다시 점검하고 막힘이 풀린다.
        vm.BackToRuleEdit();
        await vm.GoToPrecheckAsync();
        Assert.Equal(2, h.Precheck.Calls);
        Assert.False(vm.Precheck.IsStale);
        Assert.True(vm.CanStartRun);
        Assert.Equal(TargetAction.AppendOnly, vm.CurrentJob!.Tables[0].Action);
    }

    [Fact]
    public async Task After_a_run_the_result_is_not_reused_and_the_precheck_runs_again()
    {
        var h = new DataCopyHarness();
        h.Precheck = new FakePrecheck(TestJobs.Report([new CheckItem("ORDERS", "OK", CheckLevel.Pass, "통과")]));
        var vm = await h.OpenAsync(TestJobs.Job("실행 뒤", rules: TestJobs.Rule("ORDERS")));
        await vm.GoToPrecheckAsync();
        await vm.StartRunAsync();
        Assert.Equal(1, h.Runner.Calls);

        vm.BackToRuleEdit();
        Assert.False(vm.HasPrecheckResult); // 실행이 대상을 바꿨을 수 있다(만든 테이블·더한 열)
        await vm.GoToPrecheckAsync();

        Assert.Equal(2, h.Precheck.Calls);
    }
}
