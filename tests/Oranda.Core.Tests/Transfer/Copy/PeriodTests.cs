using Oranda.Core.Transfer.Copy;
using Xunit;

namespace Oranda.Core.Tests.Transfer.Copy;

public class PeriodTests
{
    private static readonly DateTime Today = new(2026, 11, 6, 15, 30, 0);

    private static ResolvedPeriod R(PeriodKind kind) => PeriodCalculator.Resolve(new CopyPeriod(kind), Today);

    [Fact]
    public void Last_month_ends_tomorrow_midnight()
    {
        Assert.Equal(new ResolvedPeriod(new DateTime(2026, 10, 7), new DateTime(2026, 11, 7)), R(PeriodKind.LastMonth));
        Assert.Equal(new ResolvedPeriod(new DateTime(2026, 10, 31), new DateTime(2026, 11, 7)), R(PeriodKind.LastWeek));
        Assert.Equal(new ResolvedPeriod(new DateTime(2026, 8, 7), new DateTime(2026, 11, 7)), R(PeriodKind.Last3Months));
    }

    [Fact]
    public void Previous_month_is_calendar_month()
        => Assert.Equal(new ResolvedPeriod(new DateTime(2026, 10, 1), new DateTime(2026, 11, 1)), R(PeriodKind.PreviousMonth));

    [Fact]
    public void Custom_keeps_saved_dates_and_none_is_open()
    {
        var custom = PeriodCalculator.Resolve(
            new CopyPeriod(PeriodKind.Custom, new DateTime(2026, 1, 1), new DateTime(2026, 2, 1)), Today);
        Assert.Equal(new ResolvedPeriod(new DateTime(2026, 1, 1), new DateTime(2026, 2, 1)), custom);
        Assert.Equal(new ResolvedPeriod(null, null), R(PeriodKind.None));
    }

    [Fact]
    public void Custom_end_inclusive_ends_at_next_midnight()
    {
        var oneDay = PeriodCalculator.Resolve(
            new CopyPeriod(PeriodKind.Custom, new DateTime(2026, 11, 1), new DateTime(2026, 11, 1), EndInclusive: true), Today);
        Assert.Equal(new ResolvedPeriod(new DateTime(2026, 11, 1), new DateTime(2026, 11, 2)), oneDay);
        Assert.Equal(("20261101", "20261102"), DateStringFormat.Bounds(oneDay, "YYYYMMDD"));

        // 끝 날짜 포함은 직접 지정에만 쓴다.
        Assert.Equal(R(PeriodKind.LastMonth), PeriodCalculator.Resolve(new CopyPeriod(PeriodKind.LastMonth, EndInclusive: true), Today));
    }

    [Fact]
    public void Day_start_hour_shifts_only_time_bounds_and_business_today()
    {
        var p = PeriodCalculator.Resolve(
            new CopyPeriod(PeriodKind.Custom, new DateTime(2026, 11, 1), new DateTime(2026, 11, 1), EndInclusive: true, DayStartHour: 7), Today);
        Assert.Equal((new DateTime(2026, 11, 1), new DateTime(2026, 11, 2)), (p.From, p.To));
        Assert.Equal((new DateTime(2026, 11, 1, 7, 0, 0), new DateTime(2026, 11, 2, 7, 0, 0)), (p.TimeFrom, p.TimeTo));
        Assert.Equal(("20261101", "20261102"), DateStringFormat.Bounds(p, "YYYYMMDD")); // 문자열 날짜는 그대로
        Assert.Equal(("202611", "202612"), DateStringFormat.Bounds(p, "YYYYMM"));

        // 11-01 05:00 은 7시 기준 업무 날짜로 10-31 이다: 지난달 = 9월.
        var early = PeriodCalculator.Resolve(new CopyPeriod(PeriodKind.PreviousMonth, DayStartHour: 7), new DateTime(2026, 11, 1, 5, 0, 0));
        Assert.Equal((new DateTime(2026, 9, 1), new DateTime(2026, 10, 1)), (early.From, early.To));
        Assert.Equal(new DateTime(2026, 10, 1, 7, 0, 0), early.TimeTo);

        Assert.Equal(p.From, PeriodCalculator.Resolve(new CopyPeriod(PeriodKind.Custom, p.From, p.From, true), Today).TimeFrom); // 0 = 자정
    }

    [Theory]
    [InlineData(6, "YYYYMM")]
    [InlineData(8, "YYYYMMDD")]
    [InlineData(10, "YYYY-MM-DD")]
    [InlineData(14, "YYYYMMDDHH24MISS")]
    [InlineData(19, "YYYY-MM-DD HH24:MI:SS")]
    [InlineData(7, null)]
    public void Guess_format_from_length(int length, string? expected)
        => Assert.Equal(expected, DateStringFormat.GuessFromLength(length));

    [Fact]
    public void Formats_with_zero_padding()
    {
        var v = new DateTime(2026, 3, 5, 4, 7, 9);
        Assert.Equal("20260305040709", DateStringFormat.Format(v, "YYYYMMDDHH24MISS"));
        Assert.Equal("2026-03-05 04:07:09", DateStringFormat.Format(v, "YYYY-MM-DD HH24:MI:SS"));
    }

    [Fact]
    public void Month_format_bounds_round_to_month()
    {
        Assert.Equal(("202610", "202612"), DateStringFormat.Bounds(R(PeriodKind.LastMonth), "YYYYMM"));
        Assert.Equal(("202610", "202611"), DateStringFormat.Bounds(R(PeriodKind.PreviousMonth), "YYYYMM"));
        Assert.Equal(("20261007", "20261107"), DateStringFormat.Bounds(R(PeriodKind.LastMonth), "YYYYMMDD"));
    }

    [Fact]
    public void Rejects_unknown_tokens()
    {
        Assert.True(DateStringFormat.IsValid("YYYY/MM/DD"));
        Assert.False(DateStringFormat.IsValid("DD-MON-YYYY"));
        Assert.All(DateStringFormat.KnownFormats, f => Assert.True(DateStringFormat.IsValid(f)));
        Assert.False(DateStringFormat.HasDay("YYYYMM"));
    }

    [Theory]
    [InlineData("DD/MM/YYYY")]
    [InlineData("MMYYYY")]
    [InlineData("YYYY")]
    [InlineData("YYYYYYYY")]
    [InlineData("YYYYDD")]
    public void Rejects_formats_that_do_not_sort_like_dates(string format)
        => Assert.False(DateStringFormat.IsValid(format));

    [Fact]
    public void Month_end_and_january_boundaries()
    {
        var r = PeriodCalculator.Resolve(new CopyPeriod(PeriodKind.LastMonth), new DateTime(2026, 3, 30));
        Assert.Equal(new ResolvedPeriod(new DateTime(2026, 2, 28), new DateTime(2026, 3, 31)), r);
        var p = PeriodCalculator.Resolve(new CopyPeriod(PeriodKind.PreviousMonth), new DateTime(2026, 1, 15));
        Assert.Equal(new ResolvedPeriod(new DateTime(2025, 12, 1), new DateTime(2026, 1, 1)), p);
    }
}
