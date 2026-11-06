using Oranda.Core.Transfer.Copy;
using Xunit;

namespace Oranda.Core.Tests.Transfer.Copy;

public class CopyOrderTests
{
    [Fact]
    public void Sorts_parents_before_children_stably()
    {
        var r = CopyOrder.Sort(["ORDER_ITEM", "ORDER", "CUSTOMER"], [("ORDER_ITEM", "ORDER"), ("ORDER", "CUSTOMER")]);
        Assert.Equal(["CUSTOMER", "ORDER", "ORDER_ITEM"], r.ParentsFirst);
        Assert.Empty(r.Cycles);
    }

    [Fact]
    public void Ignores_self_reference_and_outside_tables()
    {
        var r = CopyOrder.Sort(["B", "A"], [("A", "A"), ("A", "OUTSIDE"), ("OUTSIDE", "B")]);
        Assert.Equal(["B", "A"], r.ParentsFirst);
        Assert.Empty(r.Cycles);
    }

    [Fact]
    public void Reports_cycle_and_keeps_input_order()
    {
        var r = CopyOrder.Sort(["A", "B", "C"], [("A", "B"), ("B", "A")]);
        Assert.Equal(["C", "A", "B"], r.ParentsFirst);
        var cycle = Assert.Single(r.Cycles);
        Assert.Equal(["A", "B"], cycle);
    }

    [Fact]
    public void Cycle_descendants_follow_the_cycle()
    {
        var r = CopyOrder.Sort(["D", "A", "B"], [("D", "A"), ("A", "B"), ("B", "A")]);
        Assert.Equal(["A", "B", "D"], r.ParentsFirst);
        Assert.Single(r.Cycles);
    }
}
