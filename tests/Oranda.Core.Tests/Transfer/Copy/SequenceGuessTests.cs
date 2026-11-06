using Oranda.Core.Transfer.Copy;
using Xunit;

namespace Oranda.Core.Tests.Transfer.Copy;

public class SequenceGuessTests
{
    [Fact]
    public void Guesses_sequence_by_name_patterns()
    {
        var r = SequenceGuess.Candidates("ORDERS", ["ORDER_ID"], ["SEQ_ORDER_ID", "ORDERS_SEQ"]);
        Assert.Equal([new SequenceRule("SEQ_ORDER_ID", "ORDERS", "ORDER_ID")], r);
    }

    [Fact]
    public void Falls_back_to_later_patterns_and_skips_when_none()
    {
        var r = SequenceGuess.Candidates("ORDERS", ["ORDER_ID", "OTHER_ID"], ["ORDERS_SEQ"]);
        Assert.Equal(2, r.Count);
        Assert.All(r, x => Assert.Equal("ORDERS_SEQ", x.Sequence));
        Assert.Empty(SequenceGuess.Candidates("ORDERS", ["ORDER_ID"], ["UNRELATED"]));
    }
}
