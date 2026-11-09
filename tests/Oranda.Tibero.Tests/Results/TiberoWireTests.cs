using Oranda.Tibero.Results;

namespace Oranda.Tibero.Tests.Results;

/// <summary>실측 바이트(docs/design/tibero-driver-values.md 2장, 서버 6.2.15)를 고정 값으로 쓴 원시 바이트 해석 시험.</summary>
public class TiberoWireTests
{
    private static byte[] Hex(string hex) => Convert.FromHexString(hex.Replace(" ", string.Empty, StringComparison.Ordinal));

    [Theory]
    [InlineData("02C181", "1")]
    [InlineData("02C281", "100")]
    [InlineData("0180", "0")]
    [InlineData("043E7F4EF0", "-1.5")]
    [InlineData("034276F0", "-0.0000001")]
    [InlineData("03C181B2", "1.5")]
    public void Number_within_decimal_is_decimal(string hex, string expected)
    {
        var value = TiberoWire.DecodeNumber(Hex(hex));
        var d = Assert.IsType<decimal>(value);
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), d);
    }

    [Fact]
    public void Number_with_28_digits_stays_decimal()
    {
        var value = TiberoWire.DecodeNumber(Hex("0FCE8CA2B8CEDA8CA2B8CEDA8CA2B8CE"));
        Assert.Equal(1234567890123456789012345678m, Assert.IsType<decimal>(value));
    }

    [Fact]
    public void Number_with_29_digits_is_exact_string()
    {
        var value = TiberoWire.DecodeNumber(Hex("10CF8197ADC3D98197ADC3D98197ADC3D9"));
        Assert.Equal("12345678901234567890123456789", value);
    }

    [Fact]
    public void Number_with_38_digits_keeps_all_digits()
    {
        var value = TiberoWire.DecodeNumber(Hex("14D38197ADC3D98197ADC3D98197ADC3D98197ADC3"));
        Assert.Equal("1234567890123456789012345678901234567", value);
    }

    [Fact]
    public void Number_with_fraction_beyond_decimal_keeps_all_digits()
    {
        // 123456789012345678901234567890.123456789 → 서버가 38자리로 반올림
        var value = TiberoWire.DecodeNumber(Hex("14CF8CA2B8CEDA8CA2B8CEDA8CA2B8CEDA8CA2B8CF"));
        Assert.Equal("123456789012345678901234567890.12345679", value); // 서버는 38자리로 반올림해 저장한다
    }

    [Fact]
    public void Number_with_huge_exponent_is_scientific()
    {
        Assert.Equal("1E+125", TiberoWire.DecodeNumber(Hex("02FF8A")));
        Assert.Equal("1E-125", TiberoWire.DecodeNumber(Hex("02828A")));
    }

    [Fact]
    public void Number_rejects_wrong_length()
    {
        Assert.Throws<FormatException>(() => TiberoWire.DecodeNumber(Hex("05C181")));
    }

    [Fact]
    public void Timestamp_keeps_nanoseconds_as_ticks()
    {
        var bytes = Hex("787C010203040500075BCD15");
        Assert.Equal(new DateTime(2024, 1, 2, 3, 4, 5).AddTicks(1234567), TiberoWire.DecodeTimestamp(bytes));
        Assert.Equal(123456789, TiberoWire.DecodeNanoseconds(bytes));
    }

    [Fact]
    public void Timestamp_with_half_second()
    {
        Assert.Equal(new DateTime(2024, 1, 2, 3, 4, 5).AddMilliseconds(500), TiberoWire.DecodeTimestamp(Hex("787C0102030405001DCD6500")));
    }

    [Fact]
    public void Date_decodes_eight_bytes()
    {
        Assert.Equal(new DateTime(2024, 1, 2), TiberoWire.DecodeTimestamp(Hex("787C010200000000")));
        Assert.Equal(new DateTime(1, 1, 1), TiberoWire.DecodeTimestamp(Hex("6465010100000000")));
        Assert.Equal(new DateTime(1000, 6, 15, 13, 14, 15), TiberoWire.DecodeTimestamp(Hex("6E64060F0D0E0F00")));
        Assert.Throws<FormatException>(() => TiberoWire.DecodeTimestamp(Hex("3C64010100000000")));
    }

    [Theory]
    [InlineData("787C010112040500075BCA006D00000245", "2024-01-02 03:04:05.1234560", 9, 0)]
    [InlineData("787C010208220500000000005F1E000245", "2024-01-02 03:04:05.0000000", -5, -30)]
    [InlineData("787C010112040500000000006D0000012D", "2024-01-02 03:04:05.0000000", 9, 0)]
    [InlineData("787C010203040500000000006400000240", "2024-01-02 03:04:05.0000000", 0, 0)]
    [InlineData("787C01010D040500000000007200000245", "2024-01-02 03:04:05.0000000", 14, 0)]
    [InlineData("787C01020F040500000000005800000245", "2024-01-02 03:04:05.0000000", -12, 0)]
    public void TimestampTz_applies_offset_to_utc_instant(string hex, string local, int hours, int minutes)
    {
        var tz = TiberoWire.DecodeTimestampTz(Hex(hex));
        Assert.Equal(new TimeSpan(hours, minutes, 0), tz.Offset);
        Assert.Equal(DateTime.Parse(local, System.Globalization.CultureInfo.InvariantCulture), tz.DateTime);
    }

    [Fact]
    public void TimestampTz_region_uses_effective_offset()
    {
        // America/New_York: 1월 -05:00, 7월 -04:00 (서버가 그 시점 오프셋을 저장)
        Assert.Equal(TimeSpan.FromHours(-5), TiberoWire.DecodeTimestampTz(Hex("787C010208040500000000005F000000A6")).Offset);
        Assert.Equal(TimeSpan.FromHours(-4), TiberoWire.DecodeTimestampTz(Hex("787C0702070405000000000060000000A6")).Offset);
    }

    [Theory]
    [InlineData("400000010D", 14)]
    [InlineData("400000000B", 0)]
    [InlineData("400000000C", 1)]
    [InlineData("4000000016", 11)]
    [InlineData("400000020B", 24)]
    [InlineData("3FFFFFFF09", -14)]
    [InlineData("400000000A", -1)]
    [InlineData("400000640B", 1200)]
    public void IntervalYearMonth_is_total_months(string hex, long months)
    {
        Assert.Equal(months, TiberoWire.DecodeIntervalYearMonth(Hex(hex)));
    }

    [Fact]
    public void IntervalDaySecond_decodes_days_seconds_and_fraction()
    {
        Assert.Equal(new TimeSpan(1, 2, 3, 4, 500), TiberoWire.DecodeIntervalDaySecond(Hex("40000001 40001CD8 5DCD6500")));
    }

    [Fact]
    public void IntervalDaySecond_negative_with_nanoseconds()
    {
        var ts = TiberoWire.DecodeIntervalDaySecond(Hex("3FFFFFFB3FFEAE8104653601"));
        Assert.Equal(-new TimeSpan(5, 23, 59, 59).Add(TimeSpan.FromTicks(9999999)), ts);
    }

    [Fact]
    public void IntervalDaySecond_microsecond()
    {
        Assert.Equal(TimeSpan.FromTicks(10), TiberoWire.DecodeIntervalDaySecond(Hex("4000000040000000400003E8")));
    }

    [Fact]
    public void Raw_sbyte_array_is_copied_to_bytes()
    {
        Assert.Equal(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, TiberoWire.ToBytes([-34, -83, -66, -17]));
    }

    [Theory]
    [InlineData(9, 0, "+09:00")]
    [InlineData(-5, -30, "-05:30")]
    [InlineData(0, 0, "+00:00")]
    public void FormatOffset_is_signed_hh_mm(int hours, int minutes, string expected)
    {
        Assert.Equal(expected, TiberoWire.FormatOffset(new TimeSpan(hours, minutes, 0)));
    }

    [Fact]
    public void Number_negative_28_digits_stays_decimal()
    {
        // 위 28자리 양수의 음수형: 지수 0x3F-14, 숫자 0x80-d, 종결 F0
        var value = TiberoWire.DecodeNumber(Hex("1031" + "74 5E 48 32 26 74 5E 48 32 26 74 5E 48 32 F0"));
        Assert.Equal(-1234567890123456789012345678m, Assert.IsType<decimal>(value));
    }

    [Fact]
    public void Number_large_integer_beyond_decimal_is_plain_string()
    {
        // 1E35 = 0.10 × 100^18 → 지수 0xC0+18=D2, 숫자 0x8A
        Assert.Equal("1" + new string('0', 35), TiberoWire.DecodeNumber(Hex("02D28A")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("01")]
    [InlineData("03C181")]
    [InlineData("02C1FF")]
    public void Number_rejects_malformed_bytes(string hex)
    {
        Assert.Throws<FormatException>(() => TiberoWire.DecodeNumber(Hex(hex)));
    }

    [Fact]
    public void TimestampTz_at_range_edge_throws_format_exception()
    {
        // 9999-12-31 23:59:59 UTC +09:00
        Assert.Throws<FormatException>(() => TiberoWire.DecodeTimestampTz(Hex("C7 C7 0C 1F 17 3B 3B 00 00000000 6D 00 00 02 45")));
    }

    [Fact]
    public void IntervalDaySecond_beyond_timespan_range_throws_format_exception()
    {
        // 999,999,999 일
        Assert.Throws<FormatException>(() => TiberoWire.DecodeIntervalDaySecond(Hex("7B9AC9FF 40000000 40000000")));
    }

    [Fact]
    public void Offset_with_zero_hour_and_minutes_is_positive()
    {
        Assert.Equal(TimeSpan.FromMinutes(30), TiberoWire.ReadOffset(100, 30));
    }
}
