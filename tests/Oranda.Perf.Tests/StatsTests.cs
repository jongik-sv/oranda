using Oranda.Perf.Common;

namespace Oranda.Perf.Tests;

/// <summary>TSK-11-03 측정 도구: 통계·결과표 시험. 시간을 재는 시험이 아니므로 기본 실행에 들어간다.</summary>
public class StatsTests
{
    [Fact]
    public void Median_of_odd_and_even_counts()
    {
        Assert.Equal(3, Stats.Median([5, 1, 3]));
        Assert.Equal(2.5, Stats.Median([4, 1, 2, 3]));
        Assert.Equal(7, Stats.Median([7]));
    }

    [Fact]
    public void Percentile_uses_nearest_rank()
    {
        double[] v = Enumerable.Range(1, 20).Select(i => (double)i).ToArray();
        Assert.Equal(19, Stats.Percentile(v, 95)); // ceil(20*0.95)=19 번째
        Assert.Equal(20, Stats.Percentile(v, 100));
        Assert.Equal(1, Stats.Percentile(v, 1));
        Assert.Equal(10, Stats.Percentile(v, 50));
    }

    [Fact]
    public void Percentile_of_five_samples_p95_is_the_max()
    {
        Assert.Equal(9, Stats.Percentile([1, 9, 3, 2, 4], 95));
    }

    [Fact]
    public void Summarize_reports_n_min_median_p95_max()
    {
        var s = Stats.Summarize([30, 10, 20, 50, 40]);
        Assert.Equal(new Summary(5, 10, 30, 50, 50), s);
        Assert.Contains("n=5", s.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Single_sample_is_every_statistic()
    {
        Assert.Equal(new Summary(1, 4.5, 4.5, 4.5, 4.5), Stats.Summarize([4.5]));
    }

    [Fact]
    public void Unsorted_input_is_not_modified()
    {
        double[] v = [3, 1, 2];
        Stats.Summarize(v);
        Assert.Equal([3, 1, 2], v);
    }

    [Fact]
    public void Empty_or_nan_samples_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => Stats.Median([]));
        Assert.Throws<ArgumentException>(() => Stats.Summarize([]));
        Assert.Throws<ArgumentException>(() => Stats.Median([1, double.NaN]));
        Assert.Throws<ArgumentNullException>(() => Stats.Median(null!));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(100.1)]
    [InlineData(double.NaN)]
    public void Percentile_out_of_range_is_rejected(double percent)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Stats.Percentile([1, 2, 3], percent));
    }

    [Fact]
    public void ReportRow_judges_the_chosen_statistic_against_the_limit()
    {
        var met = ReportRow.Create("a", "ms", 16, [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 16, 16, 16, 40], "p95"); // 20개 중 19번째 = 16
        Assert.Equal(Verdict.Met, met.Verdict);

        var over = ReportRow.Create("b", "ms", 16, [10, 12, 30], "p95");
        Assert.Equal(Verdict.Exceeded, over.Verdict);

        var medianOk = ReportRow.Create("c", "ms", 16, [10, 12, 30], "median");
        Assert.Equal(Verdict.Met, medianOk.Verdict);

        var boundary = ReportRow.Create("d", "ms", 16, [16], "max");
        Assert.Equal(Verdict.Met, boundary.Verdict); // 한도와 같으면 충족(이하)
        Assert.Equal(Verdict.Exceeded, ReportRow.Create("e", "ms", 16, [16.01], "max").Verdict);
    }

    [Fact]
    public void ReportRow_without_limit_is_reference_only_and_without_samples_is_not_measured()
    {
        Assert.Equal(Verdict.Met, ReportRow.Create("ref", "ms", null, [999], "median").Verdict);
        var none = ReportRow.Create("x", "ms", 10, [], "p95", "이유");
        Assert.Equal(Verdict.NotMeasured, none.Verdict);
        Assert.Null(none.Summary);
        Assert.Equal(Verdict.NotMeasured, ReportRow.Skipped("y", "ms", 10, "사유").Verdict);
    }

    [Fact]
    public void ReportRow_rejects_unknown_basis_and_blank_item()
    {
        Assert.Throws<ArgumentException>(() => ReportRow.Create("a", "ms", 1, [1], "p99"));
        Assert.Throws<ArgumentException>(() => ReportRow.Create(" ", "ms", 1, [1]));
    }

    [Fact]
    public void Report_writes_json_and_markdown_with_korean_verdicts()
    {
        var report = new Report("제목", "환경 한 줄");
        report.Rows.Add(ReportRow.Create("항목 A", "ms", 16, [5, 6, 7], "p95", "비고|표 깨짐 없음"));
        report.Rows.Add(ReportRow.Create("항목 B", "ms", 16, [50, 60], "median"));
        report.Rows.Add(ReportRow.Skipped("항목 C", "ms", 16, "창이 필요"));
        var md = report.ToMarkdown();
        Assert.Contains("# 제목", md, StringComparison.Ordinal);
        Assert.Contains("| 항목 A | ≤ 16.0 ms | 3 | 6.0 | 7.0 | 7.0 | p95 | 충족 |", md, StringComparison.Ordinal);
        Assert.Contains("비고\\|표 깨짐 없음", md, StringComparison.Ordinal);
        Assert.Contains("| 항목 B | ≤ 16.0 ms | 2 | 55.0 | 60.0 | 60.0 | median | 초과 |", md, StringComparison.Ordinal);
        Assert.Contains("| 항목 C | ≤ 16.0 ms | 0 | - | - | - | - | 미측정 | 창이 필요 |", md, StringComparison.Ordinal);

        var dir = Path.Combine(Path.GetTempPath(), "oranda-perf-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            report.WriteTo(dir, "out");
            Assert.True(File.Exists(Path.Combine(dir, "out.md")));
            Assert.Contains("\"Exceeded\"", File.ReadAllText(Path.Combine(dir, "out.json")), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
