using Oracle.ManagedDataAccess.Types;
using Oranda.Core.Transfer.Copy.CrossDbms;
using Oranda.Oracle.Transfer.Copy;

namespace Oranda.Oracle.Tests.Transfer.Copy;

// 소유: 교차 복사 XC3. Oracle 코덱의 이식 값 변환(ToPortable·FromPortable·IsReadable). 서버 없이 ODP.NET 값만 만들어 본다.
public class OraclePortableTests
{
    private static readonly OracleBulkValueCodec Codec = OracleBulkValueCodec.Instance;

    [Fact]
    public void Number_round_trips_through_fixed_point_text()
    {
        var value = OracleDecimal.Parse("12345678901234567890.5");
        var portable = Codec.ToPortable(value, "NUMBER(38,2)");
        Assert.Equal(PortableNumber.FromText("12345678901234567890.5"), Assert.IsType<PortableNumber>(portable));
        Assert.Equal(value, Assert.IsType<OracleDecimal>(Codec.FromPortable(portable, "NUMBER")));

        var negative = OracleDecimal.Parse("-0.5");
        var negativePortable = Assert.IsType<PortableNumber>(Codec.ToPortable(negative, "NUMBER"));
        Assert.Equal("-0.5", negativePortable.Text);
        Assert.Equal(negative, Assert.IsType<OracleDecimal>(Codec.FromPortable(negativePortable, "NUMBER")));
    }

    [Fact]
    public void Number_with_exponent_is_expanded_and_round_trips()
    {
        var huge = OracleDecimal.Shift(OracleDecimal.Parse("1"), 125);
        var hugePortable = Assert.IsType<PortableNumber>(Codec.ToPortable(huge, "NUMBER"));
        Assert.Equal("1" + new string('0', 125), hugePortable.Text);
        Assert.Equal(huge, Assert.IsType<OracleDecimal>(Codec.FromPortable(hugePortable, "NUMBER")));

        var tiny = OracleDecimal.Parse("0.00000000000000000000000000000000012345");
        var tinyPortable = Assert.IsType<PortableNumber>(Codec.ToPortable(tiny, "FLOAT(126)"));
        Assert.DoesNotContain("E", tinyPortable.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(tiny, Assert.IsType<OracleDecimal>(Codec.FromPortable(tinyPortable, "NUMBER")));
    }

    [Fact]
    public void Number_outside_the_oracle_range_fails_that_row()
    {
        Assert.Throws<PortableConversionException>(() => Codec.FromPortable(PortableNumber.FromText("1E+126"), "NUMBER"));
        Assert.Throws<PortableConversionException>(() => Codec.FromPortable(PortableNumber.FromText("1E-131"), "NUMBER"));
        Assert.Throws<PortableConversionException>(
            () => Codec.FromPortable(PortableNumber.FromText("1234567890123456789012345678901234567890"), "NUMBER"));
        Assert.Equal(OracleDecimal.Zero, Assert.IsType<OracleDecimal>(Codec.FromPortable(PortableNumber.FromText("0"), "NUMBER")));
    }

    [Fact]
    public void Null_values_stay_null_in_both_directions()
    {
        Assert.Null(Codec.ToPortable(null, "NUMBER"));
        Assert.Null(Codec.ToPortable(DBNull.Value, "VARCHAR2(10)"));
        Assert.Null(Codec.ToPortable(OracleDecimal.Null, "NUMBER"));
        Assert.Null(Codec.ToPortable(OracleTimeStamp.Null, "TIMESTAMP(6)"));
        Assert.Null(Codec.FromPortable(null, "NUMBER"));
        Assert.Null(Codec.FromPortable(null, "TIMESTAMP WITH TIME ZONE"));
    }

    [Fact]
    public void Binary_float_and_double_keep_nan_and_infinity()
    {
        Assert.Equal(new PortableBinaryFloat(1.5f), Assert.IsType<PortableBinaryFloat>(Codec.ToPortable(1.5f, "BINARY_FLOAT")));
        var nan = Assert.IsType<PortableBinaryDouble>(Codec.ToPortable(double.NaN, "BINARY_DOUBLE"));
        Assert.True(double.IsNaN(nan.Value));

        Assert.Equal(2.5d, Assert.IsType<double>(Codec.FromPortable(new PortableBinaryDouble(2.5d), "BINARY_DOUBLE")));
        Assert.True(float.IsPositiveInfinity(Assert.IsType<float>(Codec.FromPortable(new PortableBinaryFloat(float.PositiveInfinity), "BINARY_FLOAT"))));
        Assert.True(double.IsNaN(Assert.IsType<double>(Codec.FromPortable(new PortableBinaryDouble(double.NaN), "BINARY_DOUBLE"))));
    }

    [Fact]
    public void Binary_values_written_to_number_use_round_trip_text_and_reject_nan_and_infinity()
    {
        var fromDouble = Codec.FromPortable(new PortableBinaryDouble(0.1d), "NUMBER");
        Assert.Equal(Codec.FromPortable(PortableNumber.FromText("0.1"), "NUMBER"), fromDouble);
        var fromFloat = Codec.FromPortable(new PortableBinaryFloat(0.5f), "NUMBER");
        Assert.Equal(Codec.FromPortable(PortableNumber.FromText("0.5"), "NUMBER"), fromFloat);

        Assert.Throws<PortableConversionException>(() => Codec.FromPortable(new PortableBinaryDouble(double.NaN), "NUMBER"));
        Assert.Throws<PortableConversionException>(() => Codec.FromPortable(new PortableBinaryDouble(double.NegativeInfinity), "NUMBER"));
        Assert.Throws<PortableConversionException>(() => Codec.FromPortable(new PortableBinaryFloat(float.NaN), "NUMBER"));
    }

    [Fact]
    public void Text_clob_raw_and_blob_pass_through()
    {
        Assert.Equal("A1  가나", Codec.ToPortable("A1  가나", "CHAR(8)"));
        Assert.Equal("A1  가나", Codec.FromPortable("A1  가나", "NCHAR(8)"));
        var clob = new string('x', 100_000);
        Assert.Equal(clob, Codec.ToPortable(clob, "CLOB"));
        Assert.Equal(clob, Codec.FromPortable(clob, "NCLOB"));

        byte[] raw = [0x00, 0xAB, 0xFF];
        Assert.Equal(raw, Assert.IsType<byte[]>(Codec.ToPortable(raw, "RAW(3)")));
        var blob = new byte[70_000];
        for (var i = 0; i < blob.Length; i++)
        {
            blob[i] = (byte)(i % 251);
        }

        Assert.Equal(blob, Assert.IsType<byte[]>(Codec.ToPortable(blob, "BLOB")));
        Assert.Equal(blob, Assert.IsType<byte[]>(Codec.FromPortable(blob, "BLOB")));
        Assert.Equal(raw, Assert.IsType<byte[]>(Codec.FromPortable(raw, "RAW")));
    }

    [Fact]
    public void Date_round_trips_in_seconds_and_bc_dates_fail_that_row()
    {
        var date = new OracleDate(new DateTime(2026, 3, 5, 14, 30, 15));
        var portable = Assert.IsType<DateTime>(Codec.ToPortable(date, "DATE"));
        Assert.Equal(new DateTime(2026, 3, 5, 14, 30, 15), portable);
        Assert.Equal(DateTimeKind.Unspecified, portable.Kind);
        Assert.Equal(portable, Assert.IsType<DateTime>(Codec.FromPortable(portable, "DATE")));

        Assert.Throws<PortableConversionException>(() => Codec.ToPortable(new OracleDate(-100, 1, 1), "DATE"));
    }

    [Fact]
    public void Timestamp_keeps_nanoseconds()
    {
        var stamp = new OracleTimeStamp(2026, 1, 2, 3, 4, 5, 123_456_789);
        var portable = Assert.IsType<PortableTimestamp>(Codec.ToPortable(stamp, "TIMESTAMP(9)"));
        Assert.Equal(new DateTime(2026, 1, 2, 3, 4, 5), portable.Seconds);
        Assert.Equal(123_456_789, portable.Nanos);

        var back = Assert.IsType<OracleTimeStamp>(Codec.FromPortable(portable, "TIMESTAMP"));
        Assert.Equal(stamp, back);
        Assert.Equal(123_456_789, back.Nanosecond);

        Assert.Throws<PortableConversionException>(() => Codec.ToPortable(new OracleTimeStamp(-100, 1, 1, 0, 0, 0, 0), "TIMESTAMP(6)"));
    }

    [Fact]
    public void Timestamp_with_offset_keeps_wall_clock_nanoseconds_and_offset()
    {
        var stamp = new OracleTimeStampTZ(2026, 11, 10, 9, 0, 0, 123_456_789, "+09:00");
        var portable = Assert.IsType<PortableTimestampTz>(Codec.ToPortable(stamp, "TIMESTAMP(9) WITH TIME ZONE"));
        Assert.Equal(new DateTime(2026, 11, 10, 9, 0, 0), portable.Local.Seconds);
        Assert.Equal(123_456_789, portable.Local.Nanos);
        Assert.Equal("+09:00", portable.Offset);
        Assert.Null(portable.Region);

        var back = Assert.IsType<OracleTimeStampTZ>(Codec.FromPortable(portable, "TIMESTAMP WITH TIME ZONE"));
        Assert.Equal(9, back.Hour);
        Assert.Equal(123_456_789, back.Nanosecond);
        Assert.Equal("+09:00", back.TimeZone);

        var west = new OracleTimeStampTZ(2026, 11, 10, 9, 0, 0, 0, "-03:30");
        var westPortable = Assert.IsType<PortableTimestampTz>(Codec.ToPortable(west, "TIMESTAMP WITH TIME ZONE"));
        Assert.Equal("-03:30", westPortable.Offset);
        Assert.Equal("-03:30", Assert.IsType<OracleTimeStampTZ>(Codec.FromPortable(westPortable, "TIMESTAMP WITH TIME ZONE")).TimeZone);
    }

    [Fact]
    public void Timestamp_with_region_keeps_region_and_unknown_region_fails_that_row()
    {
        var stamp = new OracleTimeStampTZ(2026, 11, 10, 9, 0, 0, 0, "Asia/Seoul");
        var portable = Assert.IsType<PortableTimestampTz>(Codec.ToPortable(stamp, "TIMESTAMP WITH TIME ZONE"));
        Assert.Equal("Asia/Seoul", portable.Region);
        Assert.Equal("+09:00", portable.Offset);
        Assert.Equal(9, portable.Local.Seconds.Hour);

        var back = Assert.IsType<OracleTimeStampTZ>(Codec.FromPortable(portable, "TIMESTAMP WITH TIME ZONE"));
        Assert.Equal("Asia/Seoul", back.TimeZone);

        var unknown = new PortableTimestampTz(new PortableTimestamp(new DateTime(2026, 11, 10, 9, 0, 0), 0), "+09:00", "Mars/Base");
        Assert.Throws<PortableConversionException>(() => Codec.FromPortable(unknown, "TIMESTAMP WITH TIME ZONE"));
    }

    [Fact]
    public void Overlapping_daylight_saving_wall_clock_follows_the_given_offset()
    {
        // 2026-11-01 01:30 America/New_York 는 두 번 있다(EDT -04:00, EST -05:00). 원본이 EDT 라고 했으면 지역만으로 EST 가 되면 안 된다.
        var local = new PortableTimestamp(new DateTime(2026, 11, 1, 1, 30, 0), 0);
        var edt = new PortableTimestampTz(local, "-04:00", "America/New_York");
        var back = Assert.IsType<OracleTimeStampTZ>(Codec.FromPortable(edt, "TIMESTAMP WITH TIME ZONE"));
        Assert.Equal(TimeSpan.FromHours(-4), back.GetTimeZoneOffset());
    }

    [Fact]
    public void Interval_year_to_month_round_trips_including_negative()
    {
        Assert.Equal(new PortableIntervalYm(38), Assert.IsType<PortableIntervalYm>(Codec.ToPortable(new OracleIntervalYM(3, 2), "INTERVAL YEAR(2) TO MONTH")));
        var negative = new OracleIntervalYM(-1, -5);
        var portable = Assert.IsType<PortableIntervalYm>(Codec.ToPortable(negative, "INTERVAL YEAR TO MONTH"));
        Assert.Equal(-17, portable.Months);

        var back = Assert.IsType<OracleIntervalYM>(Codec.FromPortable(portable, "INTERVAL YEAR TO MONTH"));
        Assert.Equal(-1, back.Years);
        Assert.Equal(-5, back.Months);

        var monthsOnly = Assert.IsType<OracleIntervalYM>(Codec.FromPortable(new PortableIntervalYm(-5), "INTERVAL YEAR TO MONTH"));
        Assert.Equal(0, monthsOnly.Years);
        Assert.Equal(-5, monthsOnly.Months);
    }

    [Fact]
    public void Interval_day_to_second_round_trips_with_nanoseconds_and_negative_sign()
    {
        var negative = new OracleIntervalDS(-1, -2, -3, -4, -5);
        var portable = Assert.IsType<PortableIntervalDs>(Codec.ToPortable(negative, "INTERVAL DAY(2) TO SECOND(9)"));
        Assert.Equal((Int128)(-93_784_000_000_005L), portable.Nanoseconds);

        var back = Assert.IsType<OracleIntervalDS>(Codec.FromPortable(portable, "INTERVAL DAY TO SECOND"));
        Assert.Equal(-1, back.Days);
        Assert.Equal(-2, back.Hours);
        Assert.Equal(-3, back.Minutes);
        Assert.Equal(-4, back.Seconds);
        Assert.Equal(-5, back.Nanoseconds);

        var positive = new OracleIntervalDS(10, 23, 59, 59, 999_999_999);
        var positiveBack = Assert.IsType<OracleIntervalDS>(
            Codec.FromPortable(Codec.ToPortable(positive, "INTERVAL DAY TO SECOND"), "INTERVAL DAY TO SECOND"));
        Assert.Equal(positive, positiveBack);

        var tooBig = new PortableIntervalDs((Int128)1_000_000_000L * 86_400L * 1_000_000_000L);
        Assert.Throws<PortableConversionException>(() => Codec.FromPortable(tooBig, "INTERVAL DAY TO SECOND"));
    }

    [Fact]
    public void Unexpected_values_and_unsupported_types_are_rejected()
    {
        Assert.Throws<InvalidCastException>(() => Codec.ToPortable("text", "NUMBER"));
        Assert.Throws<InvalidCastException>(() => Codec.ToPortable(1.5d, "BINARY_FLOAT"));
        Assert.Throws<InvalidCastException>(() => Codec.ToPortable("2026-01-01", "TIMESTAMP"));
        Assert.Throws<NotSupportedException>(() => Codec.ToPortable("x", "LONG"));
        Assert.Throws<NotSupportedException>(() => Codec.ToPortable(new byte[1], "LONG RAW"));
        Assert.Throws<NotSupportedException>(() => Codec.ToPortable(null, "TIMESTAMP WITH LOCAL TIME ZONE"));
        Assert.Throws<NotSupportedException>(() => Codec.FromPortable("x", "BFILE"));

        Assert.Throws<PortableConversionException>(() => Codec.FromPortable("text", "NUMBER"));
        Assert.Throws<PortableConversionException>(() => Codec.FromPortable(PortableNumber.FromText("1"), "TIMESTAMP"));
        Assert.Throws<PortableConversionException>(() => Codec.FromPortable(new PortableTimestamp(new DateTime(2026, 1, 1), 0), "TIMESTAMP WITH TIME ZONE"));
    }

    [Fact]
    public void Readable_types_match_supported_types()
    {
        Assert.True(Codec.IsReadable("NUMBER(10)"));
        Assert.True(Codec.IsReadable("TIMESTAMP(6) WITH TIME ZONE"));
        Assert.True(Codec.IsReadable("BLOB"));
        Assert.False(Codec.IsReadable("LONG"));
        Assert.False(Codec.IsReadable("LONG RAW"));
        Assert.False(Codec.IsReadable("ROWID"));
        Assert.False(Codec.IsReadable("BFILE"));
    }
}
