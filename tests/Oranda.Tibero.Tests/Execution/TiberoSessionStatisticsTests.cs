using Oranda.Core.Execution.SqlPlus;
using Oranda.Tibero.Execution;

namespace Oranda.Tibero.Tests.Execution;

// D5: Tibero 통계 = v$mystat NAME 열(실측 docs/design/tibero-driver-exec.md 6장).
public class TiberoSessionStatisticsTests
{
    [Fact]
    public void Source_reads_names_directly_from_v_mystat()
    {
        var source = TiberoSessionStatistics.Source;

        Assert.Equal("SELECT name, value FROM v$mystat WHERE value <> 0", source.Query);
        Assert.DoesNotContain("statistic#", source.Query, StringComparison.OrdinalIgnoreCase); // Oracle 잇기 = TBR-8026
        Assert.DoesNotContain(source.Interesting, n => n.Contains("SQL*Net", StringComparison.Ordinal)); // 받은 바이트는 Tibero 에 없음
        Assert.Equal(source.Interesting.Count, source.Interesting.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Describe_shows_measured_tibero_names_in_order()
    {
        var before = new Dictionary<string, decimal>(StringComparer.Ordinal) { ["logical reads"] = 10, ["user calls"] = 3, ["other stat"] = 1 };
        var after = new Dictionary<string, decimal>(StringComparer.Ordinal)
        {
            ["logical reads"] = 15, ["user calls"] = 5, ["consistent block gets"] = 4, ["other stat"] = 9,
        };

        var lines = SessionStatistics.Describe(TiberoSessionStatistics.Source, SessionStatistics.Diff(before, after)).ToList();

        Assert.Equal("통계: logical reads=5, consistent block gets=4, user calls=2", Assert.Single(lines));
    }
}
