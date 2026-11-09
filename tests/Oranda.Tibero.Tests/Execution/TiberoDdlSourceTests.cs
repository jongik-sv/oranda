using Oranda.Core.Execution.Providers;
using Oranda.Core.Journal;
using Oranda.Tibero.Execution;

namespace Oranda.Tibero.Tests.Execution;

public class TiberoDdlSourceTests
{
    private static readonly JournalEntry Template = new() { DbKey = "tibero:test" };

    private static Task<IReadOnlyList<JournalEntry>> Capture(ChangeTarget target)
        => ChangeCapture.CaptureAsync(target, Template, "SCOTT", TiberoDdlSource.Instance, ChangeCapture.Timeout, CancellationToken.None);

    [Theory]
    [InlineData("TABLE", ChangeOperation.Drop)]
    [InlineData("VIEW", ChangeOperation.CreateOrReplace)]
    [InlineData("PACKAGE", ChangeOperation.Drop)]
    [InlineData("PACKAGE", ChangeOperation.CreateOrReplace)]
    public async Task Each_ddl_leaves_one_not_captured_row_with_the_reason(string type, ChangeOperation operation)
    {
        var rows = await Capture(new ChangeTarget(null, "T1", type, operation));

        var row = Assert.Single(rows);
        Assert.Equal(TiberoDdlSource.Reason, row.FailureReason);
        Assert.Null(row.PreviousDdl);
        Assert.Equal(type, row.ObjectType);
        Assert.Equal("SCOTT", row.Owner);
    }

    [Fact]
    public async Task Rename_without_a_type_is_not_reported_as_a_missing_object()
    {
        var rows = await Capture(new ChangeTarget("HR", "OLD_NAME", null, ChangeOperation.Rename));

        var row = Assert.Single(rows);
        Assert.Equal(TiberoDdlSource.Reason, row.FailureReason);
        Assert.Equal(TiberoDdlSource.UnknownType, row.ObjectType);
        Assert.Equal("HR", row.Owner);
    }
}
