using Oranda.App.Features.DataCopy;
using Oranda.Core.Metadata;
using Oranda.Core.Transfer.Copy;

namespace Oranda.App.Tests.Features.DataCopy;

/// <summary>
/// CI1: 복사 창 위쪽 전체 기본값(규칙·대상 처리·없으면 만들기). 테이블을 더할 때 채우고, 바꾸면 따로 바꾸지 않은 테이블에만 반영하며,
/// 테이블별 값이 우선한다. 저장 파일에는 전체 기본값과 테이블별로 따로 바꾼 항목이 담기고, 옛 파일은 모든 테이블을 따로 바꾼 것으로 열린다.
/// </summary>
public sealed class RuleDefaultsTests
{
    private static readonly DbObjectRef Orders = new("APP", "ORDERS", "TABLE");
    private static readonly DbObjectRef Items = new("APP", "ORDER_ITEMS", "TABLE");

    private static async Task<(DataCopyViewModel Vm, DataCopyHarness H)> OpenAsync(CopyJob? job = null)
    {
        var h = new DataCopyHarness();
        h.Metadata.Tables[Orders] = TestJobs.Table("ORDERS", TestJobs.Column("ORDER_ID", "NUMBER", 1));
        h.Metadata.Tables[Items] = TestJobs.Table("ORDER_ITEMS", TestJobs.Column("ITEM_ID", "NUMBER", 1));
        return (await h.OpenAsync(job ?? TestJobs.Job("전체 기본값")), h);
    }

    [Fact]
    public async Task Create_if_missing_default_starts_on_and_a_new_table_takes_the_defaults()
    {
        var (vm, h) = await OpenAsync();
        var editor = vm.Editor;
        Assert.True(editor.DefaultCreateIfMissing); // 환경과 상관없이 켠 채 시작
        Assert.Equal(DefaultKindChoice.Auto, editor.DefaultKind);
        Assert.Equal(DefaultActionChoice.Auto, editor.DefaultAction);

        await vm.AddTableAsync(Orders, h.Source);

        var rule = Assert.Single(editor.Rules);
        Assert.True(rule.CreateIfMissing);
        Assert.Equal(RuleKind.All, rule.Kind);
        Assert.Equal(RuleOverrides.None, rule.Overrides);
    }

    [Fact]
    public async Task Added_tables_take_the_global_values_at_the_time_of_adding()
    {
        var (vm, h) = await OpenAsync();
        vm.Editor.DefaultCreateIfMissing = false;
        vm.Editor.DefaultAction = DefaultActionChoice.AppendOnly;

        await vm.AddTableAsync(Orders, h.Source);

        var rule = Assert.Single(vm.Editor.Rules);
        Assert.False(rule.CreateIfMissing);
        Assert.Equal(TargetAction.AppendOnly, rule.Action);
    }

    [Fact]
    public async Task Changing_a_global_value_reaches_only_tables_not_changed_individually()
    {
        var (vm, h) = await OpenAsync();
        await vm.AddTableAsync(Orders, h.Source);
        await vm.AddTableAsync(Items, h.Source);
        var orders = vm.Editor.Rules.Single(r => r.Table == "ORDERS");
        var items = vm.Editor.Rules.Single(r => r.Table == "ORDER_ITEMS");

        orders.Action = TargetAction.MergeByPk; // 이 테이블만 따로 바꿈
        orders.CreateIfMissing = false;
        Assert.Equal(RuleOverrides.Action | RuleOverrides.CreateIfMissing, orders.Overrides);

        vm.Editor.DefaultAction = DefaultActionChoice.AppendOnly;
        vm.Editor.DefaultCreateIfMissing = false;

        Assert.Equal(TargetAction.MergeByPk, orders.Action); // 따로 바꾼 테이블은 그대로
        Assert.Equal(TargetAction.AppendOnly, items.Action); // 따르는 테이블은 전체 값
        Assert.False(items.CreateIfMissing);

        vm.Editor.DefaultCreateIfMissing = true;
        Assert.False(orders.CreateIfMissing); // 표별 값이 우선
        Assert.True(items.CreateIfMissing);
    }

    [Fact]
    public async Task Reset_to_defaults_clears_the_individual_changes_and_follows_the_global_values_again()
    {
        var (vm, h) = await OpenAsync();
        await vm.AddTableAsync(Orders, h.Source);
        var rule = Assert.Single(vm.Editor.Rules);
        vm.Editor.DefaultAction = DefaultActionChoice.AppendOnly;
        rule.Action = TargetAction.MergeByPk;
        rule.CreateIfMissing = false;
        Assert.NotEqual(RuleOverrides.None, rule.Overrides);

        vm.Editor.ResetRuleToDefaults(rule);

        Assert.Equal(RuleOverrides.None, rule.Overrides);
        Assert.Equal(TargetAction.AppendOnly, rule.Action);
        Assert.True(rule.CreateIfMissing);

        vm.Editor.DefaultAction = DefaultActionChoice.DeleteAllInsert; // 다시 전체 값을 따른다
        Assert.Equal(TargetAction.DeleteAllInsert, rule.Action);
    }

    [Fact]
    public async Task A_user_chosen_kind_is_individual_and_recreate_or_blocked_tables_never_get_create_if_missing()
    {
        var (vm, h) = await OpenAsync();
        vm.Editor.DefaultCreateIfMissing = false;
        await vm.AddTableAsync(Orders, h.Source);
        await vm.AddTableAsync(Items, h.Source);
        var orders = vm.Editor.Rules.Single(r => r.Table == "ORDERS");
        var items = vm.Editor.Rules.Single(r => r.Table == "ORDER_ITEMS");

        orders.ChangeKind(RuleKind.Custom);
        Assert.True(orders.Overrides.HasFlag(RuleOverrides.Kind) && orders.Overrides.HasFlag(RuleOverrides.Action));
        orders.Action = TargetAction.Recreate; // 다시 만들기: 이미 만든다
        items.CreateBlocked = true; // 마지막 점검에서 만들 수 없음으로 나온 테이블

        vm.Editor.DefaultCreateIfMissing = true;

        Assert.False(orders.CreateIfMissing);
        Assert.False(items.CreateIfMissing);
    }

    [Fact]
    public async Task Tables_that_already_exist_in_the_target_are_left_off_when_the_default_is_applied()
    {
        var (vm, h) = await OpenAsync();
        vm.Editor.DefaultCreateIfMissing = false;
        await vm.AddTableAsync(Orders, h.Source);
        await vm.AddTableAsync(Items, h.Source);
        var orders = vm.Editor.Rules.Single(r => r.Table == "ORDERS");
        var items = vm.Editor.Rules.Single(r => r.Table == "ORDER_ITEMS");
        orders.TargetExists = true; // 사전 점검: 대상에 이미 있음

        vm.Editor.DefaultCreateIfMissing = true;

        Assert.False(orders.CreateIfMissing);
        Assert.True(items.CreateIfMissing);
    }

    [Fact]
    public async Task Precheck_hints_mark_existing_and_blocked_tables_without_changing_their_values()
    {
        var (vm, h) = await OpenAsync();
        await vm.AddTableAsync(Orders, h.Source);
        await vm.AddTableAsync(Items, h.Source);
        var job = vm.Editor.BuildJob();
        var orders = vm.Editor.Rules.Single(r => r.Table == "ORDERS");
        var items = vm.Editor.Rules.Single(r => r.Table == "ORDER_ITEMS");
        var report = TestJobs.Report(
        [
            new CheckItem("ORDER_ITEMS", CopyCheckCodes.CreateBlocked, CheckLevel.Error, "만들 수 없음"),
        ]);

        vm.Editor.ApplyPrecheckHints(job, report);

        Assert.True(items.CreateBlocked);
        Assert.False(items.TargetExists);
        Assert.True(orders.TargetExists); // 항목이 없다 = 대상에 이미 있다
        Assert.True(orders.CreateIfMissing && items.CreateIfMissing); // 이미 정한 값은 바꾸지 않는다
    }

    [Fact]
    public async Task Saved_job_keeps_the_defaults_and_the_individual_changes()
    {
        var (vm, h) = await OpenAsync();
        await vm.AddTableAsync(Orders, h.Source);
        await vm.AddTableAsync(Items, h.Source);
        vm.Editor.DefaultKind = DefaultKindChoice.All;
        vm.Editor.DefaultAction = DefaultActionChoice.AppendOnly;
        vm.Editor.DefaultCreateIfMissing = false;
        vm.Editor.Rules.Single(r => r.Table == "ORDERS").Action = TargetAction.MergeByPk;

        var job = vm.Editor.BuildJob();

        Assert.Equal(new TableDefaults(RuleKind.All, TargetAction.AppendOnly, false), job.TableDefaults);
        Assert.Equal(RuleOverrides.Action, job.Tables.Single(t => t.Table == "ORDERS").Overrides);
        Assert.Equal(RuleOverrides.None, job.Tables.Single(t => t.Table == "ORDER_ITEMS").Overrides);

        // 저장한 작업을 다시 열어도 같다.
        var reopened = await new DataCopyHarness().OpenAsync(job);
        Assert.Equal(DefaultActionChoice.AppendOnly, reopened.Editor.DefaultAction);
        Assert.False(reopened.Editor.DefaultCreateIfMissing);
        Assert.Equal(RuleOverrides.Action, reopened.Editor.Rules.Single(r => r.Table == "ORDERS").Overrides);
        reopened.Editor.DefaultAction = DefaultActionChoice.DeleteAllInsert;
        Assert.Equal(TargetAction.MergeByPk, reopened.Editor.Rules.Single(r => r.Table == "ORDERS").Action); // 따로 바꾼 값
        Assert.Equal(TargetAction.DeleteAllInsert, reopened.Editor.Rules.Single(r => r.Table == "ORDER_ITEMS").Action);
    }

    [Fact]
    public async Task An_old_job_file_without_defaults_opens_with_every_table_marked_individual()
    {
        var old = TestJobs.Job("옛 파일", "SRC", "TGT", TestJobs.Rule("ORDERS") with { Action = TargetAction.MergeByPk, CreateIfMissing = false });
        Assert.Null(old.TableDefaults);

        var h = new DataCopyHarness();
        var vm = await h.OpenAsync(old);

        var rule = Assert.Single(vm.Editor.Rules);
        Assert.Equal(RuleOverrides.All, rule.Overrides);
        vm.Editor.DefaultAction = DefaultActionChoice.AppendOnly; // 전체 값을 바꿔도 저장된 값이 그대로
        vm.Editor.DefaultCreateIfMissing = true;
        Assert.Equal(TargetAction.MergeByPk, rule.Action);
        Assert.False(rule.CreateIfMissing);
        Assert.Equal(RuleKind.All, rule.Kind);
    }

    [Fact]
    public async Task Opening_an_old_job_does_not_rewrite_it_until_the_defaults_or_the_table_list_change()
    {
        var old = TestJobs.Job("옛 파일", "SRC", "TGT", TestJobs.Rule("ORDERS"));
        var h = new DataCopyHarness();
        h.Metadata.Tables[Orders] = TestJobs.Table("ORDERS", TestJobs.Column("ORDER_ID", "NUMBER", 1));
        h.Metadata.Tables[Items] = TestJobs.Table("ORDER_ITEMS", TestJobs.Column("ITEM_ID", "NUMBER", 1));
        var vm = await h.OpenAsync(old);

        var same = vm.Editor.BuildJob();
        Assert.Null(same.TableDefaults); // 열기만 했으면 옛 형식 그대로(저장 안 한 변경으로 보이지 않는다)
        Assert.Equal(RuleOverrides.None, Assert.Single(same.Tables).Overrides);

        await vm.AddTableAsync(Items, h.Source); // 테이블을 더하면 새 형식으로: 옛 테이블은 따로 바꾼 것, 새 테이블은 전체 값을 따른다
        var changed = vm.Editor.BuildJob();
        Assert.NotNull(changed.TableDefaults);
        Assert.Equal(RuleOverrides.All, changed.Tables.Single(t => t.Table == "ORDERS").Overrides);
        Assert.Equal(RuleOverrides.None, changed.Tables.Single(t => t.Table == "ORDER_ITEMS").Overrides);
    }
}
