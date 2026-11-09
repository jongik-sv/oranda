using Oranda.Tibero.Execution;

namespace Oranda.Tibero.Tests.Execution;

// 소유: TB1 커밋 2. 대량 읽기 값 기록형(TB1 설계 1.2). 바이트 고정값은 docs/design/tibero-driver-values.md 2장 실측 그대로(드라이버 없음).
public class TiberoBulkValuesTests
{
    [Theory]
    [InlineData("02 C1 81", "1")]
    [InlineData("02 C2 81", "100")]
    [InlineData("04 3E 7F 4E F0", "-1.5")]
    [InlineData("03 42 76 F0", "-0.0000001")]
    [InlineData("01 80", "0")]
    [InlineData("10 CF 81 97 AD C3 D9 81 97 AD C3 D9 81 97 AD C3 D9", "12345678901234567890123456789")]
    // values.md 2장 표의 왼쪽 칸은 넣은 리터럴(39자리)이고 바이트는 서버가 저장한 값이다: NUMBER 는 유효 38자리로 반올림해 저장한다.
    // 정수 15그룹(30자리) 뒤 소수 그룹이 12 34 56 79(CF) → .12345679. 디코더는 바이트를 자리 손실 없이 그대로 낸다.
    [InlineData("14 CF 8C A2 B8 CE DA 8C A2 B8 CE DA 8C A2 B8 CE DA 8C A2 B8 CF", "123456789012345678901234567890.12345679")]
    public void Number_bytes_become_exact_fixed_point_text(string hex, string text)
    {
        Assert.Equal(text, TiberoBulkNumber.FromBytes(Bytes(hex)).Text);
    }

    [Fact]
    public void Number_bytes_at_the_exponent_limits_keep_every_digit()
    {
        // 1E125 = 1 뒤 0 125개(126자), 1E-125 = 0. 뒤 0 124개 + 1. 지수 표기 없음.
        Assert.Equal("1" + new string('0', 125), TiberoBulkNumber.FromBytes(Bytes("02 FF 8A")).Text);
        Assert.Equal("0." + new string('0', 124) + "1", TiberoBulkNumber.FromBytes(Bytes("02 82 8A")).Text);
        Assert.Throws<FormatException>(() => TiberoBulkNumber.FromBytes(Bytes("03 C1 81"))); // 길이 바이트가 안 맞음
    }

    [Theory]
    [InlineData("0", "0")]
    [InlineData("-0", "0")]
    [InlineData("-0.000", "0")]
    [InlineData("007", "7")]
    [InlineData("1.500", "1.5")]
    [InlineData("-0012.0100", "-12.01")]
    [InlineData("0.0000001", "0.0000001")]
    [InlineData("12345678901234567890123456789012345678", "12345678901234567890123456789012345678")]
    [InlineData("+5", "5")] // Oracle FailedRowKeys 가 받는 모양(마스킹 고정값이 두 DBMS 에서 같게 통과)
    [InlineData(".5", "0.5")]
    [InlineData("5.", "5")]
    [InlineData("-.50", "-0.5")]
    public void Number_text_is_normalized(string input, string expected)
    {
        Assert.Equal(expected, TiberoBulkNumber.Parse(input).Text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1E5")]
    [InlineData("1,5")]
    [InlineData(" 1")]
    [InlineData("0\n")]
    [InlineData("1.50\n")]
    [InlineData(".")]
    [InlineData("+")]
    [InlineData("--1")]
    public void Number_text_outside_fixed_point_shape_is_format_error(string input)
    {
        Assert.Throws<FormatException>(() => TiberoBulkNumber.Parse(input));
    }

    [Fact]
    public void Number_from_clr_values_and_equality()
    {
        Assert.Equal("1.5", TiberoBulkNumber.FromDecimal(1.50m).Text);
        Assert.Equal("-79228162514264337593543950335", TiberoBulkNumber.FromDecimal(decimal.MinValue).Text);
        Assert.Equal("-9223372036854775808", TiberoBulkNumber.FromInt64(long.MinValue).Text);
        Assert.Equal(TiberoBulkNumber.Parse("-1.50"), TiberoBulkNumber.FromBytes(Bytes("04 3E 7F 4E F0"))); // 글자가 같으면 같은 값
        Assert.NotEqual(TiberoBulkNumber.Parse("1.5"), TiberoBulkNumber.Parse("-1.5"));
        Assert.Same(TiberoBulkNumber.Zero, TiberoBulkNumber.Parse("-0"));
        Assert.Equal("-0", TiberoBulkNumber.Parse("-0.5").IntegerText);
        Assert.Equal("12", TiberoBulkNumber.Parse("12").IntegerText);
        Assert.Equal("12.5", TiberoBulkNumber.Parse("12.5").ToString());
    }

    [Fact]
    public void Timestamp_bytes_keep_nanoseconds()
    {
        var ts = TiberoBulkTimestamp.FromBytes(Bytes("78 7C 01 02 03 04 05 00 07 5B CD 15"));
        Assert.Equal(new DateTime(2024, 1, 2, 3, 4, 5), ts.Seconds);
        Assert.Equal(DateTimeKind.Unspecified, ts.Seconds.Kind);
        Assert.Equal(123_456_789, ts.Nanosecond);

        var date = TiberoBulkTimestamp.FromBytes(Bytes("64 65 01 01 00 00 00 00")); // DATE 8바이트 = 나노초 0
        Assert.Equal(new TiberoBulkTimestamp(new DateTime(1, 1, 1), 0), date);
    }

    [Fact]
    public void Timestamp_rejects_fraction_in_seconds_and_bad_nanoseconds()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TiberoBulkTimestamp(new DateTime(2024, 1, 1).AddTicks(1), 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TiberoBulkTimestamp(new DateTime(2024, 1, 1), -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TiberoBulkTimestamp(new DateTime(2024, 1, 1), 1_000_000_000));

        var fromTicks = TiberoBulkTimestamp.FromDateTime(new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Local).AddTicks(1_234_567));
        Assert.Equal(new TiberoBulkTimestamp(new DateTime(2024, 1, 2, 3, 4, 5), 123_456_700), fromTicks);
        Assert.Equal(DateTimeKind.Unspecified, fromTicks.Seconds.Kind);
    }

    [Fact]
    public void Time_zone_bytes_keep_utc_nanoseconds_and_offset()
    {
        var plus = TiberoBulkTimestampTz.FromBytes(Bytes("78 7C 01 02 03 04 05 00 07 5B CD 15 6D 00 00 02 45"));
        Assert.Equal(new DateTime(2024, 1, 2, 3, 4, 5), plus.Utc);
        Assert.Equal(123_456_789, plus.Nanosecond);
        Assert.Equal(TimeSpan.FromHours(9), plus.Offset);

        var minus = TiberoBulkTimestampTz.FromBytes(Bytes("78 7C 01 02 03 04 05 00 00 00 00 00 5F 1E 00 02 45"));
        Assert.Equal(new TimeSpan(-5, -30, 0), minus.Offset);
        Assert.Throws<FormatException>(() => TiberoBulkTimestampTz.FromBytes(Bytes("78 7C 01 02 03 04 05 00 00 00 00 00")));

        var ltz = TiberoBulkTimestampLtz.FromBytes(Bytes("78 7C 01 02 03 04 05 00 07 5B CD 15"));
        Assert.Equal(new TiberoBulkTimestampLtz(new DateTime(2024, 1, 2, 3, 4, 5), 123_456_789), ltz);
    }

    [Fact]
    public void Interval_bytes_keep_parts()
    {
        Assert.Equal(14, TiberoBulkIntervalYm.FromBytes(Bytes("40 00 00 01 0D")).Months);
        Assert.Equal(-14, TiberoBulkIntervalYm.FromBytes(Bytes("3F FF FF FF 09")).Months);
        Assert.Equal(1200, TiberoBulkIntervalYm.FromBytes(Bytes("40 00 00 64 0B")).Months);

        // -5 23:59:59.999999999: TimeSpan(틱)이 버리는 나노초 9자리가 남는다.
        Assert.Equal(new TiberoBulkIntervalDs(-5, -86399, -999_999_999), TiberoBulkIntervalDs.FromBytes(Bytes("3F FF FF FB 3F FE AE 81 04 65 36 01")));
        Assert.Throws<FormatException>(() => TiberoBulkIntervalDs.FromBytes(Bytes("40 00 00 00")));
    }

    internal static byte[] Bytes(string hex) => Convert.FromHexString(hex.Replace(" ", string.Empty, StringComparison.Ordinal));
}
