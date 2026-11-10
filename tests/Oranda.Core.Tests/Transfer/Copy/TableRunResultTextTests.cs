using Oranda.Core.Transfer.Copy;

namespace Oranda.Core.Tests.Transfer.Copy;

/// <summary>대상 건수를 모르는 표 결과(중지·건수 세기 실패는 -1)가 글에 -1 로 나오지 않는다(xdb-copy XC8c).</summary>
public sealed class TableRunResultTextTests
{
    [Fact]
    public void Unknown_target_count_of_a_stopped_table_reads_as_a_dash()
    {
        var stopped = new TableRunResult("ORDERS", 10, -1, TimeSpan.FromSeconds(1), CopyOutcome.Stopped, null);

        Assert.Equal("-", stopped.TargetRowsText);
        Assert.DoesNotContain("-1", $"원본 {stopped.SourceRows} 대상 {stopped.TargetRowsText}", StringComparison.Ordinal);
    }

    [Fact]
    public void Known_target_count_is_grouped_and_zero_is_a_number()
    {
        Assert.Equal("1,234", new TableRunResult("T", 1234, 1234, TimeSpan.Zero, CopyOutcome.Succeeded, null).TargetRowsText);
        Assert.Equal("0", new TableRunResult("T", 0, 0, TimeSpan.Zero, CopyOutcome.Succeeded, null).TargetRowsText);
    }
}
