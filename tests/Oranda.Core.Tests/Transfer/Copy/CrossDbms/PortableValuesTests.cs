using Oranda.Core.Transfer.Copy.CrossDbms;
using Xunit;

namespace Oranda.Core.Tests.Transfer.Copy.CrossDbms;

public class PortableValuesTests
{
    [Theory]
    [InlineData("0", "0")]
    [InlineData("-0", "0")]
    [InlineData("+12.50", "12.5")]
    [InlineData("007", "7")]
    [InlineData(".5", "0.5")]
    [InlineData("5.", "5")]
    [InlineData("1E+3", "1000")]
    [InlineData("-1.5e3", "-1500")]
    [InlineData("1e-5", "0.00001")]
    [InlineData("12345.678E-2", "123.45678")]
    [InlineData("  3.14  ", "3.14")]
    [InlineData("0.000E5", "0")]
    public void FromText_normalizes_to_fixed_point(string input, string expected) =>
        Assert.Equal(expected, PortableNumber.FromText(input).Text);

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("1.2.3")]
    [InlineData("1E")]
    [InlineData("1E999")]
    [InlineData(".")]
    [InlineData("-")]
    public void FromText_rejects_non_numbers(string input) =>
        Assert.Throws<PortableConversionException>(() => PortableNumber.FromText(input));

    [Fact]
    public void FromText_expands_large_exponent() =>
        Assert.Equal("1" + new string('0', 125), PortableNumber.FromText("1E+125").Text);

    [Fact]
    public void Default_number_is_zero() => Assert.Equal("0", default(PortableNumber).Text);

    [Fact]
    public void FromDouble_uses_round_trip_text()
    {
        Assert.Equal("0.1", PortableNumber.FromDouble(0.1).Text);
        Assert.Equal("0." + new string('0', 299) + "1", PortableNumber.FromDouble(1e-300).Text);
        Assert.Equal(PortableNumber.FromText("1.7976931348623157E+308").Text, PortableNumber.FromDouble(double.MaxValue).Text);
        Assert.Equal("-2.5", PortableNumber.FromDouble(-2.5).Text);
        Assert.Equal("0.1", PortableNumber.FromSingle(0.1f).Text);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void FromDouble_rejects_nan_and_infinity(double value) =>
        Assert.Throws<PortableConversionException>(() => PortableNumber.FromDouble(value));

    [Fact]
    public void FromSingle_rejects_nan() =>
        Assert.Throws<PortableConversionException>(() => PortableNumber.FromSingle(float.NaN));

    [Fact]
    public void FromText_rejects_extreme_exponent_with_conversion_exception() =>
        Assert.Throws<PortableConversionException>(() => PortableNumber.FromText("1E-9223372036854775808"));

    [Fact]
    public void Default_number_equals_zero() => Assert.Equal(PortableNumber.FromText("0"), default);

    [Fact]
    public void TimestampTz_normalizes_offset_text()
    {
        var local = new PortableTimestamp(DateTime.Today, 0);
        Assert.Equal("+00:30", new PortableTimestampTz(local, " +0:30 ", null).Offset);
        Assert.Equal(new PortableTimestampTz(local, "+0:30", null), new PortableTimestampTz(local, "+00:30", null));
    }

    [Fact]
    public void FromDecimal_drops_trailing_zeros() =>
        Assert.Equal("-123.45", PortableNumber.FromDecimal(-123.4500m).Text);

    [Fact]
    public void Timestamp_truncates_below_seconds_and_unspecifies_kind()
    {
        var t = new PortableTimestamp(new DateTime(2026, 11, 10, 1, 2, 3, DateTimeKind.Utc).AddTicks(1234567), 123_456_789);
        Assert.Equal(new DateTime(2026, 11, 10, 1, 2, 3), t.Seconds);
        Assert.Equal(DateTimeKind.Unspecified, t.Seconds.Kind);
        Assert.Equal(123_456_789, t.Nanos);
    }

    [Fact]
    public void Timestamp_from_datetime_keeps_tick_precision()
    {
        var t = PortableTimestamp.FromDateTime(new DateTime(2026, 1, 2, 3, 4, 5).AddTicks(1234567));
        Assert.Equal(new DateTime(2026, 1, 2, 3, 4, 5), t.Seconds);
        Assert.Equal(123_456_700, t.Nanos);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1_000_000_000)]
    public void Timestamp_rejects_bad_nanos(int nanos) =>
        Assert.Throws<PortableConversionException>(() => new PortableTimestamp(DateTime.Today, nanos));

    [Fact]
    public void TimestampTz_needs_offset_or_region()
    {
        var local = new PortableTimestamp(DateTime.Today, 0);
        Assert.Throws<PortableConversionException>(() => new PortableTimestampTz(local, null, " "));
        Assert.Throws<PortableConversionException>(() => new PortableTimestampTz(local, "KST", null));

        var byRegion = new PortableTimestampTz(local, null, "Asia/Seoul");
        Assert.Null(byRegion.Offset);
        Assert.Null(byRegion.OffsetMinutes);
        Assert.Equal("Asia/Seoul", byRegion.Region);

        var byOffset = new PortableTimestampTz(local, "+09:00", null);
        Assert.Equal(540, byOffset.OffsetMinutes);
        Assert.Null(byOffset.Region);
    }

    [Theory]
    [InlineData("+09:00", true, 540)]
    [InlineData("-03:30", true, -210)]
    [InlineData("+0:30", true, 30)]
    [InlineData("+00:00", true, 0)]
    [InlineData("-00:15", true, -15)]
    [InlineData("09:00", false, 0)]
    [InlineData("+09:60", false, 0)]
    [InlineData("+16:00", false, 0)]
    [InlineData("+9", false, 0)]
    [InlineData("+09:5", false, 0)]
    [InlineData("+009:00", false, 0)]
    [InlineData("+aa:bb", false, 0)]
    public void ParseOffsetMinutes(string text, bool ok, int minutes)
    {
        Assert.Equal(ok, PortableTimestampTz.TryParseOffsetMinutes(text, out var m));
        Assert.Equal(minutes, m);
    }

    [Theory]
    [InlineData(540, "+09:00")]
    [InlineData(-210, "-03:30")]
    [InlineData(0, "+00:00")]
    [InlineData(-15, "-00:15")]
    public void FormatOffset(int minutes, string text)
    {
        Assert.Equal(text, PortableTimestampTz.FormatOffset(minutes));
        Assert.True(PortableTimestampTz.TryParseOffsetMinutes(text, out var back));
        Assert.Equal(minutes, back);
    }
}
