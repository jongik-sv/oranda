using Oranda.App.Features.DataCopy;
using Oranda.Core.Transfer.Copy;

namespace Oranda.App.Tests.Features.DataCopy;

/// <summary>변수 줄 「조건에 넣기」: 고른 규칙의 WHERE 에 <c>열 IN (:이름)</c> 을 넣는다(열 짐작·AND 붙이기·중복·규칙 없음).</summary>
public sealed class VariableConditionTests
{
    private static async Task<(DataCopyViewModel Vm, TableRuleViewModel Rule)> OpenAsync(TableRule rule, params string[] columns)
    {
        var h = new DataCopyHarness();
        var vm = await h.OpenAsync(TestJobs.Job("변수 조건", rules: rule));
        var vmRule = vm.Editor.Rules[0];
        if (columns.Length > 0)
        {
            vmRule.SetSource(TestJobs.Table(rule.Table, [.. columns.Select((c, i) => TestJobs.Column(c, "VARCHAR2", i + 1, length: 20))]));
        }

        vm.Editor.SelectedRule = vmRule;
        return (vm, vmRule);
    }

    private static CopyVariableViewModel Variable(DataCopyViewModel vm, string name, string values)
    {
        var variable = vm.Editor.AddVariable();
        variable.Name = name;
        variable.ValuesText = values;
        return variable;
    }

    [Fact]
    public async Task Column_with_the_same_name_is_used()
    {
        var (vm, rule) = await OpenAsync(TestJobs.Rule("DOCS", RuleKind.Custom), "DOC_ID", "csm_doc_nos");
        var variable = Variable(vm, "CSM_DOC_NOS", "A1\nA2");

        vm.InsertVariableCondition(variable);

        Assert.Equal("csm_doc_nos IN (:CSM_DOC_NOS)", rule.CustomWhere);
        Assert.False(vm.StatusIsError);
        Assert.Null(vm.Editor.Validate()); // 넣은 :이름 은 작업 변수라 「변수 없음」 점검에 걸리지 않는다
    }

    [Fact]
    public async Task Trailing_s_is_dropped_to_find_the_column()
    {
        var (vm, rule) = await OpenAsync(TestJobs.Rule("DOCS", RuleKind.Custom), "DOC_ID", "CSM_DOC_NO");
        vm.InsertVariableCondition(Variable(vm, "CSM_DOC_NOS", "A1, A2, A3"));

        Assert.Equal("CSM_DOC_NO IN (:CSM_DOC_NOS)", rule.CustomWhere);
        Assert.Equal("ORDER_NO", RuleEditorViewModel.GuessColumn("ORDER_NO_LIST", ["ORDER_NO"]));
        Assert.Equal("COIL", RuleEditorViewModel.GuessColumn(":coil_ids", ["COIL"]));
    }

    [Fact]
    public async Task Unknown_column_gets_a_placeholder_and_asks_to_fix_it()
    {
        var (vm, rule) = await OpenAsync(TestJobs.Rule("DOCS", RuleKind.Custom), "DOC_ID");
        vm.InsertVariableCondition(Variable(vm, "CSM_DOC_NOS", "A1\nA2"));

        Assert.Equal($"{RuleEditorViewModel.ColumnPlaceholder} IN (:CSM_DOC_NOS)", rule.CustomWhere);
        Assert.True(vm.StatusIsError);
        Assert.Contains("열 이름", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Single_value_uses_equals_like_the_usage_hint()
    {
        var (vm, rule) = await OpenAsync(TestJobs.Rule("ORDERS", RuleKind.Custom), "MO_NO");
        var variable = Variable(vm, "MO_NO", "MO-26-0042");

        vm.InsertVariableCondition(variable);

        Assert.Equal("MO_NO " + variable.UsageText, rule.CustomWhere);
        Assert.Equal("MO_NO = :MO_NO", rule.CustomWhere);
    }

    [Fact]
    public async Task Existing_condition_gets_the_new_one_after_and()
    {
        var (vm, rule) = await OpenAsync(TestJobs.Rule("DOCS", RuleKind.Custom) with { CustomWhere = "STATUS = 'DONE'  " }, "CSM_DOC_NO");
        vm.InsertVariableCondition(Variable(vm, "CSM_DOC_NOS", "A1\nA2"));

        Assert.Equal("STATUS = 'DONE' AND CSM_DOC_NO IN (:CSM_DOC_NOS)", rule.CustomWhere);
    }

    [Fact]
    public async Task Period_rule_takes_it_in_the_extra_condition()
    {
        var (vm, rule) = await OpenAsync(TestJobs.Rule("DOCS", RuleKind.Period) with { ExtraWhere = "X = 1" }, "CSM_DOC_NO");
        vm.InsertVariableCondition(Variable(vm, "CSM_DOC_NOS", "A1\nA2"));

        Assert.Equal(RuleKind.Period, rule.Kind);
        Assert.Equal("X = 1 AND CSM_DOC_NO IN (:CSM_DOC_NOS)", rule.ExtraWhere);
        Assert.Null(rule.CustomWhere);
    }

    [Fact]
    public async Task All_rule_becomes_a_custom_condition_rule()
    {
        var (vm, rule) = await OpenAsync(TestJobs.Rule("DOCS"), "CSM_DOC_NO");
        Assert.Equal(TargetAction.DeleteAllInsert, rule.Action);

        vm.InsertVariableCondition(Variable(vm, "CSM_DOC_NOS", "A1\nA2"));

        Assert.Equal(RuleKind.Custom, rule.Kind);
        Assert.Equal(TargetAction.DeleteRangeInsert, rule.Action); // 조건에 든 행만 지우고 넣는다
        Assert.Equal("CSM_DOC_NO IN (:CSM_DOC_NOS)", rule.CustomWhere);
        Assert.Contains("직접 조건", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Same_variable_already_in_the_condition_is_not_added_again()
    {
        var (vm, rule) = await OpenAsync(TestJobs.Rule("DOCS", RuleKind.Custom) with { CustomWhere = "CSM_DOC_NO IN (:csm_doc_nos)" }, "CSM_DOC_NO");
        vm.InsertVariableCondition(Variable(vm, "CSM_DOC_NOS", "A1\nA2"));

        Assert.Equal("CSM_DOC_NO IN (:csm_doc_nos)", rule.CustomWhere);
        Assert.True(vm.StatusIsError);
        Assert.Contains("이미", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_a_selected_rule_nothing_is_inserted()
    {
        var (vm, rule) = await OpenAsync(TestJobs.Rule("DOCS", RuleKind.Custom), "CSM_DOC_NO");
        vm.Editor.SelectedRule = null;
        Assert.False(vm.Editor.CanInsertVariableCondition);

        vm.InsertVariableCondition(Variable(vm, "CSM_DOC_NOS", "A1\nA2"));

        Assert.Null(rule.CustomWhere);
        Assert.True(vm.StatusIsError);
    }

    [Fact]
    public async Task Rules_without_a_where_condition_disable_the_button()
    {
        var (vm, rule) = await OpenAsync(TestJobs.Rule("DOCS", RuleKind.Custom), "CSM_DOC_NO");
        Assert.True(vm.Editor.CanInsertVariableCondition);

        var changed = new List<string?>();
        vm.Editor.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        rule.ChangeKind(RuleKind.ParentKey);
        Assert.False(vm.Editor.CanInsertVariableCondition);
        Assert.Contains(nameof(RuleEditorViewModel.CanInsertVariableCondition), changed);

        vm.InsertVariableCondition(Variable(vm, "CSM_DOC_NOS", "A1\nA2"));
        Assert.Null(rule.CustomWhere);
        Assert.Equal(RuleKind.ParentKey, rule.Kind);
        Assert.True(vm.StatusIsError);
    }
}
