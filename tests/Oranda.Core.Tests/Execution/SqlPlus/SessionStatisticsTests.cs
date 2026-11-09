using Oranda.Core.Execution.SqlPlus;

namespace Oranda.Core.Tests.Execution.SqlPlus;

// 소유: TSK-06-06. 문장별 세션 통계의 차이·표시(DBMS 가 준 출처를 따른다).
public class SessionStatisticsTests
{
    private static readonly SessionStatisticsSource Source = new(
        "SELECT name, value FROM stats",
        ["session logical reads", "db block gets", "consistent gets", "physical reads", "redo size"]);

    [Fact]
    public void Statistics_diff_keeps_only_increases_and_describes_known_names()
    {
        var before = new Dictionary<string, decimal> { ["session logical reads"] = 5, ["redo size"] = 100, ["other"] = 1 };
        var after = new Dictionary<string, decimal> { ["session logical reads"] = 12, ["redo size"] = 100, ["other"] = 9, ["physical reads"] = 2 };

        var diff = SessionStatistics.Diff(before, after);

        Assert.Equal(7m, diff["session logical reads"]);
        Assert.False(diff.ContainsKey("redo size"));
        Assert.Equal("통계: session logical reads=7, physical reads=2", Assert.Single(SessionStatistics.Describe(Source, diff)));
        Assert.Empty(SessionStatistics.Describe(Source, new Dictionary<string, decimal>()));
    }
}
