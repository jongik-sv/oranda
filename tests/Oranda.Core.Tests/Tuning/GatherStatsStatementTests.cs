using Oranda.Core.Connection;
using Oranda.Core.Tuning;

namespace Oranda.Core.Tests.Tuning;

// 소유: SG-1 스키마 브라우저 통계 수집 실행. 공급자별 DBMS_STATS.GATHER_TABLE_STATS 문장(실행용, 끝에 / 없음).
public class GatherStatsStatementTests
{
    private static string N(string s) => s.Replace("\r\n", "\n", StringComparison.Ordinal);

    [Fact]
    public void Oracle_defaults_use_auto_sample_size_auto_histograms_and_cascade()
    {
        var sql = N(GatherStatsDialect.For(Dbms.Oracle).Build("SCOTT", "EMP", GatherStatsOptions.Default));

        Assert.StartsWith("BEGIN\n  DBMS_STATS.GATHER_TABLE_STATS(", sql, StringComparison.Ordinal);
        Assert.Contains("ownname          => 'SCOTT'", sql, StringComparison.Ordinal);
        Assert.Contains("tabname          => 'EMP'", sql, StringComparison.Ordinal);
        Assert.Contains("estimate_percent => DBMS_STATS.AUTO_SAMPLE_SIZE", sql, StringComparison.Ordinal);
        Assert.Contains("method_opt       => 'FOR ALL COLUMNS SIZE AUTO'", sql, StringComparison.Ordinal);
        Assert.Contains("cascade          => TRUE", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("degree", sql, StringComparison.Ordinal); // 비우면 테이블 설정(DBMS_STATS 기본)
        Assert.EndsWith("END;", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("/", sql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(GatherStatsHistogram.None, "'FOR ALL COLUMNS SIZE 1'")]
    [InlineData(GatherStatsHistogram.SkewOnly, "'FOR ALL COLUMNS SIZE SKEWONLY'")]
    public void Oracle_histogram_choices_map_to_method_opt(GatherStatsHistogram histogram, string expected)
    {
        var sql = GatherStatsDialect.For(Dbms.Oracle).Build("SCOTT", "EMP", GatherStatsOptions.Default with { Histogram = histogram });

        Assert.Contains("method_opt       => " + expected, sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Oracle_percent_degree_and_no_cascade_are_written_as_numbers()
    {
        var options = new GatherStatsOptions { SamplePercent = 12.5m, Cascade = false, Degree = 4 };

        var sql = GatherStatsDialect.For(Dbms.Oracle).Build("SCOTT", "EMP", options);

        Assert.Contains("estimate_percent => 12.5,", sql, StringComparison.Ordinal);
        Assert.Contains("cascade          => FALSE", sql, StringComparison.Ordinal);
        Assert.Contains("degree           => 4", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Mixed_case_names_are_double_quoted_and_quotes_doubled()
    {
        var sql = GatherStatsDialect.For(Dbms.Oracle).Build("App", "O'Brien Tab", GatherStatsOptions.Default);

        Assert.Contains("ownname          => '\"App\"'", sql, StringComparison.Ordinal);
        Assert.Contains("tabname          => '\"O''Brien Tab\"'", sql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("A\"B")]
    [InlineData("A\nB")]
    public void Names_that_cannot_be_identifiers_are_rejected(string table)
    {
        Assert.Throws<ArgumentException>(() => GatherStatsDialect.For(Dbms.Oracle).Build("SCOTT", table, GatherStatsOptions.Default));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(100.5)]
    public void Sample_percent_out_of_range_is_rejected(double percent)
    {
        var options = GatherStatsOptions.Default with { SamplePercent = (decimal)percent };

        Assert.Throws<ArgumentOutOfRangeException>(() => GatherStatsDialect.For(Dbms.Oracle).Build("SCOTT", "EMP", options));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1025)]
    public void Degree_out_of_range_is_rejected(int degree)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            GatherStatsDialect.For(Dbms.Oracle).Build("SCOTT", "EMP", GatherStatsOptions.Default with { Degree = degree }));
    }

    [Fact]
    public void Oracle_supports_every_option()
    {
        var s = GatherStatsDialect.For(Dbms.Oracle).Support;

        Assert.True(s.SkewOnly);
        Assert.True(s.Degree);
        Assert.True(s.Cascade);
        Assert.True(s.SamplePercent);
    }

    [Fact]
    public void Tibero_uses_auto_sample_but_never_the_auto_invalidate_constant()
    {
        var dialect = GatherStatsDialect.For(Dbms.Tibero);
        var sql = N(dialect.Build("SCOTT", "EMP", GatherStatsOptions.Default));

        Assert.True(dialect.Support.SkewOnly);
        Assert.Contains("ownname          => 'SCOTT'", sql, StringComparison.Ordinal);
        Assert.Contains("estimate_percent => DBMS_STATS.AUTO_SAMPLE_SIZE", sql, StringComparison.Ordinal);
        Assert.Contains("method_opt       => 'FOR ALL COLUMNS SIZE AUTO'", sql, StringComparison.Ordinal);
        Assert.Contains("cascade          => TRUE", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("AUTO_INVALIDATE", sql, StringComparison.Ordinal); // Tibero 6: ODP-15146
        Assert.DoesNotContain("AUTO_CASCADE", sql, StringComparison.Ordinal);
        Assert.EndsWith("END;", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Tibero_writes_percent_degree_and_skewonly_when_given()
    {
        var sql = GatherStatsDialect.For(Dbms.Tibero).Build("SCOTT", "EMP",
            new GatherStatsOptions { SamplePercent = 30, Degree = 2, Histogram = GatherStatsHistogram.SkewOnly, Cascade = false });

        Assert.Contains("estimate_percent => 30,", sql, StringComparison.Ordinal);
        Assert.Contains("method_opt       => 'FOR ALL COLUMNS SIZE SKEWONLY'", sql, StringComparison.Ordinal);
        Assert.Contains("cascade          => FALSE", sql, StringComparison.Ordinal);
        Assert.Contains("degree           => 2", sql, StringComparison.Ordinal);
    }
}
