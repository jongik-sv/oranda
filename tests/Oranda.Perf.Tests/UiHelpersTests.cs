using Oranda.Perf.Common;

namespace Oranda.Perf.Tests;

/// <summary>창 측정 도구(Oranda.Perf.Ui)의 순수 로직: 명령줄, 프레임 통계, 스크롤 계획, 합성 SQL.</summary>
public class UiHelpersTests
{
    // ---- 명령줄 ----

    [Fact]
    public void Options_parse_scenario_and_flags()
    {
        var o = UiOptions.Parse(["grid", "--out", "o", "--samples", "7", "--warmup", "0", "--lines", "9", "--rows", "11", "--seconds", "3", "--label", "r2"]);
        Assert.Equal(new UiOptions("grid", "o", 7, 0, 9, 11, 3, "r2"), o);
        Assert.Equal("perf-grid-r2", o.BaseName);
    }

    [Fact]
    public void Options_defaults_fall_back_per_scenario()
    {
        var o = UiOptions.Parse(["typing"]);
        Assert.Equal(500, o.SamplesOr(500));
        Assert.Equal(50, o.WarmupOr(50));
        Assert.Equal(10_000, o.LinesOr(10_000));
        Assert.Equal("perf-typing", o.BaseName);
        Assert.Equal(7, UiOptions.Parse(["typing", "--warmup", "7"]).WarmupOr(50));
        Assert.Equal(0, UiOptions.Parse(["typing", "--warmup", "0"]).WarmupOr(50));
    }

    [Theory]
    [InlineData]
    [InlineData("--out", "x")]
    [InlineData("nope")]
    [InlineData("grid", "--rows")]
    [InlineData("grid", "--rows", "0")]
    [InlineData("grid", "--samples", "abc")]
    [InlineData("grid", "--warmup", "-1")]
    [InlineData("grid", "--what")]
    public void Options_reject_bad_input(params string[] args)
    {
        Assert.Throws<ArgumentException>(() => UiOptions.Parse(args));
    }

    [Fact]
    public void Every_listed_scenario_parses()
    {
        foreach (var s in UiOptions.Scenarios)
        {
            Assert.Equal(s, UiOptions.Parse([s]).Scenario);
        }
    }

    // ---- 프레임 ----

    [Fact]
    public void Frame_summary_of_a_steady_60hz_cadence()
    {
        var times = Enumerable.Range(0, 61).Select(i => i * 1000.0 / 60).ToList();
        var f = FrameSummary.From(times);
        Assert.Equal(61, f.Frames);
        Assert.Equal(60, f.Fps, 6);
        Assert.Equal(1000.0 / 60, f.MeanMs, 6);
        Assert.Equal(1000.0 / 60, f.P95Ms, 6);
        Assert.Equal(0, f.SlowFrames);
    }

    [Fact]
    public void Frame_summary_counts_slow_frames_and_max()
    {
        var f = FrameSummary.From([0, 16, 32, 100, 116]);
        Assert.Equal(1, f.SlowFrames);
        Assert.Equal(68, f.MaxMs);
        Assert.Equal(116.0 / 4 * 1, f.MeanMs, 6);
    }

    [Fact]
    public void Frame_summary_rejects_too_few_or_reversed_times()
    {
        Assert.Throws<ArgumentException>(() => FrameSummary.From([]));
        Assert.Throws<ArgumentException>(() => FrameSummary.From([5]));
        Assert.Throws<ArgumentException>(() => FrameSummary.From([5, 3]));
        Assert.Throws<ArgumentNullException>(() => FrameSummary.From(null!));
    }

    [Fact]
    public void Frame_summary_with_identical_times_has_zero_fps_not_infinity()
    {
        Assert.Equal(0, FrameSummary.From([5, 5, 5]).Fps);
    }

    // ---- 스크롤 계획 ----

    [Fact]
    public void Scroll_vertical_moves_by_rows_and_wraps()
    {
        var rng = new Random(1);
        Assert.Equal((0.0, 66.0), ScrollPlan.Offset(ScrollKind.VerticalSmooth, 1, 22, 500, 10000, rng));
        Assert.Equal((0.0, 1320.0), ScrollPlan.Offset(ScrollKind.VerticalFast, 1, 22, 500, 10000, rng));
        Assert.Equal((0.0, 1320.0 * 8 % 10000), ScrollPlan.Offset(ScrollKind.VerticalFast, 8, 22, 500, 10000, rng));
        Assert.Equal((0.0, 0.0), ScrollPlan.Offset(ScrollKind.VerticalSmooth, 5, 22, 500, 0, rng)); // 스크롤할 것이 없으면 0
    }

    [Fact]
    public void Scroll_horizontal_is_a_triangle_wave_within_range()
    {
        var rng = new Random(1);
        var xs = Enumerable.Range(0, 100).Select(f => ScrollPlan.Offset(ScrollKind.Horizontal, f, 22, 200, 1000, rng).X).ToList();
        Assert.All(xs, x => Assert.InRange(x, 0, 200));
        Assert.Equal(200, xs.Max());
        Assert.Equal(0, xs[0]);
        Assert.Equal(40, xs[1]);
        Assert.Equal(160, xs[6]); // 6프레임 = 240 px → 200 을 넘어 160 으로 되돌아옴
        Assert.Equal(0, ScrollPlan.Offset(ScrollKind.Horizontal, 3, 22, 0, 1000, rng).X);
    }

    [Fact]
    public void Scroll_jump_is_in_range_and_repeatable_with_the_same_seed()
    {
        var a = Enumerable.Range(0, 20).Select(f => ScrollPlan.Offset(ScrollKind.Jump, f, 22, 300, 9000, new Random(f))).ToList();
        var b = Enumerable.Range(0, 20).Select(f => ScrollPlan.Offset(ScrollKind.Jump, f, 22, 300, 9000, new Random(f))).ToList();
        Assert.Equal(a, b);
        Assert.All(a, p => { Assert.InRange(p.X, 0, 300); Assert.InRange(p.Y, 0, 9000); });
        Assert.Equal((0.0, 0.0), ScrollPlan.Offset(ScrollKind.Idle, 9, 22, 300, 9000, new Random(1)));
    }

    [Fact]
    public void Scroll_rejects_bad_arguments()
    {
        var rng = new Random(1);
        Assert.Throws<ArgumentOutOfRangeException>(() => ScrollPlan.Offset(ScrollKind.Jump, -1, 22, 1, 1, rng));
        Assert.Throws<ArgumentOutOfRangeException>(() => ScrollPlan.Offset(ScrollKind.Jump, 0, 0, 1, 1, rng));
        Assert.Throws<ArgumentOutOfRangeException>(() => ScrollPlan.Offset(ScrollKind.Jump, 0, 22, -1, 1, rng));
        Assert.Throws<ArgumentNullException>(() => ScrollPlan.Offset(ScrollKind.Jump, 0, 22, 1, 1, null!));
    }

    // ---- 합성 SQL ----

    [Theory]
    [InlineData(1)]
    [InlineData(20)]
    [InlineData(10_000)]
    public void Editor_document_has_exactly_the_requested_lines(int lines)
    {
        var text = SampleSql.EditorDocument(lines);
        Assert.Equal(lines, text.Count(c => c == '\n'));
        Assert.EndsWith("\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_input_reaches_the_requested_lines_and_reports_the_real_count()
    {
        var (sql, count) = SampleSql.FormatInput(2000);
        Assert.InRange(count, 2000, 2100);
        Assert.Equal(count, sql.Count(c => c == (char)10));
        Assert.Contains("select", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Sample_sql_rejects_non_positive_sizes()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SampleSql.EditorDocument(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => SampleSql.FormatInput(0));
    }
}
