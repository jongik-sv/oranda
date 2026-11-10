using Oranda.App.Features.DataCopy;
using Oranda.Core.Metadata;
using Oranda.Core.Transfer.Copy;

namespace Oranda.App.Tests.Features.DataCopy;

/// <summary>
/// 적재 방식 고르기(조정 결정 88): 전체 기본값 「외부 도구로 적재」는 작업의 <see cref="TableDefaults.Load"/> 로 저장·복원되고,
/// 테이블별 값은 <see cref="LoadChoice.Default"/> = 규칙에 값 없음(전체 값 따름)이다. 「전체 값으로」는 테이블별 값도 지운다.
/// </summary>
public sealed class LoadMethodTests
{
    private static readonly DbObjectRef Orders = new("APP", "ORDERS", "TABLE");

    [Fact]
    public async Task Default_external_load_is_saved_in_the_table_defaults_and_restored_on_open()
    {
        var h = new DataCopyHarness();
        h.Metadata.Tables[Orders] = TestJobs.Table("ORDERS", TestJobs.Column("ORDER_ID", "NUMBER", 1));
        var vm = await h.OpenAsync(TestJobs.Job("적재 방식"));
        Assert.False(vm.Editor.DefaultExternalLoad);
        Assert.Equal(LoadMethod.Standard, vm.Editor.CurrentTableDefaults.Load);

        await vm.AddTableAsync(Orders, h.Source);
        vm.Editor.DefaultExternalLoad = true;
        Assert.Equal(LoadMethod.ExternalTool, vm.Editor.CurrentTableDefaults.Load);

        var job = vm.Editor.BuildJob();
        Assert.Equal(LoadMethod.ExternalTool, job.TableDefaults!.Load);
        Assert.Null(Assert.Single(job.Tables).Load); // 테이블은 전체 값을 따른다(값 없음)

        var reopened = await new DataCopyHarness().OpenAsync(job);
        Assert.True(reopened.Editor.DefaultExternalLoad);
        Assert.Equal(LoadChoice.Default, Assert.Single(reopened.Editor.Rules).Load);

        reopened.Editor.DefaultExternalLoad = false;
        Assert.Equal(LoadMethod.Standard, reopened.Editor.BuildJob().TableDefaults!.Load);
    }

    [Fact]
    public async Task Job_with_standard_defaults_opens_with_external_load_off()
    {
        var job = TestJobs.Job("기본", "SRC", "TGT", TestJobs.Rule("ORDERS")) with
        {
            TableDefaults = new TableDefaults(null, null, true) { Load = LoadMethod.Standard },
        };

        var vm = await new DataCopyHarness().OpenAsync(job);

        Assert.False(vm.Editor.DefaultExternalLoad);
    }

    [Theory]
    [InlineData(null, LoadChoice.Default)]
    [InlineData(LoadMethod.Standard, LoadChoice.Standard)]
    [InlineData(LoadMethod.ExternalTool, LoadChoice.ExternalTool)]
    public void Table_load_round_trips_between_the_rule_and_the_view_model(LoadMethod? load, LoadChoice choice)
    {
        var rule = TestJobs.Rule("ORDERS") with { Load = load };

        var vm = new TableRuleViewModel(rule);

        Assert.Equal(choice, vm.Load);
        Assert.Equal(load, vm.ToRule().Load);
    }

    [Theory]
    [InlineData(LoadChoice.Default, null)]
    [InlineData(LoadChoice.Standard, LoadMethod.Standard)]
    [InlineData(LoadChoice.ExternalTool, LoadMethod.ExternalTool)]
    public void Choosing_a_table_load_is_written_to_the_rule(LoadChoice choice, LoadMethod? expected)
    {
        var vm = new TableRuleViewModel(TestJobs.Rule("ORDERS")) { Load = choice };

        Assert.Equal(expected, vm.ToRule().Load);
    }

    [Fact]
    public void Clear_overrides_resets_the_table_load_to_follow_the_defaults()
    {
        var vm = new TableRuleViewModel(TestJobs.Rule("ORDERS") with { Load = LoadMethod.ExternalTool });

        vm.ClearOverrides();

        Assert.Equal(LoadChoice.Default, vm.Load);
        Assert.Null(vm.ToRule().Load);
        Assert.Equal(RuleOverrides.None, vm.Overrides);
    }

    [Fact]
    public async Task Reset_rule_to_defaults_also_clears_the_table_load()
    {
        var h = new DataCopyHarness();
        h.Metadata.Tables[Orders] = TestJobs.Table("ORDERS", TestJobs.Column("ORDER_ID", "NUMBER", 1));
        var vm = await h.OpenAsync(TestJobs.Job("적재 방식"));
        await vm.AddTableAsync(Orders, h.Source);
        var rule = Assert.Single(vm.Editor.Rules);
        rule.Load = LoadChoice.Standard;

        vm.Editor.ResetRuleToDefaults(rule);

        Assert.Equal(LoadChoice.Default, rule.Load);
    }

    [Fact]
    public void Load_options_offer_the_three_choices()
    {
        Assert.Equal([LoadChoice.Default, LoadChoice.Standard, LoadChoice.ExternalTool], TableRuleViewModel.LoadOptions.Select(o => o.Value));
    }

    [Fact]
    public void Precheck_code_names_cover_the_external_load_items()
    {
        Assert.Equal("외부 도구로 적재", CheckItemViewModel.CodeNameTable[CopyCheckCodes.ExternalLoad]);
        Assert.Equal("기본 방식으로 적재", CheckItemViewModel.CodeNameTable[CopyCheckCodes.ExternalLoadFallback]);

        var item = new CheckItemViewModel(new CheckItem("ORDERS", CopyCheckCodes.ExternalLoadFallback, CheckLevel.Pass, "ORDERS: 기본 방식으로 넣습니다."), []);
        Assert.Equal("기본 방식으로 적재", item.CodeText);
    }
}
