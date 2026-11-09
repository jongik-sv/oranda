using Oranda.App.Features.DataCopy;
using Oranda.Core.Transfer.Copy;

namespace Oranda.App.Tests.Features.DataCopy;

/// <summary>마스킹 줄의 「−」: 그 열의 마스킹을 빼고, 저장할 때 되살아나지 않는다.</summary>
public class MaskRemoveTests
{
    private static TableRule Orders() => TestJobs.Rule("ORDERS") with
    {
        Masks = [new MaskRule("CUST_NAME", MaskKind.Hash), new MaskRule("PHONE", MaskKind.Fixed) { Value = "000" }],
    };

    [Fact]
    public async Task Removed_mask_goes_back_to_the_add_list_and_is_not_saved()
    {
        var h = new DataCopyHarness();
        var vm = await h.OpenAsync(TestJobs.Job("마스킹 빼기", rules: Orders()));
        var rule = vm.Editor.Rules[0];
        rule.SetSource(TestJobs.Table("ORDERS",
            TestJobs.Column("ORDER_ID", "NUMBER", 1),
            TestJobs.Column("CUST_NAME", "VARCHAR2", 2, length: 50),
            TestJobs.Column("PHONE", "VARCHAR2", 3, length: 20)));
        Assert.Equal(["CUST_NAME", "PHONE"], rule.Masks.Select(m => m.Column));

        rule.RemoveMask(rule.Masks.Single(m => m.Column == "CUST_NAME"));

        Assert.Equal(["PHONE"], rule.Masks.Select(m => m.Column));
        Assert.Contains("CUST_NAME", rule.MaskableColumns);
        Assert.Equal(["PHONE"], vm.Editor.BuildJob().Tables[0].Masks.Select(m => m.Column));

        Assert.True(await vm.SaveCurrentJobAsync());
        Assert.Equal(["PHONE"], h.Store.Jobs[vm.CurrentJob!.Id].Tables[0].Masks.Select(m => m.Column));
    }

    [Fact]
    public async Task Removing_every_mask_leaves_none()
    {
        var h = new DataCopyHarness();
        var vm = await h.OpenAsync(TestJobs.Job("모두 빼기", rules: Orders()));
        var rule = vm.Editor.Rules[0];
        rule.SetSource(TestJobs.Table("ORDERS",
            TestJobs.Column("CUST_NAME", "VARCHAR2", 1, length: 50),
            TestJobs.Column("PHONE", "VARCHAR2", 2, length: 20)));

        foreach (var mask in rule.Masks.ToList())
        {
            rule.RemoveMask(mask);
        }

        Assert.Empty(rule.Masks);
        Assert.Empty(vm.Editor.BuildJob().Tables[0].Masks);
    }
}
